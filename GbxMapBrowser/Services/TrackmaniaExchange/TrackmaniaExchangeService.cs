#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GbxMapBrowser.Services.TrackmaniaExchange
{
    public sealed class TrackmaniaExchangeService
    {
        private const string BaseUrl = "https://trackmania.exchange";

        private readonly HttpClient _httpClient = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public TrackmaniaExchangeService()
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GbxMapBrowser/2.2");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }

        public async Task<IReadOnlyList<TmxMap>> GetRecentTrackOfTheDayMapsAsync(
            int count,
            CancellationToken cancellationToken = default
        )
        {
            const int maxMapsPerRequest = 1000;

            string fields = Uri.EscapeDataString("MapId,MapUid,Name,UploadedAt,UpdatedAt,Uploader.Name");
            List<TmxMap> maps = [];
            long? afterMapId = null;

            while (maps.Count < count)
            {
                int requestCount = Math.Min(maxMapsPerRequest, count - maps.Count);
                string afterQuery = afterMapId.HasValue ? $"&after={afterMapId.Value}" : "";
                string url = $"{BaseUrl}/api/maps?fields={fields}&count={requestCount}{afterQuery}&intotd=1&order1=8";

                TmxMapSearchResponse response = await GetJsonAsync<TmxMapSearchResponse>(url, cancellationToken)
                    ?? new TmxMapSearchResponse();

                if (response.Results.Count == 0)
                {
                    break;
                }

                maps.AddRange(response.Results);
                afterMapId = response.Results[^1].MapId;

                if (!response.More)
                {
                    break;
                }
            }

            return maps;
        }

        public async Task<IReadOnlyList<TmxMappack>> GetOfficialCampaignsAsync(
            CancellationToken cancellationToken = default
        )
        {
            string url = $"{BaseUrl}/mappacksearch/search?api=on&format=json&limit=100&creatorid=21&type=1";

            TmxMappackSearchResponse response = await GetJsonAsync<TmxMappackSearchResponse>(url, cancellationToken)
                ?? new TmxMappackSearchResponse();

            return response.Results
                .Where(IsOfficialSeasonalCampaign)
                .OrderByDescending(campaign => campaign.CreatedAt)
                .ToList();
        }

        public async Task<IReadOnlyList<TmxMap>> GetMappackMapsAsync(
            long mappackId,
            CancellationToken cancellationToken = default
        )
        {
            string fields = Uri.EscapeDataString("MapId,MapUid,Name,Mappack.MapPosition");
            string url = $"{BaseUrl}/api/maps?fields={fields}&count=100&mappackid={mappackId}&order1=1";

            TmxMapSearchResponse response = await GetJsonAsync<TmxMapSearchResponse>(url, cancellationToken)
                ?? new TmxMapSearchResponse();

            return response.Results
                .OrderBy(map => map.Mappack?.MapPosition ?? int.MaxValue)
                .ThenBy(map => map.Name)
                .ToList();
        }

        public async Task<string> DownloadMapAsync(
            TmxMap map,
            string destinationFolder,
            CancellationToken cancellationToken = default
        )
        {
            Directory.CreateDirectory(destinationFolder);

            string fileName = GetSafeMapFileName(map);
            string destinationPath = GetAvailableDestinationPath(destinationFolder, fileName);
            string url = $"{BaseUrl}/mapgbx/{map.MapId}";

            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using FileStream fileStream = new(destinationPath, FileMode.CreateNew, FileAccess.Write);
            await response.Content.CopyToAsync(fileStream, cancellationToken);

            return destinationPath;
        }

        private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
        }

        private static bool IsOfficialSeasonalCampaign(TmxMappack campaign)
        {
            if (campaign.Creator?.UserId != 21)
            {
                return false;
            }

            if (campaign.Type != 1 || campaign.TrackCount < 20)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(campaign.Name))
            {
                return false;
            }

            string[] seasons = ["Spring", "Summer", "Fall", "Winter"];

            return seasons.Any(season =>
                campaign.Name.StartsWith(season + " ", StringComparison.OrdinalIgnoreCase) &&
                campaign.Name.Any(char.IsDigit)
            );
        }

        private static string GetSafeMapFileName(TmxMap map)
        {
            string name = string.IsNullOrWhiteSpace(map.Name)
                ? $"TMX {map.MapId}"
                : map.Name;

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidChar, '_');
            }

            name = name.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"TMX {map.MapId}";
            }

            return name.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + ".Map.Gbx";
        }

        private static string GetAvailableDestinationPath(string destinationFolder, string fileName)
        {
            string destinationPath = Path.Combine(destinationFolder, fileName);

            if (!File.Exists(destinationPath))
            {
                return destinationPath;
            }

            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int copyNumber = 1;

            while (true)
            {
                string newFileName = $"{nameWithoutExtension} ({copyNumber}){extension}";
                string newDestinationPath = Path.Combine(destinationFolder, newFileName);

                if (!File.Exists(newDestinationPath))
                {
                    return newDestinationPath;
                }

                copyNumber++;
            }
        }
    }

    public sealed class TmxMapSearchResponse
    {
        public bool More { get; set; }
        public List<TmxMap> Results { get; set; } = [];
    }

    public sealed class TmxMap
    {
        public long MapId { get; set; }
        public string MapUid { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime? UploadedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public TmxUser? Uploader { get; set; }
        public TmxMapMappackInfo? Mappack { get; set; }
    }

    public sealed class TmxMapMappackInfo
    {
        public long MappackId { get; set; }
        public int? MapPosition { get; set; }
        public int? MapStatus { get; set; }
    }

    public sealed class TmxMappackSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmxMappack> Results { get; set; } = [];

        [JsonPropertyName("totalItemCount")]
        public int TotalItemCount { get; set; }
    }

    public sealed class TmxMappack
    {
        [JsonPropertyName("ID")]
        public long Id { get; set; }

        public string Name { get; set; } = "";
        public int Type { get; set; }
        public int TrackCount { get; set; }
        public bool Downloadable { get; set; }
        public bool TrackDownloadable { get; set; }
        public DateTime? CreatedAt { get; set; }
        public TmxUser? Creator { get; set; }
    }

    public sealed class TmxUser
    {
        public long UserId { get; set; }
        public string Name { get; set; } = "";
    }
}
