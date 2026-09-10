using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ForceDelete.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ForceDelete;

public partial class MainWindow : FluentWindow
{
    private static readonly Brush IdleBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly Brush IdleFill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ActiveFill = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0xC2, 0xFF));

    public MainWindow()
    {
        InitializeComponent();

        // Launched with a path argument (e.g. "Open with")? Pre-fill it.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && (File.Exists(args[1]) || Directory.Exists(args[1])))
        {
            PathBox.Text = args[1];
            AutoScan();
        }
    }

    private string TargetPath => PathBox.Text.Trim().Trim('"');

    // ---- Drag & drop (native WPF — works because the window is not elevated) ----

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropZone.BorderBrush = ActiveBorder;
            DropZone.Background = ActiveFill;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            PathBox.Text = files[0];
            AutoScan();
        }
    }

    private void ResetDropZone()
    {
        DropZone.BorderBrush = IdleBorder;
        DropZone.Background = IdleFill;
    }

    // ---- Browse + path details -------------------------------------------

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择要删除的文件", CheckFileExists = true };
        if (dlg.ShowDialog() == true) { PathBox.Text = dlg.FileName; AutoScan(); }
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要删除的文件夹" };
        if (dlg.ShowDialog() == true) { PathBox.Text = dlg.FolderName; AutoScan(); }
    }

    private void OnPathChanged(object sender, TextChangedEventArgs e) => UpdateDetails();
    private void OnPathCommitted(object sender, RoutedEventArgs e) => AutoScan();

    private void OnPathKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AutoScan();
    }

    /// <summary>On loading a valid file, show its details and silently scan for lockers.</summary>
    private void AutoScan()
    {
        try
        {
            UpdateDetails();
            var path = TargetPath;
            if (!File.Exists(path) && !Directory.Exists(path)) { ShowLockers(Array.Empty<RestartManager.LockingProcess>()); return; }

            var lockers = RestartManager.GetLockingProcesses(path);
            ShowLockers(lockers);

            if (DriverStore.IsDriverStorePath(path))
                Info("驱动程序包", "这是一个暂存的驱动程序 —— 强制删除将通过 pnputil 正确卸载并删除它。");
            else if (lockers.Count > 0)
                Warn($"{lockers.Count} 个进程正在占用此项目", "在关闭这些进程前，强制删除可能会失败 —— 请使用‘结束所有进程并重试’。");
            else
                Info("就绪", "没有进程占用此项目。");
        }
        catch (Exception ex)
        {
            Error("扫描失败", $"扫描占用进程时发生错误: {ex.Message}");
            ShowLockers(Array.Empty<RestartManager.LockingProcess>());
        }
    }

    private void UpdateDetails()
    {
        var path = TargetPath;
        if (string.IsNullOrWhiteSpace(path)) { Details.Text = "未选择目标。"; return; }

        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                Details.Text = $"📄 {fi.Name}  ·  {FormatSize(fi.Length)}  ·  {DescribeAttributes(fi.Attributes)}  ·  所有者: {OwnerOf(path, false)}";
            }
            else if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                Details.Text = $"📁 {di.Name}  ·  {SafeCount(di)} 个项目  ·  {DescribeAttributes(di.Attributes)}  ·  所有者: {OwnerOf(path, true)}";
            }
            else
            {
                Details.Text = "⚠ 路径不存在。";
            }
        }
        catch (Exception ex)
        {
            Details.Text = $"⚠ {ex.Message}";
        }
    }

    private static int SafeCount(DirectoryInfo di)
    {
        try { return di.GetFileSystemInfos().Length; } catch { return 0; }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes; int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.##} {units[u]}";
    }

    private static string DescribeAttributes(FileAttributes attr)
    {
        var flags = new List<string>();
        if (attr.HasFlag(FileAttributes.ReadOnly)) flags.Add("ReadOnly");
        if (attr.HasFlag(FileAttributes.Hidden)) flags.Add("Hidden");
        if (attr.HasFlag(FileAttributes.System)) flags.Add("System");
        return flags.Count == 0 ? "normal" : string.Join("+", flags);
    }

    private static string OwnerOf(string path, bool isDir)
    {
        try
        {
            IdentityReference owner = isDir
                ? new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(NTAccount))!
                : new FileInfo(path).GetAccessControl().GetOwner(typeof(NTAccount))!;
            return owner.Value;
        }
        catch { return "?"; }
    }

    // ---- Check what's locking it ------------------------------------------

    private void OnCheckLocks(object sender, RoutedEventArgs e)
    {
        if (!ValidateTarget(out var path)) return;

        var lockers = RestartManager.GetLockingProcesses(path);
        ShowLockers(lockers);

        if (lockers.Count == 0)
            Info("没有进程占用",
                "没有用户进程占用。如果仍无法删除，可能是权限问题（请试用强制删除）或系统锁定（请试用重启后删除）。");
        else
            Warn($"{lockers.Count} 个进程正在占用此文件", "结束进程并重试，或者手动关闭这些应用。");
    }

    // ---- Privileged actions ----------------------------------------------

    private async void OnForceDelete(object sender, RoutedEventArgs e)
    {
        if (!ValidateTarget(out var path)) return;

        // 驱动程序包特殊处理
        if (File.Exists(path) && DriverStore.IsDriverStorePath(path))
        {
            await TryRemoveDriverPackage(path);
            return;
        }

        if (!ConfirmDangerous(path)) return;

        // 自动化增强：先扫描占用进程
        var lockers = RestartManager.GetLockingProcesses(path);
        if (lockers.Count > 0)
        {
            var msg = $"发现 {lockers.Count} 个程序正在占用此目标，导致无法删除：\n\n" +
                      string.Join("\n", lockers.Select(l => $"• {l.Name} (PID: {l.Pid})")) +
                      "\n\n是否自动尝试关闭这些程序并强制删除？\n(注意：未保存的工作将会丢失)";

            var confirm = System.Windows.MessageBox.Show(msg, "发现占用程序",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (confirm == System.Windows.MessageBoxResult.Yes)
            {
                await RunAutoKillDelete(path);
                return;
            }
        }

        Info("正在处理…", "正在尝试强制删除 —— 请稍候。");
        var r = await Run("delete", path, SecureWipeBox.IsChecked == true);

        if (r.Ok)
        {
            Success("已删除", r.Message);
            ShowLockers(Array.Empty<RestartManager.LockingProcess>());
        }
        else
        {
            // 如果普通强删失败，再次深度扫描并提示
            var reScan = RestartManager.GetLockingProcesses(path);
            ShowLockers(reScan);
            if (reScan.Count > 0)
            {
                Warn("删除失败", "该项目仍被占用。请使用底部的‘结束所有进程并重试’。");
            }
            else
            {
                Error("删除失败", "无法识别占用进程。建议使用右侧的‘重启后删除’。");
            }
        }
    }

    private async Task RunAutoKillDelete(string path)
    {
        Info("正在处理…", "正在终止相关程序并强制删除。");
        var r = await Run("killdelete", path, SecureWipeBox.IsChecked == true);
        if (r.Ok) Success("已删除", "已成功关闭占用程序并删除了目标。");
        else Error("删除失败", r.Message);
        ShowLockers(Array.Empty<RestartManager.LockingProcess>());
    }

    private async void OnQuarantine(object sender, RoutedEventArgs e)
    {
        if (!ValidateTarget(out var path)) return;

        Info("正在处理…", "正在移入隔离区。");
        var r = await Run("quarantine", path, false);

        if (r.Ok) Success("已隔离", r.Message + "  （如果需要，可以从隔离文件夹恢复）");
        else Error("无法移入隔离区", r.Message);
    }

    private async void OnKillAndRetry(object sender, RoutedEventArgs e)
    {
        if (!ValidateTarget(out var path)) return;

        var confirm = System.Windows.MessageBox.Show(
            "这将强制终止所有占用该文件的进程，然后将其删除。这些应用中未保存的工作将会丢失。\n\n是否继续？",
            "结束进程？", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        Info("正在处理…", "正在终止占用进程并删除。");
        var r = await Run("killdelete", path, SecureWipeBox.IsChecked == true);

        if (r.Ok)
        {
            Success("已删除", r.Message);
            ShowLockers(Array.Empty<RestartManager.LockingProcess>());
        }
        else
        {
            Error("仍然被占用", r.Message);
        }
    }

    private async void OnDeleteOnReboot(object sender, RoutedEventArgs e)
    {
        if (!ValidateTarget(out var path)) return;
        if (!ConfirmDangerous(path)) return;

        Info("正在处理…", "正在安排重启时删除。");
        var r = await Run("reboot", path, false);

        if (r.Ok) Success("已安排", r.Message);
        else Error("无法安排", r.Message);
    }

    private async Task TryRemoveDriverPackage(string path)
    {
        var confirm = System.Windows.MessageBox.Show(
            "此文件是 DriverStore 中 Windows 驱动程序包的一部分。\n\n" +
            "直接删除单个文件会损坏驱动程序包。正确操作是使用 pnputil（Windows 内置驱动程序工具）删除整个驱动程序包，这也会将其从所有使用它的设备中卸载。\n\n" +
            "现在删除该驱动程序包吗？",
            "检测到驱动程序包", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        Info("正在处理…", "正在通过 pnputil 删除驱动程序包 —— 这可能需要一些时间。");
        var r = await Run("driver", path, false);

        if (r.Ok) Success("驱动程序已删除", r.Message);
        else Error("无法删除驱动程序", r.Message);
    }

    /// <summary>
    /// Try the operation in-process first (no UAC for files we can already delete);
    /// if it fails and we're not elevated, relaunch an elevated worker (one UAC).
    /// </summary>
    private async Task<Elevation.OpResult> Run(string op, string path, bool wipe)
    {
        SetActionsEnabled(false);
        try
        {
            var local = await Task.Run(() => Elevation.LocalExecute(op, path, wipe));
            if (local.Ok || Elevation.IsElevated)
            {
                Logger.Log(op, path, local.Ok ? "OK" : local.Message);
                return local;
            }

            Info("需要管理员权限", "正在提升权限以处理受保护的项目…");
            var elevated = await Elevation.RunElevated(op, path, wipe);
            Logger.Log($"{op} (via UAC)", path, elevated.Ok ? "OK" : elevated.Message);
            return elevated;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private void SetActionsEnabled(bool on)
    {
        CheckBtn.IsEnabled = on;
        DeleteBtn.IsEnabled = on;
        QuarantineBtn.IsEnabled = on;
        RebootBtn.IsEnabled = on;
        KillBtn.IsEnabled = on;
    }

    // ---- Footer tools -----------------------------------------------------

    private void OnOpenQuarantine(object sender, RoutedEventArgs e) => OpenInExplorer(Storage.QuarantineDir);

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Storage.LogFile)) OpenInExplorer(Storage.LogFile, select: true);
        else Info("暂无日志", "本次会话尚未记录任何内容。");
    }

    private static void OpenInExplorer(string path, bool select = false)
    {
        try
        {
            var args = select ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start("explorer.exe", args);
        }
        catch { /* ignore */ }
    }

    // ---- Helpers ----------------------------------------------------------

    private void ShowLockers(IReadOnlyList<RestartManager.LockingProcess> lockers)
    {
        LockList.ItemsSource = lockers;
        bool any = lockers.Count > 0;
        LockEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        KillBtn.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ValidateTarget(out string path)
    {
        path = TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Warn("无目标", "请先拖放或粘贴一个路径。");
            return false;
        }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Warn("未找到", "该路径不存在。");
            return false;
        }
        return true;
    }

    private bool ConfirmDangerous(string path)
    {
        var warning = SafetyGuard.GetDangerWarning(path);
        if (warning is null) return true;

        var result = System.Windows.MessageBox.Show(
            $"{warning}\n\n{path}\n\n你确定要继续吗？",
            "危险的目标", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Stop, System.Windows.MessageBoxResult.No);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    // ---- Status InfoBar ---------------------------------------------------

    private void Info(string title, string msg) => SetStatus(InfoBarSeverity.Informational, title, msg);
    private void Success(string title, string msg) => SetStatus(InfoBarSeverity.Success, title, msg);
    private void Warn(string title, string msg) => SetStatus(InfoBarSeverity.Warning, title, msg);
    private void Error(string title, string msg) => SetStatus(InfoBarSeverity.Error, title, msg);

    private void SetStatus(InfoBarSeverity severity, string title, string msg)
    {
        Status.Severity = severity;
        Status.Title = title;
        Status.Message = msg;
        Status.IsOpen = true;
    }
}
