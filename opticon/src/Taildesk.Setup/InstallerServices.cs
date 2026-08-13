using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Setup;

public sealed record InstallProgress(int Percent, string Message);

public sealed record InstallWarning(string Operation, string Detail);

public sealed record InstallResult(
    bool MeshConnected,
    bool RemoteDesktopReady,
    bool AgentReady,
    bool SshRecoveryReady,
    bool EnrollmentConfirmed,
    IReadOnlyList<InstallWarning> Warnings)
{
    public bool HasWarnings => Warnings.Count != 0;
}

public sealed class ExistingTailscaleSessionException : InvalidOperationException
{
    public ExistingTailscaleSessionException(string message) : base(message) { }
}

public sealed class InstallCoordinator
{
    private const string ControllerOwnershipMarkerName = ".opticon-controller-owned";
    private const string ControllerOwnershipMarkerValue = "Opticon command-center controller payload v1";
    private const string ControllerReadyMarkerName = ".opticon-controller-ready";
    private const string ControllerReadyMarkerValue = "Opticon command-center controller payload ready v1";
    private const string ControllerInstallDirectoryValueName = "InstallDirectory";
    private const string ControllerInstallLockFileName = ".controller-install.lock";
    private const string AgentTaskName = "Taildesk Agent";
    private const string AgentTaskOwnershipDescription =
        "Runs the protected Opticon background agent at system startup.";
    private const string RouteKeeperTaskName = "Taildesk Fly Route";
    private const string ControllerUiTaskName = "Opticon Command Center";
    private const int TaskPresenceProbeAbsentExitCode = 3;
    private const string TaskPresenceProbeScript = """
        $ErrorActionPreference = 'Stop'
        $service = $null
        $folder = $null
        $task = $null
        try {
            $service = New-Object -ComObject 'Schedule.Service'
            $service.Connect()
            $folder = $service.GetFolder('\')
            try {
                $task = $folder.GetTask($env:TAILDESK_EXPECTED_TASK_NAME)
            } catch {
                # HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND). PowerShell can
                # surface this as COMException or FileNotFoundException. Every
                # access, RPC, and other failure escapes and therefore fails
                # the presence probe closed.
                if ($_.Exception.HResult -eq -2147024894) { exit 3 }
                throw
            }
            exit 0
        } finally {
            if ($null -ne $task) {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null
            }
            if ($null -ne $folder) {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
            }
            if ($null -ne $service) {
                [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null
            }
        }
        """;
    private const string FlyControllerIpv4 = "213.188.217.227";

    private readonly InvitePayload _invite;
    private readonly string _bundleDirectory;
    private readonly IProgress<InstallProgress> _progress;
    private readonly HttpClient _http;
    private readonly bool _allowTailscaleReauthentication;
    private readonly SetupResumeContext? _resumeContext;
    // Resolve the profile during the named Ensure phase, not construction, so
    // preflight can report profile problems together with other repairs.
    private InteractiveUserProfile? _userProfile;
    private FileStream? _agentInstallLock;
    private AgentInstallTransactionJournal? _agentInstallTransaction;
    private MachineInstallTransactionJournal? _machineInstallTransaction;
    private bool _agentInstallCommitted;
    private bool _controllerTasksInstalled;
    private readonly List<InstallWarning> _warnings = [];

    public InstallCoordinator(
        InvitePayload invite,
        string bundleDirectory,
        IProgress<InstallProgress> progress,
        bool allowTailscaleReauthentication = false)
        : this(invite, bundleDirectory, progress, allowTailscaleReauthentication, resumeContext: null)
    {
    }

    internal InstallCoordinator(
        InvitePayload invite,
        string bundleDirectory,
        IProgress<InstallProgress> progress,
        bool allowTailscaleReauthentication,
        SetupResumeContext? resumeContext = null)
    {
        _invite = invite;
        _bundleDirectory = Path.GetFullPath(bundleDirectory);
        _progress = progress;
        _allowTailscaleReauthentication = allowTailscaleReauthentication;
        _resumeContext = resumeContext;
        _http = DirectHttp.CreateClient(TimeSpan.FromMinutes(10));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Taildesk-Setup/1.0");
    }

    public async Task<InstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        _warnings.Clear();
        var meshConnected = false;
        var remoteDesktopReady = false;
        var agentReady = false;
        var sshRecoveryReady = false;
        var enrollmentConfirmed = false;

        if (ValidationEnabled(ClientInstallValidationStep.InvitationConstraints))
            EnsureInviteIsValid();
        if (ValidationEnabled(ClientInstallValidationStep.SetupPreflight))
        {
            var preflight = await SetupPreflight.DiscoverElevatedAsync(
                _invite, _bundleDirectory, cancellationToken);
            ReportPreflight(preflight);
            if (preflight.IsBlocked) throw new SetupPreflightBlockedException(preflight);
        }

        // This is deliberately after read-only discovery. Generic Taildesk
        // state can contain secrets and remains fail-closed; regenerable
        // source provenance has its own safe rebuild policy below.
        await EnsureProtectedStorageAsync(cancellationToken);
        await AcquireAgentInstallLockAsync(cancellationToken);
        SourceInstallationBinding sourceBinding;
        var canResumeExistingSession = false;
        AgentConfig? completedEnrollmentState = null;
        string? tempDirectory = null;
        try
        {
            sourceBinding = SourceBuildProvenance.RequireActiveInstallationBinding(_invite.InviteId);
            _machineInstallTransaction = MachineInstallTransactionPersistence.LoadRecoverably(
                out var corruptMachineJournalQuarantined);
            if (corruptMachineJournalQuarantined)
            {
                _progress.Report(new InstallProgress(4,
                    "A torn protected machine journal was quarantined; revalidating every component before roll-forward."));
            }
            if (_machineInstallTransaction is not null)
            {
                if (ValidationEnabled(ClientInstallValidationStep.MachineState))
                    MachineInstallTransactionPersistence.RequireMatches(
                        _machineInstallTransaction, sourceBinding);
                canResumeExistingSession = MachineInstallTransactionPersistence.RequiresNetworkRollForward(
                    _machineInstallTransaction)
                    || _machineInstallTransaction.TailscaleReauthenticationApproved;
            }

            var hasInterruptedAgentInstall = AgentInstallTransactionPersistence.LoadRecoverably(
                out var corruptAgentJournalQuarantined) is not null;
            if (corruptAgentJournalQuarantined)
            {
                _progress.Report(new InstallProgress(4,
                    "A torn protected Agent journal was quarantined; the signed Agent generation will be revalidated and repaired."));
            }
            if (ValidationEnabled(ClientInstallValidationStep.MachineState)
                && !hasInterruptedAgentInstall && File.Exists(AppPaths.AgentConfigFile))
            {
                var installedState = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
                    .LoadAsync(cancellationToken);
                RequireSafeInvitationResume(installedState);
                if (installedState.CompletedInviteId == _invite.InviteId)
                {
                    completedEnrollmentState = installedState;
                    canResumeExistingSession = true;
                }
                canResumeExistingSession = canResumeExistingSession
                                           || installedState.PendingInviteId == _invite.InviteId;
            }
            tempDirectory = MachineStorageSecurity.CreateRestrictedChildDirectory(
                AppPaths.SetupStagingDirectory, "install-");
            _progress.Report(new InstallProgress(4, "Checking the invitation and local payload…"));
            var payload = await EnsurePayloadVerifiedAsync(cancellationToken);
            var agentPayload = payload.AgentDirectory;
            var guardianPayload = payload.GuardianDirectory;

            await EnsureMachineInstallTransactionAsync(sourceBinding, cancellationToken);
            if (corruptMachineJournalQuarantined)
            {
                await RecordEnsureOutcomeAsync(InstallerEnsureResult.Repaired(
                    "EnsureMachineInstallRecoveryAsync",
                    "The corrupt protected machine journal was quarantined and all machine phases will be revalidated.",
                    "Reconstructed the installer recovery boundary from the authenticated invitation and source binding."),
                    cancellationToken, "MachineInstallJournal");
            }
            if (corruptAgentJournalQuarantined)
            {
                await RecordEnsureOutcomeAsync(InstallerEnsureResult.Repaired(
                    "EnsureAgentRecoveryAsync",
                    "The corrupt protected Agent journal was quarantined and the Agent payload, task, and state will be revalidated.",
                    "Reconstructed the Agent repair plan from the signed payload."),
                    cancellationToken, "AgentInstallJournal");
            }
            await EnsureProtectedStorageAsync(cancellationToken);
            var buildEnvironment = await EnsureBuildEnvironmentAsync(sourceBinding, cancellationToken);
            await RecordEnsureOutcomeAsync(buildEnvironment, cancellationToken);
            await RecordEnsureOutcomeAsync(payload.Result, cancellationToken);
            var reuseJournalNetworkComponent = RequireMachineInstallTransaction().Phase
                                               >= MachineInstallTransactionPhase.NetworkComponentReady;
            await AdvanceMachineInstallTransactionAsync(
                MachineInstallTransactionPhase.NetworkComponentInstallStarted, cancellationToken);
            var tailscaleResult = await EnsureTailscaleInstalledAsync(
                tempDirectory, reuseJournalNetworkComponent, cancellationToken);
            var installedNetworkComponent = tailscaleResult.InstalledByOpticon;
            await AdvanceMachineInstallTransactionAsync(
                MachineInstallTransactionPhase.NetworkComponentReady, cancellationToken);
            _progress.Report(new InstallProgress(28, "Joining the private Opticon network…"));
            var tailscale = FindTailscale();
            var snapshot = await EnsureTailnetEnrollmentAsync(
                tailscale, canResumeExistingSession, cancellationToken);
            meshConnected = true;

            ComponentInstallation? rustDeskInstallation = null;
            string? rustDesk = null;
            try
            {
                var expectedRustDesk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "RustDesk", "rustdesk.exe");
                if (ValidationEnabled(ClientInstallValidationStep.FirewallPolicy))
                    await ConfigureFirewallAsync(snapshot.Ip, expectedRustDesk, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.RemoteIsolationPrepared, cancellationToken);
                var reuseJournalRemoteComponent = RequireMachineInstallTransaction().Phase
                                                   >= MachineInstallTransactionPhase.RemoteComponentReady;
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.RemoteComponentInstallStarted, cancellationToken);
                rustDeskInstallation = await EnsureRustDeskAsync(
                    tempDirectory, reuseJournalRemoteComponent, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.RemoteComponentReady, cancellationToken);
                rustDesk = rustDeskInstallation.Path;
                if (ValidationEnabled(ClientInstallValidationStep.FirewallPolicy))
                    await EnsureFirewallPolicyAsync(snapshot.Ip, rustDesk, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.RemoteIsolationApplied, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.RemoteConfigurationStarted, cancellationToken);
                await EnsureRustDeskListenerAsync(rustDesk, cancellationToken);
                if (ValidationEnabled(ClientInstallValidationStep.FirewallPolicy))
                    await AssertExactFirewallConfigurationAsync(snapshot.Ip, rustDesk, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.RemoteConfigured, cancellationToken);
                remoteDesktopReady = true;
                _progress.Report(new InstallProgress(
                    68,
                    "Private-network and direct remote-desktop recovery are ready; finishing management components…"));
            }
            catch (Exception remoteDesktopError) when (
                remoteDesktopError is not OperationCanceledException)
            {
                try
                {
                    await ContainRustDeskAfterFailedSetupAsync(CancellationToken.None);
                }
                catch (Exception containmentError)
                {
                    throw new AggregateException(
                        "Direct remote-desktop setup failed and RustDesk could not be safely disabled. " +
                        "The private mesh remains joined, but Setup cannot leave an unverified listener running.",
                        remoteDesktopError,
                        containmentError);
                }
                AddInstallWarning("Direct remote desktop", remoteDesktopError);
                _progress.Report(new InstallProgress(
                    68,
                    "The private mesh is connected; direct remote desktop needs repair. Continuing with SSH and Agent recovery…"));
            }

            // Everything below this fence is independently repairable from
            // the verified Tailscale + RustDesk recovery path.  Do not tear
            // down that path because a user profile, SSH capability, stable
            // Guardian, controller tool, Agent, or coordinator is temporarily
            // unavailable.
            await TryDeferredEnsureAsync(
                "Interactive user profile",
                () => EnsureInteractiveUserProfileAsync(cancellationToken),
                cancellationToken);

            var guardianReady = await TryDeferredEnsureAsync(
                "Fail-safe update Guardian",
                () => EnsureGuardianAsync(guardianPayload, cancellationToken),
                cancellationToken);
            if (guardianReady)
            {
                _progress.Report(new InstallProgress(72, "Checking the Windows OpenSSH recovery component…"));
                sshRecoveryReady = await TryEnsureOpenSshDeferredAsync(cancellationToken);
            }
            else
            {
                AddInstallWarning(
                    "Windows OpenSSH recovery",
                    "OpenSSH recovery was deferred because its independent SYSTEM Guardian is not ready.");
            }

            try
            {
                if (!remoteDesktopReady
                    && ValidationEnabled(ClientInstallValidationStep.FirewallPolicy))
                    await EnsureAgentFirewallPolicyAsync(snapshot.Ip, cancellationToken);
                await RecoverAgentInstallTransactionAsync(agentPayload, cancellationToken);
                if (ValidationEnabled(ClientInstallValidationStep.MachineState)
                    && File.Exists(AppPaths.AgentConfigFile))
                {
                    var recoveredState = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
                        .LoadAsync(cancellationToken);
                    RequireSafeInvitationResume(recoveredState);
                    if (recoveredState.CompletedInviteId == _invite.InviteId)
                        completedEnrollmentState = recoveredState;
                }
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.AgentInstallStarted, cancellationToken);
                if (completedEnrollmentState is null)
                    await EnsureAgentAsync(agentPayload, snapshot.Ip, cancellationToken);
                else
                    await EnsureExistingAgentAsync(
                        agentPayload, completedEnrollmentState, snapshot.Ip, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.AgentInstalled, cancellationToken);
                if (remoteDesktopReady
                    && ValidationEnabled(ClientInstallValidationStep.FirewallPolicy))
                    await AssertExactFirewallConfigurationAsync(snapshot.Ip, rustDesk!, cancellationToken);
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.FirewallConfigured, cancellationToken);

                _progress.Report(new InstallProgress(94, "Starting the Opticon agent…"));
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.AgentStartRequested, cancellationToken);
                var start = await RunSystemToolAsync(
                    "schtasks.exe", ["/Run", "/TN", AgentTaskName],
                    TimeSpan.FromSeconds(20), cancellationToken);
                EnsureSuccess(start, "The Opticon background agent task could not be started");
                if (ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions)
                    && !await WaitForListeningExecutableAsync(
                        45831,
                        Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe"),
                        TimeSpan.FromSeconds(30),
                        cancellationToken))
                    throw new InvalidOperationException(
                        "The Opticon agent task started but did not open its private API listener on TCP 45831.");
                await AdvanceMachineInstallTransactionAsync(
                    MachineInstallTransactionPhase.AgentRunning, cancellationToken);
                agentReady = true;
            }
            catch (Exception agentError) when (agentError is not OperationCanceledException)
            {
                try
                {
                    await RollbackAgentInstallTransactionAsync(CancellationToken.None);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The management Agent failed and its protected rollback did not complete. " +
                        "Tailscale and RustDesk remain installed for recovery.",
                        agentError,
                        rollbackError);
                }
                AddInstallWarning("Opticon management Agent", agentError);
                _progress.Report(new InstallProgress(
                    100,
                    remoteDesktopReady
                        ? "Remote desktop is ready. The management Agent needs repair; see warnings."
                        : "The private mesh is connected, but remote desktop and the management Agent need repair; see warnings."));
                return CreateInstallResult(
                    meshConnected, remoteDesktopReady, agentReady, sshRecoveryReady,
                    enrollmentConfirmed);
            }

            try
            {
                OpticonComponentIntegration.Integrate(
                    installedNetworkComponent,
                    rustDeskInstallation?.InstalledByOpticon == true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddInstallWarning("Installed-component bookkeeping", exception);
            }
            await AdvanceMachineInstallTransactionAsync(
                MachineInstallTransactionPhase.ComponentsIntegrated, cancellationToken);

            await TryDeferredEnsureAsync(
                "Controller tools",
                () => InstallControllerPayloadAsync(
                    _invite.Role == DeviceRole.ControllerAndManaged, cancellationToken),
                cancellationToken);
            await AdvanceMachineInstallTransactionAsync(
                MachineInstallTransactionPhase.ControllerInstalled, cancellationToken);

            _progress.Report(new InstallProgress(96, "Waiting for the command center to confirm enrollment…"));
            await AdvanceMachineInstallTransactionAsync(
                MachineInstallTransactionPhase.EnrollmentWaitStarted, cancellationToken);
            try
            {
                if (ValidationEnabled(ClientInstallValidationStep.EnrollmentConfirmation))
                {
                    await EnsureEnrollmentCommittedAsync(completedEnrollmentState, cancellationToken);
                    enrollmentConfirmed = true;
                }
                else
                {
                    await CommitWithoutEnrollmentConfirmationAsync(cancellationToken);
                }
            }
            catch (TimeoutException timeout)
            {
                // The exact Agent task and listener already passed locally.
                // Preserve its pending invitation so it can keep retrying the
                // idempotent coordinator enrollment after Setup exits.
                try
                {
                    enrollmentConfirmed = await CommitPendingEnrollmentAsync(cancellationToken);
                }
                catch (Exception completionError) when (
                    completionError is not OperationCanceledException && _agentInstallCommitted)
                {
                    AddInstallWarning("Pending-enrollment cleanup", completionError);
                }
                if (!enrollmentConfirmed)
                    AddInstallWarning("Command-center enrollment confirmation", timeout);
            }
            catch (Exception completionError) when (
                completionError is not OperationCanceledException && _agentInstallCommitted)
            {
                // A verified receipt is the commit fence.  Cleanup or source-
                // provenance housekeeping after that point must never roll an
                // accepted Agent back.
                enrollmentConfirmed = true;
                AddInstallWarning("Post-enrollment cleanup", completionError);
            }

            await TryDeferredEnsureAsync(
                "Controller task startup",
                () => StartControllerTasksIfInstalledAsync(cancellationToken),
                cancellationToken);
            var finalMessage = _warnings.Count == 0
                ? "Connected. This machine is ready."
                : "Connected with recovery access. Some components need repair; see warnings.";
            _progress.Report(new InstallProgress(100, finalMessage));
            return CreateInstallResult(
                meshConnected, remoteDesktopReady, agentReady, sshRecoveryReady,
                enrollmentConfirmed);
        }
        catch (Exception installError)
        {
            if (!_agentInstallCommitted && _agentInstallTransaction is not null)
            {
                try { await RollbackAgentInstallTransactionAsync(CancellationToken.None); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "Opticon installation failed and the prior Agent generation could not be restored. " +
                        "The protected transaction journal was retained for recovery.",
                        installError,
                        rollbackError);
                }
            }
            throw;
        }
        finally
        {
            try { if (tempDirectory is not null) MachineStorageSecurity.DeleteRestrictedDirectory(tempDirectory); } catch { }
            _agentInstallLock?.Dispose();
            _agentInstallLock = null;
        }
    }

    private InstallResult CreateInstallResult(
        bool meshConnected,
        bool remoteDesktopReady,
        bool agentReady,
        bool sshRecoveryReady,
        bool enrollmentConfirmed) => new(
        meshConnected,
        remoteDesktopReady,
        agentReady,
        sshRecoveryReady,
        enrollmentConfirmed,
        _warnings.ToArray());

    private async Task<bool> TryDeferredEnsureAsync<T>(
        string operation,
        Func<Task<T>> ensure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ = await ensure();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddInstallWarning(operation, exception);
            return false;
        }
    }

    private async Task<bool> TryDeferredEnsureAsync(
        string operation,
        Func<Task> ensure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ensure();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddInstallWarning(operation, exception);
            return false;
        }
    }

    private async Task<bool> TryEnsureOpenSshDeferredAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureOpenSshAsync(
                cancellationToken, scheduleRebootContinuation: false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddInstallWarning("Windows OpenSSH recovery", exception);
            return false;
        }
    }

    private void AddInstallWarning(string operation, Exception exception) =>
        AddInstallWarning(operation, exception.Message);

    private void AddInstallWarning(string operation, string detail)
    {
        static string Bounded(string value, int maximum)
        {
            var safe = new string(value
                .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
                .ToArray()).Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("\t", " ", StringComparison.Ordinal)
                .Trim();
            return safe.Length <= maximum ? safe : safe[..maximum];
        }

        var warning = new InstallWarning(
            Bounded(operation, 96),
            Bounded(string.IsNullOrWhiteSpace(detail) ? "The repair did not complete." : detail, 512));
        _warnings.Add(warning);
        _progress.Report(new InstallProgress(
            98,
            $"WARNING: {warning.Operation} — {warning.Detail}"));
    }

    private bool ValidationEnabled(ClientInstallValidationStep step) =>
        ClientInstallValidationPolicy.Normalize(_invite.ClientInstallValidation).IsEnabled(step);

    /// <summary>
    /// The source bootstrap owns the actual local build. At this point Setup
    /// verifies the attested build binding again, which is the postcondition
    /// required before any component can consume its payload.
    /// </summary>
    private Task<InstallerEnsureResult> EnsureBuildEnvironmentAsync(
        SourceInstallationBinding sourceBinding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = SourceBuildProvenance.RequireActiveInstallationBinding(_invite.InviteId);
        if (ValidationEnabled(ClientInstallValidationStep.SourceBuildProvenance) && active != sourceBinding)
            throw new InvalidDataException("The active authenticated source build changed during installer preflight.");
        return Task.FromResult(InstallerEnsureResult.Ready(
            "EnsureBuildEnvironmentAsync",
            "The authenticated source build and its isolated .NET 10 environment are bound to this invitation."));
    }

    private Task<VerifiedPayload> EnsurePayloadVerifiedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var agentDirectory = Path.GetFullPath(Path.Combine(_bundleDirectory, "Payload", "Agent"));
        var guardianDirectory = Path.GetFullPath(Path.Combine(
            _bundleDirectory, "Payload", "UpdateGuardian"));
        if (ValidationEnabled(ClientInstallValidationStep.ProtectedPaths)
            && (!IsPathWithinDirectory(agentDirectory, _bundleDirectory)
                || !IsPathWithinDirectory(guardianDirectory, _bundleDirectory)))
            throw new InvalidDataException("A component payload path escaped the authenticated local build.");

        // SourceBuildProvenance already reverified the attested release before
        // this coordinator starts.  Component-specific existence, exact tree,
        // hash, and Authenticode checks intentionally occur inside the
        // Guardian and Agent phases so a missing optional management payload
        // cannot prevent Tailscale + RustDesk recovery from being established.
        return Task.FromResult(new VerifiedPayload(
            agentDirectory,
            guardianDirectory,
            InstallerEnsureResult.Ready(
                "EnsurePayloadVerifiedAsync",
                "The authenticated local build fixed the bounded component payload paths; each component is reverified immediately before use.")));
    }

    private async Task<InstallerEnsureResult> EnsureProtectedStorageAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hadMachineState = Directory.Exists(AppPaths.MachineDataDirectory);
        var hadStaging = Directory.Exists(AppPaths.SetupStagingDirectory);
        MachineStorageSecurity.EnsureOpticonMachineState();
        var provenance = ValidationEnabled(ClientInstallValidationStep.SourceBuildProvenance)
            ? SourceBuildProvenance.EnsureRecoverableStore()
            : SourceBuildProvenance.StoreRecoveryOutcome.Ready;
        var repaired = !hadMachineState || !hadStaging
                       || provenance is not SourceBuildProvenance.StoreRecoveryOutcome.Ready;
        var result = repaired
            ? InstallerEnsureResult.Repaired(
                "EnsureProtectedStorageAsync",
                "Taildesk staging and regenerable source provenance have canonical protected ACLs.",
                provenance.ToString())
            : InstallerEnsureResult.Ready(
                "EnsureProtectedStorageAsync",
                "Taildesk staging and source provenance have canonical protected ACLs.");
        await RecordEnsureOutcomeAsync(result, cancellationToken);
        return result;
    }

    private async Task<InstallerEnsureResult> EnsureInteractiveUserProfileAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = _userProfile;
        _userProfile = InteractiveUserProfile.Resolve();
        var result = previous is null
            ? InstallerEnsureResult.Ready(
                "EnsureInteractiveUserProfileAsync",
                "The interactive user profile and permitted known-folder targets were resolved safely.")
            : InstallerEnsureResult.Repaired(
                "EnsureInteractiveUserProfileAsync",
                "The interactive user profile and permitted known-folder targets were re-resolved safely.");
        await RecordEnsureOutcomeAsync(result, cancellationToken);
        return result;
    }

    private async Task RecordEnsureOutcomeAsync(
        InstallerEnsureResult result,
        CancellationToken cancellationToken,
        string? resourceChanged = null)
    {
        if (_machineInstallTransaction is null) return;
        if (result.Outcome == InstallerEnsureOutcome.Blocked)
        {
            MachineInstallTransactionPersistence.RecordBlocked(
                _machineInstallTransaction,
                result.Operation,
                result.Detail ?? "An external decision is required before this phase can continue.");
            await MachineInstallTransactionPersistence.SaveAsync(_machineInstallTransaction, cancellationToken);
            return;
        }
        MachineInstallTransactionPersistence.RecordVerifiedRepair(
            _machineInstallTransaction,
            result.Operation,
            result.Outcome == InstallerEnsureOutcome.Repaired,
            result.Postcondition,
            resourceChanged);
        await MachineInstallTransactionPersistence.SaveAsync(_machineInstallTransaction, cancellationToken);
    }

    private async Task RecordPreviousComponentVersionAsync(
        string component,
        string executable,
        bool currentVersionIsReady,
        CancellationToken cancellationToken)
    {
        if (currentVersionIsReady || _machineInstallTransaction is null || !File.Exists(executable))
            return;
        string? version;
        try { version = FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Trim(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(version)) return;
        MachineInstallTransactionPersistence.RecordPreviousComponentVersion(
            _machineInstallTransaction, component, version);
        await MachineInstallTransactionPersistence.SaveAsync(_machineInstallTransaction, cancellationToken);
    }

    private void ReportPreflight(InstallerPreflightReport report)
    {
        foreach (var finding in report.Findings)
        {
            var prefix = finding.Severity switch
            {
                InstallerPreflightSeverity.Blocked => "Blocked",
                InstallerPreflightSeverity.Repair => "Planned repair",
                _ => "Preflight"
            };
            _progress.Report(new InstallProgress(2, $"{prefix}: {finding.Area} — {finding.Detail}"));
        }
    }

    private async Task<InstallerEnsureResult> EnsureGuardianAsync(
        string guardianPayload,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe");
        var wasReady = await IsGuardianReadyAsync(guardianPayload, cancellationToken);
        await RecordPreviousComponentVersionAsync(
            "UpdateGuardian", executable, wasReady, cancellationToken);
        if (!wasReady)
            await InstallGuardianAsync(guardianPayload, cancellationToken);
        if (ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions)
            && !await IsGuardianReadyAsync(guardianPayload, cancellationToken))
            throw new InvalidDataException(
                "The signed Update Guardian did not satisfy its executable and task postconditions after repair.");
        var result = wasReady
            ? InstallerEnsureResult.Ready(
                "EnsureGuardianAsync",
                "The signed Update Guardian is installed and compatible with the protected recovery path.")
            : InstallerEnsureResult.Repaired(
                "EnsureGuardianAsync",
                "The signed Update Guardian is installed and compatible with the protected recovery path.",
                "Repaired the Guardian payload or its SYSTEM recovery tasks.");
        await RecordEnsureOutcomeAsync(result, cancellationToken,
            wasReady ? null : "UpdateGuardian");
        return result;
    }

    private static async Task<bool> IsGuardianReadyAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var directory = AppPaths.UpdateGuardianInstallDirectory;
        var executable = Path.Combine(directory, "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(executable)) return false;
        try
        {
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
            await RequireInstalledGuardianWatchdogCompatibilityAsync(
                source, directory, cancellationToken);
            return GuardianTaskMatches(
                       await QueryTaskXmlAsync(
                           RemoteAdministrationProtocol.GuardianTaskName, cancellationToken),
                       executable, arguments: string.Empty, requiresBootTrigger: true)
                   && GuardianTaskMatches(
                       await QueryTaskXmlAsync(
                           RemoteAdministrationProtocol.GuardianWatchdogTaskName, cancellationToken),
                       executable, RemoteAdministrationProtocol.GuardianWatchdogArgument,
                       requiresBootTrigger: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool GuardianTaskMatches(
        string? xml,
        string executable,
        string arguments,
        bool requiresBootTrigger)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var document = ParseTaskXml(xml);
            XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = document.Root;
            var actions = root?.Element(task + "Actions")?.Elements().ToArray() ?? [];
            var principal = root?.Element(task + "Principals")?.Elements(task + "Principal").SingleOrDefault();
            var triggers = root?.Element(task + "Triggers")?.Elements().ToArray() ?? [];
            var exec = actions.SingleOrDefault();
            return root?.Name == task + "Task"
                   && exec?.Name == task + "Exec"
                   && string.Equals(
                       exec.Element(task + "Command")?.Value,
                       Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                   && string.Equals(exec.Element(task + "Arguments")?.Value ?? string.Empty,
                       arguments, StringComparison.Ordinal)
                   && IsSystemHighestPrincipal(principal, task)
                   && (!requiresBootTrigger || triggers.Any(trigger => trigger.Name == task + "BootTrigger"));
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private async Task<InstallerEnsureResult> EnsureOpenSshAsync(
        CancellationToken cancellationToken,
        bool scheduleRebootContinuation = true)
    {
        var openSshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");
        var serverWasPresent = File.Exists(Path.Combine(openSshDirectory, "sshd.exe"));
        var clientWasPresent = File.Exists(Path.Combine(openSshDirectory, "ssh.exe"));
        try
        {
            await EnsureOpenSshServerCapabilityAsync(cancellationToken);
            if (_invite.Role == DeviceRole.ControllerAndManaged)
                await EnsureOpenSshClientCapabilityAsync(cancellationToken);
        }
        catch (OpenSshRebootRequiredException exception)
        {
            var journal = RequireMachineInstallTransaction();
            MachineInstallTransactionPersistence.RecordRebootState(
                journal, rebootPending: true, operation: "EnsureOpenSshAsync");
            await MachineInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            if (!scheduleRebootContinuation || _resumeContext is null)
                throw new SetupRebootRequiredException(
                    exception.Message +
                    (scheduleRebootContinuation
                        ? " Restart Windows, then rerun the original protected Opticon installer."
                        : " Remote access remains available; restart Windows and run Setup repair later to enable SSH recovery."));
            await SetupResumeCoordinator.ScheduleAsync(_resumeContext, cancellationToken);
            throw new SetupRebootRequiredException(
                exception.Message + " Setup saved its protected recovery state and will resume automatically after Windows restarts.");
        }
        if (_machineInstallTransaction is not null && _machineInstallTransaction.RebootPending)
        {
            MachineInstallTransactionPersistence.RecordRebootState(
                _machineInstallTransaction, rebootPending: false, operation: "EnsureOpenSshAsync");
            await MachineInstallTransactionPersistence.SaveAsync(_machineInstallTransaction, cancellationToken);
        }
        var repaired = !serverWasPresent || (_invite.Role == DeviceRole.ControllerAndManaged && !clientWasPresent);
        var result = repaired
            ? InstallerEnsureResult.Repaired(
                "EnsureOpenSshAsync",
                "The required Windows OpenSSH capability is installed and contained for Opticon recovery.",
                "Installed a missing OpenSSH capability.")
            : InstallerEnsureResult.Ready(
                "EnsureOpenSshAsync",
                "The required Windows OpenSSH capability is installed and contained for Opticon recovery.");
        await RecordEnsureOutcomeAsync(result, cancellationToken, repaired ? "OpenSSH" : null);
        return result;
    }

    internal static async Task EnsureOpenSshClientCapabilityAsync(CancellationToken cancellationToken)
    {
        var opensshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");
        var ssh = Path.Combine(opensshDirectory, "ssh.exe");
        var sshKeygen = Path.Combine(opensshDirectory, "ssh-keygen.exe");
        if (File.Exists(ssh) && File.Exists(sshKeygen)) return;

        var installed = await InstallOpenSshCapabilityAsync(
            "OpenSSH.Client~~~~0.0.1.0",
            "OpenSSH Client",
            cancellationToken);
        EnsureCapabilityCommandSucceeded(installed, "Windows could not install the OpenSSH Client capability");
        if (!File.Exists(ssh) || !File.Exists(sshKeygen))
        {
            if (installed.ExitCode == 3010)
                throw new OpenSshRebootRequiredException(
                    "Windows installed the OpenSSH Client capability and requires a restart before its binaries are available.");
            throw new InvalidOperationException(
                "OpenSSH Client installation needs a Windows restart or capability repair before controller setup can continue.");
        }
    }

    internal static async Task EnsureOpenSshServerCapabilityAsync(CancellationToken cancellationToken)    {
        var opensshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");
        var sshd = Path.Combine(opensshDirectory, "sshd.exe");
        var sshKeygen = Path.Combine(opensshDirectory, "ssh-keygen.exe");
        var stateDirectory = Path.Combine(AppPaths.AgentDataDirectory, "SshAccess");
        var journalPath = Path.Combine(stateDirectory, "openssh-setup-journal.json");
        var journalExists = File.Exists(journalPath);

        // Existing OpenSSH belongs to Windows/the operator. Setup does not alter its
        // service, firewall rule, startup type, or configuration.
        if (File.Exists(sshd) && File.Exists(sshKeygen) && !journalExists) return;

        MachineStorageSecurity.RequireRestrictedDirectory(stateDirectory);

        var phase = journalExists
            ? System.Text.Encoding.UTF8.GetString(MachineStorageSecurity.ReadRestrictedFile(journalPath, 64 * 1024))
            : string.Empty;
        if (phase.Contains("\"phase\":\"isolated\"", StringComparison.Ordinal)
            && File.Exists(sshd) && File.Exists(sshKeygen))
            return; // The one-time Opticon installation was already contained.

        // The journal precedes DISM so a cancelled/rebooted Setup can safely finish
        // containing only the capability installation that Opticon itself began.
        await WriteSetupJournalAsync(journalPath, "installing", cancellationToken);
        var rebootRequired = false;
        if (!File.Exists(sshd) || !File.Exists(sshKeygen))
        {
            var installed = await InstallOpenSshCapabilityAsync(
                "OpenSSH.Server~~~~0.0.1.0",
                "OpenSSH Server",
                cancellationToken);
            EnsureCapabilityCommandSucceeded(installed, "Windows could not install the OpenSSH Server capability");
            rebootRequired = installed.ExitCode == 3010;
        }
        if (!File.Exists(sshd) || !File.Exists(sshKeygen))
        {
            if (rebootRequired)
                throw new OpenSshRebootRequiredException(
                    "Windows installed the OpenSSH Server capability and requires a restart before its binaries are available.");
            throw new InvalidOperationException(
                "OpenSSH Server installation needs a Windows restart or capability repair before Opticon setup can continue.");
        }

        // Complete the one-time containment without request or UI cancellation. If
        // interrupted, the durable 'installing' journal makes the next Setup retry it.
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var isolated = await RunSystemToolAsync(
            Path.GetRelativePath(Environment.SystemDirectory, powershell),
            [
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference='Stop'; " +
                "$rule=Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue; " +
                "if($null -ne $rule){$rule | Disable-NetFirewallRule}; " +
                "$service=Get-Service -Name 'sshd' -ErrorAction SilentlyContinue; " +
                "if($null -ne $service){if($service.Status -ne 'Stopped'){Stop-Service -Name 'sshd' -Force}; Set-Service -Name 'sshd' -StartupType Disabled}"
            ],
            TimeSpan.FromSeconds(45),
            CancellationToken.None);
        EnsureCapabilityCommandSucceeded(isolated, "Setup could not contain the OpenSSH service and firewall rule it installed");
        await WriteSetupJournalAsync(journalPath, "isolated", CancellationToken.None);
    }

    private static async Task<ProcessResult> InstallOpenSshCapabilityAsync(
        string capabilityName,
        string displayName,
        CancellationToken cancellationToken)
    {
        const string command = "/Online /Add-Capability /NoRestart";
        var timeout = TimeSpan.FromMinutes(30);
        var dismLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "DISM", "dism.log");
        await SetupDiagnostics.WriteAsync(
            $"Starting Windows capability installation: {displayName}",
            $"Command: dism.exe {command} /CapabilityName:{capabilityName}{Environment.NewLine}" +
            $"Timeout: {timeout.TotalMinutes:0} minutes{Environment.NewLine}" +
            $"DISM log: {dismLog}",
            cancellationToken);

        try
        {
            var result = await RunSystemToolAsync(
                "dism.exe",
                ["/Online", "/Add-Capability", $"/CapabilityName:{capabilityName}", "/NoRestart"],
                timeout,
                cancellationToken);
            await SetupDiagnostics.WriteAsync(
                $"Windows capability installation completed: {displayName}",
                $"Exit code: {result.ExitCode}{Environment.NewLine}" +
                $"Standard output:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
                $"Standard error:{Environment.NewLine}{result.StandardError}",
                CancellationToken.None);
            return result;
        }
        catch (ProcessTimeoutException exception)
        {
            await SetupDiagnostics.WriteAsync(
                $"Windows capability installation timed out: {displayName}",
                $"Timeout: {exception.Timeout.TotalMinutes:0} minutes{Environment.NewLine}" +
                $"Standard output:{Environment.NewLine}{exception.StandardOutput}{Environment.NewLine}" +
                $"Standard error:{Environment.NewLine}{exception.StandardError}",
                CancellationToken.None);
            throw new TimeoutException(
                $"Windows timed out while installing {displayName} after {exception.Timeout.TotalMinutes:0} minutes. " +
                $"Review {SetupDiagnostics.LogPath} and {dismLog}, then ensure Windows Update servicing is available and retry setup.",
                exception);
        }
    }

    private static void EnsureCapabilityCommandSucceeded(ProcessResult result, string message)
    {
        if (result.Succeeded || result.ExitCode == 3010) return;
        var detail = new[] { result.StandardError, result.StandardOutput }
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.Length != 0);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}");
    }

    private static async Task WriteSetupJournalAsync(
        string path,
        string phase,
        CancellationToken cancellationToken)
    {
        var value = $"{{\"schemaVersion\":1,\"phase\":\"{phase}\",\"updatedAt\":\"{DateTimeOffset.UtcNow:O}\"}}";
        await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
            path, System.Text.Encoding.UTF8.GetBytes(value), cancellationToken);
    }

    private async Task<TailscaleInstallation> EnsureTailscaleInstalledAsync(
        string tempDirectory,
        bool allowJournalOwnedReuse,
        CancellationToken cancellationToken)
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        var artifact = DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture);
        var wasReady = File.Exists(installed)
                       && (allowJournalOwnedReuse || OpticonComponentIntegration.IsManagedByOpticon("Private Network"))
                       && await InstalledDependencyMatchesAsync(installed, artifact, runVersionCommand: true, cancellationToken);
        await RecordPreviousComponentVersionAsync(
            "Tailscale", installed, wasReady, cancellationToken);
        var installedByOpticon = await EnsureTailscaleInstalledCoreAsync(
            tempDirectory, allowJournalOwnedReuse, cancellationToken);
        var result = wasReady
            ? InstallerEnsureResult.Ready(
                "EnsureTailscaleInstalledAsync",
                $"Pinned Tailscale {artifact.Version} is installed and verified.")
            : InstallerEnsureResult.Repaired(
                "EnsureTailscaleInstalledAsync",
                $"Pinned Tailscale {artifact.Version} is installed and verified.",
                "Repaired or upgraded the private-network component.");
        await RecordEnsureOutcomeAsync(result, cancellationToken,
            wasReady ? null : "Tailscale");
        return new TailscaleInstallation(installedByOpticon, result);
    }

    private async Task<bool> EnsureTailscaleInstalledCoreAsync(
        string tempDirectory,
        bool allowJournalOwnedReuse,
        CancellationToken cancellationToken)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        var artifact = DependencyArtifacts.Tailscale(RuntimeInformation.ProcessArchitecture);
        if (File.Exists(installed)
            && (allowJournalOwnedReuse || OpticonComponentIntegration.IsManagedByOpticon("Private Network"))
            && await InstalledDependencyMatchesAsync(installed, artifact, runVersionCommand: true, cancellationToken))
        {
            _progress.Report(new InstallProgress(12, $"Pinned Tailscale {artifact.Version} is already managed by Opticon."));
            return true;
        }

        _progress.Report(new InstallProgress(10, $"Downloading Opticon's private-network component ({artifact.Version})…"));
        await using var installer = await DownloadVerifiedAsync(artifact, tempDirectory, cancellationToken);
        _progress.Report(new InstallProgress(18, "Upgrading or repairing the Opticon private-network component…"));
        var existingProductCode = FindInstalledMsiProductCode(["Tailscale"]);
        var result = await InstallVerifiedMsiAsync(installer, cancellationToken);
        if (!result.Succeeded && result.ExitCode != 3010
            && !string.IsNullOrWhiteSpace(existingProductCode)
            && IsMsiUpgradeConflict(result))
        {
            // Prefer MSI upgrade/repair first. Only a positive MSI version
            // conflict with a discovered product code may trigger replacement;
            // an arbitrary standalone executable is never removed blindly.
            await RemoveStandaloneComponentAsync(
                "Tailscale", ["Tailscale"], installed, cancellationToken);
            result = await InstallVerifiedMsiAsync(installer, cancellationToken);
        }
        EnsureSuccess(result, "Tailscale installation failed");
        if (!File.Exists(installed)
            || !await InstalledDependencyMatchesAsync(installed, artifact, runVersionCommand: true, cancellationToken))
            throw new InvalidDataException($"Tailscale installed, but its version is not the pinned {artifact.Version}.");
        return true;
    }

    private async Task<LocalTailscaleSnapshot> EnsureTailnetEnrollmentAsync(
        string tailscale,
        bool canResumeExistingSession,
        CancellationToken cancellationToken)
    {
        var journal = RequireMachineInstallTransaction();
        var existing = await TryReadTailscaleStatusAsync(tailscale, cancellationToken);
        var matchesRecordedNode = existing is not null
                                  && !string.IsNullOrWhiteSpace(journal.TailscaleNodeIdentity)
                                  && FixedAsciiEquals(existing.DeviceId, journal.TailscaleNodeIdentity);
        var reusedEnrollment = existing is { Online: true }
                               && (!ValidationEnabled(ClientInstallValidationStep.NetworkIdentity)
                                   || (ExistingSessionHasExpectedRole(existing)
                                       && (canResumeExistingSession
                                           || matchesRecordedNode
                                           || ExistingSessionHasExpectedDeviceName(existing))));
        var repaired = false;
        LocalTailscaleSnapshot snapshot;
        if (reusedEnrollment)
        {
            _progress.Report(new InstallProgress(31,
                "The expected Opticon network identity is already present; verifying its node identity."));
            snapshot = existing!;
        }
        else
        {
            if (existing is { Online: true } && !string.IsNullOrWhiteSpace(existing.Ip))
            {
                if (!_allowTailscaleReauthentication && !journal.TailscaleReauthenticationApproved)
                {
                    MachineInstallTransactionPersistence.RecordTailscaleDecision(
                        journal, reauthenticationApproved: false);
                    await MachineInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
                    await RecordEnsureOutcomeAsync(InstallerEnsureResult.Blocked(
                        "EnsureTailnetEnrollmentAsync",
                        "The existing Tailscale identity requires one explicit reauthentication decision."),
                        cancellationToken);
                    throw new ExistingTailscaleSessionException(
                        "This machine is already connected to Tailscale. To consume this single-use invitation and enforce its exact tailnet and role, Opticon must reauthenticate it with the new invitation.");
                }

                if (_allowTailscaleReauthentication && !journal.TailscaleReauthenticationApproved)
                {
                    MachineInstallTransactionPersistence.RecordTailscaleDecision(
                        journal, reauthenticationApproved: true);
                    await MachineInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
                }
            }

            await AdvanceMachineInstallTransactionAsync(
                MachineInstallTransactionPhase.NetworkEnrollmentStarted, cancellationToken);
            var up = await RunPrivilegedChildAsync(tailscale,
                TailscaleCommandLine.BuildEnrollmentArguments(
                    _invite.HeadscaleLoginUrl, _invite.TailscaleAuthKey,
                    TailscaleCommandLine.NormalizeHostName(_invite.DeviceName, Environment.MachineName)),
                TimeSpan.FromMinutes(2), cancellationToken);
            if (!up.Succeeded)
            {
                // `tailscale up` can lose its response after the daemon has
                // accepted the key. Query the resulting local node before ever
                // considering another enrollment attempt or key use.
                var afterUncertainUp = await TryReadTailscaleStatusAsync(tailscale, cancellationToken);
                if (afterUncertainUp is { Online: true }
                    && ExistingSessionHasExpectedRole(afterUncertainUp)
                    && !string.IsNullOrWhiteSpace(afterUncertainUp.Ip))
                {
                    snapshot = afterUncertainUp;
                }
                else
                {
                    EnsureSuccess(up, "Tailscale could not join the tailnet");
                    throw new InvalidOperationException("Tailscale enrollment did not produce a queryable node identity.");
                }
            }
            else
            {
                snapshot = await WaitForExpectedTailscaleSessionAsync(tailscale, cancellationToken);
            }
            repaired = true;
        }

        if (ValidationEnabled(ClientInstallValidationStep.NetworkIdentity)
            && !ExistingSessionHasExpectedRole(snapshot))
            throw new InvalidOperationException(
                "Tailscale joined, but the resulting tailnet or device tags do not exactly match this invitation.");
        if (ValidationEnabled(ClientInstallValidationStep.NetworkIdentity)
            && (string.IsNullOrWhiteSpace(snapshot.Ip) || string.IsNullOrWhiteSpace(snapshot.DeviceId)))
            throw new InvalidOperationException(
                "Tailscale joined, but did not publish an address and stable node identity.");

        MachineInstallTransactionPersistence.RecordTailscaleDecision(
            journal,
            journal.TailscaleReauthenticationApproved || _allowTailscaleReauthentication,
            snapshot.DeviceId);
        await MachineInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        await AdvanceMachineInstallTransactionAsync(
            MachineInstallTransactionPhase.NetworkEnrolled, cancellationToken);

        if (_invite.AdvertiseExitNode)
        {
            _progress.Report(new InstallProgress(38, "Enabling this machine as an exit node…"));
            var advertise = await RunPrivilegedChildAsync(
                tailscale, ["set", "--advertise-exit-node"], TimeSpan.FromSeconds(30), cancellationToken);
            if (!advertise.Succeeded)
            {
                AddInstallWarning(
                    "Exit-node advertisement",
                    FirstProcessFailureDetail(
                        advertise,
                        "Tailscale joined the private mesh but could not advertise the optional exit route."));
            }
        }
        await AdvanceMachineInstallTransactionAsync(
            MachineInstallTransactionPhase.NetworkPolicyApplied, cancellationToken);

        var result = repaired
            ? InstallerEnsureResult.Repaired(
                "EnsureTailnetEnrollmentAsync",
                "The verified Tailscale node has the invitation's tailnet role, tags, address, and recorded node identity.",
                "Joined or reconciled the private-network identity.")
            : InstallerEnsureResult.Ready(
                "EnsureTailnetEnrollmentAsync",
                "The verified Tailscale node has the invitation's tailnet role, tags, address, and recorded node identity.");
        await RecordEnsureOutcomeAsync(result, cancellationToken,
            repaired ? "TailnetEnrollment" : null);
        return snapshot;
    }

    private async Task<ComponentInstallation> EnsureRustDeskAsync(
        string tempDirectory,
        bool allowJournalOwnedReuse,
        CancellationToken cancellationToken)
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        var artifact = DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture);
        var wasReady = File.Exists(installed)
                       && (allowJournalOwnedReuse || OpticonComponentIntegration.IsManagedByOpticon("Remote Access"))
                       && await InstalledDependencyMatchesAsync(installed, artifact, runVersionCommand: false, cancellationToken);
        await RecordPreviousComponentVersionAsync(
            "RustDesk", installed, wasReady, cancellationToken);
        var installation = await EnsureRustDeskInstalledCoreAsync(
            tempDirectory, allowJournalOwnedReuse, cancellationToken);
        var result = wasReady
            ? InstallerEnsureResult.Ready(
                "EnsureRustDeskAsync",
                $"Pinned RustDesk {artifact.Version} is installed and verified.")
            : InstallerEnsureResult.Repaired(
                "EnsureRustDeskAsync",
                $"Pinned RustDesk {artifact.Version} is installed and verified.",
                "Repaired or replaced the remote-access component.");
        await RecordEnsureOutcomeAsync(result, cancellationToken,
            wasReady ? null : "RustDesk");
        return installation;
    }

    private async Task<ComponentInstallation> EnsureRustDeskInstalledCoreAsync(
        string tempDirectory,
        bool allowJournalOwnedReuse,
        CancellationToken cancellationToken)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        var artifact = DependencyArtifacts.RustDesk(RuntimeInformation.ProcessArchitecture);
        if (File.Exists(installed)
            && (allowJournalOwnedReuse || OpticonComponentIntegration.IsManagedByOpticon("Remote Access"))
            && await InstalledDependencyMatchesAsync(installed, artifact, runVersionCommand: false, cancellationToken))
        {
            _progress.Report(new InstallProgress(47, $"Pinned RustDesk {artifact.Version} is already managed by Opticon."));
            return new ComponentInstallation(installed, true);
        }

        _progress.Report(new InstallProgress(49, $"Downloading Opticon's remote-access component ({artifact.Version})…"));
        await using var installer = await DownloadVerifiedAsync(artifact, tempDirectory, cancellationToken);
        _progress.Report(new InstallProgress(56, "Upgrading or repairing the Opticon remote-access component…"));
        var existingProductCode = FindInstalledMsiProductCode(["RustDesk", "RustDesk Remote Desktop"]);
        var install = await InstallVerifiedMsiAsync(installer, cancellationToken);
        if (!install.Succeeded && install.ExitCode != 3010
            && !string.IsNullOrWhiteSpace(existingProductCode)
            && IsMsiUpgradeConflict(install))
        {
            await RemoveStandaloneComponentAsync(
                "RustDesk", ["RustDesk", "RustDesk Remote Desktop"], installed, cancellationToken);
            install = await InstallVerifiedMsiAsync(installer, cancellationToken);
        }
        EnsureSuccess(install, "RustDesk installation failed");

        for (var attempt = 0; attempt < 20 && !File.Exists(installed); attempt++)
            await Task.Delay(500, cancellationToken);
        if (!File.Exists(installed)
            || !await InstalledDependencyMatchesAsync(installed, artifact, runVersionCommand: false, cancellationToken))
            throw new InvalidDataException($"RustDesk installed, but its version is not the pinned {artifact.Version}.");
        return new ComponentInstallation(installed, true);
    }
    private async Task ConfigureRustDeskAsync(string rustDesk, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(61, "Securing RustDesk for direct Tailscale access…"));
        var service = await RunSystemToolAsync("sc.exe", ["query", "RustDesk"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!service.Succeeded)
        {
            // RustDesk starts long-lived service/session children which inherit redirected
            // standard handles. Capturing them would wait forever after this command exits.
            var installService = await RunPrivilegedChildAsync(rustDesk, ["--install-service"],
                TimeSpan.FromSeconds(20), cancellationToken, captureOutput: false);
            EnsureSuccess(installService, "RustDesk service installation failed");
        }
        _ = await RunSystemToolAsync("sc.exe", ["stop", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        var disabled = await RunSystemToolAsync("sc.exe", ["config", "RustDesk", "start=", "disabled"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(disabled, "RustDesk could not be held disabled while its private configuration was applied");
        var recovery = await RunSystemToolAsync("sc.exe",
            ["failure", "RustDesk", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"],
            TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(recovery, "RustDesk service recovery could not be configured");
        var failureFlag = await RunSystemToolAsync("sc.exe", ["failureflag", "RustDesk", "1"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(failureFlag, "RustDesk non-crash failure recovery could not be configured");


        // RustDesk 1.4.x can launch a long-lived child while setting the password.
        // Do not redirect its inherited handles: they would otherwise keep Setup
        // waiting after the password command itself has completed.
        var password = await RunPrivilegedChildAsync(rustDesk, ["--password", _invite.RustDeskPassword],
            TimeSpan.FromSeconds(15), cancellationToken, captureOutput: false);
        EnsureSuccess(password, "RustDesk password provisioning failed");

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        RustDeskServiceProfileStore.HardenAll();
        var automatic = await RunSystemToolAsync("sc.exe", ["config", "RustDesk", "start=", "auto"], TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSuccess(automatic, "RustDesk could not be configured for automatic startup");
        var restart = await RunSystemToolAsync("sc.exe", ["start", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(restart, "The private RustDesk service could not be restarted");
    }

    private async Task<InstallerEnsureResult> EnsureRustDeskListenerAsync(
        string rustDesk,
        CancellationToken cancellationToken)
    {
        await ConfigureRustDeskAsync(rustDesk, cancellationToken);
        if (!ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
            return InstallerEnsureResult.Ready(
                "EnsureRustDeskAsync",
                "RustDesk configuration commands completed; listener validation was disabled by release policy.");
        var repaired = false;
        if (!await WaitForListeningExecutableAsync(
                21118, rustDesk, TimeSpan.FromSeconds(90), cancellationToken))
        {
            repaired = true;
            _progress.Report(new InstallProgress(66, "Repairing the RustDesk private listener…"));
            await ConfigureRustDeskAsync(rustDesk, cancellationToken);
            if (!await WaitForListeningExecutableAsync(
                    21118, rustDesk, TimeSpan.FromSeconds(90), cancellationToken))
                throw new InvalidOperationException(
                    "RustDesk did not open its private direct-access listener on TCP 21118 after an automatic repair.");
        }
        var result = repaired
            ? InstallerEnsureResult.Repaired(
                "EnsureRustDeskAsync",
                "The Opticon-owned RustDesk service is running with a verified private TCP 21118 listener.",
                "Restarted and reconfigured the RustDesk service.")
            : InstallerEnsureResult.Ready(
                "EnsureRustDeskAsync",
                "The Opticon-owned RustDesk service is running with a verified private TCP 21118 listener.");
        await RecordEnsureOutcomeAsync(result, cancellationToken,
            repaired ? "RustDeskService" : null);
        return result;
    }

    private async Task EnsureMachineInstallTransactionAsync(
        SourceInstallationBinding sourceBinding,
        CancellationToken cancellationToken)
    {
        if (_machineInstallTransaction is not null)
        {
            if (ValidationEnabled(ClientInstallValidationStep.MachineState))
                MachineInstallTransactionPersistence.RequireMatches(
                    _machineInstallTransaction, sourceBinding);
            return;
        }

        var journal = MachineInstallTransactionPersistence.Create(sourceBinding);
        await MachineInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        _machineInstallTransaction = journal;
    }

    private MachineInstallTransactionJournal RequireMachineInstallTransaction()
    {
        return _machineInstallTransaction
               ?? throw new InvalidOperationException(
                   "The protected machine-install transaction was not established before a machine mutation.");
    }

    private async Task AdvanceMachineInstallTransactionAsync(
        MachineInstallTransactionPhase next,
        CancellationToken cancellationToken)
    {
        var journal = RequireMachineInstallTransaction();
        if (journal.Phase >= next) return;
        var previous = journal.Phase;
        MachineInstallTransactionPersistence.Advance(journal, next);
        try
        {
            await MachineInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        }
        catch
        {
            journal.Phase = previous;
            throw;
        }
    }

    private async Task CompleteMachineInstallTransactionAsync(CancellationToken cancellationToken)
    {
        if (_machineInstallTransaction is null) return;
        await AdvanceMachineInstallTransactionAsync(
            MachineInstallTransactionPhase.EnrollmentReceiptWritten, cancellationToken);
        MachineInstallTransactionPersistence.Delete();
        _machineInstallTransaction = null;
    }

    private async Task<InstallerEnsureResult> EnsureAgentAsync(
        string source,
        string tailscaleIp,
        CancellationToken cancellationToken)
    {
        if (await IsPendingAgentReadyAsync(source, tailscaleIp, cancellationToken))
        {
            var ready = InstallerEnsureResult.Ready(
                "EnsureAgentAsync",
                "The pending Agent payload, protected configuration, and exact SYSTEM scheduled task are verified.");
            await RecordEnsureOutcomeAsync(ready, cancellationToken);
            return ready;
        }

        await InstallAgentCoreAsync(source, tailscaleIp, cancellationToken);
        if (ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
        {
            await VerifyInstalledExecutableDirectoryAsync(AppPaths.AgentInstallDirectory, cancellationToken);
            await RequireExactAgentTaskAsync(cancellationToken);
        }
        var result = InstallerEnsureResult.Repaired(
            "EnsureAgentAsync",
            "The Agent payload, protected configuration, and exact SYSTEM scheduled task are verified.",
            "Atomically installed or repaired the pending Agent generation.");
        await RecordEnsureOutcomeAsync(result, cancellationToken, "TaildeskAgent");
        return result;
    }

    private async Task<InstallerEnsureResult> EnsureExistingAgentAsync(
        string source,
        AgentConfig completedState,
        string tailscaleIp,
        CancellationToken cancellationToken)
    {
        if (ValidationEnabled(ClientInstallValidationStep.MachineState)
            && !EnrollmentMatchesInvitation(completedState))
            throw new InvalidDataException("The completed Agent configuration no longer matches this invitation.");

        if (await IsCompletedAgentReadyAsync(source, completedState, tailscaleIp, cancellationToken))
        {
            var ready = InstallerEnsureResult.Ready(
                "EnsureAgentAsync",
                "The completed Agent payload, protected configuration, and exact SYSTEM task remain valid.");
            await RecordEnsureOutcomeAsync(ready, cancellationToken);
            return ready;
        }

        // A completed invitation is an idempotent enrollment identity, not an
        // excuse to leave a broken Agent generation or task in place. Reuse
        // the protected completed configuration so this repair does not spend
        // another invitation or drop the existing device identity.
        await InstallAgentCoreAsync(source, tailscaleIp, completedState, cancellationToken);
        if (ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
        {
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            await RequireExactAgentTaskAsync(cancellationToken);
        }
        var result = InstallerEnsureResult.Repaired(
            "EnsureAgentAsync",
            "The completed Agent payload, protected configuration, and exact SYSTEM task were repaired without re-enrollment.",
            "Atomically promoted the current signed Agent generation and retained the completed invitation identity.");
        await RecordEnsureOutcomeAsync(result, cancellationToken);
        return result;
    }

    private async Task InstallAgentCoreAsync(
        string source,
        string tailscaleIp,
        CancellationToken cancellationToken) =>
        await InstallAgentCoreAsync(
            source, tailscaleIp, completedEnrollment: null, cancellationToken: cancellationToken);

    private async Task InstallAgentCoreAsync(
        string source,
        string tailscaleIp,
        AgentConfig? completedEnrollment,
        CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(70, "Installing the Opticon background agent…"));
        var destination = AppPaths.AgentInstallDirectory;
        if (_agentInstallTransaction is null)
        {
            var operationId = Guid.NewGuid();
            var candidate = AgentInstallTransactionPersistence.CandidateDirectory(operationId);
            var rollback = AgentInstallTransactionPersistence.RollbackDirectory(operationId);
            var failed = AgentInstallTransactionPersistence.FailedDirectory(operationId);
            RequireAgentTransactionPath(candidate, operationId, "installing");
            RequireAgentTransactionPath(rollback, operationId, "rollback");
            RequireAgentTransactionPath(failed, operationId, "failed");
            if (File.Exists(destination))
                throw new InvalidDataException("The Agent installation path is a file.");
            var hadPreviousAgent = Directory.Exists(destination);
            if (hadPreviousAgent
                && (ValidationEnabled(ClientInstallValidationStep.PayloadAuthenticity)
                    || ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions)))
                await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
            var previousAgentFiles = hadPreviousAgent
                ? await CreateAgentInstallFileRecordsAsync(destination, cancellationToken)
                : [];
            var previousConfig = File.Exists(AppPaths.AgentConfigFile)
                ? MachineStorageSecurity.ReadRestrictedFile(AppPaths.AgentConfigFile, 4 * 1024 * 1024)
                : [];
            var previousReceipt = File.Exists(AppPaths.InstallReceiptFile)
                ? MachineStorageSecurity.ReadRestrictedFile(AppPaths.InstallReceiptFile, 256 * 1024)
                : [];
            var previousTaskXml = await CaptureAgentTaskSnapshotAsync(
                hadPreviousAgent, destination, cancellationToken);
            var journal = new AgentInstallTransactionJournal
            {
                OperationId = operationId,
                InviteId = _invite.InviteId,
                Phase = AgentInstallTransactionPhase.Preparing,
                HadPreviousAgent = hadPreviousAgent,
                HadPreviousConfig = previousConfig.Length > 0,
                HadPreviousReceipt = previousReceipt.Length > 0,
                HadPreviousTask = previousTaskXml is not null,
                PreviousTaskXml = previousTaskXml ?? string.Empty,
                PreviousConfig = previousConfig,
                PreviousReceipt = previousReceipt,
                PreviousAgentFiles = previousAgentFiles
            };
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            _agentInstallTransaction = journal;

            CopyDirectory(source, candidate);
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, candidate, verifyDestinationExecutables: false, cancellationToken);

            _ = await RunSystemToolAsync(
                "schtasks.exe", ["/End", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(15), cancellationToken);
            await RequireAgentProcessesClosedAsync(destination, cancellationToken);
            if (hadPreviousAgent)
            {
                if (ValidationEnabled(ClientInstallValidationStep.PayloadAuthenticity)
                    || ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
                    await VerifyInstalledExecutableDirectoryAsync(destination, cancellationToken);
                journal.PreviousAgentFiles = await CreateAgentInstallFileRecordsAsync(destination, cancellationToken);
            }
            CryptographicOperations.ZeroMemory(journal.PreviousConfig);
            journal.PreviousConfig = File.Exists(AppPaths.AgentConfigFile)
                ? MachineStorageSecurity.ReadRestrictedFile(AppPaths.AgentConfigFile, 4 * 1024 * 1024)
                : [];
            journal.HadPreviousConfig = journal.PreviousConfig.Length > 0;
            CryptographicOperations.ZeroMemory(journal.PreviousReceipt);
            journal.PreviousReceipt = File.Exists(AppPaths.InstallReceiptFile)
                ? MachineStorageSecurity.ReadRestrictedFile(AppPaths.InstallReceiptFile, 256 * 1024)
                : [];
            journal.HadPreviousReceipt = journal.PreviousReceipt.Length > 0;
            journal.StateSnapshotReady = true;
            journal.TaskSnapshotReady = true;
            journal.Phase = AgentInstallTransactionPhase.CandidateReady;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            if (hadPreviousAgent)
            {
                if (Directory.Exists(rollback) || File.Exists(rollback))
                    throw new InvalidOperationException("The Agent rollback directory is already occupied.");
                Directory.Move(destination, rollback);
                journal.Phase = AgentInstallTransactionPhase.PreviousMoved;
                await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            }
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("The Agent destination changed during its protected swap.");
            Directory.Move(candidate, destination);
            journal.Phase = AgentInstallTransactionPhase.CandidateActivated;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, destination, verifyDestinationExecutables: true, CancellationToken.None);
        }
        else
        {
            if (ValidationEnabled(ClientInstallValidationStep.MachineState)
                && (_agentInstallTransaction.InviteId != _invite.InviteId
                    || _agentInstallTransaction.Phase is < AgentInstallTransactionPhase.CandidateActivated
                        or >= AgentInstallTransactionPhase.RollbackStarted))
                throw new InvalidDataException("The recovered Agent installation transaction cannot resume this invitation.");
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, destination, verifyDestinationExecutables: true, cancellationToken);
        }

        var roots = BuildSharedRoots(_invite.AllowedRoots);
        // A known folder is optional shared content. Its absence must not
        // prevent the recovery agent from converging; it can be added on a
        // later repair run after the interactive user creates or restores it.
        if (roots.Count == 0)
            _progress.Report(new InstallProgress(73,
                "No requested user folders are present yet; installing the agent without shared folders."));
        var config = completedEnrollment ?? new AgentConfig
        {
            DeviceName = _invite.DeviceName,
            Role = _invite.Role,
            BindAddress = tailscaleIp,
            AgentTokenHash = SecurityHelpers.HashToken(_invite.AgentToken),
            MediaSigningKeyProtected = SecretProtector.Protect(SecurityHelpers.CreateToken(), SecretScope.LocalMachine),
            UpdateHealthTokenProtected = SecretProtector.Protect(SecurityHelpers.CreateToken(), SecretScope.LocalMachine),
            CoordinatorUrl = _invite.CoordinatorUrl,
            PendingInviteId = _invite.InviteId,
            PendingInviteSecretProtected = SecretProtector.Protect(_invite.InviteSecret, SecretScope.LocalMachine),
            AdvertiseExitNode = _invite.AdvertiseExitNode,
            SharedRoots = roots,
            ExposeAllLocalVolumes = false,
            ControllerShortcutPaths = []
        };
        if (completedEnrollment is not null)
        {
            config.BindAddress = tailscaleIp;
            config.AdvertiseExitNode = _invite.AdvertiseExitNode;
            config.SharedRoots = roots;
            config.ExposeAllLocalVolumes = false;
            config.PendingInviteId = null;
            config.PendingInviteSecretProtected = string.Empty;
            config.CompletedInviteId = _invite.InviteId;
        }
        await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).SaveAsync(config, cancellationToken);

        var agentExe = Path.Combine(destination, "Taildesk.Agent.exe");
        await RegisterExactAgentTaskAsync(agentExe, cancellationToken);
        var activeJournal = _agentInstallTransaction
                            ?? throw new InvalidOperationException("The protected Agent transaction disappeared before task commit.");
        activeJournal.Phase = AgentInstallTransactionPhase.AgentTaskApplied;
        await AgentInstallTransactionPersistence.SaveAsync(activeJournal, cancellationToken);
    }

    private async Task<bool> IsPendingAgentReadyAsync(
        string source,
        string tailscaleIp,
        CancellationToken cancellationToken)
    {
        if (_agentInstallTransaction is not null || !File.Exists(AppPaths.AgentConfigFile)) return false;
        try
        {
            var state = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
                .LoadAsync(cancellationToken);
            if (state.PendingInviteId != _invite.InviteId
                || !state.BindAddress.Equals(tailscaleIp, StringComparison.OrdinalIgnoreCase)
                || !FixedAsciiEquals(state.AgentTokenHash, SecurityHelpers.HashToken(_invite.AgentToken)))
                return false;
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            await RequireExactAgentTaskAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The transaction path below revalidates every byte before it
            // promotes a replacement. This probe merely decides Ready versus
            // a safe repair attempt; it never adopts the failed state.
            return false;
        }
    }

    private async Task<bool> IsCompletedAgentReadyAsync(
        string source,
        AgentConfig completedState,
        string tailscaleIp,
        CancellationToken cancellationToken)
    {
        if (!completedState.BindAddress.Equals(tailscaleIp, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            await RequireExactAgentTaskAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task RequireExactAgentTaskAsync(CancellationToken cancellationToken)
    {
        var task = await QueryAgentTaskXmlAsync(cancellationToken)
                   ?? throw new InvalidDataException("The Opticon Agent task is missing.");
        RequireExactAgentTaskXml(
            task, Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe"));
    }

    private async Task AcquireAgentInstallLockAsync(CancellationToken cancellationToken)
    {
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory);
        _ = await MachineStorageSecurity.WriteRestrictedFileCreateNewAsync(
            AppPaths.AgentInstallTransactionLockFile, new byte[] { 0x01 }, cancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                MachineStorageSecurity.RequireRestrictedFile(AppPaths.AgentInstallTransactionLockFile);
                _agentInstallLock = new FileStream(
                    AppPaths.AgentInstallTransactionLockFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    1,
                    FileOptions.None);
                return;
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Another Agent installation still owns the protected transaction lock.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private async Task<string?> CaptureAgentTaskSnapshotAsync(
        bool hadPreviousAgent,
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var xml = await QueryAgentTaskXmlAsync(cancellationToken);
        if (!ValidationEnabled(ClientInstallValidationStep.MachineState)) return xml;
        if (hadPreviousAgent)
        {
            if (xml is null)
            {
                // A signed Agent directory without its task is a repairable
                // partial installation. There is no task state to restore on
                // rollback, so the replacement transaction safely records no
                // previous task and recreates the exact Opticon-owned task.
                return null;
            }
            try
            {
                RequireExactAgentTaskXml(xml, Path.Combine(installedDirectory, "Taildesk.Agent.exe"));
                return xml;
            }
            catch (InvalidDataException) when (IsOpticonOwnedAgentTask(xml, installedDirectory))
            {
                // A recognizable legacy/drifted Opticon task is repaired by
                // the replacement install below. It is intentionally not
                // restored on rollback because its nonexact command is not a
                // safe task contract to preserve.
                return null;
            }
        }
        if (xml is not null && !IsOpticonOwnedAgentTask(xml, installedDirectory))
            throw new InvalidDataException(
                "An unrelated Taildesk Agent task blocks installation; Opticon will not replace a task it does not own.");
        return null;
    }

    private static bool IsOpticonOwnedAgentTask(string xml, string installedDirectory)
    {
        try
        {
            var document = ParseTaskXml(xml);
            XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var description = document.Root?.Element(task + "RegistrationInfo")?.Element(task + "Description")?.Value;
            var command = document.Root?.Element(task + "Actions")?.Element(task + "Exec")?.Element(task + "Command")?.Value;
            if (string.IsNullOrWhiteSpace(command)) return false;
            var fullCommand = Path.GetFullPath(command);
            var expectedCommand = Path.GetFullPath(Path.Combine(
                installedDirectory, "Taildesk.Agent.exe"));
            return string.Equals(description, AgentTaskOwnershipDescription, StringComparison.Ordinal)
                ? IsPathWithinDirectory(fullCommand, AppPaths.InstallDirectory)
                  || IsPathWithinDirectory(fullCommand, installedDirectory)
                // Older Opticon tasks did not carry the ownership description.
                // Only the exact fixed Agent executable is a safe legacy
                // ownership marker; a same-named task that points elsewhere
                // remains blocked as user-owned or ambiguous.
                : fullCommand.Equals(expectedCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsMinimallyOwnedAgentTask(string xml, string expectedExecutable)
    {
        try
        {
            var document = ParseTaskXml(xml);
            XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = document.Root;
            var actions = root?.Element(task + "Actions")?.Elements().ToArray() ?? [];
            var principals = root?.Element(task + "Principals")
                ?.Elements(task + "Principal").ToArray() ?? [];
            var triggers = root?.Element(task + "Triggers")?.Elements().ToArray() ?? [];
            var exec = actions.SingleOrDefault();
            var principal = principals.SingleOrDefault();
            var command = exec?.Element(task + "Command")?.Value ?? string.Empty;
            var arguments = exec?.Element(task + "Arguments")?.Value ?? string.Empty;
            return root?.Name == task + "Task"
                   && actions.Length == 1
                   && exec?.Name == task + "Exec"
                   && principals.Length == 1
                   && triggers.Length == 1
                   && triggers[0].Name == task + "BootTrigger"
                   && string.Equals(
                       Path.GetFullPath(command),
                       Path.GetFullPath(expectedExecutable),
                       StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrWhiteSpace(arguments)
                   && IsSystemHighestPrincipal(principal, task);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<string?> QueryAgentTaskXmlAsync(CancellationToken cancellationToken)
        => await QueryTaskXmlAsync(AgentTaskName, cancellationToken);

    private static async Task<string?> QueryTaskXmlAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        var result = await RunSystemToolAsync(
            "schtasks.exe", ["/Query", "/TN", taskName, "/XML"],
            TimeSpan.FromSeconds(20), cancellationToken);
        if (!result.Succeeded)
        {
            if (!await IsTaskPresentAtFixedNameAsync(taskName, cancellationToken)) return null;
            var detail = (result.StandardError + " " + result.StandardOutput).Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Windows could not query scheduled task '{taskName}' (exit code {result.ExitCode})."
                    : $"Windows could not query scheduled task '{taskName}': {detail}");
        }
        var xml = result.StandardOutput.TrimStart('\uFEFF', '\r', '\n', ' ');
        if (xml.Length is <= 0 or > 256 * 1024)
            throw new InvalidDataException("The Agent scheduled-task XML has an invalid size.");
        _ = ParseTaskXml(xml);
        return xml;
    }

    private static async Task<bool> IsTaskPresentAtFixedNameAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        var environment = BuildPrivilegedEnvironment()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        environment["TAILDESK_EXPECTED_TASK_NAME"] = taskName;
        var result = await ProcessRunner.RunAsync(
            SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Restricted",
                "-Command", TaskPresenceProbeScript
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken,
            environment: environment,
            clearEnvironment: true);
        return RequireTaskPresenceProbeResult(taskName, result);
    }

    private static bool RequireTaskPresenceProbeResult(string taskName, ProcessResult result)
    {
        if (result.Succeeded) return true;
        if (result.ExitCode == TaskPresenceProbeAbsentExitCode) return false;
        var detail = (result.StandardError + " " + result.StandardOutput).Trim();
        throw new InvalidDataException(
            string.IsNullOrWhiteSpace(detail)
                ? $"Windows could not prove whether scheduled task '{taskName}' is present (exit code {result.ExitCode})."
                : $"Windows could not prove whether scheduled task '{taskName}' is present: {detail}");
    }

    private static XDocument ParseTaskXml(string xml)
    {
        using var reader = System.Xml.XmlReader.Create(
            new StringReader(xml),
            new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024
            });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static void RequireExactAgentTaskXml(string xml, string expectedExecutable)
    {
        var document = ParseTaskXml(xml);
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root;
        var actions = root?.Element(task + "Actions")?.Elements().ToArray() ?? [];
        var principals = root?.Element(task + "Principals")?.Elements(task + "Principal").ToArray() ?? [];
        var triggers = root?.Element(task + "Triggers")?.Elements().ToArray() ?? [];
        var settings = root?.Element(task + "Settings");
        var exec = actions.SingleOrDefault();
        var principal = principals.SingleOrDefault();
        var expected = Path.GetFullPath(expectedExecutable);
        var command = exec?.Element(task + "Command")?.Value ?? string.Empty;
        var arguments = exec?.Element(task + "Arguments")?.Value ?? string.Empty;
        if (root?.Name != task + "Task"
            || actions.Length != 1 || exec?.Name != task + "Exec"
            || principals.Length != 1
            || triggers.Length != 1 || triggers[0].Name != task + "BootTrigger"
            || triggers[0].Element(task + "Enabled")?.Value != "true"
            || !string.Equals(command, expected, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(arguments)
            || !IsSystemHighestPrincipal(principal, task)
            || settings?.Element(task + "MultipleInstancesPolicy")?.Value != "IgnoreNew"
            || settings.Element(task + "DisallowStartIfOnBatteries")?.Value != "false"
            || settings.Element(task + "StopIfGoingOnBatteries")?.Value != "false"
            || settings.Element(task + "StartWhenAvailable")?.Value != "true"
            || settings.Element(task + "AllowStartOnDemand")?.Value != "true"
            || settings.Element(task + "Enabled")?.Value != "true"
            || settings.Element(task + "ExecutionTimeLimit")?.Value != "PT0S"
            || settings.Element(task + "RestartOnFailure")?.Element(task + "Interval")?.Value != "PT1M"
            || settings.Element(task + "RestartOnFailure")?.Element(task + "Count")?.Value != "20")
            throw new InvalidDataException("The Taildesk Agent task does not match the exact protected contract.");
    }

    private static string BuildExactAgentTaskXml(string executable)
    {
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
                new XElement(task + "Task", new XAttribute("version", "1.4"),
                new XElement(task + "RegistrationInfo",
                    new XElement(task + "Description", AgentTaskOwnershipDescription)),
                new XElement(task + "Triggers",
                    new XElement(task + "BootTrigger", new XElement(task + "Enabled", "true"))),
                new XElement(task + "Principals",
                    new XElement(task + "Principal", new XAttribute("id", "Author"),
                        new XElement(task + "UserId", "S-1-5-18"),
                        new XElement(task + "RunLevel", "HighestAvailable"))),
                new XElement(task + "Settings",
                    new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(task + "DisallowStartIfOnBatteries", "false"),
                    new XElement(task + "StopIfGoingOnBatteries", "false"),
                    new XElement(task + "StartWhenAvailable", "true"),
                    new XElement(task + "AllowStartOnDemand", "true"),
                    new XElement(task + "Enabled", "true"),
                    new XElement(task + "ExecutionTimeLimit", "PT0S"),
                    new XElement(task + "RestartOnFailure",
                        new XElement(task + "Interval", "PT1M"),
                        new XElement(task + "Count", "20"))),
                new XElement(task + "Actions", new XAttribute("Context", "Author"),
                    new XElement(task + "Exec",
                        new XElement(task + "Command", Path.GetFullPath(executable))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static async Task ImportAgentTaskXmlAsync(string xml, CancellationToken cancellationToken)
        => await ImportTaskXmlAsync(AgentTaskName, xml, cancellationToken);

    private static async Task ImportTaskXmlAsync(
        string taskName,
        string xml,
        CancellationToken cancellationToken)
    {
        xml = NormalizeSystemTaskXmlForImport(xml);
        var path = Path.Combine(
            AppPaths.SetupStagingDirectory, $"agent-task-{Guid.NewGuid():N}.xml");
        try
        {
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                path, System.Text.Encoding.UTF8.GetBytes(xml), cancellationToken);
            var result = await RunSystemToolAsync(
                "schtasks.exe", ["/Create", "/TN", taskName, "/XML", path, "/F"],
                TimeSpan.FromSeconds(30), cancellationToken);
            EnsureSystemToolSuccess(result, "Windows refused the protected Agent scheduled-task XML");
        }
        finally
        {
            MachineStorageSecurity.DeleteRestrictedFileIfExists(path);
        }
    }

    private static async Task RegisterExactAgentTaskAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var xml = BuildExactAgentTaskXml(executable);
        Exception? xmlRegistrationError = null;
        try
        {
            await ImportAgentTaskXmlAsync(xml, cancellationToken);
            var imported = await QueryAgentTaskXmlAsync(cancellationToken)
                           ?? throw new InvalidDataException("Windows did not retain the imported Agent scheduled task.");
            RequireExactAgentTaskXml(imported, executable);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Some Task Scheduler versions reject otherwise valid XML more
            // aggressively than the command-line registration API. Keep a
            // non-XML path so a schema quirk cannot strand the recovery Agent.
            xmlRegistrationError = exception;
        }

        try
        {
            await RegisterAgentTaskWithCommandLineFallbackAsync(executable, cancellationToken);
            var installed = await QueryAgentTaskXmlAsync(cancellationToken)
                            ?? throw new InvalidDataException("Windows did not retain the fallback Agent scheduled task.");
            RequireExactAgentTaskXml(installed, executable);
        }
        catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
        {
            Exception? cleanupError = null;
            try
            {
                await DeleteMinimallyOwnedAgentTaskIfPresentAsync(
                    executable, CancellationToken.None);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }
            throw new InvalidOperationException(
                "Windows rejected both protected Agent task registration methods.",
                new AggregateException(
                    new[] { xmlRegistrationError, fallbackError, cleanupError }
                        .OfType<Exception>()));
        }
    }

    private static async Task DeleteMinimallyOwnedAgentTaskIfPresentAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var current = await QueryAgentTaskXmlAsync(cancellationToken);
        if (current is null) return;
        if (!IsMinimallyOwnedAgentTask(current, executable))
            throw new InvalidDataException(
                "The failed Agent registration left a same-named task whose exact Opticon ownership could not be proven.");
        var delete = await RunSystemToolAsync(
            "schtasks.exe", ["/Delete", "/TN", AgentTaskName, "/F"],
            TimeSpan.FromSeconds(20), cancellationToken);
        EnsureSystemToolSuccess(
            delete, "The failed partial Agent task could not be removed");
        if (await QueryAgentTaskXmlAsync(cancellationToken) is not null)
            throw new InvalidDataException(
                "The failed partial Agent task still exists after cleanup.");
    }

    private static async Task RegisterAgentTaskWithCommandLineFallbackAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var command = $"\"{Path.GetFullPath(executable)}\"";
        var created = await RunSystemToolAsync(
            "schtasks.exe",
            ["/Create", "/TN", AgentTaskName, "/SC", "ONSTART", "/RU", "SYSTEM",
                "/RL", "HIGHEST", "/TR", command, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSystemToolSuccess(
            created, "Windows could not create the SYSTEM Agent task through its compatibility API");

        // schtasks.exe deliberately exposes only a subset of reliability
        // settings. Apply the same no-battery-block and restart contract after
        // the security principal, boot trigger, and exact command are fixed.
        const string settingsScript =
            "$ErrorActionPreference='Stop';" +
            "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries " +
            "-DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 20 " +
            "-RestartInterval (New-TimeSpan -Minutes 1) " +
            "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew;" +
            "Set-ScheduledTask -TaskPath '\\' -TaskName 'Taildesk Agent' -Settings $settings | Out-Null";
        var configured = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted",
                "-Command", settingsScript],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSystemToolSuccess(
            configured, "Windows could not apply the Agent task recovery settings");
    }

    private static bool IsSystemHighestPrincipal(XElement? principal, XNamespace task)
    {
        if (principal?.Element(task + "UserId")?.Value != "S-1-5-18"
            || principal.Element(task + "RunLevel")?.Value != "HighestAvailable")
            return false;

        // TASK_LOGON_SERVICE_ACCOUNT is value 5 in the COM/API enum, but
        // "ServiceAccount" is not in the Task Scheduler XML logonType XSD.
        // Windows normally exports LocalSystem tasks with this element absent;
        // tolerate the legacy spelling only when reading an existing task.
        var logonType = principal.Element(task + "LogonType")?.Value;
        return string.IsNullOrEmpty(logonType)
               || logonType.Equals("ServiceAccount", StringComparison.Ordinal);
    }

    private static async Task RestoreAgentTaskSnapshotAsync(
        AgentInstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (!journal.TaskSnapshotReady)
            throw new InvalidDataException("The Agent task snapshot was not committed before rollback.");
        var executable = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
        if (journal.HadPreviousTask)
        {
            RequireExactAgentTaskXml(journal.PreviousTaskXml, executable);
            // The snapshot proves that the prior task had the exact semantic
            // contract, but a legacy Scheduler export can contain the invalid
            // XML lexical value <LogonType>ServiceAccount</LogonType>. Restore
            // the same contract through the canonical registrar so rollback
            // gets both the corrected XML and the secure command-line fallback.
            await RegisterExactAgentTaskAsync(executable, cancellationToken);
            var restored = await QueryAgentTaskXmlAsync(cancellationToken)
                           ?? throw new InvalidDataException("The prior Agent task was not restored.");
            RequireExactAgentTaskXml(restored, executable);
            var start = await RunSystemToolAsync(
                "schtasks.exe", ["/Run", "/TN", AgentTaskName],
                TimeSpan.FromSeconds(20), cancellationToken);
            EnsureSystemToolSuccess(start, "The restored Agent task could not be started");
            if (!await WaitForListeningExecutableAsync(
                    45831, executable, TimeSpan.FromSeconds(30), cancellationToken))
                throw new InvalidDataException("The restored Agent task did not restart the prior Agent.");
            return;
        }

        var current = await QueryAgentTaskXmlAsync(cancellationToken);
        if (current is not null)
        {
            if (!IsMinimallyOwnedAgentTask(current, executable))
                throw new InvalidDataException(
                    "The first-install rollback found a same-named task whose exact Opticon ownership could not be proven.");
            var delete = await RunSystemToolAsync(
                "schtasks.exe", ["/Delete", "/TN", AgentTaskName, "/F"],
                TimeSpan.FromSeconds(20), cancellationToken);
            EnsureSystemToolSuccess(delete, "The first-install Agent task could not be removed during rollback");
        }
        if (await QueryAgentTaskXmlAsync(cancellationToken) is not null)
            throw new InvalidDataException("The first-install Agent task still exists after rollback.");
    }

    private async Task RecoverAgentInstallTransactionAsync(string source, CancellationToken cancellationToken)
    {
        var journal = AgentInstallTransactionPersistence.Load();
        if (journal is null) return;
        _agentInstallTransaction = journal;
        var candidateDirectory = AgentInstallTransactionPersistence.CandidateDirectory(journal.OperationId);
        var rollbackDirectory = AgentInstallTransactionPersistence.RollbackDirectory(journal.OperationId);
        var failedDirectory = AgentInstallTransactionPersistence.FailedDirectory(journal.OperationId);
        if (journal.Phase <= AgentInstallTransactionPhase.PreviousMoved
            && Directory.Exists(AppPaths.AgentInstallDirectory)
            && !Directory.Exists(candidateDirectory) && !File.Exists(candidateDirectory)
            && !Directory.Exists(failedDirectory) && !File.Exists(failedDirectory)
            && Directory.Exists(rollbackDirectory) == journal.HadPreviousAgent
            && !File.Exists(rollbackDirectory))
        {
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            journal.Phase = AgentInstallTransactionPhase.CandidateActivated;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        }
        if (journal.Phase >= AgentInstallTransactionPhase.RollbackStarted)
        {
            await RollbackAgentInstallTransactionAsync(cancellationToken);
            return;
        }
        var receipt = File.Exists(AppPaths.InstallReceiptFile)
            ? await new MachineJsonFileStore<EnrollmentReceipt>(AppPaths.InstallReceiptFile).LoadAsync(cancellationToken)
            : null;
        if (receipt is not null
            && receipt.SchemaVersion == 3
            && receipt.AgentInstallOperationId == journal.OperationId
            && receipt.InviteId == journal.InviteId)
        {
            if (ValidationEnabled(ClientInstallValidationStep.MachineState)
                || ValidationEnabled(ClientInstallValidationStep.PayloadAuthenticity)
                || ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
                await VerifyCommittedReceiptAgentAsync(receipt, cancellationToken);
            _agentInstallCommitted = true;
            await FinalizeAgentInstallTransactionAsync(cancellationToken);
            return;
        }

        if (journal.InviteId == _invite.InviteId
            && journal.Phase is >= AgentInstallTransactionPhase.CandidateActivated
                and < AgentInstallTransactionPhase.RollbackStarted
            && Directory.Exists(AppPaths.AgentInstallDirectory))
        {
            await VerifyPayloadDirectoryCopyIfEnabledAsync(
                source, AppPaths.AgentInstallDirectory, verifyDestinationExecutables: true, cancellationToken);
            return;
        }

        await RollbackAgentInstallTransactionAsync(cancellationToken);
    }

    private async Task RollbackAgentInstallTransactionAsync(CancellationToken cancellationToken)
    {
        var journal = _agentInstallTransaction ?? AgentInstallTransactionPersistence.Load();
        if (journal is null) return;
        _agentInstallTransaction = journal;
        var destination = AppPaths.AgentInstallDirectory;
        var candidate = AgentInstallTransactionPersistence.CandidateDirectory(journal.OperationId);
        var rollback = AgentInstallTransactionPersistence.RollbackDirectory(journal.OperationId);
        var failed = AgentInstallTransactionPersistence.FailedDirectory(journal.OperationId);
        RequireAgentTransactionPath(candidate, journal.OperationId, "installing");
        RequireAgentTransactionPath(rollback, journal.OperationId, "rollback");
        RequireAgentTransactionPath(failed, journal.OperationId, "failed");

        var phaseAtEntry = journal.Phase;
        if (journal.Phase < AgentInstallTransactionPhase.RollbackStarted)
        {
            journal.Phase = AgentInstallTransactionPhase.RollbackStarted;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        }

        _ = await RunSystemToolAsync(
            "schtasks.exe", ["/End", "/TN", "Taildesk Agent"], TimeSpan.FromSeconds(15), cancellationToken);
        await RequireAgentProcessesClosedAsync(destination, cancellationToken);
        if (journal.HadPreviousAgent)
        {
            var destinationIsPrevious = Directory.Exists(destination)
                                        && await AgentDirectoryMatchesRecordsAsync(
                                            destination, journal.PreviousAgentFiles, cancellationToken);
            if (Directory.Exists(rollback))
            {
                await VerifyAgentDirectoryAgainstRecordsAsync(
                    rollback, journal.PreviousAgentFiles, cancellationToken);
                if (!destinationIsPrevious)
                {
                    if (Directory.Exists(destination))
                    {
                        if (Directory.Exists(failed) || File.Exists(failed))
                            throw new InvalidOperationException("The Agent failed-candidate directory is already occupied.");
                        Directory.Move(destination, failed);
                    }
                    else if (File.Exists(destination))
                        throw new InvalidDataException("The Agent destination is a file during rollback.");
                    Directory.Move(rollback, destination);
                    destinationIsPrevious = true;
                }
            }
            if (!destinationIsPrevious)
                throw new InvalidDataException("The prior Agent rollback directory is missing.");
            await VerifyAgentDirectoryAgainstRecordsAsync(
                destination, journal.PreviousAgentFiles, cancellationToken);
            journal.Phase = AgentInstallTransactionPhase.PreviousRestored;
            await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        }
        else if (Directory.Exists(destination))
        {
            if (phaseAtEntry < AgentInstallTransactionPhase.CandidateActivated)
                throw new InvalidDataException("An unexpected Agent directory blocks first-install rollback.");
            DeleteAgentCanonicalDirectory(destination);
        }
        else if (File.Exists(destination))
            throw new InvalidDataException("The Agent destination is a file during first-install rollback.");

        DeleteAgentTransactionDirectory(candidate, journal.OperationId, "installing");
        DeleteAgentTransactionDirectory(failed, journal.OperationId, "failed");
        DeleteAgentTransactionDirectory(rollback, journal.OperationId, "rollback");
        if (journal.StateSnapshotReady)
            await RestoreAgentInstallStateAsync(journal, cancellationToken);
        journal.Phase = AgentInstallTransactionPhase.StateRestored;
        await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        await RestoreAgentTaskSnapshotAsync(journal, cancellationToken);
        journal.Phase = AgentInstallTransactionPhase.TaskStateRestored;
        await AgentInstallTransactionPersistence.SaveAsync(journal, cancellationToken);
        AgentInstallTransactionPersistence.Delete();
        ClearAgentInstallTransaction(journal);
    }

    private async Task FinalizeAgentInstallTransactionAsync(CancellationToken cancellationToken)
    {
        var journal = _agentInstallTransaction;
        if (journal is null) return;
        var currentTask = await QueryAgentTaskXmlAsync(cancellationToken)
                          ?? throw new InvalidDataException("The committed Agent scheduled task is missing.");
        RequireExactAgentTaskXml(
            currentTask, Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe"));
        var rollback = AgentInstallTransactionPersistence.RollbackDirectory(journal.OperationId);
        if (Directory.Exists(rollback))
            DeleteAgentTransactionDirectory(rollback, journal.OperationId, "rollback");
        DeleteAgentTransactionDirectory(
            AgentInstallTransactionPersistence.CandidateDirectory(journal.OperationId), journal.OperationId, "installing");
        DeleteAgentTransactionDirectory(
            AgentInstallTransactionPersistence.FailedDirectory(journal.OperationId), journal.OperationId, "failed");
        AgentInstallTransactionPersistence.Delete();
        ClearAgentInstallTransaction(journal);
    }

    private void RequireSafeInvitationResume(AgentConfig state)
    {
        if (!ValidationEnabled(ClientInstallValidationStep.MachineState)) return;
        if (state.CompletedInviteId is Guid completed && completed != _invite.InviteId)
            throw new InvalidOperationException(
                "This machine is already enrolled through a different invitation. " +
                "Use the authenticated update/maintenance workflow; invitation reinstall is disabled to preserve the working recovery identity.");
        if (state.PendingInviteId is Guid pending && pending != _invite.InviteId)
            throw new InvalidOperationException(
                "A different invitation is already pending on this machine. Resume that exact invitation or use authenticated recovery.");
    }

    private static async Task VerifyCommittedReceiptAgentAsync(
        EnrollmentReceipt receipt,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
        await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
        await using var stream = new FileStream(
            executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (receipt.AgentSize <= 0 || stream.Length != receipt.AgentSize)
            throw new InvalidDataException("The committed Agent no longer matches its protected enrollment receipt size.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!FixedAsciiEquals(hash, receipt.AgentSha256))
            throw new InvalidDataException("The committed Agent no longer matches its protected enrollment receipt hash.");
    }

    private static async Task RestoreAgentInstallStateAsync(
        AgentInstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.HadPreviousConfig)
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                AppPaths.AgentConfigFile, journal.PreviousConfig, cancellationToken);
        else MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.AgentConfigFile);
        if (journal.HadPreviousReceipt)
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                AppPaths.InstallReceiptFile, journal.PreviousReceipt, cancellationToken);
        else MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.InstallReceiptFile);
    }

    private void ClearAgentInstallTransaction(AgentInstallTransactionJournal journal)
    {
        CryptographicOperations.ZeroMemory(journal.PreviousConfig);
        CryptographicOperations.ZeroMemory(journal.PreviousReceipt);
        journal.PreviousTaskXml = string.Empty;
        if (ReferenceEquals(_agentInstallTransaction, journal)) _agentInstallTransaction = null;
    }

    private static async Task RequireAgentProcessesClosedAsync(
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var processNames = Directory.Exists(installedDirectory)
            ? Directory.EnumerateFiles(installedDirectory, "*.exe", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension)
                .Append("Taildesk.Agent")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : ["Taildesk.Agent"];
        while (true)
        {
            var running = false;
            foreach (var processName in processNames)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        string processPath;
                        try
                        {
                            processPath = Path.GetFullPath(process.MainModule?.FileName
                                ?? throw new InvalidOperationException("Windows did not expose the Agent process path."));
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                $"Opticon could not prove Agent process {process.Id} stopped before the directory swap.", exception);
                        }
                        if (IsPathWithinDirectory(processPath, installedDirectory)) running = true;
                    }
                }
            }
            if (!running) return;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The prior Opticon Agent did not stop before the protected directory swap.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static async Task VerifyInstalledExecutableDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "installed executable directory");
        var executables = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories).ToArray();
        if (executables.Length == 0)
            throw new InvalidDataException("The installed executable directory contains no executable.");
        foreach (var executable in executables)
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
    }

    private static async Task<List<AgentInstallFileRecord>> CreateAgentInstallFileRecordsAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "installed Agent directory");
        var records = new List<AgentInstallFileRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            records.Add(new AgentInstallFileRecord
            {
                Path = Path.GetRelativePath(directory, path).Replace('\\', '/'),
                Size = stream.Length,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant()
            });
        }
        if (records.Count == 0)
            throw new InvalidDataException("The installed Agent directory is empty.");
        return records;
    }

    private static async Task VerifyAgentDirectoryAgainstRecordsAsync(
        string directory,
        IReadOnlyCollection<AgentInstallFileRecord> records,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "Agent rollback directory");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (files.Count != records.Count
            || files.Keys.Except(records.Select(record => record.Path), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The prior Agent directory no longer matches its protected rollback manifest.");
        foreach (var record in records)
        {
            var path = files[record.Path];
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != record.Size)
                throw new InvalidDataException("The prior Agent rollback file size changed.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!FixedAsciiEquals(hash, record.Sha256))
                throw new InvalidDataException("The prior Agent rollback file hash changed.");
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(path, cancellationToken);
        }
    }

    private static async Task<bool> AgentDirectoryMatchesRecordsAsync(
        string directory,
        IReadOnlyCollection<AgentInstallFileRecord> records,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyAgentDirectoryAgainstRecordsAsync(directory, records, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private Task VerifyPayloadDirectoryCopyIfEnabledAsync(
        string source,
        string destination,
        bool verifyDestinationExecutables,
        CancellationToken cancellationToken)
    {
        if (!ValidationEnabled(ClientInstallValidationStep.PayloadAuthenticity)
            && !ValidationEnabled(ClientInstallValidationStep.SourceBuildProvenance)
            && !ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
            return Task.CompletedTask;
        return VerifyPayloadDirectoryCopyAsync(
            source, destination, verifyDestinationExecutables, cancellationToken);
    }

    private static async Task VerifyPayloadDirectoryCopyAsync(
        string source,
        string destination,
        bool verifyDestinationExecutables,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(source, "source payload directory");
        RejectDirectoryReparsePoint(destination, "copied payload directory");
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), StringComparer.OrdinalIgnoreCase);
        var destinationFiles = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(destination, path), StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count == 0
            || sourceFiles.Count != destinationFiles.Count
            || sourceFiles.Keys.Except(destinationFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The staged payload is not the exact authenticated source tree.");
        foreach (var (relative, sourcePath) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetExtension(sourcePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(sourcePath, cancellationToken);
            var destinationPath = destinationFiles[relative];
            if (new FileInfo(sourcePath).Length != new FileInfo(destinationPath).Length)
                throw new InvalidDataException($"The staged payload size changed at {relative}.");
            await using var sourceStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destinationStream = new FileStream(
                destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
            var destinationHash = await SHA256.HashDataAsync(destinationStream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                throw new InvalidDataException($"The staged payload hash changed at {relative}.");
            if (verifyDestinationExecutables
                && Path.GetExtension(destinationPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(destinationPath, cancellationToken);
        }
    }

    private static void RequireAgentTransactionPath(string path, Guid operationId, string kind)
    {
        var expected = Path.GetFullPath(Path.Combine(
            AppPaths.InstallDirectory, $"Agent.{kind}-{operationId:N}"));
        if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(expected)!.Equals(
                Path.GetFullPath(AppPaths.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent installation transaction path is unsafe.");
    }

    private static void DeleteAgentTransactionDirectory(string path, Guid operationId, string kind)
    {
        RequireAgentTransactionPath(path, operationId, kind);
        if (File.Exists(path)) throw new InvalidDataException("An Agent transaction directory path is a file.");
        if (!Directory.Exists(path)) return;
        RejectDirectoryReparsePoint(path, "Agent transaction directory");
        Directory.Delete(path, recursive: true);
    }

    private static void DeleteAgentCanonicalDirectory(string path)
    {
        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(AppPaths.AgentInstallDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Agent installation directory is not canonical.");
        RejectDirectoryReparsePoint(path, "Agent installation directory");
        Directory.Delete(path, recursive: true);
    }

    private async Task InstallGuardianAsync(string source, CancellationToken cancellationToken)
    {
        _progress.Report(new InstallProgress(6, "Checking the fail-safe update guardian..."));
        var sourceExecutable = Path.Combine(source, "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(sourceExecutable))
            throw new FileNotFoundException("The signed update guardian payload is missing.", sourceExecutable);
        await ProductSigning.VerifyAuthenticodeAsync(sourceExecutable, cancellationToken);

        // The guardian is deliberately outside the swappable Agent directory.
        // Keep a compatible signed Guardian stable across ordinary Setup and
        // Agent releases; its product version need not equal the Setup version.
        var destination = AppPaths.UpdateGuardianInstallDirectory;
        var installedExecutable = Path.Combine(destination, "Taildesk.UpdateGuardian.exe");
        if (File.Exists(AppPaths.GuardianInstallTransactionFile) || !File.Exists(installedExecutable))
            await InstallGuardianFreshTransactionalAsync(source, destination, cancellationToken);
        else await StableGuardianMaintenance.ReconcileSignedReleaseAsync(source, destination, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
        await RequireInstalledGuardianWatchdogCompatibilityAsync(source, destination, cancellationToken);
        var installedVersion = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(installedExecutable).ProductVersion ?? string.Empty);
        var sourceVersion = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(sourceExecutable).ProductVersion ?? string.Empty);
        if (installedVersion == sourceVersion)
            SourceBuildProvenance.CommitActiveComponent(destination);
        _progress.Report(new InstallProgress(
            6,
            $"Signed stable Guardian {installedVersion} supports the watchdog contract; keeping it pinned."));

        var taskCommand = $"\"{installedExecutable}\"";
        var task = await RunSystemToolAsync("schtasks.exe",
            ["/Create", "/TN", RemoteAdministrationProtocol.GuardianTaskName, "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", taskCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(task, "Could not create the fail-safe update-guardian task");

        var watchdogCommand = $"\"{installedExecutable}\" {RemoteAdministrationProtocol.GuardianWatchdogArgument}";
        var watchdog = await RunSystemToolAsync("schtasks.exe",
            ["/Create", "/TN", RemoteAdministrationProtocol.GuardianWatchdogTaskName,
                "/SC", "MINUTE", "/MO", "1", "/RU", "SYSTEM", "/RL", "HIGHEST",
                "/TR", watchdogCommand, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(watchdog, "Could not create the fail-safe update-guardian watchdog task");

        var bootSettings =
            "$boot=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable " +
            "-RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0) " +
            "-MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianTaskName}' -Settings $boot | Out-Null";
        var watchdogSettings =
            "$watchdog=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianWatchdogTaskName}' -Settings $watchdog | Out-Null";
        var guardianTaskSettings = bootSettings + "; " + watchdogSettings;
        var settings = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", guardianTaskSettings],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(settings, "Could not apply fail-safe update-guardian recovery/watchdog settings");
    }

    private async Task InstallGuardianFreshTransactionalAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        GuardianInstallTransactionJournal? journal = null;
        if (File.Exists(AppPaths.GuardianInstallTransactionFile))
            journal = await new MachineJsonFileStore<GuardianInstallTransactionJournal>(
                AppPaths.GuardianInstallTransactionFile).LoadAsync(cancellationToken);
        if (journal is not null)
        {
            ValidateGuardianInstallJournal(journal);
            var interruptedStage = GuardianTransactionDirectory(journal.OperationId);
            if (Directory.Exists(destination))
            {
                await VerifyGuardianDirectoryAgainstJournalAsync(
                    destination, journal, verifyExecutables: true, cancellationToken);
                if (Directory.Exists(interruptedStage))
                    DeleteGuardianTransactionDirectory(interruptedStage, journal.OperationId);
                MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                return;
            }
            if (Directory.Exists(interruptedStage))
            {
                try
                {
                    await VerifyGuardianDirectoryAgainstJournalAsync(
                        interruptedStage, journal, verifyExecutables: false, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    DeleteGuardianTransactionDirectory(interruptedStage, journal.OperationId);
                    if (await SourceMatchesGuardianJournalAsync(source, journal, cancellationToken))
                    {
                        CopyDirectory(source, interruptedStage);
                        await VerifyGuardianDirectoryAgainstJournalAsync(
                            interruptedStage, journal, verifyExecutables: false, cancellationToken);
                    }
                    else
                    {
                        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                        journal = null;
                    }
                }
            }
            else
            {
                // The crash occurred after the protected journal commit but before
                // the first namespace mutation, so restarting with the current
                // authenticated source is safe.
                MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                journal = null;
            }
            if (journal is not null)
            {
                Directory.Move(interruptedStage, destination);
                await VerifyGuardianDirectoryAgainstJournalAsync(
                    destination, journal, verifyExecutables: true, CancellationToken.None);
                MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
                return;
            }
        }

        var operationId = Guid.NewGuid();
        var staging = GuardianTransactionDirectory(operationId);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidOperationException("The Guardian destination changed before its atomic installation.");
        if (Directory.Exists(staging) || File.Exists(staging))
            throw new InvalidOperationException("The Guardian staging directory is already occupied.");
        journal = new GuardianInstallTransactionJournal
        {
            OperationId = operationId,
            Files = await CreateGuardianFileRecordsAsync(source, cancellationToken)
        };
        await new MachineJsonFileStore<GuardianInstallTransactionJournal>(AppPaths.GuardianInstallTransactionFile)
            .SaveAsync(journal, cancellationToken);
        CopyDirectory(source, staging);
        await VerifyPayloadDirectoryCopyIfEnabledAsync(
            source, staging, verifyDestinationExecutables: false, cancellationToken);
        Directory.Move(staging, destination);
        await VerifyPayloadDirectoryCopyIfEnabledAsync(
            source, destination, verifyDestinationExecutables: true, CancellationToken.None);
        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.GuardianInstallTransactionFile);
    }

    private static string GuardianTransactionDirectory(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new InvalidDataException("The Guardian transaction operation ID is empty.");
        return Path.Combine(AppPaths.InstallDirectory, $"UpdateGuardian.installing-{operationId:N}");
    }

    private static void DeleteGuardianTransactionDirectory(string path, Guid operationId)
    {
        var expected = Path.GetFullPath(GuardianTransactionDirectory(operationId));
        if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Guardian transaction path is unsafe.");
        if (File.Exists(path)) throw new InvalidDataException("The Guardian transaction path is a file.");
        if (!Directory.Exists(path)) return;
        RejectDirectoryReparsePoint(path, "Guardian transaction directory");
        Directory.Delete(path, recursive: true);
    }

    private static void ValidateGuardianInstallJournal(GuardianInstallTransactionJournal journal)
    {
        if (journal.SchemaVersion != 2 || journal.OperationId == Guid.Empty
            || journal.Files.Count is < 1 or > 32
            || journal.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count
            || journal.Files.Any(file => string.IsNullOrWhiteSpace(file.Path)
                                         || Path.IsPathRooted(file.Path)
                                         || file.Path.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..")
                                         || file.Size <= 0
                                         || !Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$")))
            throw new InvalidDataException("The protected Guardian installation journal is invalid.");
    }

    private static async Task<List<GuardianInstallFileRecord>> CreateGuardianFileRecordsAsync(
        string source,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(source, "Guardian source directory");
        var records = new List<GuardianInstallFileRecord>();
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(path, cancellationToken);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            records.Add(new GuardianInstallFileRecord
            {
                Path = Path.GetRelativePath(source, path).Replace('\\', '/'),
                Size = stream.Length,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant()
            });
        }
        var journal = new GuardianInstallTransactionJournal { Files = records };
        journal.OperationId = Guid.NewGuid();
        ValidateGuardianInstallJournal(journal);
        return records;
    }

    private static async Task VerifyGuardianDirectoryAgainstJournalAsync(
        string directory,
        GuardianInstallTransactionJournal journal,
        bool verifyExecutables,
        CancellationToken cancellationToken)
    {
        ValidateGuardianInstallJournal(journal);
        RejectDirectoryReparsePoint(directory, "Guardian transaction payload");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (files.Count != journal.Files.Count
            || files.Keys.Except(journal.Files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The Guardian transaction payload does not match its protected journal.");
        foreach (var expected in journal.Files)
        {
            var path = files[expected.Path];
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expected.Size)
                throw new InvalidDataException("The Guardian transaction payload size changed.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!FixedAsciiEquals(hash, expected.Sha256))
                throw new InvalidDataException("The Guardian transaction payload hash changed.");
            if (verifyExecutables && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(path, cancellationToken);
        }
    }

    private static async Task<bool> SourceMatchesGuardianJournalAsync(
        string source,
        GuardianInstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyGuardianDirectoryAgainstJournalAsync(source, journal, verifyExecutables: true, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task RequireInstalledGuardianWatchdogCompatibilityAsync(
        string sourceDirectory,
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var sourceExecutable = Path.Combine(sourceDirectory, "Taildesk.UpdateGuardian.exe");
        var installedExecutable = Path.Combine(installedDirectory, "Taildesk.UpdateGuardian.exe");
        var sourceVersion = UpdatePackageVerifier.ParseVersion(UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(sourceExecutable).ProductVersion ?? string.Empty));
        var installedVersion = UpdatePackageVerifier.ParseVersion(UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(installedExecutable).ProductVersion ?? string.Empty));
        var minimumWatchdogVersion = UpdatePackageVerifier.ParseVersion(
            RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion);
        if (sourceVersion < minimumWatchdogVersion)
            throw new InvalidOperationException(
                $"This Setup carries Guardian {sourceVersion}, but watchdog mode requires {minimumWatchdogVersion} or newer.");
        var installedFiles = Directory.EnumerateFiles(installedDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(installedDirectory, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
        if (installedVersion != sourceVersion)
        {
            if (RemoteAdministrationProtocol.SupportsGuardianWatchdog(installedVersion)
                && installedFiles.Count == 1
                && installedFiles.ContainsKey("Taildesk.UpdateGuardian.exe"))
                return;
            if (installedVersion < minimumWatchdogVersion)
                throw new InvalidOperationException(
                    $"The existing stable Guardian {installedVersion} predates watchdog support {minimumWatchdogVersion} and was not overwritten. " +
                    "Complete attended stable-Guardian maintenance before reinstalling Opticon.");
            throw new InvalidOperationException(
                $"The stable Guardian {installedVersion} has companion files this Setup cannot attest. " +
                "Complete attended stable-Guardian maintenance before reinstalling Opticon.");
        }

        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count != installedFiles.Count
            || sourceFiles.Keys.Except(installedFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException(
                "The existing stable Guardian payload differs from this Setup and was not overwritten. " +
                "Complete attended stable-Guardian maintenance before reinstalling Opticon.");

        foreach (var (relative, sourcePath) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installedPath = installedFiles[relative];
            if (new FileInfo(sourcePath).Length != new FileInfo(installedPath).Length)
                throw new InvalidOperationException(
                    $"The existing stable Guardian payload differs at {relative}; attended Guardian maintenance is required.");
            await using var source = File.OpenRead(sourcePath);
            await using var installed = File.OpenRead(installedPath);
            var sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
            var installedHash = await SHA256.HashDataAsync(installed, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, installedHash))
                throw new InvalidOperationException(
                    $"The existing stable Guardian payload differs at {relative}; attended Guardian maintenance is required.");
        }
    }

    private async Task ConfigureFirewallAsync(string tailscaleIp, string rustDesk, CancellationToken cancellationToken)
    {
        var expectedRustDesk = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "RustDesk", "rustdesk.exe"));
        rustDesk = Path.GetFullPath(rustDesk);
        if (!rustDesk.Equals(expectedRustDesk, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The RustDesk firewall target is not the fixed Program Files executable.");
        if (!IPAddress.TryParse(tailscaleIp, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || parsed.GetAddressBytes() is not [100, >= 64 and <= 127, _, _])
            throw new InvalidDataException("The firewall binding is not an authenticated Tailscale IPv4 address.");

        var service = await RunSystemToolAsync(
            "sc.exe", ["query", "RustDesk"], TimeSpan.FromSeconds(10), cancellationToken);
        if (service.Succeeded)
        {
            _ = await RunSystemToolAsync(
                "sc.exe", ["stop", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
            var disabled = await RunSystemToolAsync(
                "sc.exe", ["config", "RustDesk", "start=", "disabled"],
                TimeSpan.FromSeconds(15), cancellationToken);
            EnsureSuccess(disabled, "RustDesk could not be disabled before firewall isolation");
        }
        await RequireFirewallProfilesSecureAsync(cancellationToken);
        _progress.Report(new InstallProgress(80, "Restricting inbound access to the Tailscale interface…"));
        var agent = Path.Combine(AppPaths.InstallDirectory, "Agent", "Taildesk.Agent.exe");
        // Exact Opticon rule names are the ownership boundary. Never delete
        // all rules for a program: administrators may intentionally have
        // their own RustDesk or Agent policy that must survive a repair.
        foreach (var ruleName in new[]
                 {
                     "Taildesk Agent (Tailscale only)", "RustDesk Direct (Tailscale only)",
                     "RustDesk External IPv4 Block", "RustDesk External IPv6 Block"
                 })
            _ = await RunSystemToolAsync("netsh.exe",
                ["advfirewall", "firewall", "delete", "rule", $"name={ruleName}"],
                TimeSpan.FromSeconds(20), cancellationToken);

        var agentRule = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=Taildesk Agent (Tailscale only)", "dir=in", "action=allow", "protocol=TCP", "localport=45831", $"localip={tailscaleIp}", "remoteip=100.64.0.0/10", $"program={agent}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(agentRule, "Could not create the Opticon agent firewall rule");

        var rustDeskRule = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk Direct (Tailscale only)", "dir=in", "action=allow", "protocol=TCP", "localport=21118", $"localip={tailscaleIp}", "remoteip=100.64.0.0/10", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskRule, "Could not create the RustDesk firewall rule");

        var rustDeskExternalV4Block = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk External IPv4 Block", "dir=out", "action=block", "remoteip=0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskExternalV4Block, "Could not block RustDesk from non-Tailscale IPv4 destinations");

        var rustDeskExternalV6Block = await RunSystemToolAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=RustDesk External IPv6 Block", "dir=out", "action=block", "remoteip=::/1,8000::/1", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(rustDeskExternalV6Block, "Could not block RustDesk from external IPv6 destinations");
        await AssertExactFirewallConfigurationAsync(tailscaleIp, rustDesk, cancellationToken);
    }

    private async Task EnsureAgentFirewallPolicyAsync(
        string tailscaleIp,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(tailscaleIp, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || parsed.GetAddressBytes() is not [100, >= 64 and <= 127, _, _])
            throw new InvalidDataException(
                "The Agent firewall binding is not an authenticated Tailscale IPv4 address.");
        await RequireFirewallProfilesSecureAsync(cancellationToken);
        var agent = Path.GetFullPath(Path.Combine(
            AppPaths.InstallDirectory, "Agent", "Taildesk.Agent.exe"));
        _ = await RunSystemToolAsync(
            "netsh.exe",
            ["advfirewall", "firewall", "delete", "rule",
                "name=Taildesk Agent (Tailscale only)"],
            TimeSpan.FromSeconds(20), cancellationToken);
        var added = await RunSystemToolAsync(
            "netsh.exe",
            ["advfirewall", "firewall", "add", "rule",
                "name=Taildesk Agent (Tailscale only)", "dir=in",
                "action=allow", "protocol=TCP", "localport=45831", $"localip={tailscaleIp}",
                "remoteip=100.64.0.0/10", $"program={agent}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(added, "Could not create the Tailscale-only Agent recovery firewall rule");

        const string script =
            "param([string]$ip,[string]$agent);$ErrorActionPreference='Stop';" +
            "$rules=@(Get-NetFirewallRule -DisplayName 'Taildesk Agent (Tailscale only)' -ErrorAction Stop);" +
            "if($rules.Count -ne 1){throw 'Agent firewall rule count drifted'};$r=$rules[0];" +
            "if(-not $r.Enabled -or $r.Direction.ToString() -ne 'Inbound' -or $r.Action.ToString() -ne 'Allow' -or $r.Profile.ToString() -ne 'Any'){throw 'Agent firewall rule contract drifted'};" +
            "$app=@(Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r);if($app.Count -ne 1 -or -not $app[0].Program.Equals($agent,[StringComparison]::OrdinalIgnoreCase)){throw 'Agent firewall application drifted'};" +
            "$pf=@(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $r);if($pf.Count -ne 1 -or $pf[0].Protocol.ToString() -ne 'TCP' -or $pf[0].LocalPort.ToString() -ne '45831'){throw 'Agent firewall port drifted'};" +
            "$af=@(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r);$remote=(@($af[0].RemoteAddress)-join ',');$local=(@($af[0].LocalAddress)-join ',');" +
            "if($af.Count -ne 1 -or @('100.64.0.0/10','100.64.0.0/255.192.0.0') -notcontains $remote -or $local -ne $ip){throw 'Agent firewall address drifted'}";
        var verified = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted",
                "-Command", script, tailscaleIp, agent],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSystemToolSuccess(
            verified, "The Tailscale-only Agent recovery firewall rule did not verify");
    }

    private static async Task ContainRustDeskAfterFailedSetupAsync(
        CancellationToken cancellationToken)
    {
        var service = await RunSystemToolAsync(
            "sc.exe", ["query", "RustDesk"], TimeSpan.FromSeconds(10), cancellationToken);
        if (!service.Succeeded)
        {
            if (service.ExitCode == 1060) return;
            EnsureSystemToolSuccess(
                service, "Windows could not determine whether the RustDesk service needed containment");
        }
        _ = await RunSystemToolAsync(
            "sc.exe", ["stop", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
        var disabled = await RunSystemToolAsync(
            "sc.exe", ["config", "RustDesk", "start=", "disabled"],
            TimeSpan.FromSeconds(15), cancellationToken);
        EnsureSystemToolSuccess(
            disabled, "RustDesk could not be disabled after remote-desktop setup failed");
    }

    private async Task<InstallerEnsureResult> EnsureFirewallPolicyAsync(
        string tailscaleIp,
        string rustDesk,
        CancellationToken cancellationToken)
    {
        var wasReady = await IsExactFirewallConfigurationAsync(
            tailscaleIp, rustDesk, cancellationToken);
        if (!wasReady)
            await ConfigureFirewallAsync(tailscaleIp, rustDesk, cancellationToken);
        await AssertExactFirewallConfigurationAsync(tailscaleIp, rustDesk, cancellationToken);
        var result = wasReady
            ? InstallerEnsureResult.Ready(
                "EnsureFirewallPolicyAsync",
                "Exactly the Opticon-owned firewall rules are present and restricted to the authenticated Tailscale identity.")
            : InstallerEnsureResult.Repaired(
                "EnsureFirewallPolicyAsync",
                "Exactly the Opticon-owned firewall rules are present and restricted to the authenticated Tailscale identity.",
                "Recreated the exact Opticon firewall rules without touching user-owned rules.");
        await RecordEnsureOutcomeAsync(result, cancellationToken,
            wasReady ? null : "OpticonFirewallRules");
        return result;
    }

    private static async Task<bool> IsExactFirewallConfigurationAsync(
        string tailscaleIp,
        string rustDesk,
        CancellationToken cancellationToken)
    {
        try
        {
            await AssertExactFirewallConfigurationAsync(tailscaleIp, rustDesk, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task RequireFirewallProfilesSecureAsync(CancellationToken cancellationToken)
    {
        const string script =
            "$ErrorActionPreference='Stop';" +
            "$p=@(Get-NetFirewallProfile);" +
            "if($p.Count -ne 3){throw 'Windows Firewall must expose exactly Domain, Private, and Public profiles.'};" +
            "$names=@($p|ForEach-Object{$_.Name.ToString()}|Sort-Object);" +
            "if(($names -join ',') -cne 'Domain,Private,Public'){throw 'Windows Firewall profile set is unexpected.'};" +
            "foreach($x in $p){if(-not $x.Enabled -or $x.DefaultInboundAction.ToString() -ne 'Block'){throw ('Windows Firewall profile is not enabled with default inbound block: '+$x.Name)}}";
        var result = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", script],
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSystemToolSuccess(result, "Windows Firewall profiles are not enabled with default inbound blocking");
    }

    private static async Task AssertExactFirewallConfigurationAsync(
        string tailscaleIp,
        string rustDesk,
        CancellationToken cancellationToken)
    {
        await RequireFirewallProfilesSecureAsync(cancellationToken);
        var agent = Path.GetFullPath(Path.Combine(
            AppPaths.InstallDirectory, "Agent", "Taildesk.Agent.exe"));
        const string script =
            "param([string]$ip,[string]$rust,[string]$agent);$ErrorActionPreference='Stop';" +
            "function Check([string]$name,[string]$direction,[string]$action,[string]$program,[string]$protocol,[string]$port,[string]$remote,[string]$local){" +
            "$rules=@(Get-NetFirewallRule -DisplayName $name -ErrorAction Stop);if($rules.Count -ne 1){throw ('Firewall rule count drifted: '+$name)};$r=$rules[0];" +
            "if(-not $r.Enabled -or $r.Direction.ToString() -ne $direction -or $r.Action.ToString() -ne $action -or $r.Profile.ToString() -ne 'Any'){throw ('Firewall rule contract drifted: '+$name)};" +
            "$app=@(Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r);if($app.Count -ne 1 -or -not $app[0].Program.Equals($program,[StringComparison]::OrdinalIgnoreCase)){throw ('Firewall application drifted: '+$name)};" +
            "$pf=@(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $r);if($pf.Count -ne 1 -or $pf[0].Protocol.ToString() -ne $protocol -or ($port -and $pf[0].LocalPort.ToString() -ne $port)){throw ('Firewall port drifted: '+$name)};" +
            "$af=@(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r);$actual=(@($af[0].RemoteAddress) -join ',');$actualLocal=(@($af[0].LocalAddress) -join ',');if($af.Count -ne 1 -or -not (($remote -split '\\|') -contains $actual) -or ($local -and $actualLocal -ne $local)){throw ('Firewall address drifted: '+$name)}};" +
            "Check 'Taildesk Agent (Tailscale only)' 'Inbound' 'Allow' $agent 'TCP' '45831' '100.64.0.0/10|100.64.0.0/255.192.0.0' $ip;" +
            "Check 'RustDesk Direct (Tailscale only)' 'Inbound' 'Allow' $rust 'TCP' '21118' '100.64.0.0/10|100.64.0.0/255.192.0.0' $ip;" +
            "Check 'RustDesk External IPv4 Block' 'Outbound' 'Block' $rust 'Any' '' '0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255' '';" +
            "Check 'RustDesk External IPv6 Block' 'Outbound' 'Block' $rust 'Any' '' '::/1,8000::/1' ''";
        var result = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", script,
                tailscaleIp, rustDesk, agent],
            TimeSpan.FromSeconds(45), cancellationToken);
        EnsureSystemToolSuccess(result, "The exact Opticon firewall rules did not verify after installation");
    }

    private async Task InstallControllerPayloadAsync(bool installController, CancellationToken cancellationToken)
    {
        if (!installController)
        {
            _progress.Report(new InstallProgress(87, "Managed-only role confirmed; controller tools are not installed."));
            return;
        }
        _progress.Report(new InstallProgress(87, "Installing controller tools for this machine..."));
        var source = Path.Combine(_bundleDirectory, "Payload", "Admin");
        var controllerExecutable = Path.Combine(source, "Opticon.exe");
        var cliExecutable = Path.Combine(source, "Cli", "opticon.exe");
        if (!File.Exists(controllerExecutable) || !File.Exists(cliExecutable))
            throw new FileNotFoundException("This controller invite is missing its signed UI or CLI payload.");
        await ProductSigning.VerifyAuthenticodeAsync(controllerExecutable, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(cliExecutable, cancellationToken);

        var destination = Path.Combine(AppPaths.InstallDirectory, "Admin");
        var backup = destination + ".previous";
        await using var transactionLock = await AcquireControllerInstallLockAsync(cancellationToken);
        RequireInstalledControllerProcessesClosed(destination, backup);
        await RecoverControllerDirectoryTransactionAsync(destination, cancellationToken);
        if (await RecoverControllerBootstrapAsync(source, destination, cancellationToken))
        {
            await EnsureControllerTasksAsync(destination, cancellationToken);
            SourceBuildProvenance.CommitActiveComponent(destination);
            return;
        }

        var bootstrap = new AdminBootstrap
        {
            CoordinatorUrl = _invite.CoordinatorUrl,
            ControllerTokenProtected = SecretProtector.Protect(_invite.ControllerToken, SecretScope.LocalMachine),
            DeviceName = _invite.DeviceName,
            IsMachineProtected = true
        };
        var bootstrapBytes = JsonSerializer.SerializeToUtf8Bytes(bootstrap, JsonDefaults.Options);
        if (File.Exists(AppPaths.ControllerBootstrapFile) || Directory.Exists(AppPaths.ControllerBootstrapFile))
            throw new InvalidOperationException(
                "A protected controller bootstrap is already waiting for the selected user to consume it.");
        var bootstrapWritten = false;
        string? routeTaskSnapshot = null;
        string? uiTaskSnapshot = null;
        try
        {
            routeTaskSnapshot = await QueryTaskXmlAsync(RouteKeeperTaskName, cancellationToken);
            uiTaskSnapshot = await QueryTaskXmlAsync(ControllerUiTaskName, cancellationToken);
            await InstallControllerDirectoryTransactionalAsync(
                source,
                destination,
                async () =>
                {
                    await MachineStorageSecurity.WriteUserBootstrapAsync(
                        AppPaths.ControllerBootstrapFile,
                        bootstrapBytes,
                        RequireInteractiveUserProfile().Sid,
                        cancellationToken);
                    bootstrapWritten = true;
                    await EnsureControllerTasksAsync(destination, cancellationToken);
                },
                cancellationToken);
            SourceBuildProvenance.CommitActiveComponent(destination);
        }
        catch
        {
            try
            {
                await RestoreControllerTaskSnapshotAsync(
                    RouteKeeperTaskName, routeTaskSnapshot, cancellationToken);
                await RestoreControllerTaskSnapshotAsync(
                    ControllerUiTaskName, uiTaskSnapshot, cancellationToken);
            }
            catch { }
            if (bootstrapWritten || File.Exists(AppPaths.ControllerBootstrapFile))
            {
                try
                {
                    MachineStorageSecurity.DeleteUserBootstrap(
                        AppPaths.ControllerBootstrapFile, RequireInteractiveUserProfile().Sid);
                }
                catch { }
            }
            throw;
        }
    }

    private async Task EnsureControllerTasksAsync(string controllerDirectory, CancellationToken cancellationToken)
    {
        var ui = Path.Combine(controllerDirectory, "Opticon.exe");
        var routeKeeper = Path.Combine(controllerDirectory, "Tools", "Taildesk.RouteKeeper.exe");
        await ProductSigning.VerifyAuthenticodeAsync(ui, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(routeKeeper, cancellationToken);
        var routeXml = BuildRouteKeeperTaskXml(routeKeeper);
        var uiXml = BuildControllerUiTaskXml(
            ui, RequireInteractiveUserProfile().Sid, enabled: false);
        await ImportTaskXmlAsync(RouteKeeperTaskName, routeXml, cancellationToken);
        await ImportTaskXmlAsync(ControllerUiTaskName, uiXml, cancellationToken);
        RequireExactRouteKeeperTaskXml(
            await QueryTaskXmlAsync(RouteKeeperTaskName, cancellationToken)
            ?? throw new InvalidDataException("The RouteKeeper task was not retained."), routeKeeper);
        RequireExactControllerUiTaskXml(
            await QueryTaskXmlAsync(ControllerUiTaskName, cancellationToken)
            ?? throw new InvalidDataException("The command-center task was not retained."),
            ui, RequireInteractiveUserProfile().Sid, false);
        _controllerTasksInstalled = true;
    }

    private async Task StartControllerTasksIfInstalledAsync(CancellationToken cancellationToken)
    {
        if (_invite.Role != DeviceRole.ControllerAndManaged) return;
        var controllerDirectory = Path.Combine(AppPaths.InstallDirectory, "Admin");
        var ui = Path.Combine(controllerDirectory, "Opticon.exe");
        var routeKeeper = Path.Combine(controllerDirectory, "Tools", "Taildesk.RouteKeeper.exe");
        if (!_controllerTasksInstalled)
            await EnsureControllerTasksAsync(controllerDirectory, cancellationToken);
        var enable = await RunSystemToolAsync(
            "schtasks.exe", ["/Change", "/TN", ControllerUiTaskName, "/ENABLE"],
            TimeSpan.FromSeconds(20), cancellationToken);
        EnsureSuccess(enable, "The least-privilege command-center task could not be enabled");
        RequireExactRouteKeeperTaskXml(
            await QueryTaskXmlAsync(RouteKeeperTaskName, cancellationToken)
            ?? throw new InvalidDataException("The RouteKeeper task disappeared before start."), routeKeeper);
        RequireExactControllerUiTaskXml(
            await QueryTaskXmlAsync(ControllerUiTaskName, cancellationToken)
            ?? throw new InvalidDataException("The command-center task disappeared before start."),
            ui, RequireInteractiveUserProfile().Sid, true);
        foreach (var taskName in new[] { RouteKeeperTaskName, ControllerUiTaskName })
        {
            var start = await RunSystemToolAsync(
                "schtasks.exe", ["/Run", "/TN", taskName], TimeSpan.FromSeconds(20), cancellationToken);
            EnsureSuccess(start, $"The protected {taskName} task could not be started");
        }
    }

    private static async Task RestoreControllerTaskSnapshotAsync(
        string taskName,
        string? snapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            _ = await RunSystemToolAsync(
                "schtasks.exe", ["/Delete", "/TN", taskName, "/F"],
                TimeSpan.FromSeconds(20), cancellationToken);
            return;
        }
        await ImportTaskXmlAsync(
            taskName, NormalizeSystemTaskXmlForImport(snapshot), cancellationToken);
    }

    private static string NormalizeSystemTaskXmlForImport(string xml)
    {
        var document = ParseTaskXml(xml);
        document.Declaration = new XDeclaration("1.0", "utf-8", null);
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var principals = document.Root?.Element(task + "Principals")
            ?.Elements(task + "Principal").ToArray() ?? [];
        if (principals.Length == 1)
        {
            var principal = principals[0];
            var logonType = principal.Element(task + "LogonType");
            if (principal.Element(task + "UserId")?.Value == "S-1-5-18"
                && principal.Element(task + "RunLevel")?.Value == "HighestAvailable"
                && logonType?.Value == "ServiceAccount")
            {
                // Normalize an older API/export spelling before sending the
                // snapshot back through the stricter task-XML parser.
                logonType.Remove();
            }
        }
        // Every caller writes UTF-8 bytes. Re-serialize every imported string
        // so a Scheduler export declaring UTF-16 cannot disagree with the file
        // encoding supplied to schtasks.exe.
        return document.Declaration + Environment.NewLine
               + (document.Root?.ToString(SaveOptions.DisableFormatting)
                  ?? throw new InvalidDataException("The scheduled-task XML has no root element."));
    }

    private static string BuildRouteKeeperTaskXml(string executable)
    {
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var start = System.Xml.XmlConvert.ToString(
            DateTime.UtcNow.AddMinutes(1), System.Xml.XmlDateTimeSerializationMode.Utc);
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(task + "Task", new XAttribute("version", "1.4"),
                new XElement(task + "Triggers",
                    new XElement(task + "BootTrigger", new XElement(task + "Enabled", "true")),
                    new XElement(task + "LogonTrigger", new XElement(task + "Enabled", "true")),
                    new XElement(task + "TimeTrigger",
                        new XElement(task + "Repetition", new XElement(task + "Interval", "PT5M")),
                        new XElement(task + "StartBoundary", start), new XElement(task + "Enabled", "true"))),
                BuildTaskPrincipal(task, "S-1-5-18", null, "HighestAvailable"),
                BuildControllerTaskSettings(task, true),
                new XElement(task + "Actions", new XAttribute("Context", "Author"),
                    new XElement(task + "Exec", new XElement(task + "Command", Path.GetFullPath(executable)),
                        new XElement(task + "Arguments", $"--controller-ip={FlyControllerIpv4}"))))).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildControllerUiTaskXml(string executable, string sid, bool enabled)
    {
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(task + "Task", new XAttribute("version", "1.4"),
                new XElement(task + "Triggers", new XElement(task + "LogonTrigger",
                    new XElement(task + "Enabled", "true"), new XElement(task + "UserId", sid))),
                BuildTaskPrincipal(task, sid, "InteractiveToken", "LeastPrivilege"),
                BuildControllerTaskSettings(task, enabled),
                new XElement(task + "Actions", new XAttribute("Context", "Author"),
                    new XElement(task + "Exec", new XElement(task + "Command", Path.GetFullPath(executable)))))).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildTaskPrincipal(
        XNamespace task,
        string sid,
        string? logonType,
        string runLevel)
    {
        var principal = new XElement(
            task + "Principal",
            new XAttribute("id", "Author"),
            new XElement(task + "UserId", sid));
        if (!string.IsNullOrEmpty(logonType))
            principal.Add(new XElement(task + "LogonType", logonType));
        principal.Add(new XElement(task + "RunLevel", runLevel));
        return new XElement(task + "Principals", principal);
    }

    private static XElement BuildControllerTaskSettings(XNamespace task, bool enabled) =>
        new(task + "Settings", new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(task + "DisallowStartIfOnBatteries", "false"),
            new XElement(task + "StopIfGoingOnBatteries", "false"),
            new XElement(task + "StartWhenAvailable", "true"),
            new XElement(task + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(task + "AllowStartOnDemand", "true"), new XElement(task + "Enabled", enabled ? "true" : "false"),
            new XElement(task + "ExecutionTimeLimit", "PT0S"));

    private static void RequireExactRouteKeeperTaskXml(string xml, string executable)
    {
        var task = RequireControllerTaskShape(
            xml, executable, "S-1-5-18", null, "HighestAvailable", true);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var triggers = task.Element(ns + "Triggers")!.Elements().ToArray();
        var exec = task.Element(ns + "Actions")!.Elements().Single();
        var repetition = triggers.SingleOrDefault(item => item.Name == ns + "TimeTrigger")?.Element(ns + "Repetition");
        if (triggers.Length != 3 || triggers.Count(item => item.Name == ns + "BootTrigger") != 1
            || triggers.Count(item => item.Name == ns + "LogonTrigger") != 1
            || triggers.Count(item => item.Name == ns + "TimeTrigger") != 1
            || triggers.Any(item => item.Element(ns + "Enabled")?.Value != "true")
            || repetition?.Element(ns + "Interval")?.Value != "PT5M"
            || repetition.Element(ns + "Duration") is not null
            || exec.Element(ns + "Arguments")?.Value != $"--controller-ip={FlyControllerIpv4}")
            throw new InvalidDataException("The RouteKeeper task does not match the exact protected contract.");
    }

    private static void RequireExactControllerUiTaskXml(
        string xml, string executable, string sid, bool enabled)
    {
        var task = RequireControllerTaskShape(xml, executable, sid, "InteractiveToken", "LeastPrivilege", enabled);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var triggers = task.Element(ns + "Triggers")!.Elements().ToArray();
        var exec = task.Element(ns + "Actions")!.Elements().Single();
        if (triggers.Length != 1 || triggers[0].Name != ns + "LogonTrigger"
            || triggers[0].Element(ns + "Enabled")?.Value != "true"
            || triggers[0].Element(ns + "UserId")?.Value != sid
            || exec.Element(ns + "Arguments") is not null)
            throw new InvalidDataException("The command-center task does not match the exact least-privilege contract.");
    }

    private static XElement RequireControllerTaskShape(
        string xml, string executable, string sid, string? logonType, string runLevel, bool enabled)
    {
        var document = ParseTaskXml(xml);
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root;
        var actions = root?.Element(task + "Actions")?.Elements().ToArray() ?? [];
        var principals = root?.Element(task + "Principals")?.Elements(task + "Principal").ToArray() ?? [];
        var settings = root?.Element(task + "Settings");
        var exec = actions.SingleOrDefault();
        var principal = principals.SingleOrDefault();
        var principalMatches = logonType is null
            ? IsSystemHighestPrincipal(principal, task)
            : principal?.Element(task + "UserId")?.Value == sid
              && principal.Element(task + "LogonType")?.Value == logonType
              && principal.Element(task + "RunLevel")?.Value == runLevel;
        if (root?.Name != task + "Task" || actions.Length != 1 || exec?.Name != task + "Exec"
            || principals.Length != 1
            || !string.Equals(exec.Element(task + "Command")?.Value, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            || !principalMatches
            || settings?.Element(task + "MultipleInstancesPolicy")?.Value != "IgnoreNew"
            || settings.Element(task + "DisallowStartIfOnBatteries")?.Value != "false"
            || settings.Element(task + "StopIfGoingOnBatteries")?.Value != "false"
            || settings.Element(task + "StartWhenAvailable")?.Value != "true"
            || settings.Element(task + "RunOnlyIfNetworkAvailable")?.Value != "false"
            || settings.Element(task + "AllowStartOnDemand")?.Value != "true"
            || settings.Element(task + "Enabled")?.Value != (enabled ? "true" : "false")
            || settings.Element(task + "ExecutionTimeLimit")?.Value != "PT0S")
            throw new InvalidDataException("A protected controller scheduled task has drifted.");
        return root;
    }

    private async Task<bool> RecoverControllerBootstrapAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(AppPaths.ControllerBootstrapFile))
            throw new InvalidDataException("The protected controller bootstrap path is a directory.");
        if (!File.Exists(AppPaths.ControllerBootstrapFile)) return false;
        var bytes = MachineStorageSecurity.ReadUserBootstrap(
            AppPaths.ControllerBootstrapFile, RequireInteractiveUserProfile().Sid, 64 * 1024);
        var existing = JsonSerializer.Deserialize<AdminBootstrap>(bytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The protected controller bootstrap is empty.");
        string token;
        try
        {
            token = SecretProtector.Unprotect(existing.ControllerTokenProtected, SecretScope.LocalMachine);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("The protected controller bootstrap cannot be authenticated.", exception);
        }
        if (existing.SchemaVersion != 1
            || !existing.IsMachineProtected
            || existing.CoordinatorUrl != _invite.CoordinatorUrl
            || existing.DeviceName != _invite.DeviceName
            || !FixedSecretEquals(token, _invite.ControllerToken))
            throw new InvalidDataException(
                "An unconsumed controller bootstrap belongs to a different authenticated invitation or user.");

        if (Directory.Exists(destination)
            && HasExactControllerReadyMarker(destination)
            && await ControllerPayloadMatchesSourceAsync(source, destination, cancellationToken))
            return true;

        MachineStorageSecurity.DeleteUserBootstrap(
            AppPaths.ControllerBootstrapFile, RequireInteractiveUserProfile().Sid);
        return false;
    }

    private static async Task<bool> ControllerPayloadMatchesSourceAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(source, "controller source directory");
        await VerifyControllerDirectoryAsync(destination, cancellationToken);
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), StringComparer.OrdinalIgnoreCase);
        var destinationFiles = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is not ControllerOwnershipMarkerName and not ControllerReadyMarkerName)
            .ToDictionary(path => Path.GetRelativePath(destination, path), StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count != destinationFiles.Count
            || sourceFiles.Keys.Except(destinationFiles.Keys, StringComparer.OrdinalIgnoreCase).Any())
            return false;
        foreach (var (relative, sourcePath) in sourceFiles)
        {
            var destinationPath = destinationFiles[relative];
            await using var sourceStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destinationStream = new FileStream(
                destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length != destinationStream.Length) return false;
            var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
            var destinationHash = await SHA256.HashDataAsync(destinationStream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash)) return false;
        }
        return true;
    }

    private static bool FixedSecretEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length
                   && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static async Task<FileStream> AcquireControllerInstallLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.InstallDirectory);
        var path = Path.Combine(AppPaths.InstallDirectory, ControllerInstallLockFileName);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        "Another Opticon controller installation, UI, or CLI still owns the installation lock.",
                        exception);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException("The Opticon controller installation lock cannot be opened.", exception);
            }
        }
    }

    private static async Task InstallControllerDirectoryTransactionalAsync(
        string source,
        string destination,
        Func<Task> configureActivatedPayload,
        CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(destination);
        var leaf = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(leaf))
            throw new InvalidOperationException("The controller installation directory is unsafe.");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $"{leaf}.installing-{Guid.NewGuid():N}");
        var backup = destination + ".previous";
        var failed = Path.Combine(parent, $"{leaf}.failed-{Guid.NewGuid():N}");
        RequireSafeInstallSibling(staging, parent, leaf + ".installing-");
        RequireSafeInstallSibling(backup, parent, leaf + ".previous");
        RequireSafeInstallSibling(failed, parent, leaf + ".failed-");

        var previousMoved = false;
        var candidateActivated = false;
        try
        {
            CopyDirectory(source, staging);
            File.Delete(Path.Combine(staging, ControllerReadyMarkerName));
            WriteControllerOwnershipMarker(staging);
            await VerifyControllerDirectoryAsync(staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(destination))
                await RequireOwnedControllerDirectoryAsync(destination, allowLegacyCanonical: true, cancellationToken);
            else if (File.Exists(destination))
                throw new InvalidDataException("The controller installation path is a file.");
            if (Directory.Exists(backup) || File.Exists(backup))
                throw new InvalidOperationException("An unrecovered controller backup is still present; refusing the swap.");

            RequireInstalledControllerProcessesClosed(destination, backup);
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                previousMoved = true;
                RequireInstalledControllerProcessesClosed(destination, backup);
            }

            Directory.Move(staging, destination);
            candidateActivated = true;
            await VerifyControllerDirectoryAsync(destination, CancellationToken.None);
            await configureActivatedPayload();
            // This flushed marker is the durable commit point and is written only
            // after the protected bootstrap and controller payload succeed.
            WriteControllerReadyMarker(destination);
            // Keep one verified .previous payload until the next locked run. Startup
            // recovery can restore it after a power loss, and PATH repair refuses it.
        }
        catch (Exception installError)
        {
            try
            {
                if (candidateActivated && Directory.Exists(destination))
                    Directory.Move(destination, failed);
                if (previousMoved && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                DeleteSafeInstallDirectory(failed, parent, leaf + ".failed-");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"Controller payload installation failed and rollback also failed. The prior payload remains at {backup}.",
                    installError,
                    rollbackError);
            }
            throw;
        }
        finally
        {
            DeleteSafeInstallDirectory(staging, parent, leaf + ".installing-");
        }
    }

    private static async Task RecoverControllerDirectoryTransactionAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new InvalidOperationException("The controller installation directory is unsafe.");
        var leaf = Path.GetFileName(destination);
        var backup = destination + ".previous";
        RequireSafeInstallSibling(backup, parent, leaf + ".previous");
        if (File.Exists(backup))
            throw new InvalidDataException($"The controller backup path is a file: {backup}");
        if (!Directory.Exists(backup)) return;

        RequireInstalledControllerProcessesClosed(destination, backup);
        await RequireOwnedControllerDirectoryAsync(backup, allowLegacyCanonical: true, cancellationToken);
        if (!Directory.Exists(destination))
        {
            if (File.Exists(destination))
                throw new InvalidDataException("The controller installation path is a file; the prior payload was preserved.");
            await RequireCommittedOrLegacyControllerDirectoryAsync(backup, cancellationToken);
            Directory.Move(backup, destination);
            return;
        }

        try
        {
            await RequireOwnedControllerDirectoryAsync(destination, allowLegacyCanonical: true, cancellationToken);
        }
        catch (Exception liveValidationError)
        {
            throw new InvalidDataException(
                $"Both the live controller directory and a recoverable prior payload exist. The prior payload was preserved at {backup}; repair the live directory before retrying.",
                liveValidationError);
        }
        RequireInstalledControllerProcessesClosed(destination, backup);
        if (HasExactControllerReadyMarker(destination))
        {
            await DeleteOwnedControllerDirectoryAsync(backup, allowLegacyCanonical: true, cancellationToken);
            return;
        }

        await RequireCommittedOrLegacyControllerDirectoryAsync(backup, cancellationToken);
        var failed = Path.Combine(parent, $"{leaf}.failed-{Guid.NewGuid():N}");
        RequireSafeInstallSibling(failed, parent, leaf + ".failed-");
        Directory.Move(destination, failed);
        try
        {
            Directory.Move(backup, destination);
            await DeleteOwnedControllerDirectoryAsync(failed, allowLegacyCanonical: true, CancellationToken.None);
        }
        catch (Exception rollbackError)
        {
            throw new InvalidDataException(
                $"An uncommitted controller payload was detected, but the prior payload could not be restored. The uncommitted payload remains at {failed}.",
                rollbackError);
        }
    }

    private static void WriteControllerOwnershipMarker(string directory)
    {
        File.WriteAllText(
            Path.Combine(directory, ControllerOwnershipMarkerName),
            ControllerOwnershipMarkerValue,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteControllerReadyMarker(string directory)
    {
        using var stream = new FileStream(
            Path.Combine(directory, ControllerReadyMarkerName),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using (var writer = new StreamWriter(
                   stream,
                   new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   4096,
                   leaveOpen: true))
        {
            var version = ReadExactControllerFileVersion(Path.Combine(directory, "Opticon.exe"), "UI");
            writer.Write($"{ControllerReadyMarkerValue}|{version}");
            writer.Flush();
        }
        stream.Flush(flushToDisk: true);
    }

    private static bool HasExactControllerReadyMarker(string directory)
    {
        var marker = Path.Combine(directory, ControllerReadyMarkerName);
        if (!File.Exists(marker)) return false;
        try
        {
            var uiVersion = ReadExactControllerFileVersion(Path.Combine(directory, "Opticon.exe"), "UI");
            var cliVersion = ReadExactControllerFileVersion(Path.Combine(directory, "Cli", "opticon.exe"), "CLI");
            return uiVersion == cliVersion
                   && File.ReadAllText(marker).Equals(
                       $"{ControllerReadyMarkerValue}|{uiVersion}",
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task RequireCommittedOrLegacyControllerDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        await RequireOwnedControllerDirectoryAsync(directory, allowLegacyCanonical: true, cancellationToken);
        if (File.Exists(Path.Combine(directory, ControllerOwnershipMarkerName))
            && !HasExactControllerReadyMarker(directory))
            throw new InvalidDataException($"The retained controller payload was owned but never durably committed: {directory}");
    }

    private static async Task VerifyControllerDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "controller directory");
        var marker = Path.Combine(directory, ControllerOwnershipMarkerName);
        if (!File.Exists(marker)
            || !string.Equals(
                await File.ReadAllTextAsync(marker, cancellationToken),
                ControllerOwnershipMarkerValue,
                StringComparison.Ordinal))
            throw new InvalidDataException("The controller installation ownership marker is missing or invalid.");
        var controller = Path.Combine(directory, "Opticon.exe");
        var cli = Path.Combine(directory, "Cli", "opticon.exe");
        if (!File.Exists(controller) || !File.Exists(cli))
            throw new FileNotFoundException("The staged controller UI or CLI is missing.");
        var executables = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories).ToArray();
        if (executables.Length < 2)
            throw new InvalidDataException("The staged controller payload is incomplete.");
        foreach (var executable in executables)
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
        var uiVersion = ReadExactControllerFileVersion(controller, "UI");
        var cliVersion = ReadExactControllerFileVersion(cli, "CLI");
        if (uiVersion != cliVersion)
            throw new InvalidDataException(
                $"The controller UI ({uiVersion}) and CLI ({cliVersion}) versions do not match.");
    }

    private static Version ReadExactControllerFileVersion(string path, string description)
    {
        var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Version.TryParse(text, out var version)
            ? version
            : throw new InvalidDataException($"The controller {description} has no valid file version.");
    }

    private static async Task RequireOwnedControllerDirectoryAsync(
        string directory,
        bool allowLegacyCanonical,
        CancellationToken cancellationToken)
    {
        RejectDirectoryReparsePoint(directory, "controller directory");
        var marker = Path.Combine(directory, ControllerOwnershipMarkerName);
        if (File.Exists(marker))
        {
            await VerifyControllerDirectoryAsync(directory, cancellationToken);
            return;
        }

        var canonical = Path.GetFullPath(Path.Combine(AppPaths.InstallDirectory, "Admin"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!allowLegacyCanonical
            || (!full.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(canonical + ".previous", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Refusing to replace unowned controller directory: {full}");

        var legacyExecutable = new[]
            {
                Path.Combine(full, "Opticon.exe"),
                Path.Combine(full, "Taildesk.Admin.exe")
            }
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidDataException($"The legacy controller directory is not recognizably Opticon-owned: {full}");
        var legacyExecutables = Directory.EnumerateFiles(full, "*.exe", SearchOption.AllDirectories).ToArray();
        if (legacyExecutables.Length == 0)
            throw new InvalidDataException($"The legacy controller directory has no executable payload: {full}");
        foreach (var executable in legacyExecutables)
            await ProductSigning.VerifyAuthenticodeAsync(executable, cancellationToken);
    }

    private static async Task DeleteOwnedControllerDirectoryAsync(
        string path,
        bool allowLegacyCanonical,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return;
        await RequireOwnedControllerDirectoryAsync(path, allowLegacyCanonical, cancellationToken);
        Directory.Delete(path, recursive: true);
    }

    private static void RequireSafeInstallSibling(string path, string parent, string leafPrefix)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith(leafPrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe controller installation transaction path: {fullPath}");
    }

    private static void DeleteSafeInstallDirectory(string path, string parent, string leafPrefix)
    {
        RequireSafeInstallSibling(path, parent, leafPrefix);
        if (File.Exists(path))
            throw new InvalidDataException($"Controller transaction directory path is a file: {path}");
        if (!Directory.Exists(path)) return;
        RejectDirectoryReparsePoint(path, "controller transaction directory");
        Directory.Delete(path, recursive: true);
    }

    private static void RejectDirectoryReparsePoint(string path, string description)
    {
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The {description} contains a reparse point: {directory}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The {description} contains a reparse point: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void RequireInstalledControllerProcessesClosed(params string[] directories)
    {
        var roots = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return;

        foreach (var processName in new[] { "Opticon", "Taildesk.Admin", "Taildesk.OpticonCli" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string runningPath;
                    try
                    {
                        runningPath = Path.GetFullPath(process.MainModule?.FileName
                            ?? throw new InvalidOperationException("Windows did not expose the process executable path."));
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            $"Opticon could not verify running process {process.ProcessName} ({process.Id}); close it before installation.",
                            exception);
                    }
                    if (roots.Any(root => IsPathWithinDirectory(runningPath, root)))
                        throw new InvalidOperationException(
                            "Close the installed or retained Opticon UI and CLI normally before upgrading. " +
                            "This lets active SSH sessions revoke their leases and erase ephemeral keys.");
                }
            }
        }
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private async Task<LocalTailscaleSnapshot> ReadTailscaleStatusAsync(string tailscale, CancellationToken cancellationToken)
    {
        var result = await RunPrivilegedChildAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(result, "Tailscale status was unavailable");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var self = root.GetProperty("Self");
        var ips = self.GetProperty("TailscaleIPs").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        return new LocalTailscaleSnapshot
        {
            DeviceId = self.TryGetProperty("ID", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            DnsName = self.TryGetProperty("DNSName", out var dns) ? (dns.GetString() ?? string.Empty).TrimEnd('.') : string.Empty,
            Ip = ips.FirstOrDefault(ip => ip.Contains('.')) ?? ips.FirstOrDefault() ?? string.Empty,
            Online = true,
            Tailnet = ReadTailnet(root),
            Tags = self.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray()
                : []
        };
    }

    private async Task<LocalTailscaleSnapshot> WaitForExpectedTailscaleSessionAsync(string tailscale, CancellationToken cancellationToken)
    {
        LocalTailscaleSnapshot? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                last = await ReadTailscaleStatusAsync(tailscale, cancellationToken);
                if (!string.IsNullOrWhiteSpace(last.Ip)
                    && (!ValidationEnabled(ClientInstallValidationStep.NetworkIdentity)
                        || ExistingSessionHasExpectedRole(last))) return last;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The Windows service can take a few seconds to publish the new
                // self identity and tags after `tailscale up` returns.
            }
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        }
        return last ?? await ReadTailscaleStatusAsync(tailscale, cancellationToken);
    }

    private async Task<LocalTailscaleSnapshot?> TryReadTailscaleStatusAsync(string tailscale, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunPrivilegedChildAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(15), cancellationToken);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var backendState = root.TryGetProperty("BackendState", out var state) ? state.GetString() ?? string.Empty : string.Empty;
            if (!backendState.Equals("Running", StringComparison.OrdinalIgnoreCase)) return null;
            var self = root.GetProperty("Self");
            var ips = self.GetProperty("TailscaleIPs").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
            return new LocalTailscaleSnapshot
            {
                DeviceId = self.TryGetProperty("ID", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                DnsName = self.TryGetProperty("DNSName", out var dns) ? (dns.GetString() ?? string.Empty).TrimEnd('.')
                    : self.TryGetProperty("HostName", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                Ip = ips.FirstOrDefault(ip => ip.Contains('.')) ?? string.Empty,
                Online = true,
                Tailnet = ReadTailnet(root),
                Tags = self.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray()
                    : []
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForEnrollmentAsync(CancellationToken cancellationToken)
    {
        var store = new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var state = await store.LoadAsync(cancellationToken);
            if (EnrollmentMatchesInvitation(state))
            {
                await CommitEnrollmentReceiptAsync(state, cancellationToken);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("The agent is installed and will keep retrying, but the command center did not confirm enrollment within two minutes. Make sure the Opticon command center is running and the private-network policy is active.");
    }

    private async Task<InstallerEnsureResult> EnsureEnrollmentCommittedAsync(
        AgentConfig? completedState,
        CancellationToken cancellationToken)
    {
        if (completedState is not null)
        {
            // The coordinator and Agent use the invitation identity as an
            // idempotency key. A lost response therefore reconciles by proving
            // the exact completed local identity rather than spending another
            // invitation key or rewriting network enrollment.
            await CommitEnrollmentReceiptAsync(completedState, cancellationToken);
            return InstallerEnsureResult.Ready(
                "EnsureEnrollmentCommittedAsync",
                "The already-accepted invitation identity was reconciled and its protected receipt was committed.");
        }

        await WaitForEnrollmentAsync(cancellationToken);
        return InstallerEnsureResult.Repaired(
            "EnsureEnrollmentCommittedAsync",
            "The command center accepted the exact invitation identity and the protected enrollment receipt was committed.",
            "Reconciled enrollment through the invitation's idempotent identity.");
    }

    private bool EnrollmentMatchesInvitation(AgentConfig state)
    {
        var expectedTokenHash = SecurityHelpers.HashToken(_invite.AgentToken);
        return state.CompletedInviteId == _invite.InviteId
               && state.PendingInviteId is null
               && string.IsNullOrEmpty(state.PendingInviteSecretProtected)
               && state.DeviceId != Guid.Empty
               && state.Role == _invite.Role
               && state.DeviceName.Equals(_invite.DeviceName, StringComparison.Ordinal)
               && state.CoordinatorUrl.Equals(_invite.CoordinatorUrl, StringComparison.Ordinal)
               && FixedAsciiEquals(state.AgentTokenHash, expectedTokenHash);
    }

    private async Task CommitEnrollmentReceiptAsync(
        AgentConfig state,
        CancellationToken cancellationToken)
    {
        if (!EnrollmentMatchesInvitation(state))
            throw new InvalidDataException(
                "The protected Agent state does not prove completion of this exact invitation.");
        var agentExecutable = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
        await ProductSigning.VerifyAuthenticodeAsync(agentExecutable, cancellationToken);
        var version = FileVersionInfo.GetVersionInfo(agentExecutable).ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("The enrolled Agent executable has no product version.");
        await using var stream = new FileStream(
            agentExecutable, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        var receipt = new EnrollmentReceipt
        {
            InviteId = _invite.InviteId,
            DeviceId = state.DeviceId,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            AgentTokenHash = state.AgentTokenHash,
            AgentVersion = version,
            AgentSize = stream.Length,
            AgentSha256 = sha256,
            AgentInstallOperationId = _agentInstallTransaction?.OperationId ?? Guid.Empty
        };
        var store = new MachineJsonFileStore<EnrollmentReceipt>(AppPaths.InstallReceiptFile);
        await store.SaveAsync(receipt, cancellationToken);
        var committed = await store.LoadAsync(cancellationToken);
        if (committed.SchemaVersion != 3
            || committed.InviteId != receipt.InviteId
            || committed.DeviceId != receipt.DeviceId
            || committed.AgentVersion != receipt.AgentVersion
            || committed.AgentSize != receipt.AgentSize
            || committed.AgentInstallOperationId != receipt.AgentInstallOperationId
            || !FixedAsciiEquals(committed.AgentTokenHash, receipt.AgentTokenHash)
            || !FixedAsciiEquals(committed.AgentSha256, receipt.AgentSha256))
            throw new InvalidDataException("The protected enrollment success receipt did not verify after commit.");

        // This durable, re-read receipt is the Agent commit fence. Nothing in
        // later journal deletion, source bookkeeping, rollback-directory
        // cleanup, or trust pruning may undo a coordinator-accepted Agent.
        _agentInstallCommitted = true;
        if (_machineInstallTransaction is not null)
        {
            MachineInstallTransactionPersistence.RecordVerifiedRepair(
                _machineInstallTransaction,
                "EnsureEnrollmentCommittedAsync",
                repaired: true,
                "The protected receipt matches the exact invitation, Agent identity, executable version, and hash.",
                "EnrollmentReceipt");
            await MachineInstallTransactionPersistence.SaveAsync(_machineInstallTransaction, cancellationToken);
        }
        await CompleteMachineInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.CommitActiveInstallation();
        await FinalizeAgentInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.PruneInstalledTrust();
    }

    private async Task<bool> CommitPendingEnrollmentAsync(CancellationToken cancellationToken)
    {
        var state = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
            .LoadAsync(cancellationToken);
        if (EnrollmentMatchesInvitation(state))
        {
            // Reconcile the narrow race in which the Agent persisted the
            // coordinator response immediately after the final polling read.
            await CommitEnrollmentReceiptAsync(state, cancellationToken);
            return true;
        }
        var expectedTokenHash = SecurityHelpers.HashToken(_invite.AgentToken);
        if (state.PendingInviteId != _invite.InviteId
            || state.CompletedInviteId is not null
            || string.IsNullOrWhiteSpace(state.PendingInviteSecretProtected)
            || state.DeviceId == Guid.Empty
            || state.Role != _invite.Role
            || !state.DeviceName.Equals(_invite.DeviceName, StringComparison.Ordinal)
            || !state.CoordinatorUrl.Equals(_invite.CoordinatorUrl, StringComparison.Ordinal)
            || !FixedAsciiEquals(state.AgentTokenHash, expectedTokenHash)
            || !IPAddress.TryParse(state.BindAddress, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || address.GetAddressBytes() is not [100, >= 64 and <= 127, _, _])
            throw new InvalidDataException(
                "The local Agent state cannot safely continue this invitation in the background.");

        var agentExecutable = Path.Combine(
            AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
        await ProductSigning.VerifyAuthenticodeAsync(agentExecutable, cancellationToken);
        await RequireExactAgentTaskAsync(cancellationToken);

        // The exact signed Agent, protected pending state, SYSTEM task, local
        // API listener, firewall, and mesh identity were all verified by the
        // caller. Preserve this generation so it can keep retrying the
        // invitation's idempotent enrollment after Setup exits.
        _agentInstallCommitted = true;
        await CompleteMachineInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.CommitActiveInstallation();
        await FinalizeAgentInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.PruneInstalledTrust();
        return false;
    }

    private async Task CommitWithoutEnrollmentConfirmationAsync(CancellationToken cancellationToken)
    {
        // Emergency policy skips the remote receipt comparison, not local
        // transaction completion. Leaving either journal pending would turn a
        // successful installation into a forced recovery on the next launch.
        if (ValidationEnabled(ClientInstallValidationStep.ComponentPostconditions))
            await RequireExactAgentTaskAsync(cancellationToken);
        _agentInstallCommitted = true;
        await CompleteMachineInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.CommitActiveInstallation();
        await FinalizeAgentInstallTransactionAsync(cancellationToken);
        SourceBuildProvenance.PruneInstalledTrust();
    }

    private static bool FixedAsciiEquals(string left, string right)
    {
        if (left.Length != right.Length || left.Any(character => character > 0x7f)
            || right.Any(character => character > 0x7f))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));
    }

    private bool ExistingSessionHasExpectedRole(LocalTailscaleSnapshot snapshot)
    {
        var expected = _invite.Role == DeviceRole.ControllerAndManaged ? "tag:taildesk-controller" : "tag:taildesk-managed";
        var opposite = _invite.Role == DeviceRole.ControllerAndManaged ? "tag:taildesk-managed" : "tag:taildesk-controller";
        var hasExitTag = snapshot.Tags.Contains("tag:taildesk-exit", StringComparer.OrdinalIgnoreCase);
        return snapshot.Tags.Contains(expected, StringComparer.OrdinalIgnoreCase)
               && !snapshot.Tags.Contains(opposite, StringComparer.OrdinalIgnoreCase)
               && hasExitTag == _invite.AdvertiseExitNode
               && snapshot.Tailnet.Equals(_invite.ExpectedTailnet, StringComparison.OrdinalIgnoreCase);
    }

    private bool ExistingSessionHasExpectedDeviceName(LocalTailscaleSnapshot snapshot)
    {
        var dnsLabel = snapshot.DnsName.Split('.', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return dnsLabel.Equals(
            TailscaleCommandLine.NormalizeHostName(_invite.DeviceName, Environment.MachineName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTailnet(JsonElement root)
    {
        if (root.TryGetProperty("CurrentTailnet", out var current) && current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() ?? string.Empty;
        }
        return root.TryGetProperty("MagicDNSSuffix", out var suffix) && suffix.ValueKind == JsonValueKind.String
            ? suffix.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task<VerifiedInstallerLease> DownloadVerifiedAsync(
        DependencyArtifact artifact,
        string protectedDirectory,
        CancellationToken cancellationToken)
    {
        MachineStorageSecurity.RequireRestrictedDirectory(protectedDirectory);
        if (!string.Equals(Path.GetFileName(artifact.FileName), artifact.FileName, StringComparison.Ordinal)
            || !artifact.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pinned dependency filename is unsafe.");
        var destination = Path.Combine(protectedDirectory, artifact.FileName);
        var errors = new List<string>();
        foreach (var url in new[] { artifact.PrimaryUrl, artifact.FallbackUrl })
        {
            try
            {
                DeleteStagedPartial(destination, protectedDirectory);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidDataException("The pinned dependency URL is not safe HTTPS.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.AcceptEncoding.Clear();
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new HttpRequestException($"Dependency server returned HTTP {(int)response.StatusCode}.");
                if (ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity)
                    && response.Content.Headers.ContentLength != artifact.Size)
                    throw new InvalidDataException("The dependency response omitted or changed its pinned Content-Length.");
                if (ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity)
                    && response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("Encoded dependency responses are not accepted.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(
                                 destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[128 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var maximumSize = ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity)
                            ? artifact.Size : long.MaxValue;
                        var remaining = maximumSize - total;
                        var requested = checked((int)Math.Min(buffer.Length, Math.Max(1L, remaining + 1)));
                        var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                        if (read == 0) break;
                        total = checked(total + read);
                        if (total > maximumSize)
                            throw new InvalidDataException("The dependency response exceeded its pinned size.");
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    if (ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity)
                        && total != artifact.Size)
                        throw new InvalidDataException("The dependency response ended before its pinned size.");
                }
                if (ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity)
                    && !CryptographicOperations.FixedTimeEquals(
                        hasher.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
                    throw new InvalidDataException("SHA-256 did not match the pinned artifact.");
                MachineStorageSecurity.SealRestrictedFile(destination);
                var lease = new VerifiedInstallerLease(
                    destination,
                    artifact,
                    new FileStream(
                        destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
                try
                {
                    await VerifyInstallerLeaseAsync(lease, cancellationToken);
                    return lease;
                }
                catch
                {
                    await lease.DisposeAsync();
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{new Uri(url).Host}: {exception.Message}");
            }
        }
        try { DeleteStagedPartial(destination, protectedDirectory); } catch { }
        throw new InvalidDataException($"Neither verified source supplied {artifact.Product} {artifact.Version}: {string.Join(" | ", errors)}");
    }

    private async Task<ProcessResult> InstallVerifiedMsiAsync(
        VerifiedInstallerLease lease,
        CancellationToken cancellationToken)
    {
        await VerifyInstallerLeaseAsync(lease, cancellationToken);
        return await ProcessRunner.RunAsync(
            SystemExecutable("msiexec.exe"),
            ["/i", lease.Path, "/qn", "/norestart"],
            TimeSpan.FromMinutes(5),
            cancellationToken,
            environment: BuildPrivilegedEnvironment(),
            clearEnvironment: true);
    }

    private async Task VerifyInstallerLeaseAsync(
        VerifiedInstallerLease lease,
        CancellationToken cancellationToken)
    {
        if (!ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity)) return;
        if (lease.Stream.Length != lease.Artifact.Size)
            throw new InvalidDataException("The held installer lease changed size.");
        lease.Stream.Position = 0;
        var hash = await SHA256.HashDataAsync(lease.Stream, cancellationToken);
        lease.Stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(lease.Artifact.Sha256)))
            throw new InvalidDataException("The held installer lease no longer matches its pinned SHA-256.");
        var signer = await RequireInstallerSignatureAsync(lease.Path, cancellationToken);
        var expectedSigner = lease.Artifact.ExpectedSignerThumbprint.ToUpperInvariant();
        if (!Regex.IsMatch(expectedSigner, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expectedSigner),
                System.Text.Encoding.ASCII.GetBytes(signer)))
            throw new InvalidDataException("The dependency signer does not match its pinned publisher.");
        if (lease.SignerThumbprint is null) lease.SignerThumbprint = signer;
        else if (!CryptographicOperations.FixedTimeEquals(
                     System.Text.Encoding.ASCII.GetBytes(lease.SignerThumbprint),
                     System.Text.Encoding.ASCII.GetBytes(signer)))
            throw new InvalidDataException("The held installer signer changed after verification.");
    }

    private static async Task<string> RequireInstallerSignatureAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var pathBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path));
        var command =
            "$ErrorActionPreference='Stop';" +
            "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + pathBase64 + "'));" +
            @"$s=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $p;" +
            "if($s.Status.ToString() -cne 'Valid' -or $null -eq $s.SignerCertificate -or $null -eq $s.TimeStamperCertificate){exit 41};" +
            "$eku=$s.SignerCertificate.EnhancedKeyUsageList | Where-Object {$_.ObjectId -eq '1.3.6.1.5.5.7.3.3'};" +
            "if($null -eq $eku){exit 42};" +
            "[Console]::Out.Write($s.SignerCertificate.Thumbprint.ToUpperInvariant())";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var result = await RunSystemToolAsync(
            @"WindowsPowerShell\v1.0\powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-EncodedCommand", encoded],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        var signer = result.StandardOutput.Trim().ToUpperInvariant();
        if (!result.Succeeded || !Regex.IsMatch(signer, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The pinned dependency is not signed and timestamped by a valid Windows publisher.");
        return signer;
    }

    private static void DeleteStagedPartial(string path, string protectedDirectory)
    {
        MachineStorageSecurity.RequireRestrictedDirectory(protectedDirectory);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(protectedDirectory),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The dependency staging path escaped its protected directory.");
        if (!File.Exists(full))
        {
            if (Directory.Exists(full))
                throw new InvalidDataException("The dependency staging path is a directory.");
            return;
        }
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The dependency staging object is not a regular file.");
        File.Delete(full);
    }

    private static string SystemExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || fileName.Contains(':'))
            throw new InvalidDataException("The privileged Windows executable name is unsafe.");
        var systemDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.SystemDirectory));
        var executable = Path.GetFullPath(Path.Combine(systemDirectory, fileName));
        if (!executable.StartsWith(systemDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable))
            throw new FileNotFoundException("The fixed System32 executable is missing.", executable);
        var relative = Path.GetRelativePath(systemDirectory, executable);
        var current = systemDirectory;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidDataException("The privileged Windows executable path is unsafe.");
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The privileged Windows executable path contains a reparse point.");
        }
        var attributes = File.GetAttributes(executable);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The fixed System32 executable is not a regular file.");
        return executable;
    }

    private static Task<ProcessResult> RunSystemToolAsync(
        string relativePath,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        bool captureOutput = true) =>
        ProcessRunner.RunAsync(
            SystemExecutable(relativePath),
            arguments,
            timeout,
            cancellationToken,
            captureOutput,
            BuildPrivilegedEnvironment(),
            clearEnvironment: true);

    private static Task<ProcessResult> RunPrivilegedChildAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        bool captureOutput = true)
    {
        var full = Path.GetFullPath(executable);
        var programFiles = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        if (!full.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The privileged child escaped the fixed Program Files root.");
        var current = programFiles;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixed Program Files root is a reparse point.");
        foreach (var component in Path.GetRelativePath(programFiles, full).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidDataException("The privileged child path is unsafe.");
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new FileNotFoundException("The fixed privileged child path is incomplete.", current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The privileged child path contains a reparse point.");
        }
        if (!File.Exists(full)
            || (File.GetAttributes(full) & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("The fixed privileged child executable is missing or unsafe.", full);
        return ProcessRunner.RunAsync(
            full,
            arguments,
            timeout,
            cancellationToken,
            captureOutput,
            BuildPrivilegedEnvironment(),
            clearEnvironment: true);
    }

    private static IReadOnlyDictionary<string, string?> BuildPrivilegedEnvironment()
    {
        var windows = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var system32 = Path.GetFullPath(Environment.SystemDirectory);
        var programFiles = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["ProgramData"] = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ["ProgramFiles"] = programFiles,
            ["CommonProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            ["ComSpec"] = SystemExecutable("cmd.exe"),
            ["PATH"] = system32 + Path.PathSeparator + Path.Combine(system32, "Wbem"),
            ["PATHEXT"] = ".COM;.EXE",
            ["PSModulePath"] = Path.Combine(system32, "WindowsPowerShell", "v1.0", "Modules"),
            ["USERPROFILE"] = Path.Combine(system32, "config", "systemprofile"),
            ["APPDATA"] = Path.Combine(system32, "config", "systemprofile", "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(system32, "config", "systemprofile", "AppData", "Local"),
            ["TEMP"] = AppPaths.SetupStagingDirectory,
            ["TMP"] = AppPaths.SetupStagingDirectory
        };
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            environment["ProgramFiles(x86)"] = Path.GetFullPath(programFilesX86);
        return environment;
    }

    private static async Task<bool> WaitForListeningExecutableAsync(
        int port,
        string expectedExecutable,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        var expected = RequireFixedProgramFilesExecutable(expectedExecutable);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        do
        {
            try
            {
                foreach (var processId in GetListeningProcessIds(port))
                {
                    using var process = Process.GetProcessById(processId);
                    var actual = Path.GetFullPath(process.MainModule?.FileName
                        ?? throw new InvalidDataException("Windows did not expose the listening process image."));
                    if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static IReadOnlyList<int> GetListeningProcessIds(int port)
    {
        const int addressFamilyInet = 2;
        const int ownerPidListenerTable = 3;
        const uint insufficientBuffer = 122;
        var size = 0;
        var first = GetExtendedTcpTable(
            IntPtr.Zero, ref size, order: false, addressFamilyInet, ownerPidListenerTable, reserved: 0);
        if (first != insufficientBuffer || size < sizeof(int))
            throw new InvalidOperationException("Windows could not size the TCP owner table.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(
                buffer, ref size, order: false, addressFamilyInet, ownerPidListenerTable, reserved: 0);
            if (result != 0)
                throw new InvalidOperationException($"Windows could not read the TCP owner table (error {result}).");
            var count = Marshal.ReadInt32(buffer);
            if (count is < 0 or > 1_000_000)
                throw new InvalidDataException("Windows returned an invalid TCP owner-table length.");
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rows = new List<int>();
            for (var index = 0; index < count; index++)
            {
                var offset = checked(sizeof(int) + (index * rowSize));
                if (offset < sizeof(int) || checked(offset + rowSize) > size)
                    throw new InvalidDataException("Windows returned a truncated TCP owner table.");
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(buffer, offset));
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort));
                if (localPort == port && row.OwningProcessId is > 0 and <= int.MaxValue)
                    rows.Add((int)row.OwningProcessId);
            }
            return rows.Distinct().ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private async Task RemoveStandaloneComponentAsync(string componentName, string[] displayNames, string executablePath, CancellationToken cancellationToken)
    {
        var productCode = FindInstalledMsiProductCode(displayNames);
        if (string.IsNullOrWhiteSpace(productCode))
        {
            throw new InvalidOperationException($"A standalone {componentName} installation was detected, but Windows did not provide a safe MSI product code for removal. Remove it manually, then run this Opticon invitation again.");
        }

        _progress.Report(new InstallProgress(componentName == "Tailscale" ? 8 : 45, $"Removing the existing standalone {componentName} installation…"));
        var uninstall = await RunSystemToolAsync("msiexec.exe", ["/x", productCode, "/qn", "/norestart"], TimeSpan.FromMinutes(5), cancellationToken);
        EnsureSuccess(uninstall, $"Could not remove the existing {componentName} installation");

        for (var attempt = 0; attempt < 20 && (File.Exists(executablePath) || FindInstalledMsiProductCode(displayNames) is not null); attempt++)
            await Task.Delay(500, cancellationToken);
        if (File.Exists(executablePath) || FindInstalledMsiProductCode(displayNames) is not null)
        {
            throw new InvalidOperationException($"Windows reported that {componentName} was removed, but its standalone installation is still present. Restart Windows, remove it, then run this Opticon invitation again.");
        }
    }

    private static string? FindInstalledMsiProductCode(IEnumerable<string> displayNames)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
            if (uninstall is null) continue;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName, writable: false);
                var displayName = entry?.GetValue("DisplayName") as string;
                if (entry is null || !displayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase)) continue;

                var candidate = ExtractMsiProductCode(entry.GetValue("UninstallString") as string)
                                ?? ExtractMsiProductCode(subKeyName);
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? ExtractMsiProductCode(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}");
        return match.Success ? match.Value : null;
    }

    private static bool IsMsiUpgradeConflict(ProcessResult result)
    {
        // ERROR_PRODUCT_VERSION (1638) is the documented indication that the
        // vendor's installed product refuses this MSI as an in-place upgrade.
        // Do not treat generic MSI failures as permission to uninstall an
        // existing component.
        return result.ExitCode == 1638;
    }

    private async Task<bool> InstalledDependencyMatchesAsync(
        string executable,
        DependencyArtifact artifact,
        bool runVersionCommand,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ValidationEnabled(ClientInstallValidationStep.DependencyIntegrity))
                return File.Exists(executable);
            var full = RequireFixedProgramFilesExecutable(executable);
            var signer = await RequireInstallerSignatureAsync(full, cancellationToken);
            var expectedSigner = artifact.ExpectedSignerThumbprint.ToUpperInvariant();
            if (!Regex.IsMatch(expectedSigner, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expectedSigner),
                    System.Text.Encoding.ASCII.GetBytes(signer)))
                return false;
            var productVersion = FileVersionInfo.GetVersionInfo(full).ProductVersion?.Trim();
            if (productVersion != artifact.Version && productVersion != artifact.Version + ".0")
                return false;
            if (!runVersionCommand) return true;
            var result = await RunPrivilegedChildAsync(
                full, ["version"], TimeSpan.FromSeconds(20), cancellationToken);
            var reportedVersion = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return result.Succeeded && reportedVersion == artifact.Version;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string RequireFixedProgramFilesExecutable(string executable)
    {
        var full = Path.GetFullPath(executable);
        var programFiles = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        if (!full.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full))
            throw new FileNotFoundException("The fixed dependency executable is missing or outside Program Files.", full);
        var current = programFiles;
        foreach (var component in Path.GetRelativePath(programFiles, full).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
                throw new InvalidDataException("The fixed dependency executable path is unsafe.");
            current = Path.Combine(current, component);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The fixed dependency executable path contains a reparse point.");
        }
        if ((File.GetAttributes(full) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("The fixed dependency executable is not a regular file.");
        return full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
    private Dictionary<string, string> BuildSharedRoots(IEnumerable<string> requested)
    {
        // Shared folders are optional management convenience, not a recovery
        // prerequisite.  A machine installed before first sign-in can enroll
        // with no roots and add them on a later attended repair.
        var profile = _userProfile;
        if (profile is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = profile.Desktop,
            ["Documents"] = profile.Documents,
            ["Downloads"] = profile.Downloads,
            ["Pictures"] = profile.Pictures,
            ["Videos"] = profile.Videos
        };
        return requested.Where(known.ContainsKey)
            .Select(name => new KeyValuePair<string, string>(
                name, PathGuard.ValidateRemoteFileRoot(known[name])))
            .Where(pair => Directory.Exists(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private InteractiveUserProfile RequireInteractiveUserProfile() => _userProfile
        ?? throw new InvalidOperationException(
            "The interactive user profile was not verified before Agent configuration.");

    private static void CopyDirectory(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"The protected payload directory is missing: {source}");
        RejectDirectoryReparsePoint(source, "source payload directory");
        var destinationParent = Path.GetDirectoryName(destination)
                                ?? throw new InvalidDataException("The payload destination has no parent directory.");
        if (Directory.Exists(destinationParent))
            RejectDirectoryReparsePoint(destinationParent, "payload destination parent");
        Directory.CreateDirectory(destination);
        RejectDirectoryReparsePoint(destination, "payload destination directory");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The source payload contains an unsafe directory.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The payload directory escaped its destination.");
            Directory.CreateDirectory(target);
            RejectDirectoryReparsePoint(target, "payload destination directory");
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var attributes = File.GetAttributes(file);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("The source payload contains an unsafe file.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The payload file escaped its destination.");
            if (File.Exists(target)
                && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The payload destination file is a reparse point.");
            using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.WriteThrough);
            input.CopyTo(output, 128 * 1024);
            output.Flush(flushToDisk: true);
        }
        RejectDirectoryReparsePoint(destination, "copied payload directory");
    }

    private static string FindTailscale()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Tailscale was installed but tailscale.exe was not found.");
    }

    private void EnsureInviteIsValid()
    {
        var supportedRoots = new HashSet<string>(["Desktop", "Documents", "Downloads", "Pictures", "Videos"], StringComparer.OrdinalIgnoreCase);
        if (!InvitationPolicy.IsSupportedPayloadSchema(_invite.SchemaVersion) || _invite.InviteId == Guid.Empty || string.IsNullOrWhiteSpace(_invite.TailscaleAuthKey)
            || !Uri.TryCreate(_invite.HeadscaleLoginUrl, UriKind.Absolute, out var loginUri) || loginUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(_invite.ExpectedTailnet)
            || string.IsNullOrWhiteSpace(_invite.AgentToken) || string.IsNullOrWhiteSpace(_invite.InviteSecret)
            || _invite.AllowedRoots is null || _invite.AllowedRoots.Length == 0
            || _invite.AllowedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _invite.AllowedRoots.Length
            || _invite.AllowedRoots.Any(root => !supportedRoots.Contains(root)))
        {
            throw new InvalidDataException("This is not a valid Opticon invitation.");
        }
        if (_invite.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("This Opticon invitation has expired. Create a new one on the command center.");
        }
    }

    private void EnsureSuccess(ProcessResult result, string message)
    {
        if (!result.Succeeded && result.ExitCode != 3010)
        {
            var detail = $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}";
            foreach (var secret in new[] { _invite.TailscaleAuthKey, _invite.AgentToken, _invite.InviteSecret, _invite.ControllerToken, _invite.RustDeskPassword })
            {
                if (!string.IsNullOrEmpty(secret)) detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
            throw new InvalidOperationException($"{message}: {detail}".Trim());
        }
    }

    private string FirstProcessFailureDetail(ProcessResult result, string fallback)
    {
        var detail = new[] { result.StandardError, result.StandardOutput }
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length != 0) ?? fallback;
        foreach (var secret in new[]
                 {
                     _invite.TailscaleAuthKey, _invite.AgentToken, _invite.InviteSecret,
                     _invite.ControllerToken, _invite.RustDeskPassword
                 })
        {
            if (!string.IsNullOrEmpty(secret))
                detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return detail;
    }

    private static void EnsureSystemToolSuccess(ProcessResult result, string message)
    {
        if (!result.Succeeded && result.ExitCode != 3010)
        {
            var detail = $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}".Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(detail) ? message : $"{message}: {detail}");
        }
    }

    private sealed class LocalTailscaleSnapshot
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DnsName { get; init; } = string.Empty;
        public string Ip { get; init; } = string.Empty;
        public bool Online { get; init; }
        public string Tailnet { get; init; } = string.Empty;
        public string[] Tags { get; init; } = [];
    }

    private sealed class EnrollmentReceipt
    {
        public int SchemaVersion { get; set; } = 3;
        public Guid InviteId { get; set; }
        public Guid DeviceId { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string AgentTokenHash { get; set; } = string.Empty;
        public string AgentVersion { get; set; } = string.Empty;
        public long AgentSize { get; set; }
        public string AgentSha256 { get; set; } = string.Empty;
        public Guid AgentInstallOperationId { get; set; }
    }

    private sealed class GuardianInstallTransactionJournal
    {
        public int SchemaVersion { get; set; } = 2;
        public Guid OperationId { get; set; }
        public List<GuardianInstallFileRecord> Files { get; set; } = [];
    }

    private sealed class GuardianInstallFileRecord
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class VerifiedInstallerLease : IAsyncDisposable
    {
        private bool _disposed;

        public VerifiedInstallerLease(string path, DependencyArtifact artifact, FileStream stream)
        {
            Path = path;
            Artifact = artifact;
            Stream = stream;
        }

        public string Path { get; }
        public DependencyArtifact Artifact { get; }
        public FileStream Stream { get; }
        public string? SignerThumbprint { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Stream.DisposeAsync();
            DeleteStagedPartial(Path, System.IO.Path.GetDirectoryName(Path)
                                      ?? throw new InvalidOperationException(
                                          "The held installer has no protected parent directory."));
        }
    }

    private sealed record ComponentInstallation(string Path, bool InstalledByOpticon);
    private sealed record TailscaleInstallation(bool InstalledByOpticon, InstallerEnsureResult Result);
    private sealed record VerifiedPayload(
        string AgentDirectory,
        string GuardianDirectory,
        InstallerEnsureResult Result);
}
