using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ForceDelete.Services;

/// <summary>
/// Splits the app into a normal-integrity GUI (so Explorer drag-drop works) and a
/// short-lived elevated worker that performs the actual privileged file operation.
///
/// The GUI tries each operation in-process first (no UAC for files the user can
/// already delete). If that fails and we're not elevated, it relaunches THIS exe
/// with --op … under the "runas" verb (one UAC prompt), waits for it, and reads the
/// outcome back from a temp result file.
/// </summary>
public static class Elevation
{
    public record OpResult(bool Ok, string Message);

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    // ---- GUI side --------------------------------------------------------

    /// <summary>Perform an operation directly in this process (no elevation).</summary>
    public static OpResult LocalExecute(string op, string path, bool wipe)
    {
        try
        {
            var engine = new DeleteEngine();
            switch (op)
            {
                case "delete":
                    var d = engine.ForceDelete(path, wipe);
                    return new OpResult(d.Success, d.Message);
                case "quarantine":
                    var q = engine.MoveToQuarantine(path);
                    return new OpResult(q.Success, q.Message);
                case "reboot":
                    var r = engine.ScheduleDeleteOnReboot(path);
                    return new OpResult(r.Success, r.Message);
                case "driver":
                    var dr = DriverStore.RemovePackage(path);
                    return new OpResult(dr.Success, dr.Message);
                case "killdelete":
                    var lockers = RestartManager.GetLockingProcesses(path);
                    int killed = lockers.Count(p => SafetyGuard.KillProcess(p.Pid));
                    var kd = engine.ForceDelete(path, wipe);
                    return new OpResult(kd.Success, $"已终止 {killed}/{lockers.Count} 个占用进程。{kd.Message}");
                default:
                    return new OpResult(false, "未知的操作。");
            }
        }
        catch (Exception ex)
        {
            return new OpResult(false, ex.Message);
        }
    }

    /// <summary>Relaunch this exe elevated to perform the operation, and read the result.</summary>
    public static async Task<OpResult> RunElevated(string op, string path, bool wipe)
    {
        string resultFile = Path.Combine(Path.GetTempPath(), $"fd_{Guid.NewGuid():N}.txt");
        string args = $"--op {op} --path \"{path}\" --result \"{resultFile}\"" + (wipe ? " --wipe" : "");

        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new OpResult(false, "无法启动提升权限的助手程序。");
            await p.WaitForExitAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new OpResult(false, "用户在 UAC 提示符处取消了权限提升。");
        }
        catch (Exception ex)
        {
            return new OpResult(false, ex.Message);
        }

        try
        {
            if (File.Exists(resultFile))
            {
                string raw = File.ReadAllText(resultFile);
                File.Delete(resultFile);
                return new OpResult(raw.StartsWith('1'), raw.Length > 1 ? raw[1..] : "");
            }
        }
        catch { }
        return new OpResult(false, "提升权限的助手程序未报告结果。");
    }

    // ---- Worker side -----------------------------------------------------

    /// <summary>
    /// If launched with --op, run that operation headlessly, write the result file,
    /// and return true so the caller can exit without showing a window.
    /// </summary>
    public static bool TryRunWorker(string[] args)
    {
        int idx = Array.IndexOf(args, "--op");
        if (idx < 0 || idx + 1 >= args.Length) return false;

        string op = args[idx + 1];
        string path = GetValue(args, "--path") ?? "";
        string resultFile = GetValue(args, "--result") ?? "";
        bool wipe = args.Contains("--wipe");

        var result = LocalExecute(op, path, wipe);
        Logger.Log($"{op} (elevated)", path, result.Ok ? "OK" : result.Message);

        try
        {
            if (!string.IsNullOrEmpty(resultFile))
                File.WriteAllText(resultFile, (result.Ok ? "1" : "0") + result.Message);
        }
        catch { }
        return true;
    }

    private static string? GetValue(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }
}
