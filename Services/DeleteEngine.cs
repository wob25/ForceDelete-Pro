using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ForceDelete.Services;

/// <summary>
/// The core deletion engine. Handles the four reasons Windows refuses a delete:
/// permissions/ownership, read-only/system attributes, long/odd paths, and
/// files that can only be removed on reboot.
/// </summary>
public class DeleteEngine
{
    public DeleteEngine()
    {
        // Enable take-ownership / restore / backup privileges up front — without
        // this, seizing SYSTEM- or TrustedInstaller-owned files throws "unauthorized".
        Privileges.EnableAll();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    private const int SHCNE_ALLEVENTS = 0x7FFFFFFF;
    private const int SHCNF_FLUSH = 0x1000;

    private static void NotifyShell()
    {
        try
        {
            // 告诉 Windows Shell 全局刷新，确保删除后的文件夹残影立即消失
            SHChangeNotify(SHCNE_ALLEVENTS, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>Prefix that disables MAX_PATH and trailing-dot/space normalization.</summary>
    private static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\")) return path;
        if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    public record DeleteResult(bool Success, string Message, bool ScheduledForReboot = false);

    /// <summary>
    /// Attempts a normal delete; on failure, clears attributes, takes ownership,
    /// grants full control, and retries. Optionally secure-wipes first.
    /// </summary>
    public DeleteResult ForceDelete(string path, bool secureWipe = false)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path))
                return new DeleteResult(false, "路径不存在。");

            // 1. Strip ReadOnly / Hidden / System so the delete isn't blocked by attributes.
            ClearAttributes(path, isDir);

            // 2. Plain attempt first — cheapest path.
            if (TryDelete(path, isDir, secureWipe))
            {
                NotifyShell();
                return new DeleteResult(true, "已删除。");
            }

            // 3. Permission problem: take ownership + grant ourselves full control, then retry.
            TakeOwnershipAndGrant(path, isDir);
            ClearAttributes(path, isDir);

            if (TryDelete(path, isDir, secureWipe))
            {
                NotifyShell();
                return new DeleteResult(true, "获取所有权后已删除。");
            }

            return new DeleteResult(false,
                "仍被锁定 —— 可能是被正在运行的进程占用。请检查谁在占用，或尝试重启后删除。");
        }
        catch (Exception ex)
        {
            return new DeleteResult(false, ex.Message);
        }
    }

    private bool TryDelete(string path, bool isDir, bool secureWipe)
    {
        try
        {
            if (isDir)
            {
                DeleteDirectoryRecursive(path, secureWipe);
            }
            else
            {
                if (secureWipe) SecureWipe(path);
                File.Delete(Extended(path));
            }
            return !(Directory.Exists(path) || File.Exists(path));
        }
        catch
        {
            return false;
        }
    }

    private void DeleteDirectoryRecursive(string dir, bool secureWipe)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ClearAttributes(file, false);
            try { TakeOwnershipAndGrant(file, false); } catch { }
            if (secureWipe) SecureWipe(file);
            File.Delete(Extended(file));
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            DeleteDirectoryRecursive(sub, secureWipe);
        }
        ClearAttributes(dir, true);
        Directory.Delete(Extended(dir), false);
    }

    private static void ClearAttributes(string path, bool isDir)
    {
        try
        {
            if (isDir)
                new DirectoryInfo(path).Attributes = FileAttributes.Normal;
            else
                File.SetAttributes(Extended(path), FileAttributes.Normal);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Managed equivalent of `takeown /f` + `icacls /grant administrators:F`.
    /// </summary>
    private static void TakeOwnershipAndGrant(string path, bool isDir)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        if (isDir)
        {
            var di = new DirectoryInfo(path);
            var sec = new DirectorySecurity();
            sec.SetOwner(currentUser ?? admins);
            di.SetAccessControl(sec);

            sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        else
        {
            var fi = new FileInfo(path);
            var sec = new FileSecurity();
            sec.SetOwner(currentUser ?? admins);
            fi.SetAccessControl(sec);

            sec = fi.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl,
                AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
    }

    /// <summary>
    /// Move-then-purge safety net: relocates the target into the quarantine folder
    /// (taking ownership if needed) instead of deleting it outright, so it can be
    /// restored later. Returns the quarantine path in the message on success.
    /// </summary>
    public DeleteResult MoveToQuarantine(string path)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path))
                return new DeleteResult(false, "路径不存在。");

            ClearAttributes(path, isDir);
            try { TakeOwnershipAndGrant(path, isDir); } catch { }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name = Path.GetFileName(path.TrimEnd('\\'));
            string dest = Path.Combine(Storage.QuarantineDir, $"{stamp}__{name}");

            if (isDir) MoveDirectorySafe(path, dest);
            else MoveFileSafe(path, dest);

            NotifyShell();
            return new DeleteResult(true, $"已移入隔离区: {dest}");
        }
        catch (Exception ex)
        {
            return new DeleteResult(false, ex.Message);
        }
    }

    // Move helpers that survive crossing volumes (Directory.Move can't; a plain
    // Move across drives throws, so we fall back to copy-then-delete).
    private static void MoveFileSafe(string src, string dest)
    {
        try { File.Move(Extended(src), Extended(dest)); }
        catch (IOException)
        {
            File.Copy(Extended(src), Extended(dest), overwrite: true);
            File.Delete(Extended(src));
        }
    }

    private static void MoveDirectorySafe(string src, string dest)
    {
        try { Directory.Move(src, dest); }
        catch (IOException)
        {
            CopyDirectory(src, dest);
            Directory.Delete(Extended(src), recursive: true);
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(Extended(file), Extended(Path.Combine(dest, Path.GetFileName(file))), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    /// <summary>
    /// Marks a path for deletion on the next reboot — the only option for files
    /// the OS won't release while running (in-use system files, pending updates).
    /// </summary>
    public DeleteResult ScheduleDeleteOnReboot(string path)
    {
        if (MoveFileEx(Extended(path), null, MOVEFILE_DELAY_UNTIL_REBOOT))
        {
            NotifyShell();
            return new DeleteResult(true, "已安排在下次重启时删除。", ScheduledForReboot: true);
        }

        int err = Marshal.GetLastWin32Error();
        return new DeleteResult(false, $"无法安排重启后删除 (Win32 错误码 {err})。");
    }

    /// <summary>Overwrites file contents with random data before deletion.</summary>
    private static void SecureWipe(string path)
    {
        try
        {
            var info = new FileInfo(Extended(path));
            long length = info.Length;
            if (length == 0) return;

            using var stream = new FileStream(Extended(path), FileMode.Open, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            long written = 0;
            while (written < length)
            {
                rng.GetBytes(buffer);
                int toWrite = (int)Math.Min(buffer.Length, length - written);
                stream.Write(buffer, 0, toWrite);
                written += toWrite;
            }
            stream.Flush(true);
        }
        catch { /* wipe is best-effort; deletion still proceeds */ }
    }
}
