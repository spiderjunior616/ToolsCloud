using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ToolsCloud.Services;

namespace ToolsCloud.Pages;

public class ManifestItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSelected { get; set; } = true;
}

public partial class LudusaviPage : Page
{
    private readonly LudusaviScanner _scanner = new();
    private List<LudusaviGame> _manifestGames = new();
    private List<LudusaviGame> _foundGames = new();
    private List<ManifestItem> _manifests = new();

    public LudusaviPage()
    {
        InitializeComponent();
        
        string localLudusaviManifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ludusavi", "manifest.yaml");
        bool localExists = File.Exists(localLudusaviManifest);

        if (localExists)
        {
            _manifests.Add(new ManifestItem 
            { 
                Name = "Local Ludusavi Cache", 
                Path = localLudusaviManifest, 
                IsSelected = true 
            });
        }

        _manifests.Add(new ManifestItem 
        { 
            Name = "Official Ludusavi Manifest (GitHub)", 
            Path = LudusaviScanner.DefaultManifestUrl, 
            IsSelected = !localExists
        });
        ManifestList.ItemsSource = _manifests;
    }

    private void AddManifestButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "YAML Files (*.yaml;*.yml)|*.yaml;*.yml|All Files (*.*)|*.*",
            Title = "Select Local Manifest File"
        };
        if (dlg.ShowDialog() == true)
        {
            _manifests.Add(new ManifestItem 
            { 
                Name = Path.GetFileName(dlg.FileName), 
                Path = dlg.FileName, 
                IsSelected = true 
            });
            ManifestList.Items.Refresh();
        }
    }

    private void BrowseCustomPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Custom Base Directory for Scanning"
        };
        if (dialog.ShowDialog() == true)
        {
            CustomPathBox.Text = dialog.FolderName;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanStatus.Text = "Downloading manifest...";

        try
        {
            var selectedManifests = _manifests.Where(m => m.IsSelected).Select(m => m.Path).ToList();
            if (selectedManifests.Count == 0)
            {
                MessageBox.Show("Please select at least one manifest.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _manifestGames = await _scanner.FetchAndParseManifestsAsync(selectedManifests);

            ScanStatus.Text = "Scanning local directories...";

            if (RadioEngineExe.IsChecked == true)
            {
                _foundGames = await _scanner.ScanWithExeAsync(_manifestGames);
            }
            else
            {
                var options = new LudusaviScanOptions
                {
                    UseGlobalScan = RadioGlobal.IsChecked == true,
                    CustomBaseDir = RadioCustom.IsChecked == true ? CustomPathBox.Text : ""
                };

                _foundGames = await _scanner.ScanLocalGamesAsync(_manifestGames, options);
            }

            var appIds = _foundGames.Where(g => g.SteamId > 0).Select(g => g.SteamId).Distinct().ToList();
            if (appIds.Count > 0)
            {
                var storeInfo = await SteamStoreClient.Shared.GetAppInfoAsync(appIds);
                foreach (var g in _foundGames)
                {
                    if (g.SteamId > 0 && storeInfo.TryGetValue(g.SteamId, out var info))
                        g.HeaderUrl = info.HeaderUrl;
                }
            }

            ApplyFilter();
            ScanStatus.Text = $"Found {_foundGames.Count} games locally.";
            
            SendToCloudButton.IsEnabled = _foundGames.Count > 0;
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
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
            GamesList.ItemsSource = null;
            GamesList.ItemsSource = _foundGames;
            return;
        }

        var filtered = _foundGames.Where(g => 
            g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || 
            g.SteamId.ToString().Contains(query)).ToList();
            
        GamesList.ItemsSource = null;
        GamesList.ItemsSource = filtered;
    }

    private async void SendToCloudButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _foundGames.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Please select at least one game to send to the cloud.", "No selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SendToCloudButton.IsEnabled = false;

        try
        {
            var steamPath = SteamDetector.FindSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                MessageBox.Show("Steam path not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Find an active account ID
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

            int copiedCount = 0;

            await Task.Run(() =>
            {
                foreach (var game in selected)
                {
                    // Copy to CloudRedirect local storage so the DLL will pick it up and sync it
                    // The DLL intercepts Steam reading/writing to here. 
                    // By placing files in CloudRedirect storage, we force them to be available to Steam 
                    // and then synced to the Cloud provider.
                    string safeAppId = game.SteamId > 0 ? game.SteamId.ToString() : $"Ludu_{Math.Abs(game.Name.GetHashCode())}";
                    var crStorageDir = Path.Combine(steamPath, "cloud_redirect", "storage", activeAccountId, safeAppId);
                    
                    // Backup current active save as a variant to ensure no data is lost
                    SaveVariantService.SaveCurrentAsVariant(steamPath, activeAccountId, safeAppId, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_PreLudusaviImport");

                    Directory.CreateDirectory(crStorageDir);

                    foreach (var file in game.FoundFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var destFile = Path.Combine(crStorageDir, fileName);
                        try
                        {
                            File.Copy(file, destFile, true);
                            copiedCount++;
                        }
                        catch { }
                    }

                    // Write import metadata so the Apps tab can show origin info
                    try
                    {
                        var metaPath = Path.Combine(crStorageDir, "_import_meta.json");
                        // Only write if not already present (don't overwrite a previous import's date)
                        if (!File.Exists(metaPath))
                        {
                            var meta = new System.Text.Json.Nodes.JsonObject
                            {
                                ["source"] = "Ludusavi Import",
                                ["importedAt"] = DateTime.UtcNow.ToString("o"),
                                ["gameName"] = game.Name,
                                ["originalPaths"] = System.Text.Json.Nodes.JsonValue.Create(string.Join("; ", game.FoundFiles))
                            };
                            File.WriteAllText(metaPath,
                                meta.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        }
                    }
                    catch { }
                }
            });

            MessageBox.Show($"Successfully queued {copiedCount} save files for {selected.Count} games to be sent to the cloud on next launch.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

            // Log to cr_ui.log
            if (copiedCount > 0)
            {
                foreach (var g in selected)
                    UILogger.LogImport("Ludusavi", g.SteamId > 0 ? g.SteamId.ToString() : $"Ludu_{Math.Abs(g.Name.GetHashCode())}",
                        g.Name, g.FoundFiles.Count, $"{g.FoundFiles.Count} file(s)");
            }

            // Record history
            if (copiedCount > 0 && !string.IsNullOrEmpty(steamPath))
            {
                var entries = selected.Select(g => new SaveHistoryEntry
                {
                    AppId = g.SteamId > 0 ? g.SteamId.ToString() : $"Ludu_{Math.Abs(g.Name.GetHashCode())}",
                    GameName = g.Name,
                    EventType = SaveEventType.LudusaviImport,
                    FileCount = g.FoundFiles.Count,
                    Detail = $"{g.FoundFiles.Count} arquivo(s) copiado(s)"
                });
                SaveHistoryService.AppendRange(steamPath, entries);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error sending to cloud: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SendToCloudButton.IsEnabled = true;
        }
    }
}
