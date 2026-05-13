using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ToolsCloud.Services;

namespace ToolsCloud.Pages;

public class AchievementItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _name;
    public string Name { get => _name; set { _name = value; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DisplayText)); } }

    private string _originalId;
    public string OriginalId { get => _originalId; set { _originalId = value; OnPropertyChanged(nameof(OriginalId)); OnPropertyChanged(nameof(DisplayText)); } }

    private string _iconUrl;
    public string IconUrl { get => _iconUrl; set { _iconUrl = value; OnPropertyChanged(nameof(IconUrl)); } }

    private string _description;
    public string Description { get => _description; set { _description = value; OnPropertyChanged(nameof(Description)); } }

    public string DisplayText => string.IsNullOrEmpty(Name) ? OriginalId : Name;

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

public class AchievementGame : System.ComponentModel.INotifyPropertyChanged
{
    public string AppId { get; set; } = "";
    
    private string _gameName = "Jogo Desconhecido";
    public string GameName { get => _gameName; set { _gameName = value; OnPropertyChanged(nameof(GameName)); } }

    private string _headerUrl;
    public string HeaderUrl { get => _headerUrl; set { _headerUrl = value; OnPropertyChanged(nameof(HeaderUrl)); } }

    private List<string> _filesFound = new();
    public List<string> FilesFound
    {
        get => _filesFound;
        set { _filesFound = value; OnPropertyChanged(nameof(FilesFound)); OnPropertyChanged(nameof(Summary)); }
    }

    private System.Collections.ObjectModel.ObservableCollection<AchievementItem> _unlockedAchievements = new();
    public System.Collections.ObjectModel.ObservableCollection<AchievementItem> UnlockedAchievements
    {
        get => _unlockedAchievements;
        set { _unlockedAchievements = value; OnPropertyChanged(nameof(UnlockedAchievements)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(UnlockedCount)); }
    }

    public int UnlockedCount => UnlockedAchievements.Count;

    public string Summary => UnlockedAchievements.Count > 0 
        ? $"{UnlockedAchievements.Count} conquistas desbloqueadas sincronizadas." 
        : $"{FilesFound.Count} arquivos de stats sincronizados.";

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

public partial class AchievementsPage : Page
{
    private List<AchievementGame> _allGames = new();

    public AchievementsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => {
            var key = ReadApiKey();
            if (string.IsNullOrEmpty(key))
                ApiKeyBanner.Visibility = Visibility.Visible;
            await LoadDataAsync();
        };
    }

    private static string ReadApiKey()
    {
        var keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_api_key.txt");
        return Services.SecureStorage.Load(keyPath);
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is MainWindow mw)
        {
            mw.RootNavigation.Navigate(typeof(SettingsPage));
        }
    }

    private void DismissBanner_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBanner.Visibility = Visibility.Collapsed;
    }

    private async Task TranslateAllAchievementsAsync(string key)
    {
        using var http = new System.Net.Http.HttpClient();
        foreach (var game in _allGames)
        {
            if (!uint.TryParse(game.AppId, out var appId)) continue;
            if (game.UnlockedAchievements.Count == 0) continue;

            try
            {
                var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={key}&appid={appId}&l=brazilian";
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("game", out var gameNode)) continue;
                if (!gameNode.TryGetProperty("availableGameStats", out var statsNode)) continue;
                if (!statsNode.TryGetProperty("achievements", out var achList)) continue;

                var nameMap = new Dictionary<string, (string DisplayName, string IconUrl, string Description)>(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                foreach (var ach in achList.EnumerateArray())
                {
                    if (ach.TryGetProperty("name", out var apiName))
                    {
                        var realName = ach.TryGetProperty("displayName", out var dispName) ? dispName.GetString() ?? "" : "";
                        var iconUrl = ach.TryGetProperty("icon", out var iconNode) ? iconNode.GetString() ?? "" : "";
                        var desc = ach.TryGetProperty("description", out var descNode) ? descNode.GetString() ?? "" : "";
                        var apiNameStr = apiName.GetString() ?? "";
                        nameMap[apiNameStr] = (realName, iconUrl, desc);
                        nameMap[index.ToString()] = (realName, iconUrl, desc);
                    }
                    index++;
                }

                foreach (var achItem in game.UnlockedAchievements)
                {
                    if (nameMap.TryGetValue(achItem.OriginalId, out var data))
                    {
                        if (!string.IsNullOrEmpty(data.DisplayName))
                            achItem.Name = data.DisplayName;
                        if (!string.IsNullOrEmpty(data.IconUrl))
                            achItem.IconUrl = data.IconUrl;
                        if (!string.IsNullOrEmpty(data.Description))
                            achItem.Description = data.Description;
                    }
                }
            }
            catch { }
        }
    }

    private void GameCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AchievementGame game)
        {
            ModalGameName.Text = game.GameName;
            ModalProgressText.Text = $"{game.UnlockedCount} conquistas desbloqueadas";
            ModalProgressBar.Value = 100; // Future improvement: track total achievements
            ModalAchievementsList.ItemsSource = game.UnlockedAchievements;
            ModalOverlay.Visibility = Visibility.Visible;
        }
    }

    private void CloseModal_Click(object sender, RoutedEventArgs e)
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        ModalAchievementsList.ItemsSource = null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private static List<string> ParseGoldbergBin(byte[] data)
    {
        var unlocked = new List<string>();
        var bitPairs = new List<(int stat, int bit)>();
        int offset = 0;
        
        string ReadString()
        {
            int start = offset;
            while (offset < data.Length && data[offset] != 0) offset++;
            var str = System.Text.Encoding.UTF8.GetString(data, start, offset - start);
            offset++;
            return str;
        }

        void ParseDict(string currentStatName)
        {
            while (offset < data.Length)
            {
                byte type = data[offset++];
                if (type == 8) break; // End of dict
                
                string key = ReadString();
                
                if (type == 0) // Dict
                {
                    string nextStat = currentStatName;
                    if (int.TryParse(key, out _)) nextStat = key;
                    
                    if (key == "AchievementTimes")
                    {
                        while (offset < data.Length)
                        {
                            byte innerType = data[offset++];
                            if (innerType == 8) break;
                            string innerKey = ReadString();
                            
                            if (innerType == 2) // Int32
                            {
                                offset += 4;
                                if (currentStatName != null && int.TryParse(currentStatName, out int statNum) && int.TryParse(innerKey, out int bitIdx))
                                {
                                    bitPairs.Add((statNum, bitIdx));
                                }
                                else
                                {
                                    unlocked.Add(innerKey);
                                }
                            }
                            else if (innerType == 1) // String
                            {
                                var val = ReadString();
                                unlocked.Add(innerKey);
                            }
                            else if (innerType == 0) // Dict
                            {
                                ParseDict(null);
                            }
                            else if (innerType == 3 || innerType == 4 || innerType == 6) offset += 4;
                            else if (innerType == 7 || innerType == 9) offset += 8;
                            else if (innerType == 5)
                            {
                                while (offset < data.Length - 1 && (data[offset] != 0 || data[offset+1] != 0)) offset += 2;
                                offset += 2;
                            }
                        }
                    }
                    else
                    {
                        ParseDict(nextStat);
                    }
                }
                else if (type == 1) // String
                {
                    var val = ReadString();
                    if (key.Contains("ACH") || val.Contains("ACH")) unlocked.Add(val);
                }
                else if (type == 2 || type == 3 || type == 4 || type == 6) offset += 4;
                else if (type == 7 || type == 9) offset += 8;
                else if (type == 5)
                {
                    while (offset < data.Length - 1 && (data[offset] != 0 || data[offset+1] != 0)) offset += 2;
                    offset += 2;
                }
            }
        }

        try
        {
            while (offset < data.Length)
            {
                ParseDict(null);
            }
        }
        catch { }
        
        if (bitPairs.Count > 0)
        {
            int baseStat = bitPairs.Min(p => p.stat);
            foreach (var p in bitPairs)
            {
                int flatIndex = (p.stat - baseStat) * 32 + p.bit;
                unlocked.Add(flatIndex.ToString());
            }
        }

        return unlocked.Distinct().ToList();
    }

    private async Task LoadDataAsync()
    {
        StatusText.Text = "Procurando conquistas...";
        var steamPath = SteamDetector.FindSteamPath();
        if (steamPath == null)
        {
            StatusText.Text = "Steam não encontrado.";
            return;
        }

        var storagePath = Path.Combine(steamPath, "cloud_redirect", "storage");
        if (!Directory.Exists(storagePath))
        {
            StatusText.Text = "Nenhum dado encontrado.";
            return;
        }

        var pathsToScan = new List<string> { storagePath };
        
        var goldbergPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Goldberg SteamEmu Saves");
        if (Directory.Exists(goldbergPath)) pathsToScan.Add(goldbergPath);

        var codexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Steam", "CODEX");
        if (Directory.Exists(codexPath)) pathsToScan.Add(codexPath);

        var runePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Steam", "RUNE");
        if (Directory.Exists(runePath)) pathsToScan.Add(runePath);

        var results = new List<AchievementGame>();
        var appIdsToFetch = new List<uint>();

        await Task.Run(() =>
        {
            foreach (var currentPath in pathsToScan)
            {
                if (!Directory.Exists(currentPath)) continue;

                var allFiles = Directory.GetFiles(currentPath, "*", SearchOption.AllDirectories);
                var achFiles = allFiles.Where(f => 
                    f.Contains("achievement", StringComparison.OrdinalIgnoreCase) || 
                    f.Contains("stat", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("UserGameStats", StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var f in achFiles)
                {
                    string appId = "";
                    var relPath = Path.GetRelativePath(currentPath, f);
                    var parts = relPath.Split(Path.DirectorySeparatorChar);

                    if (parts.Length >= 2)
                    {
                        appId = parts[0];
                        if (currentPath == storagePath && parts.Length >= 2)
                        {
                            appId = parts[1];
                        }

                        if (appId == "0" || f.Contains("UserGameStats", StringComparison.OrdinalIgnoreCase))
                        {
                            appId = Path.GetFileNameWithoutExtension(f);
                        }
                    }

                    if (string.IsNullOrEmpty(appId) || appId == "0" || !uint.TryParse(appId, out _)) continue;

                    var unlocked = new List<string>();
                    try
                    {
                        if (f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                        {
                            var content = File.ReadAllText(f);
                            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                if (line.Contains("=") && !line.StartsWith("["))
                                {
                                    var kv = line.Split('=');
                                    if (kv.Length == 2 && (kv[1].Trim() == "1" || kv[1].Trim().ToLower() == "true"))
                                    {
                                        unlocked.Add(kv[0].Trim());
                                    }
                                }
                            }
                        }
                        else if (f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.Contains("UserGameStats") || f.Contains("stats"))
                        {
                            var bytes = File.ReadAllBytes(f);
                            var parsed = ParseGoldbergBin(bytes);
                            
                            if (parsed.Count > 0)
                            {
                                unlocked.AddRange(parsed);
                            }
                            else
                            {
                                var content = System.Text.Encoding.ASCII.GetString(bytes);
                                var matches = System.Text.RegularExpressions.Regex.Matches(content, @"ACH_[A-Z0-9_]+|NEW_ACHIEVEMENT_[0-9_]+|[A-Za-z0-9_]{5,30}");
                                foreach (System.Text.RegularExpressions.Match match in matches)
                                {
                                    if (match.Value.Contains("ACH") || match.Value.Contains("ACHIEVEMENT"))
                                        unlocked.Add(match.Value);
                                }

                                var binMatches = System.Text.RegularExpressions.Regex.Matches(content, @"\x02([A-Za-z0-9_]+)\x00");
                                foreach (System.Text.RegularExpressions.Match match in binMatches)
                                {
                                    if (match.Groups.Count > 1)
                                    {
                                        unlocked.Add(match.Groups[1].Value);
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    unlocked = unlocked.Where(u => {
                        if (string.IsNullOrWhiteSpace(u)) return false;
                        var val = u.Trim();
                        var ignoreKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "data", "state", "PendingChanges", "AchievementTimes", "cache", "crc", "Stats", "pendingbits" };
                        if (ignoreKeys.Contains(val)) return false;
                        if ((val.Length == 16 || val.Length == 32) && val.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                            return false;
                        return true;
                    }).ToList();

                    var existing = results.FirstOrDefault(g => g.AppId == appId);
                    if (existing != null)
                    {
                        existing.FilesFound.Add(f);
                        existing.FilesFound = existing.FilesFound.Distinct().ToList();
                        foreach (var u in unlocked)
                        {
                            if (!existing.UnlockedAchievements.Any(a => a.OriginalId == u))
                            {
                                existing.UnlockedAchievements.Add(new AchievementItem { OriginalId = u });
                            }
                        }
                    }
                    else
                    {
                        var newGame = new AchievementGame
                        {
                            AppId = appId,
                            FilesFound = new List<string> { f }
                        };
                        foreach (var u in unlocked.Distinct())
                        {
                            newGame.UnlockedAchievements.Add(new AchievementItem { OriginalId = u });
                        }
                        results.Add(newGame);
                        
                        if (uint.TryParse(appId, out var id) && !appIdsToFetch.Contains(id))
                            appIdsToFetch.Add(id);
                    }
                }
            }
        });

        if (appIdsToFetch.Count > 0)
        {
            var storeInfo = await SteamStoreClient.Shared.GetAppInfoAsync(appIdsToFetch);
            foreach (var g in results)
            {
                if (uint.TryParse(g.AppId, out var id) && storeInfo.TryGetValue(id, out var info))
                {
                    g.GameName = info.Name;
                    g.HeaderUrl = info.HeaderUrl;
                }
            }
        }

        _allGames = results.OrderBy(g => g.GameName).ToList();
        ApplyFilter();
        
        var key = ReadApiKey();
        if (!string.IsNullOrEmpty(key))
        {
            StatusText.Text = "Sincronizado. Baixando traduções e imagens em segundo plano...";
            _ = Task.Run(async () => 
            {
                await TranslateAllAchievementsAsync(key);
                Dispatcher.Invoke(() => StatusText.Text = $"{_allGames.Count} jogos com conquistas sincronizadas.");
            });
        }
        else
        {
            StatusText.Text = $"{_allGames.Count} jogos com conquistas sincronizadas.";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            GamesList.ItemsSource = null;
            GamesList.ItemsSource = _allGames;
            return;
        }

        var filtered = _allGames.Where(g => 
            g.GameName.Contains(query, StringComparison.OrdinalIgnoreCase) || 
            g.AppId.Contains(query)).ToList();

        GamesList.ItemsSource = null;
        GamesList.ItemsSource = filtered;
    }
}
