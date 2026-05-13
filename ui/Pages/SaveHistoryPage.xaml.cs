using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToolsCloud.Services;

namespace ToolsCloud.Pages;

public partial class SaveHistoryPage : Page
{
    private List<SaveHistoryEntry> _allEntries = new();
    private string _steamPath = "";

    public SaveHistoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _steamPath = SteamDetector.FindSteamPath() ?? "";
        UILogger.LogNav("SaveHistoryPage");
        
        if (!string.IsNullOrEmpty(_steamPath))
        {
            Task.Run(() => 
            {
                HistoryScanner.ScanAndLogDifferences(_steamPath);
                Dispatcher.Invoke(Reload);
            });
        }
        else
        {
            Reload();
        }
    }

    private void Reload()
    {
        if (string.IsNullOrEmpty(_steamPath)) return;
        _allEntries = SaveHistoryService.Load(_steamPath);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (HistoryList == null || EmptyText == null) return;

        var query = SearchBox?.Text?.Trim() ?? "";
        var typeIdx = FilterType?.SelectedIndex ?? 0;

        var filtered = _allEntries.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
            filtered = filtered.Where(e =>
                e.GameName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.AppId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.EventLabel.Contains(query, StringComparison.OrdinalIgnoreCase));

        filtered = typeIdx switch
        {
            1 => filtered.Where(e => e.EventType == SaveEventType.HydraImport || e.EventType == SaveEventType.HydraImportTar),
            2 => filtered.Where(e => e.EventType == SaveEventType.LudusaviImport),
            3 => filtered.Where(e => e.EventType == SaveEventType.Replaced || e.EventType == SaveEventType.SaveModified || e.EventType == SaveEventType.SaveCreated),
            4 => filtered.Where(e => e.EventType == SaveEventType.Merged),
            5 => filtered.Where(e => e.EventType == SaveEventType.ManualRestore),
            6 => filtered.Where(e => e.EventType == SaveEventType.FilesDeleted),
            7 => filtered.Where(e => e.EventType == SaveEventType.AchievementUnlocked),
            _ => filtered
        };

        var list = filtered.Select(e => new HistoryViewModel(e)).ToList();

        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = list;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void FilterType_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Tem certeza que deseja apagar todo o histórico?\nEssa ação não pode ser desfeita.",
            "Limpar histórico",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        if (!string.IsNullOrEmpty(_steamPath))
        {
            var path = System.IO.Path.Combine(_steamPath, "cloud_redirect", "save_history.json");
            try { System.IO.File.Delete(path); } catch { }
            var snapPath = System.IO.Path.Combine(_steamPath, "cloud_redirect", "storage_snapshot.json");
            try { System.IO.File.Delete(snapPath); } catch { }
        }

        _allEntries.Clear();
        ApplyFilter();
    }
}

public class HistoryViewModel
{
    private readonly SaveHistoryEntry _entry;

    public HistoryViewModel(SaveHistoryEntry entry) => _entry = entry;

    public string AppId        => _entry.AppId;
    public string GameName     => string.IsNullOrEmpty(_entry.GameName) ? $"App {_entry.AppId}" : _entry.GameName;
    public string EventLabel   => _entry.EventLabel;
    public string FormattedTime => _entry.FormattedTime;
    public int    FileCount    => _entry.FileCount;
    public string FormattedSize => _entry.FormattedSize;
    public string Detail       => _entry.Detail ?? "";

    public Brush BadgeColor => _entry.EventType switch
    {
        SaveEventType.HydraImport    => new SolidColorBrush(Color.FromRgb(126, 87, 194)),
        SaveEventType.HydraImportTar => new SolidColorBrush(Color.FromRgb(94, 53, 177)),
        SaveEventType.LudusaviImport => new SolidColorBrush(Color.FromRgb(21, 101, 192)),
        SaveEventType.SteamCloudSync => new SolidColorBrush(Color.FromRgb(2, 119, 189)),
        SaveEventType.ManualRestore  => new SolidColorBrush(Color.FromRgb(46, 125, 50)),
        SaveEventType.FilesDeleted   => new SolidColorBrush(Color.FromRgb(183, 28, 28)),
        SaveEventType.Merged         => new SolidColorBrush(Color.FromRgb(230, 81, 0)),
        SaveEventType.Replaced       => new SolidColorBrush(Color.FromRgb(245, 127, 23)),
        SaveEventType.SaveCreated    => new SolidColorBrush(Color.FromRgb(46, 125, 50)),
        SaveEventType.SaveModified   => new SolidColorBrush(Color.FromRgb(2, 119, 189)),
        SaveEventType.AchievementUnlocked => new SolidColorBrush(Color.FromRgb(255, 193, 7)),
        _                            => new SolidColorBrush(Color.FromRgb(97, 97, 97)),
    };
}
