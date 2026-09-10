using System.IO;

namespace ForceDelete.Services;

/// <summary>
/// Resolves where ForceDelete keeps its quarantine + log. Prefers a "Data"
/// folder next to the exe (true portable behaviour); falls back to LocalAppData
/// when the exe lives somewhere read-only (e.g. a locked-down USB stick).
/// </summary>
public static class Storage
{
    private static string? _dataDir;

    public static string DataDir => _dataDir ??= ResolveDataDir();
    public static string QuarantineDir => EnsureDir(Path.Combine(DataDir, "Quarantine"));
    public static string LogFile => Path.Combine(DataDir, "forcedelete.log");

    private static string ResolveDataDir()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var candidate = Path.Combine(exeDir, "ForceDelete_Data");
            if (TryWritable(candidate)) return candidate;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForceDelete");
        EnsureDir(fallback);
        return fallback;
    }

    private static bool TryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".w");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
