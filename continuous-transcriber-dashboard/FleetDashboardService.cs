using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Taildesk.Admin;
using Taildesk.Shared;

namespace ContinuousTranscriber.Dashboard;

internal sealed record FleetDevice(
    Guid Id, string Name, string HostName, DeviceRole Role, DeviceConnectionState State,
    bool IsLocal, bool CanDownload, DateTimeOffset? LastSeen, string? Error = null);

internal sealed record FleetSnapshot(DeviceRole CurrentRole, bool CanAccessRemote, IReadOnlyList<FleetDevice> Devices);
internal sealed record DownloadRequest(DateTimeOffset Start, DateTimeOffset End);
internal sealed record SyncRequest(IReadOnlyList<Guid> DeviceIds, DateTimeOffset Start, DateTimeOffset End);
internal sealed record ScheduleRequest(string CronExpression, bool Enabled, bool DeleteFromOrigin);

internal sealed class FleetDashboardService
{
    private const string SchedulePrefix = "Continuous transcriptions · ";
    private readonly string _localRoot;
    private readonly string _cacheRoot;
    private readonly AdminState _state = new();
    private readonly AgentClient _agents = new();
    private readonly SemaphoreSlim _initialize = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _syncLocks = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _metadataRefresh = new();
    private bool _initialized;

    public FleetDashboardService(string localRoot)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ContinuousTranscriberDashboard", "devices");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<FleetSnapshot> GetFleetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var localAgent = await TranscriptionTransferService.LoadLocalAgentAsync(cancellationToken);
        var role = localAgent?.Role ?? DeviceRole.ManagedOnly;
        var canRemote = role == DeviceRole.ControllerAndManaged && _state.Config.SetupComplete;
        var localRecord = FindLocal(localAgent);
        var localId = localRecord?.Id ?? localAgent?.DeviceId ?? StableMachineId();
        var devices = new List<FleetDevice>
        {
            new(localId, localRecord is null ? Environment.MachineName : NameOf(localRecord), Environment.MachineName,
                role, localRecord?.State ?? DeviceConnectionState.Online, true, false, localRecord?.LastSeen)
        };
        if (canRemote)
        {
            devices.AddRange(_state.Config.Devices
                .Where(item => localRecord is null || item.Id != localRecord.Id)
                .OrderBy(NameOf, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FleetDevice(item.Id, NameOf(item), item.HostName, item.Role, item.State,
                    false, true, item.LastSeen)));
        }
        return new FleetSnapshot(role, canRemote, devices);
    }

    public async Task<ArchiveSummary> GetSummaryAsync(IReadOnlyCollection<Guid> selected, CancellationToken cancellationToken)
    {
        var archives = await ResolveArchivesAsync(selected, syncMetadata: false, null, null, cancellationToken);
        var summaries = archives.Select(item => item.Archive.GetSummary()).ToArray();
        if (summaries.Length == 0)
        {
            var now = DateTimeOffset.Now;
            return new ArchiveSummary(now.AddHours(-1), now, 0, 0);
        }
        return new ArchiveSummary(summaries.Min(item => item.AvailableStart), summaries.Max(item => item.AvailableEnd),
            summaries.Sum(item => item.TranscriptCount), summaries.Sum(item => item.RecordingCount));
    }

    public async Task<ArchiveEntriesResponse> GetEntriesAsync(IReadOnlyCollection<Guid> selected,
        DateTimeOffset? start, DateTimeOffset? end, string? query, CancellationToken cancellationToken)
    {
        var archives = await ResolveArchivesAsync(selected, syncMetadata: true, start, end, cancellationToken);
        var entries = archives.SelectMany(item => item.Archive.GetEntries(start, end, query).Entries)
            .OrderBy(item => item.Timestamp).ThenBy(item => item.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ArchiveEntriesResponse(entries, entries.Length, query?.Trim() ?? string.Empty);
    }

    public async Task<string?> ResolveAudioAsync(Guid deviceId, string audioId, CancellationToken cancellationToken)
    {
        var archive = (await ResolveArchivesAsync([deviceId], false, null, null, cancellationToken)).FirstOrDefault();
        return archive.Archive?.ResolveAudioPath(audioId);
    }

    public async Task<TranscriptionTransferResult> DownloadAsync(Guid deviceId, DateTimeOffset start, DateTimeOffset end,
        bool deleteFromOrigin, CancellationToken cancellationToken)
    {
        var fleet = await GetFleetAsync(cancellationToken);
        var target = fleet.Devices.FirstOrDefault(item => item.Id == deviceId)
                     ?? throw new KeyNotFoundException("The selected Opticon device was not found.");
        if (target.IsLocal) throw new InvalidOperationException("This machine's transcripts are already local.");
        TranscriptionTransferService.RequireControllerAndManaged(await TranscriptionTransferService.LoadLocalAgentAsync(cancellationToken));
        var record = _state.Config.Devices.First(item => item.Id == deviceId);
        return await new TranscriptionTransferService(_agents).SyncAsync(record, DeviceCache(deviceId), start, end,
            metadataOnly: false, deleteFromOrigin, cancellationToken);
    }

    public async Task<IReadOnlyList<TranscriptionTransferResult>> SyncAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        var results = new List<TranscriptionTransferResult>();
        foreach (var deviceId in request.DeviceIds.Distinct())
        {
            var fleet = await GetFleetAsync(cancellationToken);
            if (fleet.Devices.FirstOrDefault(item => item.Id == deviceId)?.IsLocal != false) continue;
            results.Add(await DownloadAsync(deviceId, request.Start, request.End, deleteFromOrigin: false, cancellationToken));
        }
        return results;
    }

    public async Task<IReadOnlyList<ScheduledTransferDefinition>> GetSchedulesAsync(CancellationToken cancellationToken)
    {
        TranscriptionTransferService.RequireControllerAndManaged(await TranscriptionTransferService.LoadLocalAgentAsync(cancellationToken));
        var document = await new ScheduledTransferStore().LoadAsync(cancellationToken);
        return document.Schedules.Where(item => item.Name.StartsWith(SchedulePrefix, StringComparison.Ordinal)).ToArray();
    }

    public async Task<ScheduledTransferDefinition> SaveScheduleAsync(Guid deviceId, ScheduleRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptionTransferService.RequireControllerAndManaged(await TranscriptionTransferService.LoadLocalAgentAsync(cancellationToken));
        await EnsureInitializedAsync(cancellationToken);
        var device = _state.Config.Devices.FirstOrDefault(item => item.Id == deviceId)
                     ?? throw new KeyNotFoundException("The selected Opticon device was not found.");
        var location = await new TranscriptionTransferService(_agents).ResolveArchiveAsync(device, cancellationToken);
        var store = new ScheduledTransferStore();
        var document = await store.LoadAsync(cancellationToken);
        var name = SchedulePrefix + NameOf(device);
        var definition = document.Schedules.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal))?.Copy()
                         ?? new ScheduledTransferDefinition { Name = name, DeviceId = device.Id };
        definition.Enabled = request.Enabled;
        definition.Direction = ScheduledTransferDirection.Download;
        definition.LocalFolder = DeviceCache(deviceId);
        definition.RemoteRoot = TranscriptionTransferService.PreferredRootId;
        definition.RemoteFolder = string.Empty;
        definition.Filter = ScheduledTransferFilter.Regex;
        definition.FilterPattern = @"^((transcript .+\.txt)|(recordings/kept/.+\.(wav|jsonl)))$";
        definition.Recursive = true;
        definition.Mode = request.DeleteFromOrigin ? ScheduledTransferMode.Move : ScheduledTransferMode.Copy;
        definition.Overwrite = true;
        definition.CronExpression = request.CronExpression;
        definition.TimeZoneId = TimeZoneInfo.Local.Id;
        ScheduledTransferRules.Validate(definition);
        return await store.UpsertAsync(definition, cancellationToken);
    }

    private async Task<IReadOnlyList<(FleetDevice Device, TranscriptArchive Archive)>> ResolveArchivesAsync(
        IReadOnlyCollection<Guid> selected, bool syncMetadata, DateTimeOffset? start, DateTimeOffset? end,
        CancellationToken cancellationToken)
    {
        var fleet = await GetFleetAsync(cancellationToken);
        var ids = selected.Count == 0 ? fleet.Devices.Select(item => item.Id).ToHashSet() : selected.ToHashSet();
        var result = new List<(FleetDevice, TranscriptArchive)>();
        foreach (var device in fleet.Devices.Where(item => ids.Contains(item.Id)))
        {
            var root = device.IsLocal ? _localRoot : DeviceCache(device.Id);
            if (!device.IsLocal && syncMetadata && start.HasValue && end.HasValue)
                await RefreshMetadataAsync(device, start.Value, end.Value, cancellationToken);
            Directory.CreateDirectory(root);
            result.Add((device, new TranscriptArchive(root, device.Id, device.Name)));
        }
        return result;
    }

    private async Task RefreshMetadataAsync(FleetDevice device, DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (_metadataRefresh.TryGetValue(device.Id, out var refreshed) && refreshed > DateTimeOffset.UtcNow.AddSeconds(-30)) return;
        var gate = _syncLocks.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_metadataRefresh.TryGetValue(device.Id, out refreshed) && refreshed > DateTimeOffset.UtcNow.AddSeconds(-30)) return;
            var record = _state.Config.Devices.First(item => item.Id == device.Id);
            try
            {
                await new TranscriptionTransferService(_agents).SyncAsync(record, DeviceCache(device.Id), start, end,
                    metadataOnly: true, deleteFromOrigin: false, cancellationToken);
                _metadataRefresh[device.Id] = DateTimeOffset.UtcNow;
            }
            catch when (Directory.EnumerateFiles(DeviceCache(device.Id), "transcript *.txt", SearchOption.TopDirectoryOnly).Any())
            {
                _metadataRefresh[device.Id] = DateTimeOffset.UtcNow;
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initialize.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await _state.InitializeAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initialize.Release(); }
    }

    private DeviceRecord? FindLocal(AgentConfig? agent) => _state.Config.Devices.FirstOrDefault(item =>
        item.Id == agent?.DeviceId || item.HostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || item.Name.Equals(agent?.DeviceName ?? Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    private string DeviceCache(Guid id) => Path.Combine(_cacheRoot, id.ToString("D"));
    private static string NameOf(DeviceRecord device) => string.IsNullOrWhiteSpace(device.Name) ? device.HostName : device.Name;
    private static Guid StableMachineId()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}