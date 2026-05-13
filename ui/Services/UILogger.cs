using System;
using System.IO;
using System.Text;

namespace ToolsCloud.Services;

/// <summary>
/// Writes UI-side events to cr_ui.log alongside the native cloud_redirect.log.
/// Thread-safe via a static lock. Each entry is timestamped and tagged.
/// </summary>
public static class UILogger
{
    private static readonly object _lock = new();

    private static string? GetPath()
    {
        var steamPath = SteamDetector.FindSteamPath();
        return steamPath == null ? null : Path.Combine(steamPath, "cr_ui.log");
    }

    public static void Log(string tag, string message)
    {
        try
        {
            var path = GetPath();
            if (path == null) return;

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                // Keep log under 2MB — trim oldest half if over limit
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    var lines = File.ReadAllLines(path);
                    var half = lines.Length / 2;
                    File.WriteAllLines(path, lines.Skip(half));
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch { /* never crash the app because of logging */ }
    }

    public static void LogImport(string source, string appId, string gameName, int files, string size, string? detail = null)
        => Log("IMPORT", $"{source} | appId={appId} | game=\"{gameName}\" | files={files} | size={size}" +
                          (detail != null ? $" | {detail}" : ""));

    public static void LogDelete(string appId, string gameName, int files)
        => Log("DELETE", $"appId={appId} | game=\"{gameName}\" | files={files}");

    public static void LogHistory(string action, string appId, string gameName, string? detail = null)
        => Log("HISTORY", $"{action} | appId={appId} | game=\"{gameName}\"" +
                           (detail != null ? $" | {detail}" : ""));

    public static void LogError(string context, string error)
        => Log("ERROR", $"[{context}] {error}");

    public static void LogNav(string page)
        => Log("NAV", $"Navigated to {page}");
}
