using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Taildesk.Shared;

namespace Taildesk.Agent;

public sealed record SshAccessStatus
{
    public bool Configured { get; init; }
    public bool Listening { get; init; }
    public int ActiveLeaseCount { get; init; }
    public DateTimeOffset? NextExpiry { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Manages short-lived Opticon SSH leases. A stable SYSTEM supervisor from
/// Taildesk.UpdateGuardian owns sshd and every authenticated child in a kill-on-close
/// Job Object, so expiry, reboot recovery, and revocation do not depend on the Agent
/// process or the Agent directory currently being upgraded. The stock Windows sshd
/// service and its configuration are never used or modified.
///
/// Register this type both as a singleton and as an IHostedService. Agent API
/// endpoints must pass HttpContext.Connection.RemoteIpAddress to ProvisionAsync
/// and RevokeAsync; this class independently verifies that it is the configured
/// primary coordinator address.
/// </summary>
public sealed class SshAccessManager : IHostedService, IAsyncDisposable
{
    public const int DedicatedPort = RemoteAdministrationProtocol.SshPort;
    public const string AccountName = RemoteAdministrationProtocol.SshAccountName;
    public const string SupervisorTaskName = RemoteAdministrationProtocol.SshSupervisorTaskName;
    public const string FirewallRuleName = "Opticon JIT SSH (primary only)";
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumLifetime = RemoteAdministrationProtocol.MaximumSshSession;

    private const int MaxConcurrentLeases = 8;
    private const int MaxRevocationTombstones = 256;
    private const string ManagedAccountComment = "Opticon just-in-time SSH administrator. Managed by Opticon.";
    private readonly IPAddress _bindAddress;
    private readonly IPAddress _coordinatorAddress;
    private readonly string _stateDirectory;
    private readonly string _statePath;
    private readonly string _sshdConfigPath;
    private readonly string _authorizedKeysPath;
    private readonly string _hostKeyPath;
    private readonly string _logPath;
    private readonly string _readyPath;
    private readonly string _failurePath;
    private readonly string _stateLockPath;
    private readonly string _schtasksPath;
    private readonly string _netshPath;
    private readonly string _icaclsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _expiryLoop;
    private int _started;
    private int _disposed;
    private string? _lastError;

    public SshAccessManager(AgentConfig config)
        : this(
            config.BindAddress,
            CoordinatorHost(config.CoordinatorUrl),
            Path.Combine(AppPaths.AgentDataDirectory, "SshAccess"))
    {
    }

    public SshAccessManager(
        string targetTailscaleAddress,
        string coordinatorTailscaleAddress,
        string? stateDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows OpenSSH is required.");
        _bindAddress = ParseTailscaleAddress(targetTailscaleAddress, "target");
        _coordinatorAddress = ParseTailscaleAddress(coordinatorTailscaleAddress, "coordinator");
        _stateDirectory = Path.GetFullPath(stateDirectory
                                           ?? Path.Combine(AppPaths.AgentDataDirectory, "SshAccess"));
        _statePath = Path.Combine(_stateDirectory, "leases.json");
        _sshdConfigPath = Path.Combine(_stateDirectory, "sshd_config");
        _authorizedKeysPath = Path.Combine(_stateDirectory, "authorized_keys");
        _hostKeyPath = Path.Combine(_stateDirectory, "ssh_host_ed25519_key");
        _logPath = Path.Combine(_stateDirectory, "sshd.log");
        _readyPath = Path.Combine(_stateDirectory, "supervisor.ready");
        _failurePath = Path.Combine(_stateDirectory, "supervisor.failure");
        _stateLockPath = Path.Combine(_stateDirectory, "state.lock");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new InvalidOperationException("The Windows directory is unavailable.");
        var system32 = Path.GetFullPath(Path.Combine(windows, "System32"));
        _schtasksPath = Path.Combine(system32, "schtasks.exe");
        _netshPath = Path.Combine(system32, "netsh.exe");
        _icaclsPath = Path.Combine(system32, "icacls.exe");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            await RestoreAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // SSH is an optional recovery channel. Fail closed and preserve the
            // primary Agent API if local OpenSSH has drifted or is unavailable.
            _lastError = exception.Message;
            await FailClosedAsync(CancellationToken.None);
        }
        _expiryLoop = RunExpiryLoopAsync(_lifetime.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        if (_expiryLoop is null) return;
        try { await _expiryLoop.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

        // Do not revoke active leases or stop the supervisor here. It is a
        // separate, boot-persistent SYSTEM process outside the Agent update slot and
        // remains authoritative while the Agent is replaced or rolled back.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_expiryLoop is not null)
        {
            try { await _expiryLoop; }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _gate.Dispose();
    }

    public Task<SshAccessResponse> ProvisionAsync(
        string callerAddress,
        string ephemeralPublicKey,
        TimeSpan requestedLifetime,
        CancellationToken cancellationToken = default) =>
        ProvisionAsync(ParseCaller(callerAddress), ephemeralPublicKey, requestedLifetime, cancellationToken);

    public async Task<SshAccessResponse> ProvisionAsync(
        IPAddress callerAddress,
        string ephemeralPublicKey,
        TimeSpan requestedLifetime,
        CancellationToken cancellationToken = default)
    {
        EnsurePrimaryCaller(callerAddress);
        if (requestedLifetime < MinimumLifetime || requestedLifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(
                nameof(requestedLifetime),
                $"SSH access must be requested for at least {MinimumLifetime.TotalMinutes:0} minutes and at most {MaximumLifetime.TotalHours:0} hours.");
        var normalizedKey = NormalizeClientPublicKey(ephemeralPublicKey);

        await _gate.WaitAsync(cancellationToken);
        SshLease? addedLease = null;
        var hadActiveLeases = false;
        try
        {
            await EnsureStateDirectoryAsync(cancellationToken);
            await using (await AcquireStateLockAsync(cancellationToken))
            {
                var initial = await LoadStateAsync(cancellationToken);
                NormalizeAndValidateState(initial);
                var changed = RemoveExpiredAndRevoked(initial, DateTimeOffset.UtcNow);
                hadActiveLeases = initial.Leases.Count != 0;
                if (changed) await PersistDesiredStateThenKeysAsync(initial, cancellationToken);
                if (initial.Leases.Count >= MaxConcurrentLeases)
                    throw new InvalidOperationException("The target already has the maximum number of active SSH leases.");
            }

            // Never hold state.lock during host-key/config/task/firewall work. The
            // independent supervisor must always be able to enforce expiry on time.
            await EnsureInfrastructureAsync(keepAccountEnabled: hadActiveLeases, cancellationToken);

            long generation;
            await using (await AcquireStateLockAsync(cancellationToken))
            {
                var state = await LoadStateAsync(cancellationToken);
                NormalizeAndValidateState(state);
                if (RemoveExpiredAndRevoked(state, DateTimeOffset.UtcNow))
                    await PersistDesiredStateThenKeysAsync(state, cancellationToken);
                if (state.Leases.Count >= MaxConcurrentLeases)
                    throw new InvalidOperationException("The target already has the maximum number of active SSH leases.");

                var leaseStart = DateTimeOffset.UtcNow;
                addedLease = new SshLease
                {
                    SessionId = SecurityHelpers.CreateToken(18),
                    PublicKey = normalizedKey,
                    CreatedAt = leaseStart,
                    ExpiresAt = leaseStart.Add(requestedLifetime)
                };
                state.Leases.Add(addedLease);
                AdvanceStateGeneration(state, terminateAuthenticatedSessions: false);
                await PersistDesiredStateThenKeysAsync(state, cancellationToken);
                EnsureManagedAccount(enabled: true);
                generation = state.Generation;
            }

            try { if (File.Exists(_failurePath)) File.Delete(_failurePath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Opticon could not clear the prior SSH supervisor diagnostic.", exception);
            }
            await StartSupervisorTaskAsync(cancellationToken);
            await WaitForSupervisorReadyAsync(generation, cancellationToken);
            _lastError = null;

            return new SshAccessResponse
            {
                SessionId = addedLease.SessionId,
                Host = _bindAddress.ToString(),
                Port = DedicatedPort,
                UserName = AccountName,
                HostPublicKey = await ReadHostPublicKeyAsync(cancellationToken),
                CreatedAt = addedLease.CreatedAt,
                ExpiresAt = addedLease.ExpiresAt,
                SystemRoot = GetAutomationSystemRoot()
            };
        }
        catch
        {
            var rollbackSucceeded = false;
            var deactivate = false;
            if (addedLease is not null)
            {
                try
                {
                    await using (await AcquireStateLockAsync(CancellationToken.None))
                    {
                        var rollback = await LoadStateAsync(CancellationToken.None);
                        NormalizeAndValidateState(rollback);
                        var removed = rollback.Leases.RemoveAll(lease => lease.SessionId == addedLease.SessionId) != 0;
                        AddRevocationTombstone(rollback, addedLease.SessionId, addedLease.ExpiresAt);
                        if (removed) AdvanceStateGeneration(rollback, terminateAuthenticatedSessions: true);
                        await PersistDesiredStateThenKeysAsync(rollback, CancellationToken.None);
                        deactivate = rollback.Leases.Count == 0;
                        rollbackSucceeded = true;
                    }
                    if (deactivate) await DeactivateIdleAsync(CancellationToken.None);
                }
                catch
                {
                    rollbackSucceeded = false;
                }
            }

            if (!rollbackSucceeded && (!hadActiveLeases || addedLease is not null))
                await FailClosedUnderGateAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
    public Task RevokeAsync(
        string callerAddress,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        RevokeAsync(ParseCaller(callerAddress), sessionId, cancellationToken);

    public async Task RevokeAsync(
        IPAddress callerAddress,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsurePrimaryCaller(callerAddress);
        ValidateSessionId(sessionId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStateDirectoryAsync(cancellationToken);
            var deactivate = false;
            try
            {
                await using (await AcquireStateLockAsync(cancellationToken))
                {
                    var state = await LoadStateAsync(cancellationToken);
                    NormalizeAndValidateState(state);
                    var matching = state.Leases.FirstOrDefault(lease => lease.SessionId == sessionId);
                    var tombstoneExpiry = matching?.ExpiresAt ?? DateTimeOffset.UtcNow.Add(MaximumLifetime);
                    var changed = state.Leases.RemoveAll(lease =>
                        lease.SessionId == sessionId || lease.ExpiresAt <= DateTimeOffset.UtcNow) != 0;
                    AddRevocationTombstone(state, sessionId, tombstoneExpiry);
                    RemoveExpiredTombstones(state, DateTimeOffset.UtcNow);
                    if (changed) AdvanceStateGeneration(state, terminateAuthenticatedSessions: true);

                    // A successful durable removal can never be resurrected by a
                    // restart. Any subsequent key-publication error contains access.
                    await PersistDesiredStateThenKeysAsync(state, cancellationToken);
                    deactivate = state.Leases.Count == 0;
                }
            }
            catch
            {
                await FailClosedUnderGateAsync();
                throw;
            }

            if (deactivate) await DeactivateIdleAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStateDirectoryAsync(cancellationToken);
            var deactivate = false;
            try
            {
                await using (await AcquireStateLockAsync(cancellationToken))
                {
                    var state = await LoadStateAsync(cancellationToken);
                    NormalizeAndValidateState(state);
                    if (!RemoveExpiredAndRevoked(state, DateTimeOffset.UtcNow)) return;
                    await PersistDesiredStateThenKeysAsync(state, cancellationToken);
                    deactivate = state.Leases.Count == 0;
                }
            }
            catch
            {
                await FailClosedUnderGateAsync();
                throw;
            }

            if (deactivate) await DeactivateIdleAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
    public async Task<SshAccessStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStateDirectoryAsync(cancellationToken);
            await using (await AcquireStateLockAsync(cancellationToken))
            {
                var state = await LoadStateAsync(cancellationToken);
                NormalizeAndValidateState(state);
                var active = state.Leases.Where(lease =>
                    lease.ExpiresAt > DateTimeOffset.UtcNow && !IsRevoked(state, lease.SessionId)).ToArray();
                return new SshAccessStatus
                {
                    Configured = File.Exists(_sshdConfigPath) && File.Exists(_hostKeyPath),
                    Listening = IsExactListenerActive(),
                    ActiveLeaseCount = active.Length,
                    NextExpiry = active.Length == 0 ? null : active.Min(lease => lease.ExpiresAt),
                    LastError = _lastError
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }
    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStateDirectoryAsync(cancellationToken);
            var hasActive = false;
            await using (await AcquireStateLockAsync(cancellationToken))
            {
                var initial = await LoadStateAsync(cancellationToken);
                NormalizeAndValidateState(initial);
                if (RemoveExpiredAndRevoked(initial, DateTimeOffset.UtcNow))
                    await PersistDesiredStateThenKeysAsync(initial, cancellationToken);
                else
                    await WriteAuthorizedKeysAsync(initial, cancellationToken);
                hasActive = initial.Leases.Count != 0;
            }

            if (!hasActive)
            {
                await DeactivateIdleAsync(cancellationToken);
                return;
            }

            await EnsureInfrastructureAsync(keepAccountEnabled: IsExactListenerActive(), cancellationToken);

            long generation;
            await using (await AcquireStateLockAsync(cancellationToken))
            {
                var state = await LoadStateAsync(cancellationToken);
                NormalizeAndValidateState(state);
                if (RemoveExpiredAndRevoked(state, DateTimeOffset.UtcNow))
                    await PersistDesiredStateThenKeysAsync(state, cancellationToken);
                else
                    await WriteAuthorizedKeysAsync(state, cancellationToken);
                if (state.Leases.Count == 0)
                {
                    hasActive = false;
                    generation = state.Generation;
                }
                else
                {
                    EnsureManagedAccount(enabled: true);
                    hasActive = true;
                    generation = state.Generation;
                }
            }

            if (!hasActive)
            {
                await DeactivateIdleAsync(cancellationToken);
                return;
            }
            await StartSupervisorTaskAsync(cancellationToken);
            await WaitForSupervisorReadyAsync(generation, cancellationToken);
            _lastError = null;
        }
        finally
        {
            _gate.Release();
        }
    }
    private async Task RunExpiryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await CleanupExpiredAsync(cancellationToken);
                    _lastError = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _lastError = exception.Message;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task EnsureInfrastructureAsync(
        bool keepAccountEnabled,
        CancellationToken cancellationToken)
    {
        var sshd = RequireSystemOpenSshExecutable("sshd.exe", "OpenSSH Server");
        var sshKeygen = RequireSystemOpenSshExecutable("ssh-keygen.exe", "OpenSSH Server");
        var guardian = Path.Combine(AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe");
        if (!File.Exists(guardian))
            throw new FileNotFoundException(
                "The stable Opticon Update Guardian is missing. Run Opticon Setup repair before enabling SSH.", guardian);

        await EnsureStateDirectoryAsync(cancellationToken);
        EnsureManagedAccount(enabled: keepAccountEnabled);
        try
        {
            if (!File.Exists(_hostKeyPath))
            {
                var generated = await ProcessRunner.RunAsync(
                    sshKeygen,
                    ["-q", "-t", "ed25519", "-N", string.Empty, "-C", "opticon-host", "-f", _hostKeyPath],
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                EnsureSuccess(generated, "OpenSSH could not create Opticon's host key");
            }
            else if (!File.Exists(_hostKeyPath + ".pub"))
            {
                var reconstructed = await ProcessRunner.RunAsync(
                    sshKeygen,
                    ["-y", "-f", _hostKeyPath],
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                EnsureSuccess(reconstructed, "OpenSSH could not reconstruct Opticon's host public key");
                await WriteAtomicTextAsync(
                    _hostKeyPath + ".pub",
                    reconstructed.StandardOutput.Trim() + Environment.NewLine,
                    cancellationToken);
            }
            await RestrictDaemonReadablePathAsync(_hostKeyPath, cancellationToken);
            await RestrictDaemonReadablePathAsync(_hostKeyPath + ".pub", cancellationToken);

            var expectedConfig = BuildSshdConfig();
            var configChanged = !File.Exists(_sshdConfigPath)
                                || !string.Equals(
                                    await File.ReadAllTextAsync(_sshdConfigPath, cancellationToken),
                                    expectedConfig,
                                    StringComparison.Ordinal);
            if (configChanged && !IsExactListenerActive())
                await WriteAtomicTextAsync(_sshdConfigPath, expectedConfig, cancellationToken);
            else if (configChanged)
                configChanged = false; // Defer config replacement until active recovery sessions end.
            await RestrictDaemonReadablePathAsync(_sshdConfigPath, cancellationToken);

            if (!File.Exists(_authorizedKeysPath))
                await WriteAtomicTextAsync(_authorizedKeysPath, string.Empty, cancellationToken);
            await RestrictAuthorizedKeysPathAsync(_authorizedKeysPath, cancellationToken);

            foreach (var validationArguments in new[]
                     {
                         new[] { "-t", "-f", _sshdConfigPath },
                         new[] { "-T", "-f", _sshdConfigPath }
                     })
            {
                var validation = await ProcessRunner.RunAsync(
                    sshd,
                    validationArguments,
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                EnsureSuccess(validation, "Opticon's isolated OpenSSH configuration is invalid");
            }

            await EnsureSupervisorTaskAsync(guardian, allowReplacement: !IsExactListenerActive(), cancellationToken);
            await ConfigureFirewallAsync(sshd, cancellationToken);
        }
        catch
        {
            if (!keepAccountEnabled)
            {
                try { DisableManagedAccount(); } catch { }
            }
            throw;
        }
    }

    private async Task EnsureSupervisorTaskAsync(
        string guardian,
        bool allowReplacement,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_schtasksPath, "Task Scheduler command-line tool");
        var query = await ProcessRunner.RunAsync(
            _schtasksPath, ["/Query", "/TN", SupervisorTaskName], TimeSpan.FromSeconds(15), cancellationToken);
        if (query.Succeeded && !allowReplacement) return;

        var script = string.Join("; ",
        [
            "$ErrorActionPreference='Stop'",
            $"$action=New-ScheduledTaskAction -Execute {PowerShellLiteral(guardian)} -Argument '--ssh-supervisor'",
            "$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest",
            "$trigger=New-ScheduledTaskTrigger -AtStartup",
            "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)",
            $"Register-ScheduledTask -TaskName {PowerShellLiteral(SupervisorTaskName)} -Action $action -Principal $principal -Trigger $trigger -Settings $settings -Force | Out-Null"
        ]);
        var configured = await RunWindowsPowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(configured, "Opticon could not configure its boot-persistent SYSTEM SSH supervisor");
    }

    private async Task ConfigureFirewallAsync(string sshd, CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_netshPath, "Windows firewall tool");
        await DeleteFirewallRuleAsync(cancellationToken);
        var added = await ProcessRunner.RunAsync(
            _netshPath,
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={FirewallRuleName}",
                "dir=in",
                "action=allow",
                "protocol=TCP",
                $"localport={DedicatedPort}",
                $"localip={_bindAddress}",
                $"remoteip={_coordinatorAddress}",
                $"program={sshd}",
                "profile=any",
                "enable=yes"
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        EnsureSuccess(added, "Opticon could not create its primary-only SSH firewall rule");
    }

    private async Task StartSupervisorTaskAsync(CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_schtasksPath, "Task Scheduler command-line tool");
        var started = await ProcessRunner.RunAsync(
            _schtasksPath, ["/Run", "/TN", SupervisorTaskName], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSuccess(started, "Opticon's SSH supervisor task did not start");
    }

    private async Task StopSupervisorTaskAsync(CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_schtasksPath, "Task Scheduler command-line tool");
        var stopped = await ProcessRunner.RunAsync(
            _schtasksPath, ["/End", "/TN", SupervisorTaskName], TimeSpan.FromSeconds(30), cancellationToken);
        if (!stopped.Succeeded && IsExactListenerActive())
            EnsureSuccess(stopped, "Opticon's SSH supervisor task could not be stopped");
        await WaitForListenerAsync(expectedListening: false, cancellationToken);
        try { File.Delete(_readyPath); } catch { }
    }

    private async Task DeactivateIdleAsync(CancellationToken cancellationToken)
    {
        Exception? firstError = null;
        try { await StopSupervisorTaskAsync(cancellationToken); } catch (Exception exception) { firstError ??= exception; }
        try { await DeleteFirewallRuleAsync(cancellationToken); } catch (Exception exception) { firstError ??= exception; }
        try { DisableManagedAccount(); } catch (Exception exception) { firstError ??= exception; }
        try { File.Delete(_readyPath); } catch (Exception exception) { firstError ??= exception; }
        if (firstError is not null)
            throw new InvalidOperationException("Opticon could not fully contain idle SSH access.", firstError);
    }

    private async Task DeleteFirewallRuleAsync(CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_netshPath, "Windows firewall tool");
        _ = await ProcessRunner.RunAsync(
            _netshPath,
            ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
    }

    private async Task FailClosedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try { await FailClosedUnderGateAsync(); }
            finally { _gate.Release(); }
        }
        catch
        {
            // Startup containment is best effort per step; the Agent API remains up.
        }
    }

    private async Task FailClosedUnderGateAsync()
    {
        // Containment actions are intentionally independent. Corrupt or unwritable
        // key/state files must never prevent process, firewall, or account shutdown.
        try { await StopSupervisorTaskAsync(CancellationToken.None); } catch { }
        try { await DeleteFirewallRuleAsync(CancellationToken.None); } catch { }
        try { DisableManagedAccount(); } catch { }
        try { File.Delete(_readyPath); } catch { }
        try
        {
            await EnsureStateDirectoryAsync(CancellationToken.None);
            await using (await AcquireStateLockAsync(CancellationToken.None, TimeSpan.FromSeconds(5)))
            {
                var empty = NewState();
                try { await SaveStateAsync(empty, CancellationToken.None); } catch { }
                try { await WriteAuthorizedKeysAsync(empty, CancellationToken.None); } catch { }
            }
        }
        catch { }
    }
    private async Task WaitForListenerAsync(bool expectedListening, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExactListenerActive() == expectedListening) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException(expectedListening
            ? $"Opticon SSH did not listen on {_bindAddress}:{DedicatedPort}."
            : "Opticon SSH task did not stop in time.");
    }

    private bool IsExactListenerActive() => IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners()
        .Any(endpoint => endpoint.Port == DedicatedPort
                         && endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         && endpoint.Address.Equals(_bindAddress));

    private async Task EnsureStateDirectoryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        await RestrictStateDirectoryAsync(cancellationToken);
    }

    private async Task<FileStream> AcquireStateLockAsync(
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _stateLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task<SshLeaseState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath)) return NewState();
        await using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return await JsonSerializer.DeserializeAsync<SshLeaseState>(stream, JsonDefaults.Options, cancellationToken)
                   ?? throw new InvalidDataException("Opticon SSH lease state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Opticon SSH lease state is corrupt; access remains disabled.", exception);
        }
    }

    private SshLeaseState NewState() => new()
    {
        TargetAddress = _bindAddress.ToString(),
        CoordinatorAddress = _coordinatorAddress.ToString()
    };

    private void NormalizeAndValidateState(SshLeaseState state)
    {
        if (state.SchemaVersion != 2)
            throw new InvalidDataException("Opticon SSH lease state has an unsupported schema.");
        if (!string.Equals(state.TargetAddress, _bindAddress.ToString(), StringComparison.Ordinal)
            || !string.Equals(state.CoordinatorAddress, _coordinatorAddress.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("Opticon SSH lease state does not match this target and coordinator.");
        if (state.Generation < 0 || state.SessionTerminationGeneration < 0
            || state.SessionTerminationGeneration > state.Generation
            || state.Leases is null || state.Revocations is null
            || state.Leases.Count > MaxConcurrentLeases
            || state.Revocations.Count > MaxRevocationTombstones)
            throw new InvalidDataException("Opticon SSH lease state is invalid or exceeds its bounded limits.");
        if (state.Leases.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != state.Leases.Count
            || state.Revocations.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != state.Revocations.Count)
            throw new InvalidDataException("Opticon SSH lease state contains duplicate identifiers.");
        foreach (var lease in state.Leases)
        {
            ValidateSessionId(lease.SessionId);
            _ = NormalizeClientPublicKey(lease.PublicKey);
            if (lease.CreatedAt == default || lease.ExpiresAt <= lease.CreatedAt
                || lease.ExpiresAt - lease.CreatedAt > MaximumLifetime.Add(TimeSpan.FromMinutes(1)))
                throw new InvalidDataException("Opticon SSH lease timing is invalid.");
        }
        foreach (var tombstone in state.Revocations)
        {
            ValidateSessionId(tombstone.SessionId);
            if (tombstone.ExpiresAt == default)
                throw new InvalidDataException("Opticon SSH revocation state is invalid.");
        }
    }

    private static bool IsRevoked(SshLeaseState state, string sessionId) =>
        state.Revocations.Any(item => item.SessionId == sessionId && item.ExpiresAt > DateTimeOffset.UtcNow);

    private static void AddRevocationTombstone(
        SshLeaseState state,
        string sessionId,
        DateTimeOffset expiresAt)
    {
        var existing = state.Revocations.FirstOrDefault(item => item.SessionId == sessionId);
        if (existing is null)
        {
            if (state.Revocations.Count >= MaxRevocationTombstones)
                throw new InvalidOperationException("The bounded SSH revocation journal is full; wait for prior leases to expire.");
            state.Revocations.Add(new SshRevocation { SessionId = sessionId, ExpiresAt = expiresAt });
        }
        else if (expiresAt > existing.ExpiresAt)
            existing.ExpiresAt = expiresAt;
    }

    private static void RemoveExpiredTombstones(SshLeaseState state, DateTimeOffset now) =>
        state.Revocations.RemoveAll(item => item.ExpiresAt <= now);

    private static void AdvanceStateGeneration(
        SshLeaseState state,
        bool terminateAuthenticatedSessions)
    {
        checked
        {
            state.Generation++;
            if (terminateAuthenticatedSessions) state.SessionTerminationGeneration++;
        }
    }

    private static bool RemoveExpiredAndRevoked(SshLeaseState state, DateTimeOffset now)
    {
        var beforeLeases = state.Leases.Count;
        var tombstones = state.Revocations
            .Where(item => item.ExpiresAt > now)
            .Select(item => item.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        state.Leases.RemoveAll(lease => lease.ExpiresAt <= now || tombstones.Contains(lease.SessionId));
        var beforeTombstones = state.Revocations.Count;
        RemoveExpiredTombstones(state, now);
        var terminated = state.Leases.Count != beforeLeases;
        var changed = terminated || state.Revocations.Count != beforeTombstones;
        if (changed)
            AdvanceStateGeneration(state, terminateAuthenticatedSessions: terminated);
        return changed;
    }

    private async Task PersistDesiredStateThenKeysAsync(
        SshLeaseState state,
        CancellationToken cancellationToken)
    {
        await SaveStateAsync(state, cancellationToken);
        await WriteAuthorizedKeysAsync(state, cancellationToken);
    }

    private async Task SaveStateAsync(SshLeaseState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        var temporary = _statePath + ".new";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonDefaults.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(_statePath))
            File.Replace(temporary, _statePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temporary, _statePath);
        await RestrictPathAsync(_statePath, directory: false, cancellationToken);
    }

    private async Task WriteAuthorizedKeysAsync(SshLeaseState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        var now = DateTimeOffset.UtcNow;
        var revoked = state.Revocations
            .Where(item => item.ExpiresAt > now)
            .Select(item => item.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        var active = state.Leases
            .Where(lease => lease.ExpiresAt > now && !revoked.Contains(lease.SessionId))
            .OrderBy(lease => lease.ExpiresAt)
            .ToArray();
        var lines = active.Select(lease =>
            $"expiry-time=\"{lease.ExpiresAt.UtcDateTime:yyyyMMddHHmmss}Z\",from=\"{_coordinatorAddress}\",no-agent-forwarding,no-port-forwarding,no-X11-forwarding,no-user-rc {lease.PublicKey} opticon-session:{lease.SessionId}");
        await WriteAtomicTextAsync(
            _authorizedKeysPath,
            string.Join(Environment.NewLine, lines) + (active.Length != 0 ? Environment.NewLine : string.Empty),
            cancellationToken);
        await RestrictAuthorizedKeysPathAsync(_authorizedKeysPath, cancellationToken);
    }
    private async Task<string> ReadHostPublicKeyAsync(CancellationToken cancellationToken)
    {
        var value = await File.ReadAllTextAsync(_hostKeyPath + ".pub", cancellationToken);
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "ssh-ed25519")
            throw new InvalidDataException("Opticon's SSH host public key is invalid.");
        return $"{parts[0]} {parts[1]}";
    }

    private string BuildSshdConfig()
    {
        static string P(string value) => value.Replace('\\', '/');
        return string.Join("\n",
        [
            $"Port {DedicatedPort}",
            "AddressFamily inet",
            $"ListenAddress {_bindAddress}",
            $"HostKey \"{P(_hostKeyPath)}\"",
            $"AuthorizedKeysFile \"{P(_authorizedKeysPath)}\"",
            "PubkeyAuthentication yes",
            "PasswordAuthentication no",
            "ChallengeResponseAuthentication no",
            "AuthenticationMethods publickey",
            "PermitEmptyPasswords no",
            $"AllowUsers {AccountName.ToLowerInvariant()}@{_coordinatorAddress}",
            "AllowAgentForwarding no",
            "AllowTcpForwarding no",
            "GatewayPorts no",
            "LoginGraceTime 30",
            "MaxAuthTries 3",
            "MaxSessions 4",
            "MaxStartups 3:30:6",
            "ClientAliveInterval 30",
            "ClientAliveCountMax 3",
            "LogLevel INFO",
            string.Empty
        ]);
    }

    private async Task WaitForSupervisorReadyAsync(long generation, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(_readyPath))
                {
                    var json = await File.ReadAllTextAsync(_readyPath, cancellationToken);
                    var ready = JsonSerializer.Deserialize<SupervisorReady>(json, JsonDefaults.Options);
                    if (ready is { ProcessId: > 0 }
                        && ready.Generation == generation
                        && string.Equals(ready.Address, _bindAddress.ToString(), StringComparison.Ordinal)
                        && IsExactListenerActive())
                        return;
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            var failure = await ReadSupervisorFailureAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(failure))
                throw new InvalidOperationException("The independent Opticon SSH supervisor could not start: " + failure);
            await Task.Delay(200, cancellationToken);
        }
        var detail = await ReadSupervisorFailureAsync(cancellationToken);
        throw new TimeoutException(string.IsNullOrWhiteSpace(detail)
            ? "The independent Opticon SSH supervisor did not attest its exact listener in time."
            : "The independent Opticon SSH supervisor could not start: " + detail);
    }

    private async Task<string?> ReadSupervisorFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_failurePath))
            {
                var detail = (await File.ReadAllTextAsync(_failurePath, cancellationToken)).Trim();
                if (detail.Length > 4096) detail = detail[..4096];
                return detail;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
    private async Task WriteAtomicTextAsync(string path, string value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        await File.WriteAllTextAsync(
            temporary,
            value,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    // Win32-OpenSSH reads this global absolute AuthorizedKeysFile before it
    // drops privileges. Administrator key files must be writable only by
    // LocalSystem and the built-in Administrators group.
    private async Task RestrictAuthorizedKeysPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var result = await ProcessRunner.RunAsync(
            _icaclsPath,
            [
                path,
                "/inheritance:r",
                "/grant:r", "*S-1-5-18:F",
                "/grant:r", "*S-1-5-32-544:F"
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        EnsureSuccess(result, $"Opticon could not protect {Path.GetFileName(path)} for SYSTEM and Administrators");
    }

    // The isolated daemon runs with an elevated token whose Administrators SID
    // is enabled. Windows OpenSSH rejects a host private key when the daemon's
    // named user has a direct ACE, even if that ACE is read-only, so runtime
    // inputs use only the well-known SYSTEM and Administrators principals.
    private async Task RestrictDaemonReadablePathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var existing = ReadLocalUser();
        if (existing is not null && string.Equals(existing.Value.Comment, ManagedAccountComment, StringComparison.Ordinal))
        {
            var accountSid = ((SecurityIdentifier)new NTAccount(Environment.MachineName, AccountName)
                .Translate(typeof(SecurityIdentifier))).Value;
            var removed = await ProcessRunner.RunAsync(
                _icaclsPath,
                [path, "/remove:g", $"*{accountSid}"],
                TimeSpan.FromSeconds(20),
                cancellationToken);
            EnsureSuccess(removed, $"Opticon could not remove the named daemon ACE from {Path.GetFileName(path)}");
        }

        var restricted = await ProcessRunner.RunAsync(
            _icaclsPath,
            [
                path,
                "/inheritance:r",
                "/grant:r", "*S-1-5-18:F",
                "/grant:r", "*S-1-5-32-544:F"
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        EnsureSuccess(restricted, $"Opticon could not protect {Path.GetFileName(path)} for SYSTEM and Administrators");
    }

    private async Task RestrictStateDirectoryAsync(CancellationToken cancellationToken)
    {
        var existing = ReadLocalUser();
        if (existing is null || !string.Equals(existing.Value.Comment, ManagedAccountComment, StringComparison.Ordinal))
        {
            await RestrictPathAsync(_stateDirectory, directory: true, cancellationToken);
            return;
        }

        var accountSid = ((SecurityIdentifier)new NTAccount(Environment.MachineName, AccountName)
            .Translate(typeof(SecurityIdentifier))).Value;
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var result = await ProcessRunner.RunAsync(
            _icaclsPath,
            [
                _stateDirectory,
                "/inheritance:r",
                "/grant:r", "*S-1-5-18:(OI)(CI)F",
                // Traverse/read only; no inheritance reaches lease state or keys.
                "/grant:r", $"*{accountSid}:RX"
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        EnsureSuccess(result, "Opticon could not protect the SSH state directory for SYSTEM and its exact daemon account");
    }

    private async Task RestrictPathAsync(
        string path,
        bool directory,
        CancellationToken cancellationToken)
    {
        var inheritance = directory ? "*S-1-5-18:(OI)(CI)F" : "*S-1-5-18:F";
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var result = await ProcessRunner.RunAsync(
            _icaclsPath,
            [path, "/inheritance:r", "/grant:r", inheritance],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        EnsureSuccess(result, $"Opticon could not restrict {Path.GetFileName(path)} to SYSTEM");
        var owner = await ProcessRunner.RunAsync(
            _icaclsPath,
            [path, "/setowner", "*S-1-5-18"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        EnsureSuccess(owner, $"Opticon could not set SYSTEM ownership on {Path.GetFileName(path)}");
    }

    private void EnsureManagedAccount(bool enabled)
    {
        var existing = ReadLocalUser();
        var password = SecurityHelpers.CreateHumanPassword(48);
        if (existing is null)
        {
            var add = new UserInfo1
            {
                Name = AccountName,
                Password = password,
                Privilege = UserPrivilegeUser,
                Comment = ManagedAccountComment,
                Flags = UserFlagScript | UserFlagPasswordCannotChange | UserFlagPasswordNeverExpires | UserFlagAccountDisabled
            };
            var result = NetUserAdd(null, 1, ref add, out _);
            if (result != Success) ThrowNetApi(result, "create the dedicated Opticon SSH account");
            existing = ReadLocalUser() ?? throw new InvalidOperationException("The Opticon SSH account was not created.");
        }
        else if (enabled)
        {
            if (!string.Equals(existing.Value.Comment, ManagedAccountComment, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"A local account named {AccountName} already exists but is not managed by Opticon.");
            var passwordInfo = new UserInfo1003 { Password = password };
            var passwordResult = NetUserSetInfo(null, AccountName, 1003, ref passwordInfo, out _);
            if (passwordResult != Success) ThrowNetApi(passwordResult, "rotate the unknown Opticon SSH account password");
        }
        else if (!string.Equals(existing.Value.Comment, ManagedAccountComment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A local account named {AccountName} already exists but is not managed by Opticon.");
        }

        var administrators = GetAdministratorsGroupName();
        var member = new LocalGroupMembersInfo3 { DomainAndName = $"{Environment.MachineName}\\{AccountName}" };
        if (enabled)
        {
            var groupResult = NetLocalGroupAddMembers(null, administrators, 3, ref member, 1);
            if (groupResult is not Success and not ErrorMemberInAlias)
                ThrowNetApi(groupResult, "add the Opticon SSH account to the local Administrators group");
        }

        var desiredFlags = existing.Value.Flags | UserFlagScript | UserFlagPasswordCannotChange | UserFlagPasswordNeverExpires;
        desiredFlags = enabled ? desiredFlags & ~UserFlagAccountDisabled : desiredFlags | UserFlagAccountDisabled;
        var flags = new UserInfo1008 { Flags = desiredFlags };
        var flagsResult = NetUserSetInfo(null, AccountName, 1008, ref flags, out _);
        if (flagsResult != Success) ThrowNetApi(flagsResult, "set the Opticon SSH account state");
    }

    private void DisableManagedAccount()
    {
        var existing = ReadLocalUser();
        if (existing is null || !string.Equals(existing.Value.Comment, ManagedAccountComment, StringComparison.Ordinal)) return;
        var flags = new UserInfo1008 { Flags = existing.Value.Flags | UserFlagAccountDisabled };
        var firstError = 0;
        var flagsResult = NetUserSetInfo(null, AccountName, 1008, ref flags, out _);
        if (flagsResult != Success) firstError = flagsResult;

        var password = new UserInfo1003 { Password = SecurityHelpers.CreateHumanPassword(48) };
        var passwordResult = NetUserSetInfo(null, AccountName, 1003, ref password, out _);
        if (passwordResult != Success && firstError == 0) firstError = passwordResult;

        var administrators = GetAdministratorsGroupName();
        var member = new LocalGroupMembersInfo3 { DomainAndName = $"{Environment.MachineName}\\{AccountName}" };
        var groupResult = NetLocalGroupDelMembers(null, administrators, 3, ref member, 1);
        if (groupResult is not Success and not ErrorMemberNotInAlias && firstError == 0)
            firstError = groupResult;
        if (firstError != 0) ThrowNetApi(firstError, "fully contain the idle Opticon SSH account");
    }

    private static string GetAdministratorsGroupName()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            .Translate(typeof(NTAccount)).Value;
        var slash = administrators.IndexOf('\\');
        return slash >= 0 ? administrators[(slash + 1)..] : administrators;
    }

    private NativeUser? ReadLocalUser()
    {
        var result = NetUserGetInfo(null, AccountName, 1, out var buffer);
        if (result == UserNotFound) return null;
        if (result != Success) ThrowNetApi(result, "read the Opticon SSH account");
        try
        {
            var info = Marshal.PtrToStructure<UserInfo1>(buffer);
            return new NativeUser(info.Comment ?? string.Empty, info.Flags);
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    private static string NormalizeClientPublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16 * 1024 || value.Contains('\r') || value.Contains('\n'))
            throw new InvalidDataException("The SSH public key must be one bounded line.");
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "ssh-ed25519")
            throw new InvalidDataException("Opticon accepts only ephemeral Ed25519 SSH public keys.");
        try
        {
            var decoded = Convert.FromBase64String(parts[1]);
            if (decoded.Length is < 32 or > 256) throw new FormatException();
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The SSH public key data is invalid.", exception);
        }
        return $"ssh-ed25519 {parts[1]}";
    }

    private void EnsurePrimaryCaller(IPAddress callerAddress)
    {
        if (callerAddress is null
            || callerAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !callerAddress.Equals(_coordinatorAddress))
            throw new UnauthorizedAccessException("SSH access is restricted to the primary Opticon coordinator address.");
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128
            || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Invalid SSH session identifier.", nameof(sessionId));
    }

    private static string RequireSystemOpenSshExecutable(string fileName, string capabilityName)
    {
        var system = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "OpenSSH",
            fileName));
        if (!File.Exists(system))
            throw new FileNotFoundException(
                $"Windows {capabilityName} is not installed. Run Opticon Setup repair locally while the normal Agent and RustDesk channels are healthy; SSH provisioning never installs Windows capabilities on request.",
                system);
        return system;
    }

    private static void RequireExactSystemExecutable(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException($"The exact System32 {description} is unavailable.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The exact System32 {description} is a reparse point.");
    }

    private static string GetAutomationSystemRoot()
    {
        var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(windows)
            || windows.Length > 240
            || windows.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '"' or '\''))
            throw new InvalidOperationException("The Windows directory cannot be represented safely for SSH automation.");
        foreach (var relative in new[]
                 {
                     Path.Combine("System32", "cmd.exe"),
                     Path.Combine("System32", "WindowsPowerShell", "v1.0", "powershell.exe")
                 })
        {
            if (!File.Exists(Path.Combine(windows, relative)))
                throw new FileNotFoundException("A required exact Windows SSH automation executable is missing.");
        }
        return windows;
    }

    private static Task<ProcessResult> RunWindowsPowerShellAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell was not found.", powershell);
        return ProcessRunner.RunAsync(
            powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            timeout,
            cancellationToken);
    }

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string CoordinatorHost(string coordinatorUrl)
    {
        if (!Uri.TryCreate(coordinatorUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The Agent coordinator URL is invalid.");
        return uri.Host;
    }

    private static IPAddress ParseCaller(string value) =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new UnauthorizedAccessException("The SSH caller address is invalid.");

    private static IPAddress ParseTailscaleAddress(string value, string description)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !IsTailscaleIpv4(address))
            throw new InvalidOperationException($"The {description} SSH address must be a Tailscale IPv4 address.");
        return address;
    }

    private static bool IsTailscaleIpv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.Succeeded) return;
        var detail = new[] { result.StandardError, result.StandardOutput }
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}");
    }

    private static void ThrowNetApi(int error, string action) =>
        throw new InvalidOperationException($"Windows could not {action} (NetAPI error {error}).");

    private sealed class SshLeaseState
    {
        public int SchemaVersion { get; set; } = 2;
        public long Generation { get; set; }
        public long SessionTerminationGeneration { get; set; }
        public string TargetAddress { get; set; } = string.Empty;
        public string CoordinatorAddress { get; set; } = string.Empty;
        public List<SshLease> Leases { get; set; } = [];
        public List<SshRevocation> Revocations { get; set; } = [];
    }

    private sealed class SshLease
    {
        public string SessionId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class SshRevocation
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class SupervisorReady
    {
        public long Generation { get; set; }
        public int ProcessId { get; set; }
        public string Address { get; set; } = string.Empty;
    }
    private readonly record struct NativeUser(string Comment, uint Flags);

    private const int Success = 0;
    private const int UserNotFound = 2221;
    private const int ErrorMemberInAlias = 1378;
    private const int ErrorMemberNotInAlias = 1377;
    private const uint UserPrivilegeUser = 1;
    private const uint UserFlagScript = 0x0001;
    private const uint UserFlagAccountDisabled = 0x0002;
    private const uint UserFlagPasswordCannotChange = 0x0040;
    private const uint UserFlagPasswordNeverExpires = 0x10000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
        public uint PasswordAge;
        public uint Privilege;
        [MarshalAs(UnmanagedType.LPWStr)] public string? HomeDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Password;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1008
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DomainAndName;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(
        string? serverName,
        int level,
        ref UserInfo1 buffer,
        out uint parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(
        string? serverName,
        string userName,
        int level,
        out IntPtr buffer);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(
        string? serverName,
        string userName,
        int level,
        ref UserInfo1003 buffer,
        out uint parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(
        string? serverName,
        string userName,
        int level,
        ref UserInfo1008 buffer,
        out uint parameterError);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(
        string? serverName,
        string groupName,
        int level,
        ref LocalGroupMembersInfo3 buffer,
        int totalEntries);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupDelMembers(
        string? serverName,
        string groupName,
        int level,
        ref LocalGroupMembersInfo3 buffer,
        int totalEntries);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
