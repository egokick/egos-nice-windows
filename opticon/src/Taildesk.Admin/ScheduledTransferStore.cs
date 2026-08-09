using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class ScheduledTransferStore
{
    private readonly string _path;
    private readonly string _lockPath;

    public ScheduledTransferStore(string? path = null, string? lockPath = null)
    {
        _path = path ?? AppPaths.ScheduledTransfersFile;
        _lockPath = lockPath ?? AppPaths.ScheduledTransfersLockFile;
    }

    public Task<ScheduledTransferDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync((document, _) => Task.FromResult(document), save: false, cancellationToken);

    public Task<ScheduledTransferDefinition> UpsertAsync(
        ScheduledTransferDefinition definition,
        CancellationToken cancellationToken = default) => WithLockAsync((document, now) =>
    {
        ScheduledTransferRules.Validate(definition);
        var existing = document.Schedules.FirstOrDefault(item => item.Id == definition.Id);
        definition.CreatedAt = existing?.CreatedAt ?? (definition.CreatedAt == default ? now : definition.CreatedAt);
        definition.UpdatedAt = now;
        definition.ActiveRunId = existing?.ActiveRunId;
        definition.LastStartedAt = existing?.LastStartedAt;
        definition.NextRunAt = definition.Enabled ? ScheduledTransferRules.NextRun(definition, now.AddSeconds(-1)) : null;
        if (existing is null) document.Schedules.Add(definition.Copy());
        else
        {
            var index = document.Schedules.IndexOf(existing);
            document.Schedules[index] = definition.Copy();
        }
        return Task.FromResult(definition.Copy());
    }, save: true, cancellationToken);

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithLockAsync((document, _) =>
        {
            var schedule = document.Schedules.FirstOrDefault(item => item.Id == id);
            if (schedule is null) return Task.FromResult(false);
            if (schedule.ActiveRunId.HasValue) throw new InvalidOperationException("A running schedule cannot be deleted.");
            document.Schedules.Remove(schedule);
            return Task.FromResult(true);
        }, save: true, cancellationToken);

    public Task<ScheduledTransferDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) =>
        WithLockAsync((document, now) =>
        {
            var schedule = RequireSchedule(document, id);
            schedule.Enabled = enabled;
            schedule.UpdatedAt = now;
            schedule.NextRunAt = enabled ? ScheduledTransferRules.NextRun(schedule, now.AddSeconds(-1)) : null;
            return Task.FromResult(schedule.Copy());
        }, save: true, cancellationToken);

    public Task<ScheduledTransferRun?> ClaimNextDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        WithLockAsync((document, _) =>
        {
            RecoverAbandonedRuns(document, now);
            foreach (var schedule in document.Schedules.Where(item => item.Enabled && !item.ActiveRunId.HasValue))
            {
                schedule.NextRunAt ??= ScheduledTransferRules.NextRun(schedule, now.AddMinutes(-1));
            }
            var due = document.Schedules
                .Where(item => item.Enabled && !item.ActiveRunId.HasValue && item.NextRunAt <= now)
                .OrderBy(item => item.NextRunAt)
                .FirstOrDefault();
            return Task.FromResult(due is null ? null : Claim(document, due, ScheduledTransferTrigger.Schedule, null, now));
        }, save: true, cancellationToken);

    public Task<ScheduledTransferRun> ClaimManualAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
        WithLockAsync((document, now) =>
        {
            RecoverAbandonedRuns(document, now);
            var schedule = RequireSchedule(document, scheduleId);
            if (schedule.ActiveRunId.HasValue) throw new InvalidOperationException("This scheduled transfer is already running.");
            return Task.FromResult(Claim(document, schedule, ScheduledTransferTrigger.Manual, null, now));
        }, save: true, cancellationToken);

    public Task<ScheduledTransferRun> ClaimRetryAsync(Guid runId, CancellationToken cancellationToken = default) =>
        WithLockAsync((document, now) =>
        {
            RecoverAbandonedRuns(document, now);
            var previous = document.History.FirstOrDefault(item => item.Id == runId)
                           ?? throw new KeyNotFoundException("The scheduled-transfer run was not found.");
            if (previous.State is ScheduledTransferRunState.Running or ScheduledTransferRunState.Succeeded)
                throw new InvalidOperationException("Only a failed or partially successful run can be retried.");
            var schedule = document.Schedules.FirstOrDefault(item => item.Id == previous.ScheduleId);
            if (schedule?.ActiveRunId.HasValue == true
                || (schedule is null && document.History.Any(item => item.ScheduleId == previous.ScheduleId && item.State == ScheduledTransferRunState.Running)))
                throw new InvalidOperationException("This scheduled transfer is already running.");
            var run = schedule is not null
                ? Claim(document, schedule, ScheduledTransferTrigger.Retry, previous.Id, now)
                : CreateRun(previous.ScheduleId, previous.ScheduleName, previous.Definition, ScheduledTransferTrigger.Retry, previous.Id, now);
            if (schedule is null)
            {
                document.History.Insert(0, run);
                TrimHistory(document);
            }
            run.Definition = previous.Definition.Copy();
            run.RetryCandidates = previous.Files
                .Where(item => item.State == ScheduledTransferFileState.Failed)
                .Select(item => item.Copy())
                .ToList();
            run.RetryRequiresDiscovery = run.RetryCandidates.Count == 0 && previous.Files.Count == 0;
            if (run.RetryCandidates.Count == 0 && !run.RetryRequiresDiscovery)
                throw new InvalidOperationException("The selected run has no failed file results to retry.");
            return Task.FromResult(run);
        }, save: true, cancellationToken);

    public Task CompleteAsync(ScheduledTransferRun completed, CancellationToken cancellationToken = default) =>
        WithLockAsync((document, _) =>
        {
            completed.RetryCandidates = [];
            completed.RetryRequiresDiscovery = false;
            var stored = document.History.FirstOrDefault(item => item.Id == completed.Id);
            if (stored is null) document.History.Insert(0, completed);
            else document.History[document.History.IndexOf(stored)] = completed;
            var schedule = document.Schedules.FirstOrDefault(item => item.Id == completed.ScheduleId);
            if (schedule?.ActiveRunId == completed.Id) schedule.ActiveRunId = null;
            TrimHistory(document);
            return Task.FromResult(true);
        }, save: true, cancellationToken);

    private static ScheduledTransferRun Claim(
        ScheduledTransferDocument document,
        ScheduledTransferDefinition schedule,
        ScheduledTransferTrigger trigger,
        Guid? retryOf,
        DateTimeOffset now)
    {
        var run = CreateRun(schedule.Id, schedule.Name, schedule, trigger, retryOf, now);
        schedule.ActiveRunId = run.Id;
        schedule.LastStartedAt = now;
        if (schedule.Enabled) schedule.NextRunAt = ScheduledTransferRules.NextRun(schedule, now);
        document.History.Insert(0, run);
        TrimHistory(document);
        return run;
    }

    private static ScheduledTransferRun CreateRun(Guid scheduleId, string scheduleName,
        ScheduledTransferDefinition definition, ScheduledTransferTrigger trigger, Guid? retryOf, DateTimeOffset now) => new()
    {
        ScheduleId = scheduleId,
        ScheduleName = scheduleName,
        Trigger = trigger,
        RetryOfRunId = retryOf,
        Definition = definition.Copy(),
        StartedAt = now,
        OwnerProcessId = Environment.ProcessId,
        OwnerProcessStartedAt = GetCurrentProcessStartedAt()
    };

    private async Task<T> WithLockAsync<T>(
        Func<ScheduledTransferDocument, DateTimeOffset, Task<T>> action,
        bool save,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("No scheduled-transfer data directory."));
        await using var lease = await AcquireLockAsync(cancellationToken);
        var document = await ReadCoreAsync(cancellationToken);
        var result = await action(document, DateTimeOffset.UtcNow);
        if (save) await WriteCoreAsync(document, cancellationToken);
        return result;
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(100, cancellationToken); }
        }
    }

    private async Task<ScheduledTransferDocument> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new ScheduledTransferDocument();
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonSerializer.DeserializeAsync<ScheduledTransferDocument>(stream, JsonDefaults.Options, cancellationToken)
                       ?? new ScheduledTransferDocument();
        document.Schedules ??= [];
        document.History ??= [];
        foreach (var run in document.History)
        {
            run.Files ??= [];
            run.RetryCandidates ??= [];
        }
        return document;
    }

    private async Task WriteCoreAsync(ScheduledTransferDocument document, CancellationToken cancellationToken)
    {
        var temporary = _path + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonDefaults.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, true);
    }

    private static ScheduledTransferDefinition RequireSchedule(ScheduledTransferDocument document, Guid id) =>
        document.Schedules.FirstOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException("The scheduled transfer was not found.");

    private static void RecoverAbandonedRuns(ScheduledTransferDocument document, DateTimeOffset now)
    {
        foreach (var run in document.History.Where(item => item.State == ScheduledTransferRunState.Running).ToArray())
        {
            if (OwnerIsAlive(run)) continue;
            run.State = ScheduledTransferRunState.Failed;
            run.FinishedAt = now;
            run.Message = "The previous Opticon process stopped before this run completed.";
            var owner = document.Schedules.FirstOrDefault(item => item.ActiveRunId == run.Id);
            if (owner is not null) owner.ActiveRunId = null;
        }
        foreach (var schedule in document.Schedules.Where(item => item.ActiveRunId.HasValue
                     && !document.History.Any(run => run.Id == item.ActiveRunId && run.State == ScheduledTransferRunState.Running)))
            schedule.ActiveRunId = null;
    }

    private static void TrimHistory(ScheduledTransferDocument document)
        => ScheduledTransferHistoryPolicy.Trim(document);

    private static DateTimeOffset? GetCurrentProcessStartedAt()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private static bool OwnerIsAlive(ScheduledTransferRun run)
    {
        if (run.OwnerProcessId <= 0 || !run.OwnerProcessStartedAt.HasValue) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(run.OwnerProcessId);
            return !process.HasExited
                   && Math.Abs((process.StartTime.ToUniversalTime() - run.OwnerProcessStartedAt.Value.UtcDateTime).TotalSeconds) < 2;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch
        {
            // If Windows will not expose an elevated owner's start time, favor
            // avoiding a duplicate transfer until the bounded seven-day lease ages out.
            return run.StartedAt >= DateTimeOffset.UtcNow.AddDays(-7);
        }
    }
}
