using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ToolsCloud.Services;

using System.Formats.Tar;

namespace ToolsCloud.Pages;

public class HydraBackupItem
{
    public bool IsSelected { get; set; } = true;
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string TarPath { get; set; } = "";
    public bool IsTar { get; set; } = false;
    public string? HeaderUrl { get; set; }
}

public enum ImportMode { Replace, Merge }

public partial class HydraPage : Page
{
    private List<HydraBackupItem> _backups = new();

    public HydraPage()
    {
        InitializeComponent();
    }

    private async void LoadBackups_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Loading backups...";
        ImportBtn.IsEnabled = false;

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var hydraBackupsDir = Path.Combine(appData, "hydralauncher", "Backups");

            if (!Directory.Exists(hydraBackupsDir))
            {
                StatusText.Text = "Hydra backups folder not found.";
                return;
            }

            var items = new List<HydraBackupItem>();

            await Task.Run(async () =>
            {
                var dirs = Directory.GetDirectories(hydraBackupsDir, "steam-*");
                var ids = dirs.Select(d => Path.GetFileName(d).Replace("steam-", "")).ToList();
                
                var storeInfo = new Dictionary<uint, StoreAppInfo>();
                var uintIds = ids.Select(id => uint.TryParse(id, out var uid) ? uid : 0).Where(id => id != 0).ToList();
                
                if (uintIds.Count > 0)
                {
                    storeInfo = await SteamStoreClient.Shared.GetAppInfoAsync(uintIds);
                }

                foreach (var dir in dirs)
                {
                    var appIdStr = Path.GetFileName(dir).Replace("steam-", "");
                    var innerDir = Path.Combine(dir, appIdStr);
                    if (!Directory.Exists(innerDir)) continue;

                    var driveCDir = Path.Combine(innerDir, "drive-C");
                    long size = 0;
                    if (Directory.Exists(driveCDir))
                    {
                        var files = Directory.GetFiles(driveCDir, "*", SearchOption.AllDirectories);
                        size = files.Sum(f => new FileInfo(f).Length);
                    }

                    var name = "Unknown Game";
                    StoreAppInfo? info = null;
                    if (uint.TryParse(appIdStr, out var appId) && storeInfo.TryGetValue(appId, out var parsedInfo))
                    {
                        info = parsedInfo;
                        name = info.Name;
                    }

                    // Only add if there's actual data; skip empty backup shells
                    if (size > 0)
                    {
                        items.Add(new HydraBackupItem
                        {
                            AppId = appIdStr,
                            Name = name,
                            Size = FileUtils.FormatSize(size),
                            FolderPath = innerDir,
                            HeaderUrl = info?.HeaderUrl
                        });
                    }
                }

                // ALSO find .tar files directly in hydralauncher root!
                var hydraRoot = Path.Combine(appData, "hydralauncher");
                if (Directory.Exists(hydraRoot))
                {
                    var tarFiles = Directory.GetFiles(hydraRoot, "*.tar");
                    foreach (var tarFile in tarFiles)
                    {
                        try
                        {
                            string? foundAppId = null;

                            using (var stream = File.OpenRead(tarFile))
                            using (var reader = new TarReader(stream))
                            {
                                TarEntry? entry;
                                int maxEntries = 10;
                                while (maxEntries-- > 0 && (entry = reader.GetNextEntry()) != null)
                                {
                                    var rawName = entry.Name ?? "";
                                    // Strip null chars — .NET TarReader concatenates prefix+name with nulls
                                    var cleanName = new string(rawName.Where(c => c != '\0').ToArray());
                                    
                                    // Look for pattern: <digits>/drive- or <digits>/mapping
                                    // The real AppId is embedded like: ...prefix..././1593500/drive-C/...
                                    var match = System.Text.RegularExpressions.Regex.Match(
                                        cleanName, @"(\d{3,8})/(?:drive-|mapping)");
                                    if (match.Success)
                                    {
                                        foundAppId = match.Groups[1].Value;
                                        break;
                                    }
                                }
                            }

                            if (foundAppId == null) continue;

                            // Dedup: if a folder backup already exists for this appId,
                            // keep whichever has more data (folder vs tar).
                            var existingBackup = items.FirstOrDefault(i => i.AppId == foundAppId && !i.IsTar);
                            if (existingBackup != null)
                            {
                                long folderBytes = 0;
                                var driveCCheck = Path.Combine(existingBackup.FolderPath, "drive-C");
                                if (Directory.Exists(driveCCheck))
                                {
                                    try { folderBytes = Directory.GetFiles(driveCCheck, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { }
                                }

                                // Re-calculate tar uncompressed size for comparison
                                long tarUncompressed = 0;
                                try
                                {
                                    using var cmpStream = File.OpenRead(tarFile);
                                    using var cmpReader = new TarReader(cmpStream);
                                    TarEntry? cmpEntry;
                                    while ((cmpEntry = cmpReader.GetNextEntry()) != null)
                                        if (cmpEntry.EntryType == TarEntryType.RegularFile || cmpEntry.EntryType == TarEntryType.V7RegularFile)
                                            tarUncompressed += cmpEntry.Length;
                                }
                                catch { tarUncompressed = new FileInfo(tarFile).Length; }

                                if (folderBytes >= tarUncompressed)
                                    continue; // folder has equal or more data, keep it

                                // Tar has more data — replace folder entry with tar
                                items.Remove(existingBackup);
                            }

                            var nameStr = "Unknown Game";
                            if (uint.TryParse(foundAppId, out var appId) && storeInfo.TryGetValue(appId, out var info))
                            {
                                nameStr = info.Name;
                            }
                            else if (uint.TryParse(foundAppId, out var appId2))
                            {
                                var newInfo = await SteamStoreClient.Shared.GetAppInfoAsync(new List<uint> { appId2 });
                                if (newInfo.TryGetValue(appId2, out var i2))
                                    nameStr = i2.Name;
                            }

                            // Calculate uncompressed size by summing entry sizes (not the compressed .tar size)
                            long uncompressedSize = 0;
                            try
                            {
                                using var sizeStream = File.OpenRead(tarFile);
                                using var sizeReader = new TarReader(sizeStream);
                                TarEntry? sizeEntry;
                                while ((sizeEntry = sizeReader.GetNextEntry()) != null)
                                {
                                    if (sizeEntry.EntryType == TarEntryType.RegularFile ||
                                        sizeEntry.EntryType == TarEntryType.V7RegularFile)
                                        uncompressedSize += sizeEntry.Length;
                                }
                            }
                            catch { uncompressedSize = new FileInfo(tarFile).Length; }

                            items.Add(new HydraBackupItem
                            {
                                AppId = foundAppId,
                                Name = nameStr + " (Tar)",
                                Size = FileUtils.FormatSize(uncompressedSize),
                                TarPath = tarFile,
                                IsTar = true,
                                HeaderUrl = storeInfo.TryGetValue(uint.TryParse(foundAppId, out var aid) ? aid : 0, out var i) ? i.HeaderUrl : null
                            });
                        }
                        catch { }
                    }
                }
            });

            _backups = items;
            ApplyFilter();
            StatusText.Text = $"Found {_backups.Count} backups.";
            ImportBtn.IsEnabled = _backups.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            BackupList.ItemsSource = null;
            BackupList.ItemsSource = _backups;
            return;
        }

        var filtered = _backups.Where(b => 
            b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || 
            b.AppId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            
        BackupList.ItemsSource = null;
        BackupList.ItemsSource = filtered;
    }

    private static long ParseStoredBytes(string formatted, long fallback = 0)
    {
        try
        {
            var idx = formatted.LastIndexOf(' ');
            if (idx < 0) return fallback;
            var numStr = formatted[..idx].Trim().Replace(',', '.');
            var unit = formatted[(idx + 1)..].Trim().ToUpperInvariant();
            if (!double.TryParse(numStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
                return fallback;
            return unit switch
            {
                "B"  => (long)num,
                "KB" => (long)(num * 1024),
                "MB" => (long)(num * 1024 * 1024),
                "GB" => (long)(num * 1024 * 1024 * 1024),
                _    => fallback,
            };
        }
        catch { return fallback; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var selected = _backups.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "No items selected.";
            return;
        }

        ImportBtn.IsEnabled = false;
        StatusText.Text = $"Importing {selected.Count} games...";

        try
        {
            var steamPath = SteamDetector.FindSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                MessageBox.Show("Steam path not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error: Steam path not found.";
                return;
            }

            string activeAccountId = "0";
            var userdataDir = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userdataDir))
            {
                var accounts = Directory.GetDirectories(userdataDir)
                    .Select(Path.GetFileName)
                    .Where(n => n != "0" && n != "anonymous")
                    .ToList();
                if (accounts.Count > 0)
                {
                    activeAccountId = accounts[0];
                }
            }

            int copiedFiles = 0;
            var errors = new List<string>();
                            // Check which selected games already have saves in storage
            var existingApps = selected
                .Where(b =>
                {
                    var dir = Path.Combine(steamPath, "cloud_redirect", "storage", activeAccountId, b.AppId);
                    return Directory.Exists(dir) &&
                           Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                               .Any(f => Path.GetFileName(f) is not ("cn.dat" or "root_token.dat" or "file_tokens.dat" or "_import_meta.json"));
                })
                .ToList();

            // If any existing saves found, ask the user what to do
            ImportMode importMode = ImportMode.Replace;
            if (existingApps.Count > 0)
            {
                var gameNames = string.Join("\n  • ", existingApps.Select(b => b.Name).Distinct().Take(5));
                if (existingApps.Count > 5) gameNames += $"\n  ... e mais {existingApps.Count - 5}";

                var msg = $"Os seguintes jogos já possuem saves no CloudRedirect:\n\n  • {gameNames}\n\n" +
                          "Como deseja importar os saves do Hydra?\n\n" +
                          "• Substituir — apaga os saves atuais e coloca os do Hydra\n" +
                          "• Fundir — mantém os saves atuais e adiciona os do Hydra ao lado\n" +
                          "• Cancelar — não importa nada";

                var dlg = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Saves existentes encontrados",
                    Content = msg,
                    PrimaryButtonText = "Substituir",
                    SecondaryButtonText = "Fundir",
                    CloseButtonText = "Cancelar",
                    Owner = Window.GetWindow(this)
                };

                var dlgResult = await dlg.ShowDialogAsync();

                if (dlgResult == Wpf.Ui.Controls.MessageBoxResult.None)
                {
                    ImportBtn.IsEnabled = true;
                    return;
                }

                importMode = dlgResult == Wpf.Ui.Controls.MessageBoxResult.Primary
                    ? ImportMode.Replace
                    : ImportMode.Merge;
            }

            await Task.Run(() =>
            {
                foreach (var backup in selected)
                {
                    var crStorageDir = Path.Combine(steamPath, "cloud_redirect", "storage", activeAccountId, backup.AppId);

                    // Backup current active save as a variant to ensure no data is lost
                    SaveVariantService.SaveCurrentAsVariant(steamPath, activeAccountId, backup.AppId, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_PreHydraImport");

                    // Apply import mode for existing apps
                    bool hasExisting = existingApps.Any(b => b.AppId == backup.AppId);
                    if (hasExisting && importMode == ImportMode.Replace)
                    {
                        // Delete existing save files (preserve metadata files and _variants)
                        if (Directory.Exists(crStorageDir))
                        {
                            foreach (var f in Directory.GetFiles(crStorageDir, "*", SearchOption.AllDirectories))
                            {
                                if (f.Contains(Path.Combine(crStorageDir, "_variants"))) continue;

                                var name = Path.GetFileName(f);
                                if (name is "cn.dat" or "root_token.dat" or "file_tokens.dat" or "_import_meta.json") continue;
                                try { File.Delete(f); } catch { }
                            }
                        }
                    }

                    Directory.CreateDirectory(crStorageDir);

                    if (backup.IsTar)
                    {
                        try
                        {
                            var tempFolder = Path.Combine(Path.GetTempPath(), "HydraTars", Guid.NewGuid().ToString());
                            Directory.CreateDirectory(tempFolder);

                            // Extract tar manually entry by entry to handle null-padded prefix paths
                            using (var stream = File.OpenRead(backup.TarPath))
                            using (var reader = new TarReader(stream))
                            {
                                TarEntry? entry;
                                while ((entry = reader.GetNextEntry()) != null)
                                {
                                    if (entry.EntryType == TarEntryType.RegularFile || entry.EntryType == TarEntryType.V7RegularFile)
                                    {
                                        var rawName = entry.Name ?? "";
                                        // Strip null chars from the prefix padding
                                        var cleanName = new string(rawName.Where(c => c != '\0').ToArray()).Trim();
                                        
                                        // Find the real path: look for AppId/drive- or AppId/mapping
                                        // The name looks like: "1512726452315127264523/./1593500/drive-C/Users/..."
                                        // We want: "1593500/drive-C/Users/..."
                                        var match = System.Text.RegularExpressions.Regex.Match(
                                            cleanName, @"(\d{3,8}/(?:drive-|mapping).*)");
                                        
                                        string entryName;
                                        if (match.Success)
                                        {
                                            entryName = match.Groups[1].Value;
                                        }
                                        else
                                        {
                                            // Fallback: strip ./ and use as-is
                                            entryName = cleanName;
                                            if (entryName.StartsWith("./")) entryName = entryName.Substring(2);
                                        }
                                        
                                        if (string.IsNullOrWhiteSpace(entryName)) continue;

                                        var destPath = Path.Combine(tempFolder, entryName.Replace('/', Path.DirectorySeparatorChar));
                                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                                        entry.ExtractToFile(destPath, true);
                                    }
                                }
                            }

                            // Now find files inside the extracted structure
                            // Structure: tempFolder/<AppId>/drive-C/... or tempFolder/<AppId>/mapping.yaml
                            var gameFolder = Path.Combine(tempFolder, backup.AppId);
                            if (!Directory.Exists(gameFolder))
                            {
                                // Maybe it extracted without the appId subfolder
                                gameFolder = tempFolder;
                            }

                            var allDrives = Directory.GetDirectories(gameFolder, "drive-*", SearchOption.TopDirectoryOnly);
                            if (allDrives.Length > 0)
                            {
                                foreach (var drive in allDrives)
                                {
                                    var files = Directory.GetFiles(drive, "*", SearchOption.AllDirectories);
                                    foreach (var file in files)
                                    {
                                        // Preserve full relative path structure!
                                        var relativePath = Path.GetRelativePath(drive, file);
                                        var destFile = Path.Combine(crStorageDir, relativePath);
                                        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                                        File.Copy(file, destFile, true);
                                        copiedFiles++;
                                    }
                                }
                            }
                            else
                            {
                                // Fallback: copy all non-metadata files preserving structure
                                var files = Directory.GetFiles(gameFolder, "*", SearchOption.AllDirectories)
                                    .Where(f => !f.EndsWith("mapping.yaml")).ToArray();
                                foreach (var file in files)
                                {
                                    var relativePath = Path.GetRelativePath(gameFolder, file);
                                    var destFile = Path.Combine(crStorageDir, relativePath);
                                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                                    File.Copy(file, destFile, true);
                                    copiedFiles++;
                                }
                            }

                            // Clean up temp
                            try { Directory.Delete(tempFolder, true); } catch { }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{backup.Name}: TAR error - {ex.Message}");
                        }
                    }
                    else
                    {
                        // Regular backup folder
                        var driveDirs = new List<string>();
                        if (Directory.Exists(backup.FolderPath))
                        {
                            driveDirs = Directory.GetDirectories(backup.FolderPath, "drive-*").ToList();
                        }

                        if (driveDirs.Count == 0)
                        {
                            errors.Add($"{backup.Name}: No drive-* folders found in {backup.FolderPath}");
                            continue;
                        }

                        foreach (var driveDir in driveDirs)
                        {
                            var files = Directory.GetFiles(driveDir, "*", SearchOption.AllDirectories);
                            foreach (var file in files)
                            {
                                // Preserve full relative path structure!
                                var relativePath = Path.GetRelativePath(driveDir, file);
                                var destFile = Path.Combine(crStorageDir, relativePath);
                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                                    File.Copy(file, destFile, true);
                                    copiedFiles++;
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"{backup.Name}: Copy error - {ex.Message}");
                                }
                            }
                        }
                    }

                    // Write import metadata
                    try
                    {
                        var metaPath = Path.Combine(crStorageDir, "_import_meta.json");
                        var meta = new System.Text.Json.Nodes.JsonObject
                        {
                            ["source"] = backup.IsTar ? "Hydra Import (Tar)" : "Hydra Import",
                            ["importedAt"] = DateTime.UtcNow.ToString("o"),
                            ["originalPath"] = backup.IsTar ? backup.TarPath : backup.FolderPath
                        };
                        File.WriteAllText(metaPath, meta.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                }
            });

            var resultMsg = $"Imported {copiedFiles} files for {selected.Count} games.";
            if (errors.Count > 0)
            {
                resultMsg += $"\n\nWarnings ({errors.Count}):\n" + string.Join("\n", errors.Take(10));
            }
            StatusText.Text = resultMsg.Split('\n')[0];
            MessageBox.Show(resultMsg, copiedFiles > 0 ? "Import Complete" : "Import Failed", MessageBoxButton.OK,
                copiedFiles > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            // Log to cr_ui.log
            if (copiedFiles > 0)
            {
                foreach (var b in selected)
                {
                    bool wasExisting = existingApps.Any(e => e.AppId == b.AppId);
                    UILogger.LogImport(
                        b.IsTar ? "HydraTAR" : "Hydra",
                        b.AppId, b.Name.Replace(" (Tar)", ""),
                        copiedFiles / Math.Max(selected.Count, 1),
                        b.Size,
                        wasExisting ? $"mode={importMode}" : null);
                }
            }

            // Write history entries
            if (copiedFiles > 0 && !string.IsNullOrEmpty(steamPath))
            {
                var historyEntries = selected.Select(b =>
                {
                    bool wasExisting = existingApps.Any(e => e.AppId == b.AppId);
                    return new SaveHistoryEntry
                    {
                        AppId = b.AppId,
                        GameName = b.Name.Replace(" (Tar)", ""),
                        EventType = wasExisting
                            ? (importMode == ImportMode.Replace ? SaveEventType.Replaced : SaveEventType.Merged)
                            : (b.IsTar ? SaveEventType.HydraImportTar : SaveEventType.HydraImport),
                        FileCount = copiedFiles / Math.Max(selected.Count, 1),
                        Bytes = ParseStoredBytes(b.Size),
                        SourcePath = b.IsTar ? b.TarPath : b.FolderPath,
                        Detail = wasExisting
                            ? $"Modo: {(importMode == ImportMode.Replace ? "substituição" : "fusão")}"
                            : null
                    };
                });
                SaveHistoryService.AppendRange(steamPath, historyEntries);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Import failed:\n{ex}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportBtn.IsEnabled = true;
        }
    }
}
