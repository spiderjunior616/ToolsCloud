using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ToolsCloud.Services;

public class SaveVariant
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public string Origin { get; set; } = "";
}

public static class SaveVariantService
{
    public static List<SaveVariant> GetVariants(string steamPath, string accountId, string appId)
    {
        var variants = new List<SaveVariant>();
        var variantsDir = Path.Combine(steamPath, "cloud_redirect", "storage", accountId, appId, "_variants");
        
        if (!Directory.Exists(variantsDir))
            return variants;

        foreach (var dir in Directory.GetDirectories(variantsDir))
        {
            var name = Path.GetFileName(dir);
            long size = 0;
            int count = 0;
            string origin = "Desconhecida";

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var fName = Path.GetFileName(f);
                if (fName == "_import_meta.json")
                {
                    try
                    {
                        var json = File.ReadAllText(f);
                        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
                        if (root.TryGetProperty("source", out var src))
                            origin = src.GetString() ?? origin;
                    }
                    catch { }
                    continue;
                }
                
                size += new FileInfo(f).Length;
                count++;
            }

            variants.Add(new SaveVariant
            {
                Name = name,
                Path = dir,
                CreatedAt = Directory.GetCreationTimeUtc(dir),
                TotalSize = size,
                FileCount = count,
                Origin = origin
            });
        }

        return variants.OrderByDescending(v => v.CreatedAt).ToList();
    }

    public static void SaveCurrentAsVariant(string steamPath, string accountId, string appId, string variantName)
    {
        var appDir = Path.Combine(steamPath, "cloud_redirect", "storage", accountId, appId);
        if (!Directory.Exists(appDir)) return;

        var targetVariantDir = Path.Combine(appDir, "_variants", variantName);
        Directory.CreateDirectory(targetVariantDir);

        var files = Directory.GetFiles(appDir, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            // Skip the _variants folder itself to avoid recursion
            if (file.Contains(Path.Combine(appDir, "_variants"))) continue;

            var relPath = Path.GetRelativePath(appDir, file);
            var destPath = Path.Combine(targetVariantDir, relPath);
            
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, true);
        }
    }

    public static void SetActiveVariant(string steamPath, string accountId, string appId, SaveVariant variant)
    {
        var appDir = Path.Combine(steamPath, "cloud_redirect", "storage", accountId, appId);
        if (!Directory.Exists(appDir)) return;

        // 1. Backup the current active save first to prevent data loss
        var currentOrigin = "Steam Cloud";
        var metaPath = Path.Combine(appDir, "_import_meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
                if (root.TryGetProperty("source", out var src))
                    currentOrigin = src.GetString() ?? "Steam Cloud";
            }
            catch { }
        }

        string backupName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{currentOrigin.Replace(" ", "")}";
        SaveCurrentAsVariant(steamPath, accountId, appId, backupName);

        // 2. Delete all files in the root folder (except _variants)
        var rootFiles = Directory.GetFiles(appDir, "*", SearchOption.AllDirectories);
        foreach (var f in rootFiles)
        {
            if (f.Contains(Path.Combine(appDir, "_variants"))) continue;
            try { File.Delete(f); } catch { }
        }

        // Clean empty directories (except _variants)
        var rootDirs = Directory.GetDirectories(appDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length);
        foreach (var d in rootDirs)
        {
            if (d.Contains(Path.Combine(appDir, "_variants"))) continue;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d);
            }
            catch { }
        }

        // 3. Copy files from the selected variant to the root folder
        var variantFiles = Directory.GetFiles(variant.Path, "*", SearchOption.AllDirectories);
        foreach (var file in variantFiles)
        {
            var relPath = Path.GetRelativePath(variant.Path, file);
            var destPath = Path.Combine(appDir, relPath);
            
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, true);
        }
    }
}
