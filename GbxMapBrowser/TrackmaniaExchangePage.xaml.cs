using GbxMapBrowser.Services.TrackmaniaExchange;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GbxMapBrowser
{
    public partial class TrackmaniaExchangePage : UserControl
    {
        private const string OfficialCampaignsFolderName = "Official Campaigns";

        private readonly string _defaultMapFolder;
        private readonly Action _goBack;
        private readonly TrackmaniaExchangeService _trackmaniaExchangeService = new();
        private readonly ObservableCollection<TrackmaniaExchangeResultItem> _items = [];

        public TrackmaniaExchangePage(string defaultMapFolder, Action goBack)
        {
            InitializeComponent();

            _defaultMapFolder = defaultMapFolder;
            _goBack = goBack;
            resultsDataGrid.ItemsSource = _items;
            SetStatus("Choose a Trackmania Exchange action.");
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _goBack();
        }

        private async void TrackOfTheDayButton_Click(object sender, RoutedEventArgs e)
        {
            NumberInputWindow window = new(
                "Track of the Day",
                "How many recent Track of the Day maps should be checked?",
                100,
                1
            )
            {
                Owner = Window.GetWindow(this)
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            await RunWithBusyStateAsync(async () =>
            {
                SetStatus("Loading Track of the Day maps from TMX...");
                IReadOnlyList<TmxMap> maps = await _trackmaniaExchangeService.GetRecentTrackOfTheDayMapsAsync(window.Value);

                SetStatus("Scanning local maps in the default folder and all subfolders...");
                LocalMapInventory localMaps = await Task.Run(() => ScanLocalMaps(_defaultMapFolder));

                int alreadyOwnedCount = 0;
                int downloadedCount = 0;
                int failedCount = 0;
                _items.Clear();

                foreach (TmxMap map in maps)
                {
                    bool alreadyOwned = HasLocalMap(localMaps, map);

                    var item = new TrackmaniaExchangeResultItem
                    {
                        Id = map.MapId,
                        Name = map.Name,
                        MapCount = 1,
                        DownloadedCount = alreadyOwned ? 1 : 0,
                        Status = alreadyOwned ? "Already downloaded" : "Missing"
                    };

                    _items.Add(item);

                    if (alreadyOwned)
                    {
                        alreadyOwnedCount++;
                        continue;
                    }

                    try
                    {
                        SetStatus($"Downloading {map.Name}...");
                        await _trackmaniaExchangeService.DownloadMapAsync(map, _defaultMapFolder);
                        AddTmxMapToInventory(localMaps, map);
                        item.DownloadedCount = 1;
                        item.Status = "Downloaded";
                        downloadedCount++;
                    }
                    catch
                    {
                        item.Status = "Failed";
                        failedCount++;
                    }

                    resultsDataGrid.Items.Refresh();
                }

                SetStatus(
                    $"Track of the Day check finished. Checked: {maps.Count}. " +
                    $"Already downloaded: {alreadyOwnedCount}. Downloaded: {downloadedCount}. Failed: {failedCount}."
                );
            });
        }

        private async void OfficialCampaignsButton_Click(object sender, RoutedEventArgs e)
        {
            await RunWithBusyStateAsync(LoadOfficialCampaignsAsync);
        }

        private async void DownloadSelectedCampaignButton_Click(object sender, RoutedEventArgs e)
        {
            if (resultsDataGrid.SelectedItem is not TrackmaniaExchangeResultItem item ||
                item.Campaign == null)
            {
                MessageBox.Show(
                    "Select a campaign first.",
                    "No campaign selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                return;
            }

            await RunWithBusyStateAsync(async () =>
            {
                await DownloadCampaignAsync(item);
            });
        }

        private async Task LoadOfficialCampaignsAsync()
        {
            SetStatus("Loading official campaigns from TMX...");
            IReadOnlyList<TmxMappack> campaigns = await _trackmaniaExchangeService.GetOfficialCampaignsAsync();

            _items.Clear();

            foreach (TmxMappack campaign in campaigns)
            {
                SetStatus($"Checking {campaign.Name}...");

                IReadOnlyList<TmxMap> maps = await _trackmaniaExchangeService.GetMappackMapsAsync(campaign.Id);
                string campaignFolder = GetOfficialCampaignFolder(campaign.Name);
                int downloadedCount = await Task.Run(() => GetDownloadedCampaignMapCount(campaignFolder, maps));

                _items.Add(new TrackmaniaExchangeResultItem
                {
                    Id = campaign.Id,
                    Name = campaign.Name,
                    Status = GetCampaignStatus(downloadedCount, maps.Count),
                    MapCount = maps.Count,
                    DownloadedCount = downloadedCount,
                    Campaign = campaign,
                    CampaignMaps = maps
                });
            }

            SetStatus($"Loaded {campaigns.Count} official campaigns.");
        }

        private async Task DownloadCampaignAsync(TrackmaniaExchangeResultItem item)
        {
            IReadOnlyList<TmxMap> maps = item.CampaignMaps.Count > 0
                ? item.CampaignMaps
                : await _trackmaniaExchangeService.GetMappackMapsAsync(item.Id);

            string campaignFolder = GetOfficialCampaignFolder(item.Name);
            HashSet<string> existingMapUids = await Task.Run(() => ScanMapUids(campaignFolder));
            int downloadedNowCount = 0;
            int failedCount = 0;

            foreach (TmxMap map in maps)
            {
                if (!string.IsNullOrWhiteSpace(map.MapUid) && existingMapUids.Contains(map.MapUid))
                {
                    continue;
                }

                try
                {
                    SetStatus($"Downloading {item.Name}: {map.Name}...");
                    await _trackmaniaExchangeService.DownloadMapAsync(map, campaignFolder);

                    if (!string.IsNullOrWhiteSpace(map.MapUid))
                    {
                        existingMapUids.Add(map.MapUid);
                    }

                    downloadedNowCount++;
                }
                catch
                {
                    failedCount++;
                }
            }

            item.CampaignMaps = maps;
            item.MapCount = maps.Count;
            item.DownloadedCount = await Task.Run(() => GetDownloadedCampaignMapCount(campaignFolder, maps));
            item.Status = GetCampaignStatus(item.DownloadedCount, item.MapCount);
            resultsDataGrid.Items.Refresh();

            SetStatus(
                $"Campaign download finished. {item.Name}: downloaded now {downloadedNowCount}, " +
                $"downloaded total {item.DownloadedCount}/{item.MapCount}, failed {failedCount}."
            );
        }

        private async Task RunWithBusyStateAsync(Func<Task> action)
        {
            SetButtonsEnabled(false);
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                SetStatus("TMX action failed: " + ex.Message);
                MessageBox.Show(
                    ex.Message,
                    "Trackmania Exchange",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            finally
            {
                Mouse.OverrideCursor = null;
                SetButtonsEnabled(true);
            }
        }

        private void SetButtonsEnabled(bool isEnabled)
        {
            trackOfTheDayButton.IsEnabled = isEnabled;
            officialCampaignsButton.IsEnabled = isEnabled;
            downloadSelectedCampaignButton.IsEnabled = isEnabled;
        }

        private string GetOfficialCampaignFolder(string campaignName)
        {
            string folderName = GetSafeFolderName(campaignName);

            return Path.Combine(
                _defaultMapFolder,
                OfficialCampaignsFolderName,
                folderName
            );
        }

        private static string GetCampaignStatus(int downloadedCount, int totalCount)
        {
            if (totalCount == 0)
            {
                return "No maps found";
            }

            if (downloadedCount == 0)
            {
                return "Not downloaded";
            }

            if (downloadedCount >= totalCount)
            {
                return "Downloaded";
            }

            return "Partial";
        }

        private static HashSet<string> ScanMapUids(string folder)
        {
            return ScanLocalMaps(folder).MapUids;
        }

        private static LocalMapInventory ScanLocalMaps(string folder)
        {
            LocalMapInventory inventory = new();

            if (!Directory.Exists(folder))
            {
                return inventory;
            }

            foreach (string mapFile in EnumerateMapFilesRecursively(folder))
            {
                try
                {
                    MapInfo mapInfo = new(mapFile, true);

                    if (mapInfo.IsWorking && !string.IsNullOrWhiteSpace(mapInfo.MapUid))
                    {
                        inventory.MapUids.Add(mapInfo.MapUid);
                    }

                    if (mapInfo.IsWorking)
                    {
                        AddNameToInventory(inventory, mapInfo.DisplayName);
                    }
                }
                catch
                {
                    // Skip unreadable maps while checking local ownership.
                }

                AddNameToInventory(inventory, Path.GetFileNameWithoutExtension(mapFile));
            }

            return inventory;
        }

        private static bool HasLocalMap(LocalMapInventory inventory, TmxMap map)
        {
            if (inventory == null || map == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(map.MapUid) &&
                inventory.MapUids.Contains(map.MapUid))
            {
                return true;
            }

            string normalizedName = NormalizeMapName(map.Name);

            return !string.IsNullOrWhiteSpace(normalizedName) &&
                inventory.MapNames.Contains(normalizedName);
        }

        private static void AddTmxMapToInventory(LocalMapInventory inventory, TmxMap map)
        {
            if (inventory == null || map == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(map.MapUid))
            {
                inventory.MapUids.Add(map.MapUid);
            }

            AddNameToInventory(inventory, map.Name);
        }

        private static void AddNameToInventory(LocalMapInventory inventory, string mapName)
        {
            string normalizedName = NormalizeMapName(mapName);

            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                inventory.MapNames.Add(normalizedName);
            }
        }

        private static string NormalizeMapName(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
            {
                return "";
            }

            string normalizedName = mapName.Trim();
            int copySuffixStart = normalizedName.LastIndexOf(" (", StringComparison.Ordinal);

            if (copySuffixStart > 0 &&
                normalizedName.EndsWith(")", StringComparison.Ordinal) &&
                int.TryParse(normalizedName[(copySuffixStart + 2)..^1], out _))
            {
                normalizedName = normalizedName[..copySuffixStart];
            }

            if (normalizedName.EndsWith(".Map", StringComparison.OrdinalIgnoreCase))
            {
                normalizedName = normalizedName[..^4];
            }
            else if (normalizedName.EndsWith(".Challenge", StringComparison.OrdinalIgnoreCase))
            {
                normalizedName = normalizedName[..^10];
            }

            return normalizedName.Trim().ToLowerInvariant();
        }

        private static IReadOnlyList<string> EnumerateMapFilesRecursively(string folder)
        {
            List<string> mapFiles = [];

            if (!Directory.Exists(folder))
            {
                return mapFiles;
            }

            Stack<string> foldersToScan = new();
            foldersToScan.Push(folder);

            while (foldersToScan.Count > 0)
            {
                string currentFolder = foldersToScan.Pop();

                try
                {
                    foreach (string mapFile in Directory.EnumerateFiles(currentFolder, "*.Gbx", SearchOption.TopDirectoryOnly))
                    {
                        if (mapFile.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase) ||
                            mapFile.EndsWith(".Challenge.Gbx", StringComparison.OrdinalIgnoreCase))
                        {
                            mapFiles.Add(mapFile);
                        }
                    }
                }
                catch
                {
                    // Skip unreadable folders while continuing the rest of the tree.
                }

                try
                {
                    foreach (string subFolder in Directory.EnumerateDirectories(currentFolder, "*", SearchOption.TopDirectoryOnly))
                    {
                        foldersToScan.Push(subFolder);
                    }
                }
                catch
                {
                    // Skip folders whose children cannot be listed.
                }
            }

            return mapFiles;
        }

        private static int GetDownloadedCampaignMapCount(string campaignFolder, IReadOnlyList<TmxMap> maps)
        {
            if (maps == null || maps.Count == 0)
            {
                return 0;
            }

            HashSet<string> existingMapUids = ScanMapUids(campaignFolder);
            int downloadedByUidCount = maps.Count(map =>
                !string.IsNullOrWhiteSpace(map.MapUid) &&
                existingMapUids.Contains(map.MapUid)
            );

            int downloadedByFileCount = CountLocalMapFiles(campaignFolder);

            return Math.Min(maps.Count, Math.Max(downloadedByUidCount, downloadedByFileCount));
        }

        private static int CountLocalMapFiles(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            try
            {
                return EnumerateMapFilesRecursively(folder).Count;
            }
            catch
            {
                return 0;
            }
        }

        private static string GetSafeFolderName(string folderName)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                folderName = folderName.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(folderName)
                ? "Campaign"
                : folderName.Trim();
        }

        private void SetStatus(string status)
        {
            statusTextBlock.Text = status;
        }
    }

    public sealed class TrackmaniaExchangeResultItem
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public int MapCount { get; set; }
        public int DownloadedCount { get; set; }
        public TmxMappack Campaign { get; set; }
        public IReadOnlyList<TmxMap> CampaignMaps { get; set; } = [];
    }

    internal sealed class LocalMapInventory
    {
        public HashSet<string> MapUids { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MapNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
