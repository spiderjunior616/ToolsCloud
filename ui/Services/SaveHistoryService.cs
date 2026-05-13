using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolsCloud.Services;

public enum SaveEventType
{
    HydraImport,
    HydraImportTar,
    LudusaviImport,
    SteamCloudSync,
    ManualRestore,
    FilesDeleted,
    Merged,
    Replaced,
    SaveCreated,
    SaveModified,
    AchievementUnlocked
}

public class SaveHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public SaveEventType EventType { get; set; }
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int FileCount { get; set; }
    public long Bytes { get; set; }
    public string? SourcePath { get; set; }
    public string? Detail { get; set; }

    public string EventLabel => EventType switch
    {
        SaveEventType.HydraImport     => "Importado (Hydra)",
        SaveEventType.HydraImportTar  => "Importado (Hydra TAR)",
        SaveEventType.LudusaviImport  => "Importado (Ludusavi)",
        SaveEventType.SteamCloudSync  => "Sync Steam Cloud",
        SaveEventType.ManualRestore   => "Restaurado",
        SaveEventType.FilesDeleted    => "Arquivos deletados",
        SaveEventType.Merged          => "Fundido",
        SaveEventType.Replaced        => "Substituído",
        SaveEventType.SaveCreated     => "Novo Save",
        SaveEventType.SaveModified    => "Save Atualizado",
        SaveEventType.AchievementUnlocked => "Conquista/Estatística",
        _                             => "Evento"
    };

    public string FormattedSize => FileUtils.FormatSize(Bytes);
    public string FormattedTime => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}

public static class SaveHistoryService
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    private static string GetHistoryPath(string steamPath)
        => Path.Combine(steamPath, "cloud_redirect", "save_history.json");

    public static List<SaveHistoryEntry> Load(string steamPath)
    {
        var path = GetHistoryPath(steamPath);
        if (!File.Exists(path)) return new();
        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<SaveHistoryEntry>>(json);
            return list ?? new();
        }
        catch { return new(); }
    }

    public static void Append(string steamPath, SaveHistoryEntry entry)
    {
        var path = GetHistoryPath(steamPath);
        var list = Load(steamPath);
        list.Insert(0, entry); // newest first
        if (list.Count > 500) list = list.Take(500).ToList();
        FileUtils.AtomicWriteAllText(path, JsonSerializer.Serialize(list, _opts));
    }

    public static void AppendRange(string steamPath, IEnumerable<SaveHistoryEntry> entries)
    {
        var path = GetHistoryPath(steamPath);
        var list = Load(steamPath);
        foreach (var e in entries)
            list.Insert(0, e);
        if (list.Count > 500) list = list.Take(500).ToList();
        FileUtils.AtomicWriteAllText(path, JsonSerializer.Serialize(list, _opts));
    }
}

public static class HistoryScanner
{
    private class AppChangeInfo
    {
        public string AppId { get; set; } = "";
        public List<string> Added { get; set; } = new();
        public List<string> Modified { get; set; } = new();
        public List<string> Deleted { get; set; } = new();
    }

    public static void ScanAndLogDifferences(string steamPath)
    {
        var storagePath = Path.Combine(steamPath, "cloud_redirect", "storage");
        if (!Directory.Exists(storagePath)) return;

        var snapshotPath = Path.Combine(steamPath, "cloud_redirect", "storage_snapshot.json");
        var oldSnapshot = new Dictionary<string, DateTime>();
        if (File.Exists(snapshotPath))
        {
            try { oldSnapshot = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(snapshotPath)) ?? new(); }
            catch { }
        }

        var newSnapshot = new Dictionary<string, DateTime>();
        var allFiles = Directory.GetFiles(storagePath, "*", SearchOption.AllDirectories);
        
        var appChanges = new Dictionary<string, AppChangeInfo>();

        foreach (var file in allFiles)
        {
            var relPath = Path.GetRelativePath(storagePath, file);
            var lastWrite = File.GetLastWriteTimeUtc(file);
            newSnapshot[relPath] = lastWrite;

            var parts = relPath.Split(Path.DirectorySeparatorChar);
            string appId = "";
            if (parts.Length >= 2 && uint.TryParse(parts[1], out _)) appId = parts[1];
            else if (parts.Length >= 1 && uint.TryParse(parts[0], out _)) appId = parts[0];
            
            if (string.IsNullOrEmpty(appId)) continue;

            if (!appChanges.ContainsKey(appId)) appChanges[appId] = new AppChangeInfo { AppId = appId };

            if (!oldSnapshot.ContainsKey(relPath))
                appChanges[appId].Added.Add(file);
            else if (oldSnapshot[relPath] != lastWrite)
                appChanges[appId].Modified.Add(file);
        }

        foreach (var kvp in oldSnapshot)
        {
            if (!newSnapshot.ContainsKey(kvp.Key))
            {
                var parts = kvp.Key.Split(Path.DirectorySeparatorChar);
                string appId = "";
                if (parts.Length >= 2 && uint.TryParse(parts[1], out _)) appId = parts[1];
                else if (parts.Length >= 1 && uint.TryParse(parts[0], out _)) appId = parts[0];

                if (string.IsNullOrEmpty(appId)) continue;
                if (!appChanges.ContainsKey(appId)) appChanges[appId] = new AppChangeInfo { AppId = appId };
                appChanges[appId].Deleted.Add(kvp.Key);
            }
        }

        var historyToAppend = new List<SaveHistoryEntry>();
        bool IsAch(string f) => f.Contains("achievement", StringComparison.OrdinalIgnoreCase) || f.Contains("stat", StringComparison.OrdinalIgnoreCase) || f.Contains("UserGameStats", StringComparison.OrdinalIgnoreCase);

        foreach (var change in appChanges.Values)
        {
            var addedAch = change.Added.Where(IsAch).ToList();
            var modAch = change.Modified.Where(IsAch).ToList();
            var addedSave = change.Added.Where(f => !IsAch(f)).ToList();
            var modSave = change.Modified.Where(f => !IsAch(f)).ToList();
            var delSave = change.Deleted.Where(f => !IsAch(f)).ToList();

            DateTime GetLatest(IEnumerable<string> files)
            {
                try { return files.Max(f => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.UtcNow); }
                catch { return DateTime.UtcNow; }
            }

            if (addedAch.Count > 0 || modAch.Count > 0)
            {
                historyToAppend.Add(new SaveHistoryEntry {
                    EventType = SaveEventType.AchievementUnlocked,
                    AppId = change.AppId,
                    Timestamp = GetLatest(addedAch.Concat(modAch)),
                    Detail = "Progresso de conquistas ou estatísticas modificado.",
                    FileCount = addedAch.Count + modAch.Count
                });
            }

            if (addedSave.Count > 0)
            {
                historyToAppend.Add(new SaveHistoryEntry {
                    EventType = SaveEventType.SaveCreated,
                    AppId = change.AppId,
                    Timestamp = GetLatest(addedSave),
                    Detail = "Novos arquivos de save detectados.",
                    FileCount = addedSave.Count
                });
            }

            if (modSave.Count > 0)
            {
                historyToAppend.Add(new SaveHistoryEntry {
                    EventType = SaveEventType.SaveModified,
                    AppId = change.AppId,
                    Timestamp = GetLatest(modSave),
                    Detail = "O progresso do jogo foi reescrito (salvo).",
                    FileCount = modSave.Count
                });
            }

            if (delSave.Count > 0)
            {
                historyToAppend.Add(new SaveHistoryEntry {
                    EventType = SaveEventType.FilesDeleted,
                    AppId = change.AppId,
                    Timestamp = DateTime.UtcNow,
                    Detail = "Arquivos de save foram apagados.",
                    FileCount = delSave.Count
                });
            }
        }

        if (historyToAppend.Count > 0)
        {
            var sorted = historyToAppend.OrderBy(x => x.Timestamp).ToList();
            SaveHistoryService.AppendRange(steamPath, sorted);
        }

        try { File.WriteAllText(snapshotPath, JsonSerializer.Serialize(newSnapshot)); } catch { }
    }
}
