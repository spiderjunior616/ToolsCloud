using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;

namespace ToolsCloud.Services;

public class LudusaviGame
{
    public string Name { get; set; } = "";
    public List<string> FilePaths { get; set; } = new();
    public List<string> InstallDirs { get; set; } = new();
    public uint SteamId { get; set; }
    public string DisplayName => Name;
    public string Status { get; set; } = "";
    public List<string> FoundFiles { get; set; } = new();
    public bool IsSelected { get; set; }
    public string? HeaderUrl { get; set; }
}

public class LudusaviScanOptions
{
    public bool UseGlobalScan { get; set; }
    public string CustomBaseDir { get; set; } = "";
}

public class LudusaviScanner
{
    public const string DefaultManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
    private readonly HttpClient _http = new();

    public async Task<List<LudusaviGame>> FetchAndParseManifestsAsync(IEnumerable<string> pathsOrUrls)
    {
        var gamesDict = new Dictionary<string, LudusaviGame>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in pathsOrUrls)
        {
            try
            {
                string yaml;
                if (source.StartsWith("http"))
                {
                    yaml = await _http.GetStringAsync(source);
                }
                else
                {
                    yaml = await File.ReadAllTextAsync(source);
                }

                var tempGames = new List<LudusaviGame>();
                ParseYamlManifest(yaml, tempGames);
                
                foreach (var g in tempGames)
                {
                    if (!gamesDict.ContainsKey(g.Name))
                    {
                        gamesDict[g.Name] = g;
                    }
                    else
                    {
                        var existing = gamesDict[g.Name];
                        foreach (var path in g.FilePaths)
                        {
                            if (!existing.FilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                                existing.FilePaths.Add(path);
                        }
                        if (g.SteamId > 0 && existing.SteamId == 0)
                            existing.SteamId = g.SteamId;

                        foreach (var idir in g.InstallDirs)
                        {
                            if (!existing.InstallDirs.Contains(idir, StringComparer.OrdinalIgnoreCase))
                                existing.InstallDirs.Add(idir);
                        }
                    }
                }
            }
            catch { }
        }

        return gamesDict.Values.ToList();
    }

    private void ParseYamlManifest(string yaml, List<LudusaviGame> games)
    {
        var lines = yaml.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        LudusaviGame? currentGame = null;
        string state = "root";

        foreach (var line in lines)
        {
            if (line.StartsWith("#")) continue;

            if (!line.StartsWith(" ") && line.EndsWith(":"))
            {
                if (currentGame != null && currentGame.FilePaths.Count > 0)
                {
                    games.Add(currentGame);
                }

                var gameName = line.TrimEnd(':').Trim();
                if ((gameName.StartsWith("\"") && gameName.EndsWith("\"")) || (gameName.StartsWith("'") && gameName.EndsWith("'")))
                {
                    gameName = gameName.Substring(1, gameName.Length - 2);
                }

                currentGame = new LudusaviGame
                {
                    Name = gameName
                };
                state = "game";
                continue;
            }

            if (currentGame == null) continue;

            if (line.StartsWith("  files:"))
            {
                state = "files";
                continue;
            }
            if (line.StartsWith("  steam:"))
            {
                state = "steam";
                continue;
            }
            if (line.StartsWith("  registry:") || line.StartsWith("  launch:"))
            {
                state = "other";
                continue;
            }
            if (line.StartsWith("  installDir:"))
            {
                state = "installDir";
                continue;
            }

            if (state == "installDir" && line.StartsWith("    ") && !line.StartsWith("      "))
            {
                var match = Regex.Match(line, @"^    ""?([^""]+)""?:");
                if (match.Success)
                {
                    currentGame.InstallDirs.Add(match.Groups[1].Value);
                }
            }

            if (state == "steam" && line.StartsWith("    id:"))
            {
                var idStr = line.Replace("    id:", "").Trim();
                if (uint.TryParse(idStr, out var id))
                {
                    currentGame.SteamId = id;
                }
            }

            if (state == "files" && line.StartsWith("    ") && !line.StartsWith("      "))
            {
                var match = Regex.Match(line, @"^    ""?([^""]+)""?:");
                if (match.Success)
                {
                    currentGame.FilePaths.Add(match.Groups[1].Value);
                }
            }
        }

        if (currentGame != null && currentGame.FilePaths.Count > 0)
        {
            games.Add(currentGame);
        }
    }

    private List<string> GetSteamLibraryFolders()
    {
        var paths = new List<string>();
        var steamPath = SteamDetector.FindSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return paths;
        
        paths.Add(steamPath);
        var vdfPath = Path.Combine(steamPath, "config", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                foreach (var line in File.ReadLines(vdfPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split('"');
                        if (parts.Length >= 4)
                        {
                            var p = parts[3].Replace("\\\\", "\\");
                            if (Directory.Exists(p) && !paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                                paths.Add(p);
                        }
                    }
                }
            }
            catch { }
        }
        return paths;
    }

    public Task<List<LudusaviGame>> ScanLocalGamesAsync(List<LudusaviGame> manifestGames, LudusaviScanOptions options)
    {
        return Task.Run(() =>
        {
            var foundGames = new List<LudusaviGame>();
            var steamLibraries = GetSteamLibraryFolders();
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

            string steamRoot = @"C:\Program Files (x86)\Steam";
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        var path = key.GetValue("SteamPath")?.ToString();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            steamRoot = path.Replace('/', '\\');
                        }
                    }
                }
            }
            catch { }

            var osVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "<winAppData>", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) },
                { "<winLocalAppData>", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) },
                { "<winDocuments>", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                { "<winPublic>", Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) },
                { "<home>", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
                { "<osUserName>", Environment.UserName },
                { "<winDir>", Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
                { "<root>", steamRoot }
            };

            foreach (var game in manifestGames)
            {
                var foundFiles = new List<string>();
                var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Determine base paths based on scope
                var possibleBases = new List<string>();
                if (options.UseGlobalScan)
                {
                    foreach (var d in drives)
                    {
                        possibleBases.Add(Path.Combine(d, game.Name));
                        possibleBases.Add(Path.Combine(d, "Games", game.Name));
                        possibleBases.Add(Path.Combine(d, "Program Files", game.Name));
                        possibleBases.Add(Path.Combine(d, "Program Files (x86)", game.Name));
                        possibleBases.Add(Path.Combine(d, "Program Files (x86)", "Steam", "steamapps", "common", game.Name));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(options.CustomBaseDir))
                {
                    // Custom directory only! 
                    possibleBases.Add(options.CustomBaseDir);
                    possibleBases.Add(Path.Combine(options.CustomBaseDir, game.Name));
                }
                else
                {
                    // Standard scan: use Steam libraries for <base>
                    foreach (var lib in steamLibraries)
                    {
                        if (game.InstallDirs.Count > 0)
                        {
                            foreach (var idir in game.InstallDirs)
                            {
                                possibleBases.Add(Path.Combine(lib, "steamapps", "common", idir));
                            }
                        }
                        else
                        {
                            possibleBases.Add(Path.Combine(lib, "steamapps", "common", game.Name));
                        }
                    }
                }

                if (possibleBases.Count == 0) possibleBases.Add("C:\\DummyBase\\");

                var gamePathsToScan = new List<string>(game.FilePaths);
                if (game.SteamId > 0)
                {
                    gamePathsToScan.Add($"<root>/userdata/<storeUserId>/{game.SteamId}/remote");
                }

                foreach (var pathRaw in gamePathsToScan)
                {
                    // If custom directory is checked, user wants to restrict scan to custom directory ONLY.
                    // To do this, we map ALL variables (OS vars too) to the custom base dir.
                    if (!string.IsNullOrWhiteSpace(options.CustomBaseDir) && !options.UseGlobalScan)
                    {
                        foreach (var baseDir in possibleBases)
                        {
                            var resolvedPath = pathRaw;
                            var allVars = new[] { "<winAppData>", "<winLocalAppData>", "<winDocuments>", "<winPublic>", "<home>", "<winDir>", "<base>", "<steam>", "<epic>", "<gog>", "<uplay>", "<origin>" };
                            foreach (var cv in allVars)
                            {
                                resolvedPath = resolvedPath.Replace(cv, baseDir, StringComparison.OrdinalIgnoreCase);
                            }
                            resolvedPath = resolvedPath.Replace("<osUserName>", "CustomUser", StringComparison.OrdinalIgnoreCase);
                            resolvedPath = resolvedPath.Replace('/', '\\');
                            expandedPaths.Add(resolvedPath);
                        }
                    }
                    else
                    {
                        // Global or Standard Scan
                        var resolvedOsPath = pathRaw;

                        // Ludusavi natively handles Steam Emulators by redirecting <root>/userdata to emulator folders
                        if (pathRaw.StartsWith("<root>/userdata", StringComparison.OrdinalIgnoreCase) || pathRaw.StartsWith("<root>\\userdata", StringComparison.OrdinalIgnoreCase))
                        {
                            var gseSaves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSE Saves");
                            var emuPath1 = pathRaw.Replace("<root>/userdata", gseSaves, StringComparison.OrdinalIgnoreCase).Replace("<root>\\userdata", gseSaves, StringComparison.OrdinalIgnoreCase);
                            expandedPaths.Add(emuPath1.Replace('/', '\\'));
                            
                            var goldbergSaves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Goldberg SteamEmu Saves");
                            var emuPath2 = pathRaw.Replace("<root>/userdata", goldbergSaves, StringComparison.OrdinalIgnoreCase).Replace("<root>\\userdata", goldbergSaves, StringComparison.OrdinalIgnoreCase);
                            expandedPaths.Add(emuPath2.Replace('/', '\\'));
                        }

                        foreach (var kv in osVars)
                        {
                            resolvedOsPath = resolvedOsPath.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
                        }

                        if (resolvedOsPath.Contains("<base>") || resolvedOsPath.Contains("<steam>") || resolvedOsPath.Contains("<epic>") || resolvedOsPath.Contains("<gog>") || resolvedOsPath.Contains("<uplay>") || resolvedOsPath.Contains("<origin>"))
                        {
                            foreach (var baseDir in possibleBases)
                            {
                                var p = resolvedOsPath
                                    .Replace("<base>", baseDir, StringComparison.OrdinalIgnoreCase)
                                    .Replace("<steam>", baseDir, StringComparison.OrdinalIgnoreCase)
                                    .Replace("<epic>", baseDir, StringComparison.OrdinalIgnoreCase)
                                    .Replace("<gog>", baseDir, StringComparison.OrdinalIgnoreCase)
                                    .Replace("<uplay>", baseDir, StringComparison.OrdinalIgnoreCase)
                                    .Replace("<origin>", baseDir, StringComparison.OrdinalIgnoreCase)
                                    .Replace('/', '\\');
                                expandedPaths.Add(p);
                            }
                        }
                        else
                        {
                            expandedPaths.Add(resolvedOsPath.Replace('/', '\\'));
                        }
                    }
                }

                foreach (var resolvedPath in expandedPaths)
                {
                    // Replace any remaining unknown <variables> with wildcards
                    var finalPath = Regex.Replace(resolvedPath, @"<[^>]+>", "*").Replace('/', '\\');

                    // If it has wildcards, use our recursive search
                    if (finalPath.Contains("*") || finalPath.Contains("?"))
                    {
                        var parts = finalPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            // On Windows, the root (e.g. C:) needs the slash
                            var root = parts[0] + "\\";
                            if (!root.Contains("*") && !root.Contains("?"))
                            {
                                FindFilesWithWildcards(root, parts, 1, foundFiles);
                            }
                        }
                    }
                    else
                    {
                        if (Directory.Exists(finalPath))
                        {
                            try
                            {
                                foundFiles.AddRange(Directory.GetFiles(finalPath, "*", SearchOption.AllDirectories));
                            }
                            catch { }
                        }
                        else if (File.Exists(finalPath))
                        {
                            foundFiles.Add(finalPath);
                        }
                    }
                }

                foundFiles = foundFiles.Distinct().ToList();

                if (foundFiles.Count > 0)
                {
                    var clone = new LudusaviGame
                    {
                        Name = game.Name,
                        SteamId = game.SteamId,
                        FilePaths = game.FilePaths,
                        FoundFiles = foundFiles,
                        Status = $"{foundFiles.Count} save files found."
                    };
                    foundGames.Add(clone);
                }
            }

            return foundGames.OrderBy(g => g.Name).ToList();
        });
    }

    private void FindFilesWithWildcards(string basePath, string[] parts, int partIndex, List<string> foundFiles)
    {
        if (partIndex >= parts.Length)
        {
            if (File.Exists(basePath))
                foundFiles.Add(basePath);
            else if (Directory.Exists(basePath))
            {
                try { foundFiles.AddRange(Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)); } catch { }
            }
            return;
        }

        string part = parts[partIndex];

        if (part.Contains("*") || part.Contains("?"))
        {
            if (!Directory.Exists(basePath)) return;
            try
            {
                var dirs = Directory.GetDirectories(basePath, part);
                foreach (var d in dirs)
                    FindFilesWithWildcards(d, parts, partIndex + 1, foundFiles);

                if (partIndex == parts.Length - 1)
                {
                    var files = Directory.GetFiles(basePath, part);
                    foreach (var f in files)
                        foundFiles.Add(f);
                }
            }
            catch { }
        }
        else
        {
            FindFilesWithWildcards(Path.Combine(basePath, part), parts, partIndex + 1, foundFiles);
        }
    }

    public async Task<List<LudusaviGame>> ScanWithExeAsync(List<LudusaviGame> manifestGames)
    {
        var exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hydralauncher", "ludusavi", "ludusavi.exe");
        if (!File.Exists(exePath))
            throw new Exception("ludusavi.exe not found at " + exePath);

        var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = exePath;
        process.StartInfo.Arguments = "backup --preview --api";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;

        var tcs = new TaskCompletionSource<string>();
        process.EnableRaisingEvents = true;
        var outputStr = new System.Text.StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null) outputStr.AppendLine(e.Data);
        };

        process.Exited += (s, e) => tcs.SetResult(outputStr.ToString());
        
        process.Start();
        process.BeginOutputReadLine();

        var jsonOutput = await tcs.Task;

        var foundGames = new List<LudusaviGame>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonOutput);
            var gamesNode = doc.RootElement.GetProperty("games");
            
            foreach (var prop in gamesNode.EnumerateObject())
            {
                var gameName = prop.Name;
                var filesNode = prop.Value.GetProperty("files");
                var files = new List<string>();
                
                foreach (var fileProp in filesNode.EnumerateObject())
                {
                    files.Add(fileProp.Name);
                }

                if (files.Count > 0)
                {
                    // Find matching game in manifest to get SteamId
                    var manifestGame = manifestGames.FirstOrDefault(g => g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase));
                    
                    foundGames.Add(new LudusaviGame
                    {
                        Name = gameName,
                        FoundFiles = files,
                        SteamId = manifestGame?.SteamId ?? 0,
                        InstallDirs = manifestGame?.InstallDirs ?? new List<string>(),
                        Status = $"Found {files.Count} files via ludusavi.exe"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to parse ludusavi.exe output: " + ex.Message);
        }

        return foundGames;
    }
}
