using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class LibraryCatalogStore
{
    private readonly string _catalogRootPath;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public LibraryCatalogStore(YoutubeSyncPaths paths)
    {
        _catalogRootPath = Path.Combine(paths.RootPath, "library-catalog");
        Directory.CreateDirectory(_catalogRootPath);
    }

    public IReadOnlyList<VideoItem> Load(
        string scopeFolderName,
        IReadOnlyDictionary<string, int>? watchLaterOrderByVideoId = null)
    {
        lock (_gate)
        {
            return LoadCatalogItems(scopeFolderName, watchLaterOrderByVideoId);
        }
    }

    public IReadOnlyList<VideoItem> LoadOrScan(
        string scopeFolderName,
        string downloadsPath,
        IReadOnlyDictionary<string, int>? watchLaterOrderByVideoId = null)
    {
        lock (_gate)
        {
            var catalogPath = GetCatalogPath(scopeFolderName);
            var catalogItems = LoadCatalogItems(scopeFolderName, watchLaterOrderByVideoId);
            if (!ShouldRescan(downloadsPath, catalogPath, catalogItems.Count))
            {
                return catalogItems;
            }

            return ScanAndSave(scopeFolderName, downloadsPath, watchLaterOrderByVideoId);
        }
    }

    public IReadOnlyList<VideoItem> Refresh(
        string scopeFolderName,
        string downloadsPath,
        IReadOnlyDictionary<string, int>? watchLaterOrderByVideoId = null)
    {
        lock (_gate)
        {
            return ScanAndSave(scopeFolderName, downloadsPath, watchLaterOrderByVideoId);
        }
    }

    private IReadOnlyList<VideoItem> LoadCatalogItems(
        string scopeFolderName,
        IReadOnlyDictionary<string, int>? watchLaterOrderByVideoId)
    {
        try
        {
            var path = GetCatalogPath(scopeFolderName);
            if (!File.Exists(path))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), _jsonOptions) ?? new CatalogFile();
            var items = file.Videos
                .Where(item => item is not null)
                .Select(item => item!.ToVideoItem())
                .Where(item => !string.IsNullOrWhiteSpace(item.VideoId) && File.Exists(item.VideoPath))
                .ToList();
            return VideoItem.SortForLibrary(items, watchLaterOrderByVideoId);
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<VideoItem> ScanAndSave(
        string scopeFolderName,
        string downloadsPath,
        IReadOnlyDictionary<string, int>? watchLaterOrderByVideoId)
    {
        var scannedItems = VideoItem.LoadFromDownloads(downloadsPath, watchLaterOrderByVideoId);
        Save(scopeFolderName, scannedItems);
        return scannedItems;
    }

    private void Save(string scopeFolderName, IReadOnlyList<VideoItem> items)
    {
        Directory.CreateDirectory(_catalogRootPath);
        var file = new CatalogFile
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            Videos = items.Select(CatalogVideoItem.FromVideoItem).ToList()
        };

        File.WriteAllText(
            GetCatalogPath(scopeFolderName),
            JsonSerializer.Serialize(file, _jsonOptions));
    }

    private string GetCatalogPath(string scopeFolderName) =>
        Path.Combine(_catalogRootPath, scopeFolderName + ".json");

    private static bool ShouldRescan(string downloadsPath, string catalogPath, int catalogItemCount)
    {
        if (!Directory.Exists(downloadsPath))
        {
            return catalogItemCount != 0;
        }

        if (!File.Exists(catalogPath))
        {
            return true;
        }

        try
        {
            var downloadsInfo = new DirectoryInfo(downloadsPath);
            var catalogInfo = new FileInfo(catalogPath);
            if (!catalogInfo.Exists)
            {
                return true;
            }

            if (downloadsInfo.LastWriteTimeUtc > catalogInfo.LastWriteTimeUtc)
            {
                return true;
            }

            if (catalogItemCount == 0 && Directory.EnumerateFiles(downloadsPath, "*", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private sealed class CatalogFile
    {
        public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public List<CatalogVideoItem> Videos { get; set; } = [];
    }

    private sealed class CatalogVideoItem
    {
        public string VideoId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string UploaderName { get; set; } = string.Empty;

        public string VideoPath { get; set; } = string.Empty;

        public string ThumbnailPath { get; set; } = string.Empty;

        public string InfoPath { get; set; } = string.Empty;

        public int PlaylistIndex { get; set; }

        public List<CatalogCaptionTrack> CaptionTracks { get; set; } = [];

        public static CatalogVideoItem FromVideoItem(VideoItem item)
        {
            return new CatalogVideoItem
            {
                VideoId = item.VideoId,
                Title = item.Title,
                UploaderName = item.UploaderName,
                VideoPath = item.VideoPath,
                ThumbnailPath = item.ThumbnailPath,
                InfoPath = item.InfoPath,
                PlaylistIndex = item.PlaylistIndex,
                CaptionTracks = item.CaptionTracks
                    .Select(track => new CatalogCaptionTrack
                    {
                        TrackKey = track.TrackKey,
                        Label = track.Label,
                        LanguageCode = track.LanguageCode,
                        SourcePath = track.SourcePath,
                        Format = track.Format,
                        SortOrder = track.SortOrder
                    })
                    .ToList()
            };
        }

        public VideoItem ToVideoItem()
        {
            return new VideoItem
            {
                VideoId = VideoId,
                Title = Title,
                UploaderName = UploaderName,
                VideoPath = VideoPath,
                ThumbnailPath = ThumbnailPath,
                InfoPath = InfoPath,
                PlaylistIndex = PlaylistIndex,
                CaptionTracks = CaptionTracks
                    .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.SourcePath) && File.Exists(track.SourcePath))
                    .Select(track => new VideoCaptionTrack(
                        track!.TrackKey,
                        track.Label,
                        track.LanguageCode,
                        track.SourcePath,
                        track.Format,
                        track.SortOrder))
                    .ToList()
            };
        }
    }

    private sealed class CatalogCaptionTrack
    {
        public string TrackKey { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string LanguageCode { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        public string Format { get; set; } = string.Empty;

        public int SortOrder { get; set; }
    }
}
