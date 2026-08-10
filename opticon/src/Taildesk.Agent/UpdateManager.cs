using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed class UpdateManager
{
    private const int MaximumDownloadAttempts = 4;
    private readonly AgentState _state;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SourceUpdateBuildRunner _sourceBuild = new();
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        CheckCertificateRevocationList = true,
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public UpdateManager(AgentState state) => _state = state;

    public static string CurrentVersion =>
        UpdatePackageVerifier.NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");

    public static string CurrentArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
    };

    public UpdateStatusDto? GetStatus()
    {
        try { return UpdateJournalPersistence.Load()?.ToStatus(); }
        catch (Exception exception)
        {
            return new UpdateStatusDto
            {
                Phase = UpdatePhase.Failed,
                CurrentVersion = CurrentVersion,
                Message = "The protected update journal could not be read: " + exception.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public async Task<string> EnsureHealthTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_state.Config.UpdateHealthTokenProtected))
            return UpdateHealthTokenStore.Load(
                _state.Config.UpdateHealthTokenProtected, _state.Config.DeviceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsurePrivateUpdateDirectoryAsync(cancellationToken);
            return await UpdateHealthTokenStore.LoadOrCreateSidecarAsync(
                _state.Config.UpdateHealthTokenProtected,
                _state.Config.DeviceId,
                cancellationToken: cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public object GetInternalHealth()
    {
        var journal = UpdateJournalPersistence.Load();
        return new
        {
            service = "taildesk-agent",
            status = "ok",
            agentVersion = CurrentVersion,
            deviceId = _state.Config.DeviceId,
            bindAddress = _state.Config.BindAddress,
            operationId = journal?.OperationId,
            updatePhase = journal?.Phase.ToString() ?? UpdatePhase.None.ToString(),
            rustDeskReady = IsRustDeskReady()
        };
    }

    public async Task<UpdateStatusDto> PrepareAsync(OpticonUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        UpdateJournal? journal = null;
        IDisposable? coordinationLease = null;
        try
        {
            UpdatePackageVerifier.ValidateRequest(request);
            ValidateTarget(request);
            if (!IsRustDeskReady())
                throw new InvalidOperationException("The RustDesk recovery channel is not healthy on TCP 21118. Opticon refuses to stage a remote update without remote-control fallback.");

            var guardian = GuardianExecutable();
            await ProductSigning.VerifyAuthenticodeAsync(guardian, cancellationToken);
            EnsureFreeSpace(request.PackageSize);
            await EnsurePrivateUpdateDirectoryAsync(cancellationToken);

            // Hold this OS-released lease through every durable staging phase,
            // including any failure save, so a boot/manual Guardian invocation
            // cannot read or act on a half-replaced transaction.
            coordinationLease = await UpdateJournalCoordination.AcquireAsync(
                TimeSpan.FromMinutes(20), cancellationToken);
            var existing = UpdateJournalPersistence.Load();
            if (existing is not null && existing.OperationId == request.OperationId
                && existing.Phase is UpdatePhase.Ready or UpdatePhase.ActivationScheduled or UpdatePhase.Activating or UpdatePhase.AwaitingCommit or UpdatePhase.Committed)
                return existing.ToStatus();
            if (existing is not null && existing.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying
                or UpdatePhase.ActivationScheduled or UpdatePhase.Activating or UpdatePhase.AwaitingCommit or UpdatePhase.RollingBack)
                throw new InvalidOperationException($"Update {existing.OperationId:N} is already {existing.Phase}.");

            var operationDirectory = Path.Combine(AppPaths.UpdateDataDirectory, request.OperationId.ToString("N"));
            MachineStorageSecurity.EnsureRestrictedDirectoryTree(
                AppPaths.MachineDataDirectory, operationDirectory);
            var packagePath = Path.Combine(operationDirectory, "package.zip");
            var stageAgent = Path.Combine(operationDirectory, "staged-agent");
            journal = new UpdateJournal
            {
                OperationId = request.OperationId,
                Phase = UpdatePhase.Downloading,
                CurrentVersion = CurrentVersion,
                TargetVersion = UpdatePackageVerifier.NormalizeVersion(request.TargetVersion),
                Role = request.Role,
                Architecture = request.Architecture.ToLowerInvariant(),
                PackagePath = packagePath,
                PackageSha256 = request.PackageSha256.ToLowerInvariant(),
                PackageSize = request.PackageSize,
                StagedAgentDirectory = stageAgent,
                CandidateDirectory = AppPaths.AgentInstallDirectory + ".candidate-" + request.OperationId.ToString("N"),
                RollbackDirectory = AppPaths.AgentInstallDirectory + ".rollback-" + request.OperationId.ToString("N"),
                FailedCandidateDirectory = AppPaths.AgentInstallDirectory + ".failed-" + request.OperationId.ToString("N"),
                BindAddress = _state.Config.BindAddress,
                AgentProcessId = Environment.ProcessId,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Downloading the immutable, hash-pinned Opticon release. The active Agent and recovery channels remain untouched."
            };
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            await DownloadWithResumeAsync(new Uri(request.DownloadUrl), packagePath, request.PackageSize, cancellationToken);

            journal.Phase = UpdatePhase.Verifying;
            journal.Message = "Verifying the package hash, signed inner manifest, publisher, role, architecture, and binary version.";
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            var manifest = await UpdatePackageVerifier.VerifyAndExtractAgentAsync(packagePath, stageAgent, request, cancellationToken);
            ValidateGuardianCompatibility(guardian, manifest.MinimumGuardianVersion);

            journal.Phase = UpdatePhase.Ready;
            journal.Message = "Verified and staged. Activation still requires an explicit request and will roll back unless the command center confirms health.";
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            return journal.ToStatus();
        }
        catch (OperationCanceledException) when (journal is not null)
        {
            journal.Phase = UpdatePhase.Failed;
            journal.Message = "Update staging was canceled or timed out before the installed Agent changed.";
            try { await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: CancellationToken.None); }
            catch { }
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (journal is not null)
            {
                journal.Phase = UpdatePhase.Failed;
                journal.Message = "Update staging failed without changing the installed Agent: " + exception.Message;
                try { await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: CancellationToken.None); } catch { }
            }
            throw;
        }
        finally
        {
            coordinationLease?.Dispose();
            _gate.Release();
        }
    }

    /// <summary>
    /// Stages a locally built source release.  It intentionally uses a distinct
    /// API and journal schema: a Guardian that only knows executable bundles
    /// must reject it before any installed Agent files are changed.
    /// </summary>
    public async Task<UpdateStatusDto> PrepareSourceAsync(
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        UpdateJournal? journal = null;
        IDisposable? coordinationLease = null;
        try
        {
            SourceUpdatePackageVerifier.ValidateRequest(request);
            ValidateSourceTarget(request);
            if (!IsRustDeskReady())
                throw new InvalidOperationException(
                    "The RustDesk recovery channel is not healthy on TCP 21118. Opticon refuses to stage a source update without remote-control fallback.");

            var guardian = GuardianExecutable();
            await ProductSigning.VerifyAuthenticodeAsync(guardian, cancellationToken);
            ValidateSourceGuardianCompatibility(guardian);
            EnsureFreeSpaceForSourceBuild(request.SourceSize);
            await EnsurePrivateUpdateDirectoryAsync(cancellationToken);

            coordinationLease = await UpdateJournalCoordination.AcquireAsync(
                TimeSpan.FromMinutes(60), cancellationToken);
            var existing = UpdateJournalPersistence.Load();
            if (existing is not null && existing.OperationId == request.OperationId
                && existing.DeliveryMode == UpdateDeliveryMode.SourceArchive
                && existing.Phase is UpdatePhase.Ready or UpdatePhase.ActivationScheduled or UpdatePhase.Activating
                    or UpdatePhase.AwaitingCommit or UpdatePhase.Committed)
                return existing.ToStatus();
            if (existing is not null && existing.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying
                or UpdatePhase.ActivationScheduled or UpdatePhase.Activating or UpdatePhase.AwaitingCommit or UpdatePhase.RollingBack)
                throw new InvalidOperationException($"Update {existing.OperationId:N} is already {existing.Phase}.");

            var operationDirectory = Path.Combine(AppPaths.UpdateDataDirectory, request.OperationId.ToString("N"));
            var sourceDirectory = Path.Combine(operationDirectory, "source");
            var sourceBuildDirectory = Path.Combine(operationDirectory, "source-build");
            MachineStorageSecurity.EnsureRestrictedDirectoryTree(
                AppPaths.MachineDataDirectory, operationDirectory, sourceDirectory, sourceBuildDirectory);
            var archivePath = Path.Combine(operationDirectory, "package.zip");
            var attestationPath = Path.Combine(operationDirectory, "source-build-attestation.json");
            var stageAgent = Path.Combine(sourceBuildDirectory, "Payload", "Agent");
            journal = new UpdateJournal
            {
                SchemaVersion = 2,
                DeliveryMode = UpdateDeliveryMode.SourceArchive,
                OperationId = request.OperationId,
                Phase = UpdatePhase.Downloading,
                CurrentVersion = CurrentVersion,
                TargetVersion = UpdatePackageVerifier.NormalizeVersion(request.TargetVersion),
                Role = request.Role,
                Architecture = request.Architecture.ToLowerInvariant(),
                PackagePath = archivePath,
                PackageSha256 = request.SourceSha256.ToLowerInvariant(),
                PackageSize = request.SourceSize,
                SourceDownloadUrl = request.DownloadUrl,
                SourceFile = request.SourceFile,
                SourceManifestSha256 = request.SourceManifestSha256.ToLowerInvariant(),
                SourceManifestKeyId = request.SourceManifestKeyId,
                SigningProfile = request.SigningProfile,
                ProductSignerThumbprint = request.ProductSignerThumbprint,
                SdkVersion = request.SdkVersion,
                RuntimeVersion = request.RuntimeVersion,
                TargetRuntime = request.TargetRuntime,
                SourceBuildOutputDirectory = sourceBuildDirectory,
                SourceBuildAttestationPath = attestationPath,
                StagedAgentDirectory = stageAgent,
                CandidateDirectory = AppPaths.AgentInstallDirectory + ".candidate-" + request.OperationId.ToString("N"),
                RollbackDirectory = AppPaths.AgentInstallDirectory + ".rollback-" + request.OperationId.ToString("N"),
                FailedCandidateDirectory = AppPaths.AgentInstallDirectory + ".failed-" + request.OperationId.ToString("N"),
                BindAddress = _state.Config.BindAddress,
                AgentProcessId = Environment.ProcessId,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Downloading the immutable hash-pinned Opticon source archive. The active Agent and recovery channels remain untouched."
            };
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            await DownloadWithResumeAsync(new Uri(request.DownloadUrl), archivePath, request.SourceSize, cancellationToken);

            journal.Phase = UpdatePhase.Verifying;
            journal.Message = "Verifying the signed source manifest and every source file before the exact local SDK build.";
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            var manifest = await SourceUpdatePackageVerifier.VerifyAndExtractAsync(
                archivePath, sourceDirectory, request, cancellationToken);
            journal.Message = $"Building Agent and Guardian locally with exact .NET SDK {request.SdkVersion}; the installed Agent remains untouched.";
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            await _sourceBuild.BuildAsync(
                manifest, request, operationDirectory, sourceDirectory, archivePath,
                sourceBuildDirectory, attestationPath, cancellationToken);

            journal.Phase = UpdatePhase.Ready;
            journal.Message = "Verified source archive and sealed local build are staged. Activation still requires an explicit request and will roll back unless the command center confirms health.";
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
            return journal.ToStatus();
        }
        catch (OperationCanceledException) when (journal is not null)
        {
            journal.Phase = UpdatePhase.Failed;
            journal.Message = "Source update staging was canceled or timed out before the installed Agent changed.";
            try { await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: CancellationToken.None); }
            catch { }
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (journal is not null)
            {
                journal.Phase = UpdatePhase.Failed;
                journal.Message = "Source update staging failed without changing the installed Agent: " + exception.Message;
                try { await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: CancellationToken.None); }
                catch { }
            }
            throw;
        }
        finally
        {
            coordinationLease?.Dispose();
            _gate.Release();
        }
    }

    /// <summary>
    /// Promotes the Guardian built from the same already-attested source
    /// archive after the corresponding Agent has committed.  No executable
    /// bundle is downloaded as a fallback.
    /// </summary>
    public async Task<GuardianMaintenanceStatusDto> ReconcileSourceGuardianAsync(
        SourceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SourceUpdatePackageVerifier.ValidateRequest(request);
            ValidateSourceGuardianTarget(request);
            if (!IsRustDeskReady())
                throw new InvalidOperationException(
                    "The RustDesk recovery channel is not healthy on TCP 21118. Opticon refuses to replace its stable Guardian without remote-control fallback.");
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == RemoteAdministrationProtocol.SshPort))
                throw new InvalidOperationException("Close the active Opticon SSH lease before updating the stable Guardian.");

            var journal = RequireCommittedSourceJournal(request);
            var guardian = GuardianExecutable();
            await ProductSigning.VerifyAuthenticodeAsync(guardian, cancellationToken);
            var previousVersion = ReadExecutableVersion(guardian);
            await EnsurePrivateUpdateDirectoryAsync(cancellationToken);

            var manifest = await SourceUpdatePackageVerifier.VerifyArchiveAsync(
                journal.PackagePath, request, cancellationToken);
            var attestation = await SourceUpdatePackageVerifier.VerifyBuiltOutputAsync(
                journal.SourceBuildAttestationPath, journal.SourceBuildOutputDirectory, request, cancellationToken);
            var stagedGuardian = Path.Combine(journal.SourceBuildOutputDirectory, "Payload", "UpdateGuardian");
            await ProductSigning.VerifyAuthenticodeAsync(
                Path.Combine(stagedGuardian, "Taildesk.UpdateGuardian.exe"), cancellationToken);
            if (UpdatePackageVerifier.ParseVersion(manifest.Version)
                < UpdatePackageVerifier.ParseVersion(SourceUpdateProtocol.MinimumGuardianVersion))
                throw new InvalidDataException("The source-built Guardian does not meet the source-update Guardian protocol floor.");

            await StableGuardianMaintenance.ReconcileSignedReleaseAsync(
                stagedGuardian, AppPaths.UpdateGuardianInstallDirectory, cancellationToken);
            await ProductSigning.VerifyAuthenticodeAsync(guardian, cancellationToken);
            var installedVersion = ReadExecutableVersion(guardian);
            var expectedVersion = UpdatePackageVerifier.NormalizeVersion(request.TargetVersion);
            if (!installedVersion.Equals(expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Source Guardian maintenance retained {installedVersion}, not the locally built release {expectedVersion}.");

            var watchdog = await ProcessRunner.RunAsync(
                guardian,
                [RemoteAdministrationProtocol.GuardianWatchdogArgument],
                TimeSpan.FromSeconds(30), cancellationToken,
                environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
            if (!watchdog.Succeeded)
                throw new InvalidOperationException(
                    "The promoted source-built Guardian did not pass its SYSTEM watchdog startup probe: " +
                    string.Join(" ", watchdog.StandardOutput.Trim(), watchdog.StandardError.Trim()).Trim());

            return new GuardianMaintenanceStatusDto
            {
                OperationId = request.OperationId,
                PreviousVersion = previousVersion,
                GuardianVersion = installedVersion,
                Changed = !installedVersion.Equals(previousVersion, StringComparison.Ordinal),
                Message = installedVersion.Equals(previousVersion, StringComparison.Ordinal)
                    ? $"The installed source-built Guardian {installedVersion} already satisfies this release."
                    : $"The source-built stable Guardian was atomically updated from {previousVersion} to {installedVersion} and passed its watchdog startup probe."
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GuardianMaintenanceStatusDto> ReconcileGuardianAsync(
        OpticonUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            UpdatePackageVerifier.ValidateRequest(request);
            ValidateGuardianTarget(request);
            if (!IsRustDeskReady())
                throw new InvalidOperationException(
                    "The RustDesk recovery channel is not healthy on TCP 21118. Opticon refuses to replace its stable Guardian without remote-control fallback.");
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == RemoteAdministrationProtocol.SshPort))
                throw new InvalidOperationException("Close the active Opticon SSH lease before updating the stable Guardian.");

            var installedGuardian = GuardianExecutable();
            await ProductSigning.VerifyAuthenticodeAsync(installedGuardian, cancellationToken);
            var previousVersion = ReadExecutableVersion(installedGuardian);
            await EnsurePrivateUpdateDirectoryAsync(cancellationToken);
            EnsureFreeSpace(request.PackageSize);

            var operationDirectory = Path.Combine(
                AppPaths.UpdateDataDirectory,
                "guardian-" + request.OperationId.ToString("N"));
            MachineStorageSecurity.EnsureRestrictedDirectoryTree(
                AppPaths.MachineDataDirectory, operationDirectory);
            var packagePath = ReusableCommittedPackage(request) ?? Path.Combine(operationDirectory, "package.zip");
            var downloadedForGuardian = !File.Exists(packagePath);
            if (downloadedForGuardian)
                await DownloadWithResumeAsync(new Uri(request.DownloadUrl), packagePath, request.PackageSize, cancellationToken);

            var stagedGuardian = Path.Combine(operationDirectory, "staged-guardian");
            var manifest = await UpdatePackageVerifier.VerifyAndExtractGuardianAsync(
                packagePath, stagedGuardian, request, cancellationToken);
            if (UpdatePackageVerifier.ParseVersion(manifest.Version)
                < UpdatePackageVerifier.ParseVersion(manifest.MinimumGuardianVersion))
                throw new InvalidDataException("The signed release Guardian is older than its own declared minimum Guardian version.");

            await StableGuardianMaintenance.ReconcileSignedReleaseAsync(
                stagedGuardian,
                AppPaths.UpdateGuardianInstallDirectory,
                cancellationToken);

            await ProductSigning.VerifyAuthenticodeAsync(installedGuardian, cancellationToken);
            var installedVersion = ReadExecutableVersion(installedGuardian);
            var expectedVersion = UpdatePackageVerifier.NormalizeVersion(request.TargetVersion);
            if (UpdatePackageVerifier.ParseVersion(installedVersion)
                < UpdatePackageVerifier.ParseVersion(expectedVersion))
                throw new InvalidDataException(
                    $"Stable Guardian maintenance retained {installedVersion}, which is older than signed release {expectedVersion}.");

            var watchdog = await ProcessRunner.RunAsync(
                installedGuardian,
                [RemoteAdministrationProtocol.GuardianWatchdogArgument],
                TimeSpan.FromSeconds(30),
                cancellationToken,
                environment: BuildPrivilegedEnvironment(),
                clearEnvironment: true);
            if (!watchdog.Succeeded)
                throw new InvalidOperationException(
                    "The promoted signed Guardian did not pass its SYSTEM watchdog startup probe: " +
                    string.Join(" ", watchdog.StandardOutput.Trim(), watchdog.StandardError.Trim()).Trim());

            return new GuardianMaintenanceStatusDto
            {
                OperationId = request.OperationId,
                PreviousVersion = previousVersion,
                GuardianVersion = installedVersion,
                Changed = !installedVersion.Equals(previousVersion, StringComparison.Ordinal),
                Message = installedVersion.Equals(previousVersion, StringComparison.Ordinal)
                    ? $"The installed signed Guardian {installedVersion} already satisfies this release."
                    : $"The signed stable Guardian was atomically updated from {previousVersion} to {installedVersion} and passed its watchdog startup probe."
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdateStatusDto> ActivateAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            UpdateJournal journal;
            using (await UpdateJournalCoordination.AcquireAsync(TimeSpan.FromMinutes(20), cancellationToken))
            {
                journal = RequireJournal(operationId);
                if (journal.Phase is UpdatePhase.Activating or UpdatePhase.AwaitingCommit or UpdatePhase.Committed)
                    return journal.ToStatus();
                if (journal.Phase is not UpdatePhase.Ready and not UpdatePhase.ActivationScheduled)
                    throw new InvalidOperationException($"Update {operationId:N} is {journal.Phase}, not ready for activation.");
                if (!IsRustDeskReady())
                    throw new InvalidOperationException("The RustDesk recovery channel failed immediately before activation. No installed files were changed.");
                if (!File.Exists(Path.Combine(journal.StagedAgentDirectory, "Taildesk.Agent.exe")))
                    throw new FileNotFoundException("The verified staged Agent is missing.");

                if (journal.Phase == UpdatePhase.Ready)
                {
                    MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.UpdateCommitRequestFile);
                    journal.Phase = UpdatePhase.ActivationScheduled;
                    journal.GuardianClaimedAt = null;
                    journal.SshWasListening = false;
                    journal.ActivateAfter = DateTimeOffset.UtcNow.AddSeconds(12);
                    journal.CommitDeadline = journal.ActivateAfter.Value.Add(RemoteAdministrationProtocol.UpdateCommitWindow);
                    journal.AgentProcessId = Environment.ProcessId;
                    journal.Message = "Activation scheduled. The guardian will restore the previous Agent unless the command center confirms the new Agent and recovery channels.";
                    await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
                }
            }

            try
            {
                // An idempotent retry must re-kick a transaction that is still
                // ActivationScheduled; a bare return can strand the candidate.
                await RunGuardianTaskAsync(
                    cancellationToken, scheduledOperationId: operationId);
            }
            catch (Exception startFailure)
            {
                try
                {
                    await RestoreReadyAfterGuardianStartFailureAsync(operationId, CancellationToken.None);
                }
                catch (Exception restorationFailure)
                {
                    throw new AggregateException(
                        "Guardian start failed and Opticon could not durably restore the staged update to Ready. The installed Agent was not intentionally changed.",
                        startFailure, restorationFailure);
                }
                throw;
            }

            var durable = UpdateJournalPersistence.Load();
            return durable?.OperationId == operationId ? durable.ToStatus() : journal.ToStatus();
        }
        finally { _gate.Release(); }
    }

    public async Task<UpdateStatusDto> RequestCommitAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var journal = RequireJournal(operationId);
            if (journal.Phase == UpdatePhase.Committed) return journal.ToStatus();
            if (journal.Phase != UpdatePhase.AwaitingCommit)
                throw new InvalidOperationException($"Update {operationId:N} is {journal.Phase}, not awaiting health confirmation.");
            if (journal.CommitDeadline is not { } deadline || DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("The update commit window has closed; the guardian will restore the previous Agent.");
            if (!CurrentVersion.Equals(journal.TargetVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The running Agent version does not match the staged release.");
            if (!IsRustDeskReady())
                throw new InvalidOperationException("RustDesk is not healthy, so the new Agent will not be committed.");
            await UpdateJournalPersistence.RequestCommitAsync(operationId, cancellationToken);
            var durable = await RunGuardianTaskForCommitAsync(operationId, cancellationToken);
            return durable.ToStatus();
        }
        finally { _gate.Release(); }
    }

    public static bool IsRustDeskReady()
    {
        try
        {
            return Process.GetProcessesByName("rustdesk").Length > 0
                   && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 21118);
        }
        catch { return false; }
    }

    private void ValidateTarget(OpticonUpdateRequest request)
    {
        if (request.Role != _state.Config.Role) throw new InvalidOperationException("The update package role does not match this device.");
        if (!request.Architecture.Equals(CurrentArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The update is for {request.Architecture}, but this device is {CurrentArchitecture}.");
        var current = UpdatePackageVerifier.ParseVersion(CurrentVersion);
        var target = UpdatePackageVerifier.ParseVersion(request.TargetVersion);
        if (target <= current) throw new InvalidOperationException($"Opticon {request.TargetVersion} is not newer than installed version {CurrentVersion}.");
        if (!RemoteAdministrationProtocol.IsTailscaleIpv4(_state.Config.BindAddress))
            throw new InvalidOperationException("The Agent is not bound to a valid Tailscale IPv4 address.");
    }

    private void ValidateSourceTarget(SourceUpdateRequest request)
    {
        if (request.Role != _state.Config.Role)
            throw new InvalidOperationException("The source update role does not match this device.");
        if (!request.Architecture.Equals(CurrentArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The source update is for {request.Architecture}, but this device is {CurrentArchitecture}.");
        var current = UpdatePackageVerifier.ParseVersion(CurrentVersion);
        var target = UpdatePackageVerifier.ParseVersion(request.TargetVersion);
        if (target <= current)
            throw new InvalidOperationException(
                $"Opticon source release {request.TargetVersion} is not newer than installed version {CurrentVersion}.");
        if (!RemoteAdministrationProtocol.IsTailscaleIpv4(_state.Config.BindAddress))
            throw new InvalidOperationException("The Agent is not bound to a valid Tailscale IPv4 address.");
    }

    private void ValidateGuardianTarget(OpticonUpdateRequest request)
    {
        if (request.Role != _state.Config.Role)
            throw new InvalidOperationException("The Guardian release role does not match this device.");
        if (!request.Architecture.Equals(CurrentArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The Guardian release is for {request.Architecture}, but this device is {CurrentArchitecture}.");
        var currentAgent = UpdatePackageVerifier.ParseVersion(CurrentVersion);
        var target = UpdatePackageVerifier.ParseVersion(request.TargetVersion);
        if (target != currentAgent)
            throw new InvalidOperationException(
                $"Update the Agent to {request.TargetVersion} before reconciling the same signed Guardian; the running Agent is {CurrentVersion}.");
        if (!RemoteAdministrationProtocol.IsTailscaleIpv4(_state.Config.BindAddress))
            throw new InvalidOperationException("The Agent is not bound to a valid Tailscale IPv4 address.");
    }

    private void ValidateSourceGuardianTarget(SourceUpdateRequest request)
    {
        if (request.Role != _state.Config.Role)
            throw new InvalidOperationException("The source Guardian role does not match this device.");
        if (!request.Architecture.Equals(CurrentArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The source Guardian is for {request.Architecture}, but this device is {CurrentArchitecture}.");
        var current = UpdatePackageVerifier.ParseVersion(CurrentVersion);
        var target = UpdatePackageVerifier.ParseVersion(request.TargetVersion);
        if (target != current)
            throw new InvalidOperationException(
                $"Update the Agent to {request.TargetVersion} before reconciling its source-built Guardian; the running Agent is {CurrentVersion}.");
        if (!RemoteAdministrationProtocol.IsTailscaleIpv4(_state.Config.BindAddress))
            throw new InvalidOperationException("The Agent is not bound to a valid Tailscale IPv4 address.");
    }

    private static string? ReusableCommittedPackage(OpticonUpdateRequest request)
    {
        try
        {
            var journal = UpdateJournalPersistence.Load();
            if (journal?.Phase != UpdatePhase.Committed
                || journal.OperationId == Guid.Empty
                || !journal.TargetVersion.Equals(
                    UpdatePackageVerifier.NormalizeVersion(request.TargetVersion), StringComparison.Ordinal)
                || journal.PackageSize != request.PackageSize
                || !journal.PackageSha256.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(journal.PackagePath))
                return null;
            return journal.PackagePath;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadExecutableVersion(string path) =>
        UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty);

    private static void ValidateGuardianCompatibility(string guardian, string minimumVersion)
    {
        var installedText = UpdatePackageVerifier.NormalizeVersion(FileVersionInfo.GetVersionInfo(guardian).ProductVersion ?? string.Empty);
        if (UpdatePackageVerifier.ParseVersion(installedText) < UpdatePackageVerifier.ParseVersion(minimumVersion))
            throw new InvalidOperationException($"This release requires update guardian {minimumVersion} or newer; installed is {installedText}.");
    }

    private static void ValidateSourceGuardianCompatibility(string guardian)
    {
        var installed = ReadExecutableVersion(guardian);
        if (UpdatePackageVerifier.ParseVersion(installed)
            < UpdatePackageVerifier.ParseVersion(SourceUpdateProtocol.MinimumGuardianVersion))
            throw new InvalidOperationException(
                $"Source updates require Guardian {SourceUpdateProtocol.MinimumGuardianVersion} or newer; installed is {installed}. Use the source-only fresh installer or source Guardian maintenance first.");
    }

    private static void EnsureFreeSpaceForSourceBuild(long sourceSize)
    {
        var root = Path.GetPathRoot(AppPaths.UpdateDataDirectory)
                   ?? throw new InvalidOperationException("The update drive could not be determined.");
        var drive = new DriveInfo(root);
        var currentSize = Directory.Exists(AppPaths.AgentInstallDirectory)
            ? Directory.EnumerateFiles(AppPaths.AgentInstallDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;
        var required = checked(sourceSize * 4 + currentSize * 2 + 2L * 1024 * 1024 * 1024);
        if (!drive.IsReady || drive.AvailableFreeSpace < required)
            throw new IOException(
                $"The local source update requires {required / (1024 * 1024)} MiB for the verified archive, offline build, candidate, and rollback; only {drive.AvailableFreeSpace / (1024 * 1024)} MiB is available.");
    }

    private static UpdateJournal RequireCommittedSourceJournal(SourceUpdateRequest request)
    {
        var journal = UpdateJournalPersistence.Load()
                      ?? throw new InvalidOperationException("No source update transaction is available for Guardian maintenance.");
        if (journal.SchemaVersion != 2 || journal.DeliveryMode != UpdateDeliveryMode.SourceArchive
            || journal.Phase != UpdatePhase.Committed || journal.OperationId != request.OperationId)
            throw new InvalidOperationException(
                "The requested source Guardian release is not the exact committed source Agent transaction.");
        var durable = SourceRequestFromJournal(journal);
        SourceUpdatePackageVerifier.ValidateRequest(durable);
        if (!SameSourceRequest(request, durable))
            throw new InvalidDataException("The requested source Guardian pins differ from the committed source Agent transaction.");

        var operation = Path.GetFullPath(Path.Combine(AppPaths.UpdateDataDirectory, request.OperationId.ToString("N")));
        var expectedArchive = Path.Combine(operation, "package.zip");
        var expectedBuild = Path.Combine(operation, "source-build");
        var expectedAttestation = Path.Combine(operation, "source-build-attestation.json");
        if (!Path.GetFullPath(journal.PackagePath).Equals(expectedArchive, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(journal.SourceBuildOutputDirectory).Equals(expectedBuild, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(journal.SourceBuildAttestationPath).Equals(expectedAttestation, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The committed source update journal has an unsafe staging layout.");
        MachineStorageSecurity.RequireRestrictedDirectory(operation);
        MachineStorageSecurity.RequireRestrictedFile(journal.PackagePath);
        MachineStorageSecurity.RequireRestrictedFile(journal.SourceBuildAttestationPath);
        return journal;
    }

    private static SourceUpdateRequest SourceRequestFromJournal(UpdateJournal journal) => new()
    {
        ProtocolVersion = SourceUpdateProtocol.Version,
        OperationId = journal.OperationId,
        TargetVersion = journal.TargetVersion,
        Role = journal.Role,
        Architecture = journal.Architecture,
        DownloadUrl = journal.SourceDownloadUrl,
        SourceFile = journal.SourceFile,
        SourceSize = journal.PackageSize,
        SourceSha256 = journal.PackageSha256,
        SourceManifestSha256 = journal.SourceManifestSha256,
        SourceManifestKeyId = journal.SourceManifestKeyId,
        SigningProfile = journal.SigningProfile,
        ProductSignerThumbprint = journal.ProductSignerThumbprint,
        SdkVersion = journal.SdkVersion,
        RuntimeVersion = journal.RuntimeVersion,
        TargetRuntime = journal.TargetRuntime
    };

    private static bool SameSourceRequest(SourceUpdateRequest left, SourceUpdateRequest right) =>
        left.ProtocolVersion == right.ProtocolVersion
        && left.OperationId == right.OperationId
        && left.TargetVersion.Equals(right.TargetVersion, StringComparison.Ordinal)
        && left.Role == right.Role
        && left.Architecture.Equals(right.Architecture, StringComparison.OrdinalIgnoreCase)
        && left.DownloadUrl.Equals(right.DownloadUrl, StringComparison.Ordinal)
        && left.SourceFile.Equals(right.SourceFile, StringComparison.Ordinal)
        && left.SourceSize == right.SourceSize
        && left.SourceSha256.Equals(right.SourceSha256, StringComparison.OrdinalIgnoreCase)
        && left.SourceManifestSha256.Equals(right.SourceManifestSha256, StringComparison.OrdinalIgnoreCase)
        && left.SourceManifestKeyId.Equals(right.SourceManifestKeyId, StringComparison.Ordinal)
        && left.SigningProfile.Equals(right.SigningProfile, StringComparison.Ordinal)
        && left.ProductSignerThumbprint.Equals(right.ProductSignerThumbprint, StringComparison.Ordinal)
        && left.SdkVersion.Equals(right.SdkVersion, StringComparison.Ordinal)
        && left.RuntimeVersion.Equals(right.RuntimeVersion, StringComparison.Ordinal)
        && left.TargetRuntime.Equals(right.TargetRuntime, StringComparison.Ordinal);

    private async Task DownloadWithResumeAsync(Uri uri, string destination, long expectedSize, CancellationToken cancellationToken)
    {
        var partial = destination + ".partial";
        var destinationDirectory = Path.GetDirectoryName(destination)
                                   ?? throw new InvalidOperationException("The update package has no parent directory.");
        MachineStorageSecurity.RequireRestrictedDirectory(destinationDirectory);
        MachineStorageSecurity.RequireRestrictedFileIfExists(destination);
        MachineStorageSecurity.RequireRestrictedFileIfExists(partial);
        Exception? last = null;
        for (var attempt = 1; attempt <= MaximumDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > expectedSize) { File.Delete(partial); offset = 0; }
                if (offset == expectedSize)
                {
                    File.Move(partial, destination, true);
                    MachineStorageSecurity.RequireRestrictedFile(destination);
                    return;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    File.Delete(partial);
                    offset = 0;
                }
                else if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        File.Delete(partial);
                    response.EnsureSuccessStatusCode();
                    throw new InvalidDataException("The release server did not honor the resumable download range.");
                }
                if (offset > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != offset || range.Length != expectedSize)
                        throw new InvalidDataException("The release server returned mismatched resumable range metadata.");
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("The release server returned an encoded package body.");
                var expectedResponseBytes = expectedSize - offset;
                if (response.Content.Headers.ContentLength is { } declared
                    && declared != expectedResponseBytes)
                    throw new InvalidDataException("The release server returned a mismatched package length.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                long total = offset;
                await using (var output = new FileStream(
                                 partial,
                                 offset == 0 ? FileMode.Create : FileMode.Append,
                                 FileAccess.Write,
                                 FileShare.Read,
                                 1024 * 1024,
                                 true))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += read;
                        if (total > expectedSize) throw new InvalidDataException("The release server sent more bytes than declared.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                MachineStorageSecurity.SealRestrictedFile(partial);
                if (total != expectedSize) throw new IOException($"The release download stopped at {total} of {expectedSize} bytes.");
                File.Move(partial, destination, true);
                MachineStorageSecurity.RequireRestrictedFile(destination);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                last = exception;
                if (File.Exists(partial)) MachineStorageSecurity.SealRestrictedFile(partial);
                if (attempt < MaximumDownloadAttempts) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
        var detail = last?.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail?.Length > 512) detail = detail[..512] + "...";
        throw new IOException(
            $"The Opticon release could not be downloaded after {MaximumDownloadAttempts} attempts." +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : " Last error: " + detail),
            last);
    }

    private static Task EnsurePrivateUpdateDirectoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MachineStorageSecurity.EnsureOpticonMachineState();
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.UpdateDataDirectory);
        return Task.CompletedTask;
    }

    private static void EnsureFreeSpace(long packageSize)
    {
        var root = Path.GetPathRoot(AppPaths.UpdateDataDirectory) ?? throw new InvalidOperationException("The update drive could not be determined.");
        var drive = new DriveInfo(root);
        var currentSize = Directory.Exists(AppPaths.AgentInstallDirectory)
            ? Directory.EnumerateFiles(AppPaths.AgentInstallDirectory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
        var required = checked(packageSize * 2 + currentSize * 2 + 512L * 1024 * 1024);
        if (!drive.IsReady || drive.AvailableFreeSpace < required)
            throw new IOException($"The update requires {required / (1024 * 1024)} MiB free for staging and rollback; only {drive.AvailableFreeSpace / (1024 * 1024)} MiB is available.");
    }

    private static UpdateJournal RequireJournal(Guid operationId)
    {
        var journal = UpdateJournalPersistence.Load() ?? throw new InvalidOperationException("No Opticon update is staged.");
        if (operationId == Guid.Empty || journal.OperationId != operationId)
            throw new InvalidOperationException("The update operation ID does not match the staged release.");
        return journal;
    }

    private static string GuardianExecutable()
    {
        var path = Path.Combine(AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException(
            "The fail-safe Opticon update guardian is not installed. Use the signed one-time bootstrap before remotely updating this device.", path);
    }

    private static async Task RunGuardianTaskAsync(
        CancellationToken cancellationToken,
        Guid scheduledOperationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        string lastError = string.Empty;
        do
        {
            var result = await ProcessRunner.RunAsync(
                RequireSystemTool("schtasks.exe"), ["/Run", "/TN", RemoteAdministrationProtocol.GuardianTaskName],
                TimeSpan.FromSeconds(15), cancellationToken,
                environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
            if (await WaitForGuardianPickupAsync(
                    scheduledOperationId, TimeSpan.FromSeconds(3), cancellationToken))
                return;
            lastError = result.Succeeded
                ? "Task Scheduler accepted the request, but no Guardian process claimed the durable transaction."
                : string.Join(" ", result.StandardOutput.Trim(), result.StandardError.Trim()).Trim();

            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException(
            "Windows could not start the fail-safe update guardian after bounded retries: " + lastError);
    }

    private static async Task<UpdateJournal> RunGuardianTaskForCommitAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        string lastError = string.Empty;
        do
        {
            var result = await ProcessRunner.RunAsync(
                RequireSystemTool("schtasks.exe"), ["/Run", "/TN", RemoteAdministrationProtocol.GuardianTaskName],
                TimeSpan.FromSeconds(15), cancellationToken,
                environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
            var observationDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
            if (observationDeadline > deadline) observationDeadline = deadline;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var durable = UpdateJournalPersistence.Load()
                              ?? throw new InvalidDataException(
                                  "The durable update transaction disappeared while waking the Guardian for commit.");
                if (durable.OperationId != operationId)
                    throw new InvalidDataException(
                        "A different durable update transaction appeared while waking the Guardian for commit.");
                if (durable.Phase is UpdatePhase.Committed or UpdatePhase.RolledBack or UpdatePhase.Failed)
                    return durable;
                if (durable.Phase is not UpdatePhase.AwaitingCommit and not UpdatePhase.RollingBack)
                    throw new InvalidDataException(
                        $"Update {operationId:N} entered unexpected phase {durable.Phase} while committing.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            } while (DateTimeOffset.UtcNow < observationDeadline);

            lastError = result.Succeeded
                ? "Task Scheduler accepted the request, but the exact durable transaction remained AwaitingCommit."
                : string.Join(" ", result.StandardOutput.Trim(), result.StandardError.Trim()).Trim();
            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException(
            "The fail-safe Guardian did not produce durable exact-operation commit evidence after bounded retries: " +
            lastError);
    }

    private static async Task<bool> WaitForGuardianPickupAsync(
        Guid operationId,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(maximumWait);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var durable = UpdateJournalPersistence.Load()
                          ?? throw new InvalidDataException("The durable update transaction disappeared while starting the Guardian.");
            if (durable.OperationId != operationId)
                throw new InvalidDataException("A different durable update transaction appeared while starting the Guardian.");
            if (durable.Phase != UpdatePhase.ActivationScheduled
                || durable.GuardianClaimedAt is not null)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    private static async Task RestoreReadyAfterGuardianStartFailureAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using var coordinationLease = await UpdateJournalCoordination.AcquireAsync(
            TimeSpan.FromMinutes(2), cancellationToken);
        var durable = RequireJournal(operationId);
        if (durable.Phase != UpdatePhase.ActivationScheduled) return;

        durable.Phase = UpdatePhase.Ready;
        durable.ActivateAfter = null;
        durable.GuardianClaimedAt = null;
        durable.SshWasListening = false;
        durable.CommitDeadline = null;
        durable.AgentProcessId = 0;
        durable.Message = "Guardian start failed before the installed Agent changed. The verified candidate remains Ready and activation can be retried.";
        await UpdateJournalPersistence.SaveAsync(durable, cancellationToken: cancellationToken);
    }

    private static string RequireSystemTool(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException("A fixed Windows system tool name is invalid.");
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory))
            throw new DirectoryNotFoundException("The Windows System32 directory is unavailable.");
        var path = Path.GetFullPath(Path.Combine(systemDirectory, fileName));
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("A required fixed Windows system tool is missing or unsafe.", path);
        return path;
    }

    private static IReadOnlyDictionary<string, string?> BuildPrivilegedEnvironment()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(windows) || string.IsNullOrWhiteSpace(system))
            throw new DirectoryNotFoundException("The fixed Windows directories are unavailable.");
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ComSpec"] = Path.Combine(system, "cmd.exe"),
            ["PATH"] = string.Join(Path.PathSeparator, system, windows),
            ["TEMP"] = Path.Combine(windows, "Temp"),
            ["TMP"] = Path.Combine(windows, "Temp")
        };
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) result["ProgramFiles(x86)"] = programFilesX86;
        return result;
    }
}
