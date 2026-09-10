using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ForceDelete.Services;

/// <summary>
/// Uses the Windows Restart Manager API to discover which running processes
/// currently hold an open handle to a given file. This is what lets us answer
/// "what's locking this?" instead of just failing the delete.
/// </summary>
public static class RestartManager
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    private const int RmRebootReasonNone = 0;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    public record LockingProcess(int Pid, string Name, string? ImagePath);

    /// <summary>
    /// Returns the list of processes holding a handle to <paramref name="path"/>.
    /// Empty list means nothing is locking it (or it's locked by the OS kernel,
    /// which Restart Manager cannot enumerate).
    /// </summary>
    public static IReadOnlyList<LockingProcess> GetLockingProcesses(string path)
    {
        var result = new List<LockingProcess>();
        var seenPids = new HashSet<int>();
        var pathsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string fullPath = Path.GetFullPath(path);
            AddPathVariants(pathsToScan, fullPath);

            if (Directory.Exists(fullPath))
            {
                var options = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true, MaxRecursionDepth = 5 };
                foreach (var f in Directory.EnumerateFileSystemEntries(fullPath, "*", options))
                {
                    AddPathVariants(pathsToScan, f);
                    if (pathsToScan.Count > 1000) break;
                }
            }
        }
        catch { }

        // 1. 系统级硬锁扫描 (Restart Manager API)
        ScanHardLocks(pathsToScan, seenPids, result);

        // 2. 窗口标题软锁扫描 (针对记事本等不产生硬锁的程序)
        ScanSoftLocks(path, seenPids, result);

        return result;
    }

    private static void ScanHardLocks(HashSet<string> pathsToScan, HashSet<int> seenPids, List<LockingProcess> result)
    {
        if (pathsToScan.Count == 0) return;
        int rv = RmStartSession(out uint session, 0, Guid.NewGuid().ToString());
        if (rv != 0) return;
        try
        {
            var resources = pathsToScan.ToArray();
            const int BatchSize = 80;
            for (int i = 0; i < resources.Length; i += BatchSize)
            {
                var batch = resources.Skip(i).Take(BatchSize).ToArray();
                RmRegisterResources(session, (uint)batch.Length, batch, 0, null, 0, null);
            }
            uint pnProcInfoNeeded = 0;
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = RmRebootReasonNone;
            if (RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons) == ERROR_MORE_DATA)
            {
                var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                if (RmGetList(session, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons) == 0)
                {
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        int pid = processInfo[i].Process.dwProcessId;
                        if (pid <= 4 || pid == Environment.ProcessId || !seenPids.Add(pid)) continue;
                        result.Add(CreateLockingProcess(pid, processInfo[i].strAppName));
                    }
                }
            }
        }
        finally { RmEndSession(session); }
    }

    private static void ScanSoftLocks(string targetPath, HashSet<int> seenPids, List<LockingProcess> result)
    {
        try
        {
            var matchKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 获取目标本身的名称 (解决 VS Code 这种以文件夹名为标题的情况)
            try
            {
                string cleanedPath = targetPath.TrimEnd('\\', '/');
                if (cleanedPath.Length > 3)
                {
                    string targetName = Path.GetFileName(cleanedPath);
                    if (!string.IsNullOrEmpty(targetName)) matchKeywords.Add(targetName);
                }
            }
            catch { }

            // 2. 收集内部文件名 (仅当目标是目录时执行深度扫描)
            if (Directory.Exists(targetPath))
            {
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 };
                    int count = 0;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(targetPath, "*", options))
                    {
                        try
                        {
                            string name = Path.GetFileName(entry);
                            if (!string.IsNullOrEmpty(name)) matchKeywords.Add(name);
                        }
                        catch { }
                        if (++count > 100) break;
                    }
                }
                catch { }
            }
            else if (File.Exists(targetPath))
            {
                try { matchKeywords.Add(Path.GetFileName(targetPath)); } catch { }
            }

            if (matchKeywords.Count == 0) return;

            // 3. 遍历所有带窗口的进程进行标题匹配
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4 || proc.Id == Environment.ProcessId || seenPids.Contains(proc.Id)) continue;

                    string title = proc.MainWindowTitle;
                    if (string.IsNullOrEmpty(title)) continue;

                    // 模糊匹配：如果窗口标题包含任何一个关键字
                    if (matchKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (seenPids.Add(proc.Id))
                            result.Add(CreateLockingProcess(proc.Id, proc.ProcessName));
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    private static LockingProcess CreateLockingProcess(int pid, string defaultName)
    {
        string name = defaultName;
        string? path = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            path = p.MainModule?.FileName;
        }
        catch { }
        return new LockingProcess(pid, name, path);
    }

    private static void AddPathVariants(HashSet<string> set, string path)
    {
        set.Add(path);
        set.Add(path.TrimEnd('\\') + "\\");
        if (!path.StartsWith(@"\\?\"))
        {
            set.Add(@"\\?\" + path);
            set.Add(@"\\?\" + path.TrimEnd('\\') + "\\");
        }
    }
}
