using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContinuousTranscriber.Dashboard;

internal sealed partial class TranscriptArchive
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, CachedWaveInfo> _waveCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _audioGate = new();
    private Dictionary<string, string> _audioPaths = new(StringComparer.OrdinalIgnoreCase);

    public TranscriptArchive(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public ArchiveSummary GetSummary()
    {
        var snapshot = BuildSnapshot();
        if (snapshot.Count == 0)
        {
            var now = DateTimeOffset.Now;
            return new ArchiveSummary(now.AddHours(-1), now, 0, 0);
        }

        return new ArchiveSummary(
            snapshot.Min(entry => entry.Audio is null || entry.Timestamp <= entry.Audio.Start
                ? entry.Timestamp
                : entry.Audio.Start),
            snapshot.Max(entry => entry.Audio is null || entry.Timestamp >= entry.Audio.End
                ? entry.Timestamp
                : entry.Audio.End),
            snapshot.Count,
            snapshot.Count(entry => entry.Audio is not null));
    }

    public ArchiveEntriesResponse GetEntries(DateTimeOffset? start, DateTimeOffset? end, string? query)
    {
        var snapshot = BuildSnapshot();
        var normalizedQuery = query?.Trim();
        var entries = snapshot
            .Where(entry => !start.HasValue || entry.Timestamp >= start.Value)
            .Where(entry => !end.HasValue || entry.Timestamp <= end.Value)
            .Where(entry => string.IsNullOrWhiteSpace(normalizedQuery)
                            || entry.Text.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Timestamp)
            .ToArray();
        return new ArchiveEntriesResponse(entries, entries.Length, normalizedQuery ?? string.Empty);
    }

    public string? ResolveAudioPath(string id)
    {
        lock (_audioGate)
        {
            if (_audioPaths.TryGetValue(id, out var path) && File.Exists(path))
            {
                return path;
            }
        }

        BuildSnapshot();
        lock (_audioGate)
        {
            return _audioPaths.TryGetValue(id, out var path) && File.Exists(path) ? path : null;
        }
    }

    private List<ArchiveEntry> BuildSnapshot()
    {
        var audioByLineHash = ReadAudioClips()
            .GroupBy(clip => clip.TranscriptLineHash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<AudioClipSource>(group.OrderBy(clip => clip.Start)),
                StringComparer.OrdinalIgnoreCase);
        var entries = new List<ArchiveEntry>();

        foreach (var transcriptPath in Directory.EnumerateFiles(_root, "transcript *.txt", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(transcriptPath, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var match = TranscriptLinePattern().Match(lines[lineIndex]);
                if (!match.Success
                    || !DateTime.TryParseExact(
                        match.Groups["timestamp"].Value,
                        "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var localDateTime)
                    || !int.TryParse(match.Groups["confidence"].Value, out var confidence))
                {
                    continue;
                }

                var timestamp = ToLocalOffset(localDateTime);
                var fullLine = lines[lineIndex] + "\n";
                var lineHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullLine))).ToLowerInvariant();
                AudioClipSource? source = null;
                if (audioByLineHash.TryGetValue(lineHash, out var matchingClips) && matchingClips.Count > 0)
                {
                    source = matchingClips.Dequeue();
                }
                else
                {
                    var noNewlineHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(lines[lineIndex]))).ToLowerInvariant();
                    if (audioByLineHash.TryGetValue(noNewlineHash, out matchingClips) && matchingClips.Count > 0)
                    {
                        source = matchingClips.Dequeue();
                    }
                }

                var entryId = StableId($"{transcriptPath}|{lineIndex}|{lineHash}");
                entries.Add(new ArchiveEntry(
                    entryId,
                    timestamp,
                    confidence,
                    match.Groups["text"].Value,
                    source is null ? null : new ArchiveAudio(
                        source.Id,
                        $"/api/archive/audio/{source.Id}",
                        source.Start,
                        source.End,
                        source.DurationSeconds,
                        source.TrimStartSeconds,
                        source.TrimEndSeconds)));
            }
        }

        return entries;
    }

    private List<AudioClipSource> ReadAudioClips()
    {
        var clips = new List<AudioClipSource>();
        var audioPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keptRoot = Path.Combine(_root, "recordings", "kept");
        if (!Directory.Exists(keptRoot))
        {
            lock (_audioGate)
            {
                _audioPaths = audioPaths;
            }

            return clips;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(keptRoot, "manifest.jsonl", SearchOption.AllDirectories))
        {
            IEnumerable<string> lines;
            try
            {
                lines = File.ReadLines(manifestPath);
                foreach (var line in lines)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("audio_file", out var audioProperty)
                            || !root.TryGetProperty("transcript_line_sha256", out var hashProperty))
                        {
                            continue;
                        }

                        var audioName = audioProperty.GetString();
                        var lineHash = hashProperty.GetString();
                        if (string.IsNullOrWhiteSpace(audioName) || string.IsNullOrWhiteSpace(lineHash))
                        {
                            continue;
                        }

                        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
                        var audioPath = Path.GetFullPath(Path.Combine(manifestDirectory, audioName));
                        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifestDirectory))
                                                  + Path.DirectorySeparatorChar;
                        if (!audioPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase)
                            || !File.Exists(audioPath))
                        {
                            continue;
                        }

                        var info = GetWaveInfo(audioPath);
                        var id = StableId(audioPath.ToLowerInvariant());
                        audioPaths[id] = audioPath;
                        clips.Add(new AudioClipSource(
                            id,
                            lineHash,
                            info.Start,
                            info.End,
                            info.DurationSeconds,
                            info.TrimStartSeconds,
                            info.TrimEndSeconds));
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        lock (_audioGate)
        {
            _audioPaths = audioPaths;
        }

        return clips;
    }

    private CachedWaveInfo GetWaveInfo(string path)
    {
        var fileInfo = new FileInfo(path);
        var cacheKey = $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
        if (_waveCache.TryGetValue(path, out var cached) && cached.CacheKey == cacheKey)
        {
            return cached;
        }

        var wave = WaveInspector.Inspect(path);
        var end = new DateTimeOffset(fileInfo.LastWriteTime);
        var result = new CachedWaveInfo(
            cacheKey,
            end.AddSeconds(-wave.DurationSeconds),
            end,
            wave.DurationSeconds,
            wave.TrimStartSeconds,
            wave.TrimEndSeconds);
        _waveCache[path] = result;
        return result;
    }

    private static DateTimeOffset ToLocalOffset(DateTime value)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    private static string StableId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();

    [GeneratedRegex(@"^\[(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<timezone>[^\]]+)\] \[speech-confidence >=(?<confidence>\d+)%\] (?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TranscriptLinePattern();

    private sealed record AudioClipSource(
        string Id,
        string TranscriptLineHash,
        DateTimeOffset Start,
        DateTimeOffset End,
        double DurationSeconds,
        double TrimStartSeconds,
        double TrimEndSeconds);

    private sealed record CachedWaveInfo(
        string CacheKey,
        DateTimeOffset Start,
        DateTimeOffset End,
        double DurationSeconds,
        double TrimStartSeconds,
        double TrimEndSeconds);
}

internal static class WaveInspector
{
    public static WaveInspection Inspect(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (ReadFourCc(reader) != "RIFF")
            {
                return WaveInspection.Empty;
            }

            reader.ReadUInt32();
            if (ReadFourCc(reader) != "WAVE")
            {
                return WaveInspection.Empty;
            }

            ushort format = 0;
            ushort channels = 0;
            uint sampleRate = 0;
            uint byteRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            long dataOffset = 0;
            uint dataLength = 0;

            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = ReadFourCc(reader);
                var chunkLength = reader.ReadUInt32();
                var nextChunk = Math.Min(stream.Length, stream.Position + chunkLength + (chunkLength % 2));
                if (chunkId == "fmt " && chunkLength >= 16)
                {
                    format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadUInt32();
                    byteRate = reader.ReadUInt32();
                    blockAlign = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                }
                else if (chunkId == "data")
                {
                    dataOffset = stream.Position;
                    dataLength = (uint)Math.Min(chunkLength, stream.Length - stream.Position);
                }

                stream.Position = nextChunk;
            }

            if (byteRate == 0 || dataLength == 0)
            {
                return WaveInspection.Empty;
            }

            var duration = dataLength / (double)byteRate;
            if (format != 1 || bitsPerSample != 16 || channels == 0 || sampleRate == 0 || blockAlign == 0
                || dataLength > 128 * 1024 * 1024)
            {
                return new WaveInspection(duration, 0, duration);
            }

            stream.Position = dataOffset;
            var bytes = reader.ReadBytes((int)dataLength);
            var samplesPerWindow = Math.Max(1, (int)(sampleRate * .02));
            var bytesPerWindow = samplesPerWindow * blockAlign;
            var firstActive = -1;
            var lastActive = -1;
            var windowIndex = 0;
            for (var offset = 0; offset + 1 < bytes.Length; offset += bytesPerWindow, windowIndex++)
            {
                var limit = Math.Min(bytes.Length - 1, offset + bytesPerWindow);
                long squareSum = 0;
                var sampleCount = 0;
                var peak = 0;
                for (var sampleOffset = offset; sampleOffset + 1 < limit; sampleOffset += 2)
                {
                    var sample = (short)(bytes[sampleOffset] | (bytes[sampleOffset + 1] << 8));
                    var absolute = Math.Abs((int)sample);
                    peak = Math.Max(peak, absolute);
                    squareSum += (long)sample * sample;
                    sampleCount++;
                }

                var rms = sampleCount == 0 ? 0 : Math.Sqrt(squareSum / (double)sampleCount);
                if (peak >= 900 || rms >= 260)
                {
                    firstActive = firstActive < 0 ? windowIndex : firstActive;
                    lastActive = windowIndex;
                }
            }

            if (firstActive < 0)
            {
                return new WaveInspection(duration, 0, duration);
            }

            var trimStart = Math.Max(0, firstActive * .02 - .16);
            var trimEnd = Math.Min(duration, (lastActive + 1) * .02 + .24);
            return new WaveInspection(duration, trimStart, Math.Max(trimStart + .05, trimEnd));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return WaveInspection.Empty;
        }
    }

    private static string ReadFourCc(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));
}

internal sealed record WaveInspection(double DurationSeconds, double TrimStartSeconds, double TrimEndSeconds)
{
    public static WaveInspection Empty { get; } = new(0, 0, 0);
}

internal sealed record ArchiveSummary(
    DateTimeOffset AvailableStart,
    DateTimeOffset AvailableEnd,
    int TranscriptCount,
    int RecordingCount);

internal sealed record ArchiveEntriesResponse(
    IReadOnlyList<ArchiveEntry> Entries,
    int MatchCount,
    string Query);

internal sealed record ArchiveEntry(
    string Id,
    DateTimeOffset Timestamp,
    int Confidence,
    string Text,
    ArchiveAudio? Audio);

internal sealed record ArchiveAudio(
    string Id,
    string Url,
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    double TrimStartSeconds,
    double TrimEndSeconds);
