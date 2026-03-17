namespace YouTubeSyncTray;

internal readonly record struct VideoCaptionTrack(
    string TrackKey,
    string Label,
    string LanguageCode,
    string SourcePath,
    string Format,
    int SortOrder);
