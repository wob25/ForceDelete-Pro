using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ForceDelete.Services;

/// <summary>
/// Files under %SystemRoot%\System32\DriverStore\FileRepository are staged driver
/// packages. Windows protects them even from the owner, and raw-deleting a single
/// file leaves the package half-broken. The correct removal is pnputil, which this
/// wraps: find the published "oemNN.inf" name for the package, then delete-driver it.
/// </summary>
public static class DriverStore
{
    private static readonly Regex OemName = new(@"\boem\d+\.inf\b", RegexOptions.IgnoreCase);

    public static bool IsDriverStorePath(string path)
    {
        try
        {
            var repo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "DriverStore", "FileRepository");
            return Path.GetFullPath(path)
                       .StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public record Result(bool Success, string Message);

    /// <summary>
    /// Resolves the package's published oemNN.inf name and removes it with
    /// pnputil /delete-driver … /uninstall /force. Locale-independent: it matches
    /// on the (non-localized) value tokens, not the translated field labels.
    /// </summary>
    public static Result RemovePackage(string infPath)
    {
        // The original .inf file name, e.g. "AWCC_IM_Driver_Component.inf".
        string infName = Path.GetFileName(infPath);

        var (enumOut, enumOk) = RunPnpUtil("/enum-drivers");
        if (!enumOk)
            return new Result(false, "无法运行 pnputil 来列举驱动程序。");

        string? published = FindPublishedName(enumOut, infName, infPath);
        if (published is null)
            return new Result(false,
                $"无法将此文件匹配到已发布的驱动程序包。请尝试手动运行：\n  pnputil /enum-drivers");

        var (delOut, delOk) = RunPnpUtil($"/delete-driver {published} /uninstall /force");
        return delOk
            ? new Result(true, $"已通过 pnputil 删除了驱动程序包 {published}。")
            : new Result(false, $"pnputil 无法删除 {published}。\n{delOut.Trim()}");
    }

    /// <summary>Find the oemNN.inf block whose original name matches our file.</summary>
    private static string? FindPublishedName(string enumOutput, string infName, string infPath)
    {
        // Split into per-driver blocks on blank lines; values aren't localized.
        var blocks = Regex.Split(enumOutput, @"\r?\n\s*\r?\n");
        foreach (var block in blocks)
        {
            if (block.IndexOf(infName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = OemName.Match(block);
            if (m.Success) return m.Value;
        }
        return null;
    }

    private static (string output, bool ok) RunPnpUtil(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return ("", false);

            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return (output, p.HasExited && p.ExitCode == 0);
        }
        catch (Exception ex)
        {
            return (ex.Message, false);
        }
    }
}
