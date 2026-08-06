using System.Diagnostics;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

internal sealed class GuardianRunner
{
    private static readonly TimeSpan InitialHealthWindow = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan CommittedBootHealthWindow = TimeSpan.FromMinutes(6.5);
    private static readonly TimeSpan RollbackHealthWindow = TimeSpan.FromMinutes(6.5);
    private static readonly TimeSpan SustainedHealthFailureWindow = TimeSpan.FromSeconds(15);
    private readonly GuardianPathPolicy _paths = new();
    private readonly LifelineInspector _lifelines = new();
    private readonly AgentTaskController _agentTask;

    public GuardianRunner() => _agentTask = new AgentTaskController(_paths);

    public async Task<int> RunAsync(bool watchdogOnly, CancellationToken cancellationToken)
    {
        _paths.ValidateRunningGuardian();
        await InvitationSigning.VerifyAuthenticodeAsync(_paths.GuardianExecutable, cancellationToken);
        await ProtectUpdateDirectoryAsync(cancellationToken);
        using var coordinationLease = await AcquireCoordinationAsync(watchdogOnly, cancellationToken);
        if (coordinationLease is null)
            // A producer/full Guardian owns the durable state. A later minute
            // retry closes the crash gap without occupying the singleton.
            return 0;
        _paths.EnsureProtectedPath(
            AppPaths.UpdateCoordinationLockFile, _paths.UpdateRoot, "update coordination lock");

        UpdateJournal? journal;
        try
        {
            _paths.EnsureProtectedPath(_paths.JournalPath, _paths.UpdateRoot, "update journal");
            journal = UpdateJournalPersistence.Load(_paths.JournalPath);
        }
        catch
        {
            await TryStartStableAgentAsync(cancellationToken);
            throw;
        }

        if (journal is null) return 0;
        if (watchdogOnly
            && journal.Phase is UpdatePhase.None
                or UpdatePhase.Ready
                or UpdatePhase.Committed
                or UpdatePhase.RolledBack
                or UpdatePhase.Failed)
            return 0;

        OperationPaths operation;
        try { operation = _paths.ValidateJournal(journal); }
        catch
        {
            await TryStartStableAgentAsync(cancellationToken);
            throw;
        }

        return journal.Phase switch
        {
            UpdatePhase.Downloading or UpdatePhase.Verifying => await RecoverInterruptedStagingAsync(
                journal, cancellationToken),
            UpdatePhase.ActivationScheduled => await ActivateAsync(journal, operation, cancellationToken),
            UpdatePhase.Activating => await RecoverByRollbackAsync(
                journal, operation, "The guardian recovered an interrupted Agent directory swap.", cancellationToken),
            UpdatePhase.AwaitingCommit => await ResumeAwaitingCommitAsync(journal, operation, cancellationToken),
            UpdatePhase.RollingBack => await RecoverByRollbackAsync(
                journal, operation, "The guardian resumed an interrupted rollback.", cancellationToken),
            UpdatePhase.Committed => await VerifyCommittedAsync(journal, operation, cancellationToken),
            UpdatePhase.RolledBack => await VerifyRolledBackAsync(journal, operation, cancellationToken),
            UpdatePhase.Failed => await RescueFailedStateAsync(journal, operation, cancellationToken),
            _ => 0
        };
    }

    private static async Task<IDisposable?> AcquireCoordinationAsync(
        bool watchdogOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            return await UpdateJournalCoordination.AcquireAsync(
                watchdogOnly ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(20),
                cancellationToken);
        }
        catch (TimeoutException) when (watchdogOnly)
        {
            return null;
        }
    }

    private async Task<int> ActivateAsync(
        UpdateJournal journal,
        OperationPaths operation,
        CancellationToken cancellationToken)
    {
        if (journal.ActivateAfter is not { } activateAfter || journal.CommitDeadline is not { } deadline)
            throw new InvalidDataException("The scheduled activation has no protected deadline.");
        if (DateTimeOffset.UtcNow >= deadline)
        {
            await RetainOldAgentAsync(journal, operation,
                "The activation deadline passed before the Agent was changed; the previous Agent was retained.",
                cancellationToken);
            return 0;
        }
        journal.GuardianClaimedAt = DateTimeOffset.UtcNow;
        journal.Message = "The fail-safe Guardian claimed this transaction and is completing protected preflight checks.";
        await SaveJournalAsync(journal, CancellationToken.None);
        if (DateTimeOffset.UtcNow < activateAfter)
            await Task.Delay(activateAfter - DateTimeOffset.UtcNow, cancellationToken);
        if (DateTimeOffset.UtcNow >= deadline)
        {
            await RetainOldAgentAsync(journal, operation,
                "The activation deadline passed before the Agent was changed; the previous Agent was retained.",
                cancellationToken);
            return 0;
        }

        var enteredSwap = false;
        try
        {
            if (Directory.Exists(operation.RollbackDirectory))
                throw new InvalidOperationException("A rollback directory already exists for this operation; recovery is required.");

            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            var lifelines = _lifelines.CapturePreflight(journal.BindAddress);
            using var health = new InternalHealthClient();
            if (!journal.MaintenanceBootstrap)
            {
                var preflightHealth = await health.CheckAsync(
                    journal, journal.CurrentVersion, UpdatePhase.ActivationScheduled, cancellationToken);
                if (!preflightHealth.IsHealthy)
                    throw new InvalidOperationException("The old Agent failed protected preflight health: " + preflightHealth.Message);
            }
            await ValidateAgentDirectoryAsync(_paths.AgentDirectory, journal.CurrentVersion, cancellationToken);

            await BuildVerifiedCandidateAsync(journal, operation, cancellationToken);
            if (DateTimeOffset.UtcNow >= deadline)
                throw new CommitWindowExpiredException("The commit window expired while re-verifying the signed candidate.");

            // Re-snapshot immediately before stopping the Agent. If SSH became
            // active during verification, it is protected for the whole swap too.
            var finalPreflight = _lifelines.CapturePreflight(journal.BindAddress);
            lifelines = new LifelineSnapshot(lifelines.SshWasListening || finalPreflight.SshWasListening);
            journal.SshWasListening = lifelines.SshWasListening;
            _lifelines.Validate(journal.BindAddress, lifelines.SshWasListening);

            journal.Phase = UpdatePhase.Activating;
            journal.Message = "The fail-safe guardian is swapping only the signed Agent directory; Tailscale, RustDesk, and SSH remain untouched.";
            await SaveJournalAsync(journal, CancellationToken.None);
            enteredSwap = true;

            await _agentTask.StopAgentOnlyAsync(CancellationToken.None);
            _paths.EnsureSafeTree(_paths.AgentDirectory, _paths.ProgramFilesRoot);
            _paths.EnsureSafeTree(operation.CandidateDirectory, _paths.ProgramFilesRoot);
            await MoveDirectoryWithRetryAsync(_paths.AgentDirectory, operation.RollbackDirectory, CancellationToken.None);
            await MoveDirectoryWithRetryAsync(operation.CandidateDirectory, _paths.AgentDirectory, CancellationToken.None);
            await ValidateAgentDirectoryAsync(_paths.AgentDirectory, journal.TargetVersion, CancellationToken.None);

            journal.Phase = UpdatePhase.AwaitingCommit;
            journal.Message = "The signed Agent candidate is active. It will be rolled back unless protected health and the command center commit it before the deadline.";
            await SaveJournalAsync(journal, CancellationToken.None);
            await _agentTask.StartAgentAsync(CancellationToken.None);

            return await AwaitCommitOrRollbackAsync(journal, operation, lifelines, health, cancellationToken);
        }
        catch (Exception exception)
        {
            if (enteredSwap || Directory.Exists(operation.RollbackDirectory)
                            || journal.Phase is UpdatePhase.Activating or UpdatePhase.AwaitingCommit)
            {
                var rolledBack = await TryRollbackAsync(
                    journal, operation, "Activation failed: " + exception.Message, CancellationToken.None);
                if (!rolledBack) throw;
            }
            else
            {
                await MarkFailedWithoutChangingAgentAsync(journal,
                    "Activation was refused before the installed Agent changed: " + exception.Message);
            }

            if (exception is OperationCanceledException) throw;
            return exception is CommitWindowExpiredException ? 0 : 1;
        }
    }

    private async Task<int> ResumeAwaitingCommitAsync(
        UpdateJournal journal,
        OperationPaths operation,
        CancellationToken cancellationToken)
    {
        if (journal.CommitDeadline is not { } deadline || DateTimeOffset.UtcNow >= deadline)
            return await RecoverByRollbackAsync(journal, operation,
                "The external commit deadline passed while the guardian or device was unavailable.", cancellationToken);

        try
        {
            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            if (!Directory.Exists(operation.RollbackDirectory))
                throw new InvalidOperationException("The rollback copy is missing while the candidate awaits commit.");
            await ValidateAgentDirectoryAsync(_paths.AgentDirectory, journal.TargetVersion, cancellationToken);
            var lifelines = new LifelineSnapshot(journal.SshWasListening);
            using var health = new InternalHealthClient();
            await _agentTask.StartAgentAsync(cancellationToken);
            return await AwaitCommitOrRollbackAsync(journal, operation, lifelines, health, cancellationToken);
        }
        catch (Exception exception)
        {
            var rolledBack = await TryRollbackAsync(
                journal, operation, "Awaiting-commit recovery failed: " + exception.Message, CancellationToken.None);
            if (!rolledBack) throw;
            if (exception is OperationCanceledException) throw;
            return 1;
        }
    }

    private async Task<int> AwaitCommitOrRollbackAsync(
        UpdateJournal journal,
        OperationPaths operation,
        LifelineSnapshot lifelines,
        InternalHealthClient health,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForHealthyAgentAsync(
                journal, journal.TargetVersion, UpdatePhase.AwaitingCommit, lifelines,
                health, InitialHealthWindow, cancellationToken);

            var lastHealthy = DateTimeOffset.UtcNow;
            var lastFailure = string.Empty;
            while (journal.CommitDeadline is { } deadline && DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var healthy = _agentTask.IsAgentRunning();
                if (!healthy) lastFailure = "The candidate Agent process exited.";
                try { _lifelines.Validate(journal.BindAddress, lifelines.SshWasListening); }
                catch (Exception exception) { healthy = false; lastFailure = exception.Message; }

                var result = await health.CheckAsync(
                    journal, journal.TargetVersion, UpdatePhase.AwaitingCommit, cancellationToken);
                if (!result.IsHealthy) { healthy = false; lastFailure = result.Message; }
                if (healthy)
                {
                    lastHealthy = DateTimeOffset.UtcNow;
                    if (TryReadValidCommit(journal, out var commitFailure))
                    {
                        await CommitAsync(journal, operation, lifelines, health, cancellationToken);
                        return 0;
                    }
                    if (!string.IsNullOrWhiteSpace(commitFailure)) lastFailure = commitFailure;
                }
                else if (DateTimeOffset.UtcNow - lastHealthy >= SustainedHealthFailureWindow)
                {
                    throw new InvalidOperationException("Candidate health failed for the rollback grace period: " + lastFailure);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            throw new CommitWindowExpiredException("No valid external commit arrived before the protected deadline.");
        }
        catch (Exception exception)
        {
            var rolledBack = await TryRollbackAsync(
                journal, operation, "The candidate was not committed: " + exception.Message, CancellationToken.None);
            if (!rolledBack) throw;
            if (exception is OperationCanceledException) throw;
            return exception is CommitWindowExpiredException ? 0 : 1;
        }
    }

    private async Task CommitAsync(
        UpdateJournal journal,
        OperationPaths operation,
        LifelineSnapshot lifelines,
        InternalHealthClient health,
        CancellationToken cancellationToken)
    {
        _lifelines.Validate(journal.BindAddress, lifelines.SshWasListening);
        await ValidateAgentDirectoryAsync(_paths.AgentDirectory, journal.TargetVersion, cancellationToken);
        var finalHealth = await health.CheckAsync(
            journal, journal.TargetVersion, UpdatePhase.AwaitingCommit, cancellationToken);
        if (!finalHealth.IsHealthy)
            throw new InvalidOperationException("The final protected health check failed: " + finalHealth.Message);

        journal.Phase = UpdatePhase.Committed;
        journal.CurrentVersion = UpdatePackageVerifier.NormalizeVersion(journal.TargetVersion);
        // A committed release is no longer governed by the activation window.
        // Leaving the old deadline in the durable journal makes the ONSTART
        // health check time out immediately and roll back every successful update.
        journal.ActivateAfter = null;
        journal.GuardianClaimedAt = null;
        journal.SshWasListening = false;
        journal.CommitDeadline = null;
        journal.Message = "An authorized health observer committed the healthy Agent. The signed rollback copy is retained for boot-time recovery.";
        await SaveJournalAsync(journal, CancellationToken.None);
        TryDeleteCommitRequest();
    }

    private async Task<int> VerifyCommittedAsync(
        UpdateJournal journal,
        OperationPaths operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            await ValidateAgentDirectoryAsync(_paths.AgentDirectory, journal.TargetVersion, cancellationToken);
            var lifelines = new LifelineSnapshot(journal.SshWasListening);
            using var health = new InternalHealthClient();
            await _agentTask.StartAgentAsync(cancellationToken);
            await WaitForHealthyAgentAsync(
                journal, journal.TargetVersion, UpdatePhase.Committed, lifelines,
                health, CommittedBootHealthWindow, cancellationToken);
            return 0;
        }
        catch (Exception exception)
        {
            if (Directory.Exists(operation.RollbackDirectory))
            {
                var rolledBack = await TryRollbackAsync(
                    journal, operation, "The committed Agent failed boot-time health: " + exception.Message, CancellationToken.None);
                if (rolledBack) return 1;
            }
            await MarkFailedWithoutChangingAgentAsync(journal,
                "The committed Agent failed health and no usable rollback was available: " + exception.Message);
            await TryStartStableAgentAsync(CancellationToken.None);
            if (exception is OperationCanceledException) throw;
            return 1;
        }
    }

    private async Task<int> VerifyRolledBackAsync(
        UpdateJournal journal,
        OperationPaths operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            if (!Directory.Exists(_paths.AgentDirectory) && Directory.Exists(operation.RollbackDirectory))
                await MoveDirectoryWithRetryAsync(operation.RollbackDirectory, _paths.AgentDirectory, CancellationToken.None);
            var version = await ValidateAgentDirectoryAsync(_paths.AgentDirectory, expectedVersion: null, cancellationToken);
            journal.CurrentVersion = version;
            journal.Phase = UpdatePhase.RolledBack;
            journal.Message = "The previous Agent is installed at the stable path after rollback.";
            await SaveJournalAsync(journal, CancellationToken.None);
            await _agentTask.StartAgentAsync(cancellationToken);
            return 0;
        }
        catch (Exception exception)
        {
            await MarkFailedWithoutChangingAgentAsync(journal, "Rollback recovery could not start the stable Agent: " + exception.Message);
            if (exception is OperationCanceledException) throw;
            return 1;
        }
    }

    private async Task<int> RescueFailedStateAsync(
        UpdateJournal journal,
        OperationPaths operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            if (!Directory.Exists(_paths.AgentDirectory) && Directory.Exists(operation.RollbackDirectory))
            {
                await MoveDirectoryWithRetryAsync(operation.RollbackDirectory, _paths.AgentDirectory, CancellationToken.None);
                var restored = await ValidateAgentDirectoryAsync(_paths.AgentDirectory, expectedVersion: null, CancellationToken.None);
                journal.CurrentVersion = restored;
                journal.Phase = UpdatePhase.RolledBack;
                journal.Message = "The guardian restored the rollback copy because the stable Agent directory was missing.";
                await SaveJournalAsync(journal, CancellationToken.None);
            }
            if (Directory.Exists(_paths.AgentDirectory)) await _agentTask.StartAgentAsync(cancellationToken);
            return 0;
        }
        catch (Exception exception)
        {
            await MarkFailedWithoutChangingAgentAsync(journal, "The guardian could not rescue the stable Agent: " + exception.Message);
            if (exception is OperationCanceledException) throw;
            return 1;
        }
    }

    private async Task<int> RecoverByRollbackAsync(
        UpdateJournal journal,
        OperationPaths operation,
        string reason,
        CancellationToken cancellationToken)
    {
        var rolledBack = await TryRollbackAsync(journal, operation, reason, CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return rolledBack ? 0 : 1;
    }

    private async Task<bool> TryRollbackAsync(
        UpdateJournal journal,
        OperationPaths operation,
        string reason,
        CancellationToken cancellationToken)
    {
        var previousPhase = journal.Phase;
        try
        {
            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            journal.Phase = UpdatePhase.RollingBack;
            journal.Message = reason + " Restoring the previous Agent from the protected rollback directory.";
            await SaveJournalAsync(journal, CancellationToken.None);
            await _agentTask.StopAgentOnlyAsync(cancellationToken);

            string? rollbackVersion = null;
            if (Directory.Exists(operation.RollbackDirectory))
            {
                rollbackVersion = await ValidateAgentDirectoryAsync(
                    operation.RollbackDirectory,
                    previousPhase is UpdatePhase.Committed or UpdatePhase.RollingBack ? null : journal.CurrentVersion,
                    cancellationToken);
            }

            if (Directory.Exists(_paths.AgentDirectory))
            {
                var activeVersion = await ValidateAgentDirectoryAsync(_paths.AgentDirectory, expectedVersion: null, cancellationToken);
                if (rollbackVersion is not null
                    && !activeVersion.Equals(rollbackVersion, StringComparison.Ordinal))
                {
                    await MoveActiveToFailedAsync(operation, cancellationToken);
                }
                else if (rollbackVersion is null
                         && !activeVersion.Equals(UpdatePackageVerifier.NormalizeVersion(journal.CurrentVersion), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The candidate is active but its rollback copy is missing.");
                }
            }

            if (!Directory.Exists(_paths.AgentDirectory))
            {
                if (rollbackVersion is null)
                    throw new DirectoryNotFoundException("Neither the stable Agent nor its rollback copy is available.");
                await MoveDirectoryWithRetryAsync(operation.RollbackDirectory, _paths.AgentDirectory, cancellationToken);
            }

            if (Directory.Exists(operation.CandidateDirectory))
                await MoveCandidateToFailedAsync(operation, cancellationToken);

            var restoredVersion = await ValidateAgentDirectoryAsync(_paths.AgentDirectory, rollbackVersion, cancellationToken);
            journal.CurrentVersion = restoredVersion;
            journal.ActivateAfter = null;
            journal.CommitDeadline = null;
            journal.Phase = UpdatePhase.RolledBack;
            journal.Message = reason + " The previous Agent was restored; Tailscale, RustDesk, and SSH were never modified.";
            await SaveJournalAsync(journal, CancellationToken.None);
            await _agentTask.StartAgentAsync(cancellationToken);

            try
            {
                var lifelines = new LifelineSnapshot(journal.SshWasListening);
                if (journal.MaintenanceBootstrap)
                {
                    await WaitForLegacyRollbackAsync(
                        journal.BindAddress, lifelines, RollbackHealthWindow, cancellationToken);
                }
                else
                {
                    using var health = new InternalHealthClient();
                    await WaitForHealthyAgentAsync(
                        journal, restoredVersion, UpdatePhase.RolledBack, lifelines,
                        health, RollbackHealthWindow, cancellationToken);
                }
            }
            catch (Exception healthFailure) when (healthFailure is not OperationCanceledException)
            {
                journal.Phase = UpdatePhase.Failed;
                journal.Message = "Rollback files were restored and the Agent task was started, but protected health did not recover: "
                                  + healthFailure.Message;
                await SaveJournalAsync(journal, CancellationToken.None);
                return false;
            }
            journal.GuardianClaimedAt = null;
            journal.SshWasListening = false;
            await SaveJournalAsync(journal, CancellationToken.None);
            return true;
        }
        catch (Exception rollbackFailure)
        {
            journal.Phase = UpdatePhase.Failed;
            journal.Message = "Automatic rollback could not be completed. Connectivity services were left untouched and any stable Agent was started: "
                              + rollbackFailure.Message;
            try { await SaveJournalAsync(journal, CancellationToken.None); } catch { }
            try { await TryStartStableAgentAsync(CancellationToken.None); } catch { }
            return false;
        }
    }

    private async Task RetainOldAgentAsync(
        UpdateJournal journal,
        OperationPaths operation,
        string message,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(operation.CandidateDirectory))
            await MoveCandidateToFailedAsync(operation, cancellationToken);
        var version = await ValidateAgentDirectoryAsync(_paths.AgentDirectory, journal.CurrentVersion, cancellationToken);
        journal.CurrentVersion = version;
        journal.ActivateAfter = null;
        journal.CommitDeadline = null;
        journal.GuardianClaimedAt = null;
        journal.SshWasListening = false;
        journal.Phase = UpdatePhase.RolledBack;
        journal.Message = message;
        await SaveJournalAsync(journal, CancellationToken.None);
        await _agentTask.VerifyDefinitionAsync(cancellationToken);
        await _agentTask.StartAgentAsync(cancellationToken);
    }

    private async Task BuildVerifiedCandidateAsync(
        UpdateJournal journal,
        OperationPaths operation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(operation.PackagePath)
            || new FileInfo(operation.PackagePath).Length != journal.PackageSize)
            throw new FileNotFoundException("The exact hash-pinned update package is missing or has the wrong size.", operation.PackagePath);
        _paths.EnsureProtectedPath(operation.PackagePath, _paths.UpdateRoot, "staged package");
        if (Directory.Exists(operation.CandidateDirectory))
            SafeDeleteDirectory(operation.CandidateDirectory, _paths.ProgramFilesRoot);

        var manifest = await UpdatePackageVerifier.VerifyAndExtractAgentAsync(
            operation.PackagePath,
            operation.CandidateDirectory,
            _paths.CreateVerificationRequest(journal),
            cancellationToken);
        _paths.EnsureSafeTree(operation.CandidateDirectory, _paths.ProgramFilesRoot);

        var installedGuardianVersion = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(_paths.GuardianExecutable).ProductVersion ?? string.Empty);
        if (UpdatePackageVerifier.ParseVersion(installedGuardianVersion)
            < UpdatePackageVerifier.ParseVersion(manifest.MinimumGuardianVersion))
            throw new InvalidOperationException(
                $"This release requires guardian {manifest.MinimumGuardianVersion}, but {installedGuardianVersion} is installed.");
        await ValidateAgentDirectoryAsync(operation.CandidateDirectory, journal.TargetVersion, cancellationToken);
    }

    private async Task<string> ValidateAgentDirectoryAsync(
        string directory,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        _paths.EnsureSafeTree(directory, _paths.ProgramFilesRoot);
        var executable = Path.Combine(directory, "Taildesk.Agent.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The Agent executable is missing.", executable);
        await InvitationSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
        var version = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? string.Empty);
        _ = UpdatePackageVerifier.ParseVersion(version);
        if (expectedVersion is not null
            && !version.Equals(UpdatePackageVerifier.NormalizeVersion(expectedVersion), StringComparison.Ordinal))
            throw new InvalidDataException($"The Agent directory reports version {version}, not {expectedVersion}.");
        return version;
    }

    private async Task WaitForHealthyAgentAsync(
        UpdateJournal journal,
        string expectedVersion,
        UpdatePhase expectedPhase,
        LifelineSnapshot lifelines,
        InternalHealthClient health,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(maximumWait);
        if (journal.CommitDeadline is { } commitDeadline && commitDeadline < deadline) deadline = commitDeadline;
        var lastFailure = "The Agent has not started.";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_agentTask.IsAgentRunning())
                    throw new InvalidOperationException("The exact stable-path Agent process is not running.");
                _lifelines.Validate(journal.BindAddress, lifelines.SshWasListening);
                var result = await health.CheckAsync(journal, expectedVersion, expectedPhase, cancellationToken);
                if (result.IsHealthy) return;
                lastFailure = result.Message;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastFailure = exception.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("Protected Agent health did not become ready: " + lastFailure);
    }

    private async Task WaitForLegacyRollbackAsync(
        string bindAddress,
        LifelineSnapshot lifelines,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        const int requiredHealthySamples = 3;
        var deadline = DateTimeOffset.UtcNow.Add(maximumWait);
        var healthySamples = 0;
        var lastFailure = "The restored legacy Agent process has not started.";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _lifelines.Validate(bindAddress, lifelines.SshWasListening);
                if (!_agentTask.IsAgentRunning())
                    throw new InvalidOperationException("The exact restored legacy Agent process is not running.");
                healthySamples++;
                if (healthySamples >= requiredHealthySamples) return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                healthySamples = 0;
                lastFailure = exception.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException(
            $"The restored legacy Agent or a recovery lifeline did not recover within {maximumWait}. Last failure: {lastFailure}");
    }

    private async Task<int> RecoverInterruptedStagingAsync(
        UpdateJournal journal,
        CancellationToken cancellationToken)
    {
        await MarkFailedWithoutChangingAgentAsync(journal,
            "The device restarted or the staging process ended before verification completed. The installed Agent was never changed; staging can be retried.");
        await TryStartStableAgentAsync(cancellationToken);
        return 0;
    }

    private bool TryReadValidCommit(UpdateJournal journal, out string failure)
    {
        failure = string.Empty;
        try
        {
            _paths.EnsureProtectedPath(_paths.CommitRequestPath, _paths.UpdateRoot, "update commit request");
            var request = UpdateJournalPersistence.LoadCommitRequest();
            if (request is null) return false;
            var earliest = journal.ActivateAfter ?? journal.StartedAt;
            if (request.OperationId != journal.OperationId
                || request.RequestedAt < earliest
                || journal.CommitDeadline is not { } deadline
                || request.RequestedAt > deadline
                || request.RequestedAt > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                failure = "An invalid or stale external commit request was ignored.";
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            failure = "The external commit request could not be validated: " + exception.Message;
            return false;
        }
    }

    private async Task MoveActiveToFailedAsync(OperationPaths operation, CancellationToken cancellationToken)
    {
        if (Directory.Exists(operation.FailedCandidateDirectory))
            SafeDeleteDirectory(operation.FailedCandidateDirectory, _paths.ProgramFilesRoot);
        await MoveDirectoryWithRetryAsync(_paths.AgentDirectory, operation.FailedCandidateDirectory, cancellationToken);
    }

    private async Task MoveCandidateToFailedAsync(OperationPaths operation, CancellationToken cancellationToken)
    {
        if (Directory.Exists(operation.FailedCandidateDirectory))
            SafeDeleteDirectory(operation.FailedCandidateDirectory, _paths.ProgramFilesRoot);
        await MoveDirectoryWithRetryAsync(operation.CandidateDirectory, operation.FailedCandidateDirectory, cancellationToken);
    }

    private async Task MoveDirectoryWithRetryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        _paths.EnsureSafeTree(source, _paths.ProgramFilesRoot);
        _paths.EnsureProtectedPath(destination, _paths.ProgramFilesRoot, "directory move destination");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"The protected source directory is missing: {source}");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"The protected destination already exists: {destination}");

        Exception? last = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                if (attempt < 6) await Task.Delay(TimeSpan.FromMilliseconds(attempt * 500), cancellationToken);
            }
        }
        throw new IOException($"Windows could not atomically move {source} to {destination}.", last);
    }

    private void SafeDeleteDirectory(string directory, string allowedRoot)
    {
        _paths.EnsureSafeTree(directory, allowedRoot);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task MarkFailedWithoutChangingAgentAsync(UpdateJournal journal, string message)
    {
        journal.ActivateAfter = null;
        journal.CommitDeadline = null;
        journal.GuardianClaimedAt = null;
        journal.SshWasListening = false;
        journal.Phase = UpdatePhase.Failed;
        journal.Message = message;
        try { await SaveJournalAsync(journal, CancellationToken.None); } catch { }
    }

    private async Task SaveJournalAsync(UpdateJournal journal, CancellationToken cancellationToken)
    {
        var durable = UpdateJournalPersistence.Load(_paths.JournalPath);
        if (durable is null || durable.OperationId != journal.OperationId)
            throw new InvalidOperationException(
                "A different durable update transaction superseded this Guardian invocation; no stale transition was saved.");
        _ = _paths.ValidateJournal(journal);
        await UpdateJournalPersistence.SaveAsync(journal, _paths.JournalPath, cancellationToken);
        _paths.EnsureProtectedPath(_paths.JournalPath, _paths.UpdateRoot, "update journal");
    }

    private async Task ProtectUpdateDirectoryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UpdateRoot);
        _paths.EnsureSafeTree(_paths.UpdateRoot, _paths.ProgramDataRoot);
        var result = await WindowsCommand.RunAsync(
            "icacls.exe",
            [
                _paths.UpdateRoot,
                "/inheritance:r",
                "/grant:r",
                "*S-1-5-18:(OI)(CI)F",
                "*S-1-5-32-544:(OI)(CI)F",
                "/setowner",
                "*S-1-5-18"
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("Windows could not protect the update journal directory: " + result.ErrorDetail);
    }

    private async Task TryStartStableAgentAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_paths.AgentDirectory)) return;
            _paths.EnsureSafeTree(_paths.AgentDirectory, _paths.ProgramFilesRoot);
            await _agentTask.VerifyDefinitionAsync(cancellationToken);
            await _agentTask.StartAgentAsync(cancellationToken);
        }
        catch { }
    }

    private void TryDeleteCommitRequest()
    {
        try
        {
            _paths.EnsureProtectedPath(_paths.CommitRequestPath, _paths.UpdateRoot, "update commit request");
            if (File.Exists(_paths.CommitRequestPath)) File.Delete(_paths.CommitRequestPath);
        }
        catch { }
    }

    private sealed class CommitWindowExpiredException(string message) : Exception(message);
}
