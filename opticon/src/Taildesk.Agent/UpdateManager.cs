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
    private readonly HttpClient _http = new(new HttpClientHandler { CheckCertificateRevocationList = true })
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
            await InvitationSigning.VerifyAuthenticodeAsync(guardian, cancellationToken);
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
            Directory.CreateDirectory(operationDirectory);
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
                    TryDelete(AppPaths.UpdateCommitRequestFile);
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

    private static void ValidateGuardianCompatibility(string guardian, string minimumVersion)
    {
        var installedText = UpdatePackageVerifier.NormalizeVersion(FileVersionInfo.GetVersionInfo(guardian).ProductVersion ?? string.Empty);
        if (UpdatePackageVerifier.ParseVersion(installedText) < UpdatePackageVerifier.ParseVersion(minimumVersion))
            throw new InvalidOperationException($"This release requires update guardian {minimumVersion} or newer; installed is {installedText}.");
    }

    private static async Task DownloadWithResumeAsync(Uri uri, string destination, long expectedSize, CancellationToken cancellationToken)
    {
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Exception? last = null;
        using var handler = new HttpClientHandler
        {
            CheckCertificateRevocationList = true,
            UseProxy = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        for (var attempt = 1; attempt <= MaximumDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > expectedSize) { File.Delete(partial); offset = 0; }
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    File.Delete(partial);
                    offset = 0;
                }
                else if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    response.EnsureSuccessStatusCode();
                    throw new InvalidDataException("The release server did not honor the resumable download range.");
                }
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write, FileShare.Read, 1024 * 1024, true);
                var buffer = new byte[1024 * 1024];
                long total = offset;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > expectedSize) throw new InvalidDataException("The release server sent more bytes than declared.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                if (total != expectedSize) throw new IOException($"The release download stopped at {total} of {expectedSize} bytes.");
                File.Move(partial, destination, true);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                last = exception;
                if (attempt < MaximumDownloadAttempts) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
        throw new IOException($"The Opticon release could not be downloaded after {MaximumDownloadAttempts} attempts.", last);
    }

    private static async Task EnsurePrivateUpdateDirectoryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.UpdateDataDirectory);
        var result = await ProcessRunner.RunAsync("icacls.exe",
            [AppPaths.UpdateDataDirectory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException("Windows could not protect the update staging and rollback directory.");
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
                "schtasks.exe", ["/Run", "/TN", RemoteAdministrationProtocol.GuardianTaskName],
                TimeSpan.FromSeconds(15), cancellationToken);
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
                "schtasks.exe", ["/Run", "/TN", RemoteAdministrationProtocol.GuardianTaskName],
                TimeSpan.FromSeconds(15), cancellationToken);
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
