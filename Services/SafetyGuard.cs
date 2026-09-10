using System.Diagnostics;
using System.IO;

namespace ForceDelete.Services;

/// <summary>
/// Guards against catastrophic deletes (System32, the Windows folder, drive roots)
/// and provides the "unlock by killing the holder" action.
/// </summary>
public static class SafetyGuard
{
    private static readonly string[] ProtectedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    };

    /// <summary>
    /// Returns a warning string if the path is dangerous to delete, otherwise null.
    /// The UI should require explicit extra confirmation when this is non-null.
    /// </summary>
    public static string? GetDangerWarning(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return null; }

        // Drive root, e.g. C:\
        if (full.Length <= 3 && full.EndsWith(":"))
            return "这是驱动器根目录。删除它几乎肯定是个错误。";

        foreach (var root in ProtectedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var r = root.TrimEnd('\\');
            if (full.Equals(r, StringComparison.OrdinalIgnoreCase))
                return $"'{root}' 是关键系统文件夹。删除它可能会导致 Windows 崩溃。";
            if (full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                return $"这位于关键系统文件夹 ({root}) 中。只有在确定无误的情况下才继续。";
        }
        return null;
    }

    /// <summary>Attempts to terminate a process that's holding a file handle.</summary>
    public static bool KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
