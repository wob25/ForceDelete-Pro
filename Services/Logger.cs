using System.IO;

namespace ForceDelete.Services;

/// <summary>Append-only action log so every delete/kill is auditable.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static void Log(string action, string target, string outcome)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{action}\t{target}\t{outcome}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(Storage.LogFile, line);
        }
        catch { /* logging must never break the app */ }
    }
}
