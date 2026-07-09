using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Win32;

namespace SteamLibraryLauncher
{
    public sealed partial class SteamLibraryLauncherCommandsProvider : CommandProvider
    {
        private readonly SteamListPage _rootPage = new();

        public SteamLibraryLauncherCommandsProvider()
        {
            DisplayName = "Steam Library Launcher";
        }

        public override ICommandItem[] TopLevelCommands()
        {
            return new ICommandItem[] {
                new CommandItem(_rootPage)
                {
                    Title = "Open Steam Library",
                    Subtitle = "Launches your local Steam shortcut collection view"
                }
            };
        }
    }

    public sealed partial class SteamListPage : ListPage
    {
        private readonly List<SteamGame> _installedGames = new();

        public SteamListPage()
        {
            Title = "Steam Library";
            PlaceholderText = "Search Steam games...";
            Icon = new IconInfo("\uE9A6");
            IndexGlobalSteamLibraries();
        }

        private void IndexGlobalSteamLibraries()
        {
            _installedGames.Clear();
            var uniqueAppIds = new HashSet<string>();

            string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
            {
                steamPath = @"C:\Program Files (x86)\Steam";
            }

            var libraryPaths = new List<string> { steamPath };
            string configFilePath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");

            if (File.Exists(configFilePath))
            {
                try
                {
                    var lines = File.ReadAllLines(configFilePath);
                    foreach (var line in lines)
                    {
                        if (line.Contains("\"path\"") && !line.Contains("path_to_ext_tf"))
                        {
                            var parts = line.Split('"');
                            if (parts.Length >= 4)
                            {
                                string path = parts[3].Replace("\\\\", "\\");
                                if (Directory.Exists(path) && !libraryPaths.Contains(path))
                                {
                                    libraryPaths.Add(path);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 1. PROCESS NATIVE STEAM GAMES (.acf files)
            foreach (var library in libraryPaths)
            {
                string appsDir = Path.Combine(library, "steamapps");
                if (!Directory.Exists(appsDir)) continue;

                var acfFiles = Directory.GetFiles(appsDir, "appmanifest_*.acf");
                foreach (var file in acfFiles)
                {
                    try
                    {
                        string appId = "";
                        string name = "";
                        var lines = File.ReadAllLines(file);

                        foreach (var line in lines)
                        {
                            if (line.Contains("\"appid\""))
                            {
                                var parts = line.Split('"');
                                if (parts.Length >= 4) appId = parts[3];
                            }
                            if (line.Contains("\"name\""))
                            {
                                var parts = line.Split('"');
                                if (parts.Length >= 4) name = parts[3];
                            }
                        }

                        if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name) && appId != "228980")
                        {
                            if (uniqueAppIds.Add(appId))
                            {
                                _installedGames.Add(new SteamGame { AppId = appId, Name = name, IsNativeSteam = true });
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2. PROCESS NON-STEAM GAMES (shortcuts.vdf parsing)
            string userDataPath = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userDataPath))
            {
                var userDirs = Directory.GetDirectories(userDataPath);
                foreach (var userDir in userDirs)
                {
                    string shortcutsFile = Path.Combine(userDir, "config", "shortcuts.vdf");
                    if (!File.Exists(shortcutsFile)) continue;

                    try
                    {
                        byte[] bytes = File.ReadAllBytes(shortcutsFile);
                        ParseNonSteamShortcuts(bytes, uniqueAppIds);
                    }
                    catch { }
                }
            }

            _installedGames.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        }

        private void ParseNonSteamShortcuts(byte[] bytes, HashSet<string> uniqueAppIds)
        {
            int i = 0;
            while (i < bytes.Length - 10)
            {
                bool isAppNameTag = bytes[i] == 0x01 &&
                    ((bytes[i + 1] == 'A' && bytes[i + 2] == 'p' && bytes[i + 3] == 'p' && bytes[i + 4] == 'N' && bytes[i + 5] == 'a' && bytes[i + 6] == 'm' && bytes[i + 7] == 'e') ||
                     (bytes[i + 1] == 'a' && bytes[i + 2] == 'p' && bytes[i + 3] == 'p' && bytes[i + 4] == 'n' && bytes[i + 5] == 'a' && bytes[i + 6] == 'm' && bytes[i + 7] == 'e')) &&
                    bytes[i + 8] == 0x00;

                if (isAppNameTag)
                {
                    i += 9;
                    int nameStart = i;
                    while (i < bytes.Length && bytes[i] != 0x00) { i++; }
                    string appName = Encoding.UTF8.GetString(bytes, nameStart, i - nameStart);

                    if (string.IsNullOrWhiteSpace(appName) || appName.Equals("shortcuts", StringComparison.OrdinalIgnoreCase)) continue;

                    string exePath = "";
                    int searchAhead = i;

                    while (searchAhead < Math.Min(bytes.Length - 5, i + 500))
                    {
                        bool isExeTag = bytes[searchAhead] == 0x01 &&
                            ((bytes[searchAhead + 1] == 'e' && bytes[searchAhead + 2] == 'x' && bytes[searchAhead + 3] == 'e') ||
                             (bytes[searchAhead + 1] == 'E' && bytes[searchAhead + 2] == 'x' && bytes[searchAhead + 3] == 'e')) &&
                            bytes[searchAhead + 4] == 0x00;

                        if (isExeTag)
                        {
                            searchAhead += 5;
                            int exeStart = searchAhead;
                            while (searchAhead < bytes.Length && bytes[searchAhead] != 0x00) { searchAhead++; }
                            exePath = Encoding.UTF8.GetString(bytes, exeStart, searchAhead - exeStart).Replace("\"", "");
                            break;
                        }
                        searchAhead++;
                    }

                    if (!string.IsNullOrEmpty(exePath))
                    {
                        string uniqueId = exePath.ToLowerInvariant();
                        if (uniqueAppIds.Add(uniqueId))
                        {
                            _installedGames.Add(new SteamGame { AppId = exePath, Name = appName, IsNativeSteam = false });
                        }
                    }
                }
                i++;
            }
        }

        public override IListItem[] GetItems()
        {
            var currentFilter = SearchText ?? string.Empty;

            var filteredItems = _installedGames
                .Where(game => game.Name.Contains(currentFilter, StringComparison.OrdinalIgnoreCase))
                .Select(game => new ListItem(new LaunchGameCommand(game.AppId, game.IsNativeSteam))
                {
                    Title = game.Name,
                    Icon = new IconInfo("\uE71B")
                });

            return filteredItems.ToArray();
        }
    }

    public sealed partial class LaunchGameCommand : InvokableCommand
    {
        private readonly string _launchPayload;
        private readonly bool _isNativeSteam;

        public override string Name => "Launch Game";

        public LaunchGameCommand(string launchPayload, bool isNativeSteam)
        {
            _launchPayload = launchPayload;
            _isNativeSteam = isNativeSteam;
        }

        public override CommandResult Invoke()
        {
            try
            {
                var startInfo = new ProcessStartInfo { UseShellExecute = true };

                if (_isNativeSteam)
                {
                    startInfo.FileName = $"steam://run/{_launchPayload}";
                }
                else
                {
                    startInfo.FileName = _launchPayload;
                    if (File.Exists(_launchPayload))
                    {
                        startInfo.WorkingDirectory = Path.GetDirectoryName(_launchPayload);
                    }
                }

                Process.Start(startInfo);
                return CommandResult.Dismiss();
            }
            catch (Exception)
            {
                return CommandResult.KeepOpen();
            }
        }
    }

    public struct SteamGame
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public bool IsNativeSteam { get; set; }
    }
}