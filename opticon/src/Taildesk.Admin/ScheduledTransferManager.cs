using System.Collections.ObjectModel;
using System.Windows;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class ScheduledTransferRow
{
    public required ScheduledTransferDefinition Definition { get; init; }
    public string Id => Definition.Id.ToString("D");
    public string Name => Definition.Name;
    public string Device { get; init; } = string.Empty;
    public string Direction => Definition.Direction.ToString();
    public string Mode => Definition.Mode.ToString();
    public string Source => Definition.Direction == ScheduledTransferDirection.Upload
        ? Definition.LocalFolder : $"{Definition.RemoteRoot}:/{Definition.RemoteFolder}";
    public string Destination => Definition.Direction == ScheduledTransferDirection.Upload
        ? $"{Definition.RemoteRoot}:/{Definition.RemoteFolder}" : Definition.LocalFolder;
    public string Files => Definition.Filter switch
    {
        ScheduledTransferFilter.All => Definition.Recursive ? "All files (including subfolders)" : "All files",
        ScheduledTransferFilter.Extension => $"*{Definition.FilterPattern}" + (Definition.Recursive ? " (including subfolders)" : string.Empty),
        ScheduledTransferFilter.Regex => $"Regex: {Definition.FilterPattern}" + (Definition.Recursive ? " (including subfolders)" : string.Empty),
        _ => string.Empty
    };
    public string Schedule => CronSchedule.Describe(Definition.CronExpression);
    public string Enabled => Definition.Enabled ? Definition.ActiveRunId.HasValue ? "Running" : "Enabled" : "Paused";
    public DateTimeOffset? NextRun => Definition.NextRunAt?.ToLocalTime();
}

public sealed class ScheduledTransferHistoryRow
{
    public required ScheduledTransferRun Run { get; init; }
    public Guid Id => Run.Id;
    public string Name => Run.ScheduleName;
    public DateTimeOffset Started => Run.StartedAt.ToLocalTime();
    public string Result => Run.State.ToString();
    public string Trigger => Run.Trigger.ToString();
    public string Files => $"{Run.FilesTransferred}/{Run.FilesDiscovered}";
    public string Message => Run.Message;
    public bool CanRetry => Run.State is ScheduledTransferRunState.Failed or ScheduledTransferRunState.PartiallySucceeded;
}

public sealed class ScheduledTransferManager : IAsyncDisposable
{
    private readonly AdminState _state;
    private readonly ScheduledTransferStore _store;
    private readonly ScheduledTransferEngine _engine;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _runSlots = new(2, 2);
    private Task? _loop;

    public ScheduledTransferManager(AdminState state, AgentClient agents)
    {
        _state = state;
        _store = new ScheduledTransferStore();
        _engine = new ScheduledTransferEngine(state, agents, _store);
    }

    public ObservableCollection<ScheduledTransferRow> Schedules { get; } = [];
    public ObservableCollection<ScheduledTransferHistoryRow> History { get; } = [];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken);
        _loop ??= Task.Run(() => SchedulerLoopAsync(_shutdown.Token));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var document = await _store.LoadAsync(cancellationToken);
        var devices = _state.Config.Devices.ToDictionary(item => item.Id, item => item.Name);
        await OnUiAsync(() =>
        {
            Schedules.Clear();
            foreach (var schedule in document.Schedules.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                Schedules.Add(new ScheduledTransferRow
                {
                    Definition = schedule,
                    Device = devices.TryGetValue(schedule.DeviceId, out var name) ? name : "Removed device"
                });
            History.Clear();
            foreach (var run in document.History.OrderByDescending(item => item.StartedAt))
                History.Add(new ScheduledTransferHistoryRow { Run = run });
        });
    }

    public async Task<ScheduledTransferDefinition> SaveAsync(ScheduledTransferDefinition definition, CancellationToken cancellationToken = default)
    {
        var saved = await _store.UpsertAsync(definition, cancellationToken);
        await RefreshAsync(cancellationToken);
        return saved;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _store.DeleteAsync(id, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await _store.SetEnabledAsync(id, enabled, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task<ScheduledTransferRun> RunNowAsync(Guid id, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var run = await _store.ClaimManualAsync(id, cancellationToken);
        await RefreshAsync(cancellationToken);
        await _runSlots.WaitAsync(cancellationToken);
        try { return await _engine.RunClaimedAsync(run, progress, cancellationToken); }
        finally { _runSlots.Release(); await RefreshAsync(CancellationToken.None); }
    }

    public async Task<ScheduledTransferRun> RetryAsync(Guid runId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var run = await _store.ClaimRetryAsync(runId, cancellationToken);
        await RefreshAsync(cancellationToken);
        await _runSlots.WaitAsync(cancellationToken);
        try { return await _engine.RunClaimedAsync(run, progress, cancellationToken); }
        finally { _runSlots.Release(); await RefreshAsync(CancellationToken.None); }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                while (await _runSlots.WaitAsync(0, cancellationToken))
                {
                    var run = await _store.ClaimNextDueAsync(DateTimeOffset.UtcNow, cancellationToken);
                    if (run is null) { _runSlots.Release(); break; }
                    await RefreshAsync(cancellationToken);
                    _ = ExecuteBackgroundAsync(run, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch
            {
                // A transient persistence failure is retried on the next tick. Individual
                // transfer failures are persisted by the engine and do not stop this loop.
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ExecuteBackgroundAsync(ScheduledTransferRun run, CancellationToken cancellationToken)
    {
        try { await _engine.RunClaimedAsync(run, cancellationToken: cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _runSlots.Release();
            try { await RefreshAsync(CancellationToken.None); } catch { }
        }
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
        // Background runs observe _shutdown and release their slots while the
        // process exits. Do not dispose the semaphore out from under that finally.
    }
}
