using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed class RemoteDeviceUpdateCoordinator
{
    private readonly AgentClient _agents;

    public RemoteDeviceUpdateCoordinator(AgentClient agents) => _agents = agents;

    public async Task<UpdateStatusDto> UpdateAsync(
        DeviceRecord device,
        string agentToken,
        OpticonUpdateRelease release,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var currentAgentVersion = UpdatePackageVerifier.ParseVersion(device.AgentVersion);
        var releaseVersion = UpdatePackageVerifier.ParseVersion(release.Version);
        var sourceProtocolFloor = UpdatePackageVerifier.ParseVersion(SourceUpdateProtocol.MinimumGuardianVersion);
        var installedGuardianVersion = ParseInstalledGuardianVersion(device.GuardianVersion);
        if (release.RequiresCleanReinstall
            || currentAgentVersion < sourceProtocolFloor
            || installedGuardianVersion < sourceProtocolFloor)
            throw new InvalidOperationException(
                $"Opticon Agent {device.AgentVersion} and Guardian {device.GuardianVersion} cannot stage the source-only release {release.Version}. " +
                "No remote candidate was staged. Perform an attended clean uninstall and re-enroll with a new signed source invitation.");
        if (release.RequiresMaintenanceBootstrap)
            throw new InvalidOperationException(
                "Source-only remote updates do not support the retired maintenance bootstrap.");
        if (release.SourceRelease is not { } sourceRelease)
            throw new InvalidDataException("The selected Opticon release does not contain a verified source archive.");
        if (releaseVersion <= currentAgentVersion)
            throw new InvalidOperationException(
                $"Opticon source release {release.Version} is not newer than installed version {device.AgentVersion}.");
        if (!AgentClient.IsTailscaleIp(device.TailscaleIp))
            throw new InvalidOperationException("The selected device has no valid Tailscale address.");
        if (device.State != DeviceConnectionState.Online)
            throw new InvalidOperationException("Fail-safe updates require a live Opticon Agent, not only a Tailscale presence.");
        if (!await AgentClient.ProbeTcpAsync(device.TailscaleIp, 21118, TimeSpan.FromSeconds(10), cancellationToken))
            throw new InvalidOperationException("RustDesk TCP 21118 is unavailable. Opticon will not update a distant device without a verified recovery channel.");

        var operationId = Guid.NewGuid();
        var sourceRequest = CreateSourceUpdateRequest(device, release, sourceRelease, operationId);
        progress?.Report(
            $"Staging the signed Opticon source release {release.Version} on {device.Name}; " +
            "the target will verify the archive and locally build an attested candidate while the installed Agent and recovery services remain active.");
        var prepareTask = _agents.PrepareSourceUpdateAsync(device, agentToken, sourceRequest, cancellationToken);
        var lastPreparationProgress = string.Empty;
        while (!prepareTask.IsCompleted)
        {
            var completed = await Task.WhenAny(
                prepareTask,
                Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            if (completed == prepareTask) break;
            try
            {
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var status = await _agents.GetUpdateStatusAsync(device, agentToken, pollTimeout.Token);
                if (status.OperationId != operationId) continue;
                var message = $"Remote Agent: {status.Phase} — {status.Message}";
                if (!message.Equals(lastPreparationProgress, StringComparison.Ordinal))
                {
                    progress?.Report(message);
                    lastPreparationProgress = message;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                const string message = "The update request is still running; its live journal poll timed out.";
                if (!message.Equals(lastPreparationProgress, StringComparison.Ordinal))
                {
                    progress?.Report(message);
                    lastPreparationProgress = message;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = "The update request is still running; its live journal is temporarily unavailable. " + exception.Message;
                if (!message.Equals(lastPreparationProgress, StringComparison.Ordinal))
                {
                    progress?.Report(message);
                    lastPreparationProgress = message;
                }
            }
        }

        UpdateStatusDto prepared;
        try
        {
            prepared = await prepareTask;
        }
        catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
        {
            try
            {
                var failed = await _agents.GetUpdateStatusAsync(device, agentToken, cancellationToken);
                if (failed.OperationId == operationId && failed.Phase == UpdatePhase.Failed)
                {
                    progress?.Report($"Update failed safely: {failed.Message}");
                    return failed;
                }
            }
            catch (Exception statusException) when (IsRecoverableRemoteFailure(statusException, cancellationToken))
            {
                throw new InvalidOperationException(
                    "The update request failed and Opticon could not retrieve the remote failure journal. " +
                    $"Request: {exception.Message} Journal: {statusException.Message}", exception);
            }
            throw;
        }
        if (prepared.Phase != UpdatePhase.Ready)
            throw new InvalidOperationException($"The target did not reach the verified Ready state ({prepared.Phase}: {prepared.Message}).");

        // Snapshot the real SSH listener immediately before activation. If it
        // exists now, the replacement must prove both its own readiness and the
        // same fixed TCP listener before it can earn a commit.
        var sshWasListening = await AgentClient.ProbeTcpAsync(
            device.TailscaleIp, RemoteAdministrationProtocol.SshPort,
            TimeSpan.FromSeconds(5), cancellationToken);
        progress?.Report(
            "The signed source archive and its local attested build are ready. Scheduling the guarded Agent swap; " +
            "Tailscale, RustDesk, SSH, credentials, and routing will not be changed.");
        UpdateStatusDto activation;
        try
        {
            activation = await _agents.ActivateUpdateAsync(device, agentToken, operationId, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
        {
            // The protected request can be durable even when the Agent begins
            // its scheduled swap before HTTP has delivered the acknowledgement.
            // Do not turn that expected restart window into a generic cancelled
            // task: inspect the exact durable transaction through its terminal
            // state instead.
            progress?.Report(
                "Activation delivery is indeterminate; polling the Guardian's durable terminal state. " +
                exception.Message);
            var activationTerminal = await WaitForTerminalStatusAsync(
                device, agentToken, operationId,
                DateTimeOffset.UtcNow
                    .Add(RemoteAdministrationProtocol.UpdateCommitWindow)
                    .AddMinutes(2),
                progress, cancellationToken);
            return await CompleteSourceTransactionAsync(
                activationTerminal, device, agentToken, sourceRequest, progress, cancellationToken);
        }
        var deadline = activation.CommitDeadline ?? DateTimeOffset.UtcNow.Add(RemoteAdministrationProtocol.UpdateCommitWindow);
        var requiredHealthySamples = 3;
        var healthySamples = 0;
        Exception? lastConnectionError = null;

        while (DateTimeOffset.UtcNow < deadline.Subtract(TimeSpan.FromSeconds(15)))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            try
            {
                var status = await _agents.GetStatusAsync(device, agentToken, cancellationToken);
                var update = status.UpdateStatus;
                if (update?.OperationId == operationId && update.Phase == UpdatePhase.AwaitingCommit
                    && UpdatePackageVerifier.NormalizeVersion(status.AgentVersion) == UpdatePackageVerifier.NormalizeVersion(release.Version)
                    && status.TailscaleIp.Equals(device.TailscaleIp, StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(device.TailnetDeviceId)
                        || string.Equals(status.TailnetDeviceId, device.TailnetDeviceId, StringComparison.Ordinal))
                    && status.RustDeskReady
                    && await AgentClient.ProbeTcpAsync(device.TailscaleIp, 21118, TimeSpan.FromSeconds(5), cancellationToken)
                    && (!sshWasListening
                        || (status.SshReady
                            && status.SshPort == RemoteAdministrationProtocol.SshPort
                            && await AgentClient.ProbeTcpAsync(
                                device.TailscaleIp, RemoteAdministrationProtocol.SshPort,
                                TimeSpan.FromSeconds(5), cancellationToken))))
                {
                    healthySamples++;
                    progress?.Report($"New Agent health sample {healthySamples}/{requiredHealthySamples}: version, identity, API, and recovery channels are stable.");
                    if (healthySamples >= requiredHealthySamples) break;
                }
                else
                {
                    healthySamples = 0;
                    if (update?.Phase is UpdatePhase.RollingBack or UpdatePhase.RolledBack or UpdatePhase.Failed)
                    {
                        var rollbackTerminal = await WaitForTerminalStatusAsync(
                            device, agentToken, operationId, deadline.AddMinutes(2), progress, cancellationToken);
                        return await CompleteSourceTransactionAsync(
                            rollbackTerminal, device, agentToken, sourceRequest, progress, cancellationToken);
                    }
                }
            }
            catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
            {
                lastConnectionError = exception;
                healthySamples = 0;
                progress?.Report("Waiting for the replacement Agent; no commit will be sent until all health checks pass.");
            }
        }

        if (healthySamples < requiredHealthySamples)
        {
            progress?.Report("The replacement did not earn external health confirmation. Withholding commit so the guardian restores the previous Agent.");
            var rollbackTerminal = await WaitForTerminalStatusAsync(
                device, agentToken, operationId, deadline.AddMinutes(2), progress, cancellationToken);
            if (rollbackTerminal.Phase is UpdatePhase.RolledBack or UpdatePhase.Failed) return rollbackTerminal;
            throw new TimeoutException("The new Agent never became safely committable. The guardian was instructed by omission to roll back." +
                                       (lastConnectionError is null ? string.Empty : " Last connection error: " + lastConnectionError.Message));
        }

        progress?.Report("All independent checks passed. Sending the idempotent commit request.");
        try
        {
            await _agents.CommitUpdateAsync(device, agentToken, operationId, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
        {
            // A lost HTTP response is not proof that the commit was rejected:
            // the protected request may already be durable on the target.
            progress?.Report(
                "Commit delivery is indeterminate; polling the Guardian's durable terminal state. " + exception.Message);
        }
        var terminal = await WaitForTerminalStatusAsync(
            device, agentToken, operationId, DateTimeOffset.UtcNow.AddMinutes(2), progress, cancellationToken);
        return await CompleteSourceTransactionAsync(
            terminal, device, agentToken, sourceRequest, progress, cancellationToken);
    }

    private async Task<UpdateStatusDto> CompleteSourceTransactionAsync(
        UpdateStatusDto terminal,
        DeviceRecord device,
        string agentToken,
        SourceUpdateRequest sourceRequest,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (terminal.Phase == UpdatePhase.Committed)
            await ReconcileSourceGuardianAsync(device, agentToken, sourceRequest, progress, cancellationToken);
        return terminal;
    }

    private async Task ReconcileSourceGuardianAsync(
        DeviceRecord device,
        string agentToken,
        SourceUpdateRequest sourceRequest,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(
            $"Reconciling the locally built, attested stable Guardian from source operation {sourceRequest.OperationId:N}; " +
            "no executable bundle or arbitrary command is used.");
        var result = await _agents.ReconcileSourceGuardianAsync(
            device, agentToken, sourceRequest, cancellationToken);
        if (result.OperationId != sourceRequest.OperationId)
            throw new InvalidDataException("The Agent returned a different source Guardian operation ID.");
        var expectedVersion = UpdatePackageVerifier.NormalizeVersion(sourceRequest.TargetVersion);
        if (!UpdatePackageVerifier.NormalizeVersion(result.GuardianVersion)
            .Equals(expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The Agent reported Guardian {result.GuardianVersion} after source operation {sourceRequest.OperationId:N}.");
        var status = await _agents.GetStatusAsync(device, agentToken, cancellationToken);
        if (!UpdatePackageVerifier.NormalizeVersion(status.AgentVersion)
                .Equals(expectedVersion, StringComparison.Ordinal)
            || !UpdatePackageVerifier.NormalizeVersion(status.GuardianVersion)
                .Equals(expectedVersion, StringComparison.Ordinal)
            || !status.RustDeskReady
            || !status.TailscaleIp.Equals(device.TailscaleIp, StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(device.TailnetDeviceId)
                ? false
                : !status.TailnetDeviceId.Equals(device.TailnetDeviceId, StringComparison.Ordinal)))
            throw new InvalidDataException(
                "The post-source-maintenance Agent sample did not attest the expected Agent and Guardian, Tailnet identity, and RustDesk recovery channel.");
        progress?.Report(result.Message);
    }

    private static SourceUpdateRequest CreateSourceUpdateRequest(
        DeviceRecord device,
        OpticonUpdateRelease release,
        OpticonSourceRelease source,
        Guid operationId)
    {
        var architecture = release.Architecture.ToLowerInvariant();
        var targetRuntime = architecture switch
        {
            "x64" => "win-x64",
            "arm64" => "win-arm64",
            _ => throw new InvalidDataException("The selected source release has an unsupported device architecture.")
        };
        var deviceArchitecture = string.IsNullOrWhiteSpace(device.Architecture)
            ? "x64"
            : device.Architecture.ToLowerInvariant();
        if (release.Role != device.Role
            || !architecture.Equals(deviceArchitecture, StringComparison.Ordinal)
            || !source.Version.Equals(release.Version, StringComparison.Ordinal)
            || source.Size != release.Size
            || !source.Sha256.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)
            || !source.DownloadUri.AbsoluteUri.Equals(release.DownloadUri.AbsoluteUri, StringComparison.Ordinal)
            || !source.TargetRuntimes.Contains(targetRuntime, StringComparer.Ordinal))
            throw new InvalidDataException(
                "The selected source release does not exactly match the requested device role, architecture, and immutable archive pins.");

        var request = new SourceUpdateRequest
        {
            OperationId = operationId,
            TargetVersion = source.Version,
            Role = release.Role,
            Architecture = architecture,
            DownloadUrl = source.DownloadUri.AbsoluteUri,
            SourceFile = source.File,
            SourceSize = source.Size,
            SourceSha256 = source.Sha256,
            SourceManifestSha256 = source.SourceManifestSha256,
            SourceManifestKeyId = source.SourceManifestKeyId,
            SigningProfile = source.SigningProfile,
            ProductSignerThumbprint = source.ProductSignerThumbprint,
            SdkVersion = source.SdkVersion,
            RuntimeVersion = source.RuntimeVersion,
            TargetRuntime = targetRuntime
        };
        SourceUpdatePackageVerifier.ValidateRequest(request);
        return request;
    }

    private static Version ParseInstalledGuardianVersion(string value)
    {
        try { return UpdatePackageVerifier.ParseVersion(value); }
        catch (InvalidDataException) { return new Version(0, 0, 0); }
    }

    public async Task<UpdateStatusDto> ObserveMaintenanceBootstrapAsync(
        DeviceRecord device,
        string agentToken,
        OpticonUpdateRelease release,
        Guid operationId,
        bool sshWasListening,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!release.RequiresMaintenanceBootstrap)
            throw new InvalidOperationException("External bootstrap observation is only valid for a legacy Agent release.");
        if (operationId == Guid.Empty)
            throw new InvalidDataException("Maintenance requires a non-empty command-center operation ID.");
        if (!AgentClient.IsTailscaleIp(device.TailscaleIp))
            throw new InvalidOperationException("The selected device has no valid Tailscale address.");
        if (string.IsNullOrWhiteSpace(device.TailnetDeviceId))
            throw new InvalidOperationException("The selected device has no exact Tailnet identity.");
        if (device.State != DeviceConnectionState.Online)
            throw new InvalidOperationException("The legacy Agent must be live before maintenance observation starts.");

        var expectedVersion = UpdatePackageVerifier.NormalizeVersion(release.Version);
        if (expectedVersion.Length == 0)
            throw new InvalidDataException("The selected maintenance release has no valid version.");
        var discoveryDeadline = DateTimeOffset.UtcNow.AddMinutes(30);
        var maximumAdvertisedWindow =
            RemoteAdministrationProtocol.UpdateCommitWindow.Add(TimeSpan.FromSeconds(30));
        DateTimeOffset? commitCutoff = null;
        var sawExpectedOperation = false;
        var healthySamples = 0;
        string lastFailure = "The exact maintenance operation has not appeared.";
        progress?.Report(
            $"Watching {device.Name} for maintenance operation {operationId:N}; no commit can be sent until three exact external samples pass.");

        while (DateTimeOffset.UtcNow < (commitCutoff ?? discoveryDeadline))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            try
            {
                var status = await _agents.GetStatusAsync(device, agentToken, cancellationToken);
                var update = status.UpdateStatus;
                if (update?.OperationId != operationId)
                {
                    healthySamples = 0;
                    lastFailure = "The authenticated Agent has not reported the copied operation ID.";
                    continue;
                }

                sawExpectedOperation = true;
                if (!update.MaintenanceBootstrap)
                    throw new InvalidDataException("The copied operation ID is not marked as a maintenance bootstrap.");
                if (!UpdatePackageVerifier.NormalizeVersion(update.TargetVersion)
                    .Equals(expectedVersion, StringComparison.Ordinal))
                    throw new InvalidDataException("The copied operation ID targets a different Opticon release.");
                if (update.CommitDeadline is not { } remoteDeadline || update.StartedAt == default)
                    throw new InvalidDataException("The maintenance candidate did not advertise a bounded commit deadline.");
                var advertisedWindow = remoteDeadline - update.StartedAt;
                if (advertisedWindow <= TimeSpan.Zero || advertisedWindow > maximumAdvertisedWindow)
                    throw new InvalidDataException("The maintenance candidate advertised an unsafe commit window.");

                if (update.Phase is UpdatePhase.RolledBack or UpdatePhase.Failed)
                    return update;
                if (update.Phase == UpdatePhase.RollingBack)
                    return await WaitForTerminalStatusAsync(
                        device, agentToken, operationId, DateTimeOffset.UtcNow.AddMinutes(2),
                        progress, cancellationToken,
                        requireMaintenanceBootstrap: true,
                        expectedTargetVersion: expectedVersion,
                        allowCommitted: false);
                if (update.Phase == UpdatePhase.Committed)
                    throw new InvalidDataException(
                        "The maintenance operation was committed before this command center authorized it.");
                if (status.ServerTime == default)
                    throw new InvalidDataException("The authenticated status omitted the target clock.");

                var remaining = remoteDeadline - status.ServerTime;
                if (remaining <= TimeSpan.Zero || remaining > maximumAdvertisedWindow)
                {
                    healthySamples = 0;
                    lastFailure = "The target commit deadline is no longer safely usable.";
                    break;
                }
                var translatedCutoff =
                    DateTimeOffset.UtcNow.Add(remaining).Subtract(TimeSpan.FromSeconds(15));
                if (commitCutoff is null || translatedCutoff < commitCutoff.Value)
                    commitCutoff = translatedCutoff;

                if (update.Phase != UpdatePhase.AwaitingCommit)
                {
                    healthySamples = 0;
                    lastFailure = $"The exact operation is still {update.Phase}.";
                    continue;
                }

                var exactIdentity =
                    UpdatePackageVerifier.NormalizeVersion(status.AgentVersion)
                        .Equals(expectedVersion, StringComparison.Ordinal)
                    && string.Equals(status.Architecture, release.Architecture, StringComparison.Ordinal)
                    && status.UpdateProtocolVersion == RemoteAdministrationProtocol.UpdateVersion
                    && string.Equals(status.TailscaleIp, device.TailscaleIp, StringComparison.Ordinal)
                    && string.Equals(status.TailnetDeviceId, device.TailnetDeviceId, StringComparison.Ordinal);
                var recoveryReady = exactIdentity
                    && status.RustDeskReady
                    && await AgentClient.ProbeTcpAsync(
                        device.TailscaleIp, 21118, TimeSpan.FromSeconds(5), cancellationToken)
                    && (!sshWasListening
                        || (status.SshReady
                            && status.SshPort == RemoteAdministrationProtocol.SshPort
                            && await AgentClient.ProbeTcpAsync(
                                device.TailscaleIp, RemoteAdministrationProtocol.SshPort,
                                TimeSpan.FromSeconds(5), cancellationToken)));
                if (!recoveryReady)
                {
                    healthySamples = 0;
                    lastFailure =
                        "The latest authenticated sample did not exactly match version, architecture, protocol, IP, Tailnet identity, RustDesk, and snapshotted SSH.";
                    continue;
                }

                healthySamples++;
                progress?.Report(
                    $"Maintenance external health sample {healthySamples}/3 passed for exact operation {operationId:N}.");
                if (healthySamples >= 3) break;
            }
            catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
            {
                healthySamples = 0;
                lastFailure = exception.Message;
                progress?.Report(
                    "Waiting for the exact maintenance candidate; commit remains withheld.");
            }
        }

        if (healthySamples < 3
            || commitCutoff is null
            || DateTimeOffset.UtcNow >= commitCutoff.Value)
        {
            var reason = sawExpectedOperation
                ? "The exact candidate did not provide three consecutive external samples before its safe commit cutoff."
                : "The exact candidate did not appear during the 30-minute observation window.";
            throw new TimeoutException(
                reason + " No commit was sent; if activation began, the Guardian will roll back by omission. " + lastFailure);
        }

        progress?.Report(
            "Three authenticated external samples passed. Sending the exact idempotent maintenance commit.");
        try
        {
            await _agents.CommitUpdateAsync(device, agentToken, operationId, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
        {
            progress?.Report(
                "Maintenance commit delivery is indeterminate; polling the exact durable terminal state. " +
                exception.Message);
        }

        return await WaitForTerminalStatusAsync(
            device, agentToken, operationId, DateTimeOffset.UtcNow.AddMinutes(2),
            progress, cancellationToken,
            requireMaintenanceBootstrap: true,
            expectedTargetVersion: expectedVersion);
    }

    private async Task<UpdateStatusDto> WaitForTerminalStatusAsync(
        DeviceRecord device,
        string token,
        Guid operationId,
        DateTimeOffset deadline,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool requireMaintenanceBootstrap = false,
        string? expectedTargetVersion = null,
        bool allowCommitted = true)
    {
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await _agents.GetUpdateStatusAsync(device, token, cancellationToken);
                if (status.OperationId != operationId)
                    throw new InvalidDataException("The target returned a different update transaction.");
                if (requireMaintenanceBootstrap && !status.MaintenanceBootstrap)
                    throw new InvalidDataException("The target returned a non-maintenance update transaction.");
                if (expectedTargetVersion is not null
                    && !UpdatePackageVerifier.NormalizeVersion(status.TargetVersion)
                        .Equals(expectedTargetVersion, StringComparison.Ordinal))
                    throw new InvalidDataException("The target returned a different update release.");
                switch (status.Phase)
                {
                    case UpdatePhase.Committed when !allowCommitted:
                        throw new InvalidDataException(
                            "The maintenance operation committed without this command center's authorization.");
                    case UpdatePhase.Committed:
                        progress?.Report($"Opticon Agent {status.TargetVersion} committed. The prior Agent remains available as the local rollback copy.");
                        return status;
                    case UpdatePhase.RolledBack:
                        progress?.Report("The guardian restored the previous Agent; Tailscale and remote control were left untouched.");
                        return status;
                    case UpdatePhase.Failed:
                        return status;
                }
                last = null;
            }
            catch (Exception exception) when (IsRecoverableRemoteFailure(exception, cancellationToken))
            {
                last = exception;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        throw new TimeoutException("The target did not report a terminal update state." +
                                   (last is null ? string.Empty : " Last connection error: " + last.Message));
    }

    private static bool IsRecoverableRemoteFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception is not InvalidDataException;
}
