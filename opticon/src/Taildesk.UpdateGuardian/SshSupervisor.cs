using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

/// <summary>
/// Runs the isolated Opticon SSH daemon outside the Agent update lifecycle.
/// Program.Main must dispatch <see cref="ModeArgument"/> before acquiring the
/// normal update-guardian mutex; this mode owns its own protected lock file.
/// No caller-supplied paths, ports, accounts, or addresses are accepted.
/// </summary>
internal sealed class SshSupervisor : IAsyncDisposable
{
    public const string ModeArgument = "--ssh-supervisor";

    private const string FirewallRuleName = "Opticon JIT SSH (primary only)";
    private const string ManagedAccountComment = "Opticon just-in-time SSH administrator. Managed by Opticon.";
    private const int StateSchemaVersion = 2;
    private const int MaximumLeases = 8;
    private const int MaximumRevocations = 256;
    private const int MaximumStateBytes = 256 * 1024;
    private const long MaximumArchivedLogBytes = 8L * 1024 * 1024;
    private const uint UserPrivilegeUser = 1;
    private const uint UserFlagScript = 0x0001;
    private const uint UserFlagAccountDisabled = 0x0002;
    private const uint UserFlagPasswordCannotChange = 0x0040;
    private const uint UserFlagPasswordNeverExpires = 0x10000;
    private const int Success = 0;
    private const int UserNotFound = 2221;
    private const int ErrorMemberInAlias = 1378;
    private const int ErrorMemberNotInAlias = 1377;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeyRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StateLockTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InfrastructureAuditInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DaemonTerminationTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions StrictJson = CreateStrictJsonOptions();

    private readonly string _stateDirectory;
    private readonly string _statePath;
    private readonly string _authorizedKeysPath;
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly string _readyPath;
    private readonly string _supervisorLockPath;
    private readonly string _stateLockPath;
    private readonly string _hostKeyPath;
    private readonly string _sshdPath;
    private readonly string _netshPath;
    private readonly string _icaclsPath;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private FileStream? _supervisorLock;
    private SafeKernelHandle? _daemonProcess;
    private SafeKernelHandle? _daemonJob;
    private SshDaemonUserContext? _daemonUserContext;
    private uint _daemonProcessId;
    private string _runningConfigHash = string.Empty;
    private long _configuredGeneration = -1;
    private long _observedTerminationGeneration = -1;
    private HashSet<string> _observedActiveSessionIds = new(StringComparer.Ordinal);
    private DateTimeOffset _nextInfrastructureAudit = DateTimeOffset.MinValue;
    private bool _accountWasEnabledByThisProcess;
    private bool _disposed;

    private SshSupervisor()
    {
        _stateDirectory = Path.GetFullPath(Path.Combine(AppPaths.AgentDataDirectory, "SshAccess"));
        _statePath = Path.Combine(_stateDirectory, "leases.json");
        _authorizedKeysPath = Path.Combine(_stateDirectory, "authorized_keys");
        _configPath = Path.Combine(_stateDirectory, "sshd_config");
        _logPath = Path.Combine(_stateDirectory, "sshd.log");
        _readyPath = Path.Combine(_stateDirectory, "supervisor.ready");
        _supervisorLockPath = Path.Combine(_stateDirectory, "supervisor.lock");
        _stateLockPath = Path.Combine(_stateDirectory, "state.lock");
        _hostKeyPath = Path.Combine(_stateDirectory, "ssh_host_ed25519_key");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new InvalidOperationException("The Windows directory is unavailable.");
        var system32 = Path.GetFullPath(Path.Combine(windows, "System32"));
        _sshdPath = Path.Combine(system32, "OpenSSH", "sshd.exe");
        _netshPath = Path.Combine(system32, "netsh.exe");
        _icaclsPath = Path.Combine(system32, "icacls.exe");
    }

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0].Equals(ModeArgument, StringComparison.Ordinal);

    public static async Task<int> RunFromArgumentsAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (!IsRequested(args))
        {
            Console.Error.WriteLine($"SSH supervisor mode accepts only the exact {ModeArgument} argument.");
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Opticon SSH supervisor can run only on Windows.");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            Console.Error.WriteLine("The Opticon SSH supervisor must run as LocalSystem.");
            return 2;
        }

        await using var supervisor = new SshSupervisor();
        try
        {
            if (!await supervisor.InitializeAndAcquireLockAsync(cancellationToken)) return 0;
            await supervisor.RunLoopAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Opticon SSH supervisor failed: " + exception);
            await supervisor.FailClosedAsync();
            return 1;
        }
    }

    private async Task<bool> InitializeAndAcquireLockAsync(CancellationToken cancellationToken)
    {
        ValidateFixedLayout();
        Directory.CreateDirectory(_stateDirectory);
        RejectReparsePoint(_stateDirectory, "SSH state directory");
        await RestrictSystemOnlyAsync(_stateDirectory, directory: true, cancellationToken);

        await EnsureProtectedLockFileAsync(
            _supervisorLockPath,
            "SSH supervisor singleton lock",
            cancellationToken);
        await EnsureProtectedLockFileAsync(
            _stateLockPath,
            "SSH lease-state transaction lock",
            cancellationToken);

        try
        {
            _supervisorLock = new FileStream(
                _supervisorLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            // A healthy supervisor already owns the fixed singleton lock.
            return false;
        }

        _supervisorLock.SetLength(0);
        var owner = Utf8NoBom.GetBytes($"{Environment.ProcessId}{Environment.NewLine}");
        await _supervisorLock.WriteAsync(owner, cancellationToken);
        await _supervisorLock.FlushAsync(cancellationToken);
        _supervisorLock.Flush(flushToDisk: true);
        return true;
    }

    private async Task EnsureProtectedLockFileAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            await using var seed = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await seed.FlushAsync(cancellationToken);
            seed.Flush(flushToDisk: true);
            created = true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another SYSTEM process created the fixed lock first.
        }

        RejectReparsePoint(path, description);
        if (created)
            await RestrictSystemOnlyAsync(path, directory: false, cancellationToken);
    }

    private async Task<FileStream> AcquireStateLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        RejectReparsePoint(_stateDirectory, "SSH state directory");

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(_stateLockPath, "SSH lease-state transaction lock");
            try
            {
                return new FileStream(
                    _stateLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 128,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(_stateLockPath))
            {
                if (Stopwatch.GetElapsedTime(started) >= StateLockTimeout)
                    throw new TimeoutException("Timed out acquiring the SSH lease-state transaction lock.");
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            RunControlLoopAsync(cancellationToken),
            RunKeyRefreshLoopAsync(cancellationToken));
    }

    private async Task RunControlLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _controlGate.WaitAsync(cancellationToken);
                TimeSpan delay;
                try
                {
                    delay = await ReconcileControlOnceAsync(cancellationToken);
                }
                finally
                {
                    _controlGate.Release();
                }
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} SSH reconciliation failed: {exception.Message}");
                await FailClosedAsync();
                await Task.Delay(FailureBackoff, cancellationToken);
            }
        }
    }

    // Called only while _controlGate is held. Keeping listener, firewall,
    // account, and readiness mutations in one serialized transaction prevents
    // the independent expiry loop from racing a daemon restart.
    private async Task<TimeSpan> ReconcileControlOnceAsync(CancellationToken cancellationToken)
    {
        SupervisorState? state;
        IReadOnlyList<SupervisorLease> active;
        await using (await AcquireStateLockAsync(cancellationToken))
        {
            state = await LoadStateAsync(cancellationToken);
            if (state is null)
            {
                active = Array.Empty<SupervisorLease>();
                await WriteAtomicTextAsync(_authorizedKeysPath, string.Empty, cancellationToken);
                await RestrictAuthorizedKeysAsync(_authorizedKeysPath, cancellationToken);
            }
            else
            {
                active = SelectActiveLeases(state, DateTimeOffset.UtcNow);
                await WriteAuthorizedKeysAsync(state, active, cancellationToken);
            }
        }

        if (state is null || active.Count == 0)
        {
            await FailClosedCoreAsync();
            return ReconcileInterval;
        }

        var config = BuildSshdConfig(state);
        var configHash = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(config)));
        var auditDue = state.Generation != _configuredGeneration
                       || DateTimeOffset.UtcNow >= _nextInfrastructureAudit;
        if (auditDue)
        {
            await EnsureInfrastructureAsync(state, config, cancellationToken);
            _configuredGeneration = state.Generation;
            _nextInfrastructureAudit = DateTimeOffset.UtcNow.Add(InfrastructureAuditInterval);
        }

        // Removing or expiring any lease must close shells that authenticated
        // before authorized_keys changed. Restarting the kill-on-close job is
        // the only fail-closed way to terminate those process trees.
        var activeSessionIds = active.Select(lease => lease.SessionId).ToHashSet(StringComparer.Ordinal);
        var authorizationSetShrank = _observedActiveSessionIds.Except(activeSessionIds).Any();
        var sessionTerminationRequired = _observedTerminationGeneration >= 0
                                         && state.SessionTerminationGeneration != _observedTerminationGeneration;
        var mustRestart = !_runningConfigHash.Equals(configHash, StringComparison.Ordinal)
                          || !IsDaemonProcessRunning()
                          || !HasExactListener(state.TargetAddress, _daemonProcessId)
                          || sessionTerminationRequired
                          || authorizationSetShrank;
        if (mustRestart)
        {
            await DeleteReadyAsync();
            await StopDaemonAsync();
            await StartDaemonAsync(state, configHash, cancellationToken);
        }

        if (!HasExactListener(state.TargetAddress, _daemonProcessId))
            throw new InvalidOperationException("The SSH daemon lost its exact Tailscale listener.");

        _observedTerminationGeneration = state.SessionTerminationGeneration;
        _observedActiveSessionIds = activeSessionIds;
        await WriteReadyAsync(state, cancellationToken);
        var untilExpiry = active.Min(lease => lease.ExpiresAt) - DateTimeOffset.UtcNow;
        return untilExpiry <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(100)
            : untilExpiry < ReconcileInterval ? untilExpiry : ReconcileInterval;
    }
    private async Task RunKeyRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            try
            {
                await using (await AcquireStateLockAsync(cancellationToken))
                {
                    var state = await LoadStateAsync(cancellationToken);
                    if (state is null)
                    {
                        await WriteAtomicTextAsync(_authorizedKeysPath, string.Empty, cancellationToken);
                        await RestrictAuthorizedKeysAsync(_authorizedKeysPath, cancellationToken);
                    }
                    else
                    {
                        var active = SelectActiveLeases(state, DateTimeOffset.UtcNow);
                        await WriteAuthorizedKeysAsync(state, active, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} SSH key refresh failed: {exception.Message}");
                await FailClosedAsync();
            }

            var remaining = KeyRefreshInterval - Stopwatch.GetElapsedTime(started);
            await Task.Delay(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }
    }

    private async Task<SupervisorState?> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath)) return null;
        RejectReparsePoint(_statePath, "SSH lease state");

        var info = new FileInfo(_statePath);
        if (info.Length is <= 0 or > MaximumStateBytes)
            throw new InvalidDataException("The SSH lease state has an invalid size.");

        await using var stream = new FileStream(
            _statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync<SupervisorState>(stream, StrictJson, cancellationToken)
                    ?? throw new InvalidDataException("The SSH lease state is empty.");
        ValidateState(state);
        return state;
    }

    private static void ValidateState(SupervisorState state)
    {
        if (state.SchemaVersion != StateSchemaVersion || state.Generation < 0
            || state.SessionTerminationGeneration < 0
            || state.SessionTerminationGeneration > state.Generation)
            throw new InvalidDataException("The SSH lease state schema or generation is invalid.");
        var target = ParseTailscaleIpv4(state.TargetAddress, "target");
        var coordinator = ParseTailscaleIpv4(state.CoordinatorAddress, "coordinator");
        if (target.Equals(coordinator))
            throw new InvalidDataException("The SSH target and coordinator addresses must differ.");
        if (state.Leases is null || state.Leases.Count > MaximumLeases)
            throw new InvalidDataException("The SSH lease state contains too many leases.");
        if (state.Revocations is null || state.Revocations.Count > MaximumRevocations)
            throw new InvalidDataException("The SSH lease state contains too many revocations.");

        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var publicKeys = new HashSet<string>(StringComparer.Ordinal);
        var leases = new Dictionary<string, SupervisorLease>(StringComparer.Ordinal);
        foreach (var lease in state.Leases)
        {
            ValidateSessionId(lease.SessionId);
            if (!sessions.Add(lease.SessionId))
                throw new InvalidDataException("The SSH lease state contains duplicate session identifiers.");
            lease.PublicKey = NormalizeEd25519PublicKey(lease.PublicKey);
            if (!publicKeys.Add(lease.PublicKey))
                throw new InvalidDataException("The SSH lease state reuses one public key across leases.");
            if (lease.CreatedAt == default || lease.ExpiresAt == default
                || lease.ExpiresAt <= lease.CreatedAt
                || lease.ExpiresAt - lease.CreatedAt > RemoteAdministrationProtocol.MaximumSshSession.Add(TimeSpan.FromMinutes(1))
                || lease.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new InvalidDataException("The SSH lease state contains an invalid lifetime.");
            leases.Add(lease.SessionId, lease);
        }

        var revokedSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var revocation in state.Revocations)
        {
            ValidateSessionId(revocation.SessionId);
            if (!revokedSessions.Add(revocation.SessionId) || revocation.ExpiresAt == default)
                throw new InvalidDataException("The SSH lease state contains an invalid revocation.");
            if (leases.TryGetValue(revocation.SessionId, out var matching)
                && revocation.ExpiresAt < matching.ExpiresAt)
                throw new InvalidDataException("An SSH revocation expires before its matching lease.");
        }
    }

    private static IReadOnlyList<SupervisorLease> SelectActiveLeases(
        SupervisorState state,
        DateTimeOffset now)
    {
        var revoked = state.Revocations
            .Select(item => item.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        return state.Leases
            .Where(lease => lease.ExpiresAt > now && !revoked.Contains(lease.SessionId))
            .OrderBy(lease => lease.ExpiresAt)
            .ToArray();
    }

    private async Task EnsureInfrastructureAsync(
        SupervisorState state,
        string config,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_sshdPath, "OpenSSH daemon");
        if (!File.Exists(_hostKeyPath) || !File.Exists(_hostKeyPath + ".pub"))
            throw new FileNotFoundException("The Agent has not provisioned the isolated SSH host key.", _hostKeyPath);
        RejectReparsePoint(_hostKeyPath, "SSH host private key");
        RejectReparsePoint(_hostKeyPath + ".pub", "SSH host public key");
        await RestrictSystemOnlyAsync(_hostKeyPath, directory: false, cancellationToken);
        await RestrictSystemOnlyAsync(_hostKeyPath + ".pub", directory: false, cancellationToken);

        await WriteAtomicTextIfChangedAsync(_configPath, config, cancellationToken);
        await RestrictSystemOnlyAsync(_configPath, directory: false, cancellationToken);
        await RestrictAuthorizedKeysAsync(_authorizedKeysPath, cancellationToken);
        await RestrictSystemOnlyAsync(_statePath, directory: false, cancellationToken);

        var validation = await WindowsCommand.RunAsync(
            _sshdPath,
            ["-t", "-f", _configPath],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!validation.Succeeded)
            throw new InvalidOperationException("The isolated Windows OpenSSH configuration is invalid: " + validation.ErrorDetail);

        // The exact account is enabled before its non-inheriting read ACLs are applied.
        EnsureManagedAccount(enabled: true);
        await GrantDaemonRuntimeAccessAsync(cancellationToken);
        await ConfigureFirewallAsync(state, cancellationToken);
    }

    private async Task ConfigureFirewallAsync(SupervisorState state, CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_netshPath, "Windows firewall tool");
        _ = await WindowsCommand.RunAsync(
            _netshPath,
            ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        var added = await WindowsCommand.RunAsync(
            _netshPath,
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={FirewallRuleName}",
                "dir=in",
                "action=allow",
                "protocol=TCP",
                $"localport={RemoteAdministrationProtocol.SshPort}",
                $"localip={state.TargetAddress}",
                $"remoteip={state.CoordinatorAddress}",
                $"program={_sshdPath}",
                "profile=any",
                "enable=yes"
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!added.Succeeded)
            throw new InvalidOperationException("Windows could not install the exact Opticon SSH firewall rule: " + added.ErrorDetail);
    }

    private async Task StartDaemonAsync(
        SupervisorState state,
        string configHash,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_sshdPath, "OpenSSH daemon");
        await RotateDaemonLogAsync(cancellationToken);
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the SSH containment job.");

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not configure the SSH containment job.");
        }

        var commandLine = string.Join(" ",
        [
            QuoteWindowsArgument(_sshdPath),
            "-D",
            "-f", QuoteWindowsArgument(_configPath),
            "-E", QuoteWindowsArgument(_logPath)
        ]);

        SshDaemonUserContext? userContext = null;
        SshDaemonUserContext.CreatedProcess processInfo;
        try
        {
            userContext = SshDaemonUserContext.Create();
            processInfo = userContext.CreateProcessSuspended(
                _sshdPath,
                commandLine,
                _stateDirectory,
                CreateSuspended | CreateNoWindow);
        }
        catch (Exception startError)
        {
            job.Dispose();
            if (userContext is not null)
            {
                try { userContext.Dispose(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "The SSH daemon could not start and its administrator profile cleanup also failed.",
                        startError,
                        cleanupError);
                }
            }
            throw;
        }

        using var thread = new SafeKernelHandle(processInfo.ThreadHandle);
        var process = new SafeKernelHandle(processInfo.ProcessHandle);
        try
        {
            if (!AssignProcessToJobObject(job, process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign SSH to its kill-on-close job.");
            if (ResumeThread(thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the isolated SSH daemon.");

            _daemonJob = job;
            _daemonProcess = process;
            _daemonProcessId = processInfo.ProcessId;
            _runningConfigHash = configHash;

            // Keep the profile and full user token alive for the complete job lifetime.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDaemonProcessRunning())
                    throw new InvalidOperationException("The isolated SSH daemon exited before opening its listener.");
                if (HasExactListener(state.TargetAddress, _daemonProcessId))
                {
                    _daemonUserContext = userContext;
                    userContext = null;
                    return;
                }
                await Task.Delay(200, cancellationToken);
            }
            throw new TimeoutException($"SSH did not listen exactly on {state.TargetAddress}:{RemoteAdministrationProtocol.SshPort}.");
        }
        catch (Exception startError)
        {
            var failures = new List<Exception> { startError };
            try { await TerminateDaemonProcessTreeAsync(job, process); }
            catch (Exception cleanupError) { failures.Add(cleanupError); }
            try { job.Dispose(); } catch (Exception cleanupError) { failures.Add(cleanupError); }
            try { process.Dispose(); } catch (Exception cleanupError) { failures.Add(cleanupError); }
            _daemonJob = null;
            _daemonProcess = null;
            _daemonProcessId = 0;
            _runningConfigHash = string.Empty;
            if (userContext is not null)
            {
                try { userContext.Dispose(); }
                catch (Exception cleanupError) { failures.Add(cleanupError); }
            }
            if (failures.Count > 1)
                throw new AggregateException("The SSH daemon could not start and cleanup was incomplete.", failures);
            throw;
        }
    }

    private bool IsDaemonProcessRunning()
    {
        if (_daemonProcess is null || _daemonProcess.IsInvalid || _daemonProcess.IsClosed) return false;
        var result = WaitForSingleObject(_daemonProcess, 0);
        return result == WaitTimeout;
    }

    private async Task StopDaemonAsync()
    {
        await DeleteReadyAsync();
        var process = _daemonProcess;
        var job = _daemonJob;
        var userContext = _daemonUserContext;
        _daemonProcess = null;
        _daemonJob = null;
        _daemonUserContext = null;
        _daemonProcessId = 0;
        _runningConfigHash = string.Empty;
        _observedTerminationGeneration = -1;
        _observedActiveSessionIds.Clear();

        var failures = new List<Exception>();
        try { await TerminateDaemonProcessTreeAsync(job, process); }
        catch (Exception exception) { failures.Add(exception); }
        try { job?.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        try { process?.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        if (userContext is not null)
        {
            try { userContext.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }

        if (failures.Count == 1)
            throw new InvalidOperationException("The isolated SSH daemon did not stop cleanly.", failures[0]);
        if (failures.Count > 1)
            throw new AggregateException("The isolated SSH daemon did not stop cleanly.", failures);
    }

    private static async Task TerminateDaemonProcessTreeAsync(
        SafeKernelHandle? job,
        SafeKernelHandle? process)
    {
        var failures = new List<Exception>();
        var jobTerminationRequested = false;
        if (job is not null && !job.IsInvalid && !job.IsClosed)
        {
            if (!TerminateJobObject(job, 1))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not terminate the isolated SSH job."));
            }
            else
            {
                jobTerminationRequested = true;
                try
                {
                    var deadline = Stopwatch.GetTimestamp() + (long)(DaemonTerminationTimeout.TotalSeconds * Stopwatch.Frequency);
                    while (true)
                    {
                        if (!QueryInformationJobObject(
                                job,
                                JobObjectBasicAccountingInformationClass,
                                out var accounting,
                                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                                out _))
                        {
                            throw new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "Windows could not inspect the terminating SSH job.");
                        }
                        if (accounting.ActiveProcesses == 0) break;
                        if (Stopwatch.GetTimestamp() >= deadline)
                            throw new TimeoutException("The isolated SSH process tree did not terminate within ten seconds.");
                        await Task.Delay(50);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        if (process is not null && !process.IsInvalid && !process.IsClosed)
        {
            var wait = WaitForSingleObject(
                process,
                jobTerminationRequested ? checked((uint)DaemonTerminationTimeout.TotalMilliseconds) : 0);
            if (wait == WaitTimeout)
            {
                if (!TerminateProcess(process, 1))
                {
                    failures.Add(new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not terminate the isolated SSH daemon process."));
                }
                else
                {
                    wait = WaitForSingleObject(process, checked((uint)DaemonTerminationTimeout.TotalMilliseconds));
                    if (wait == WaitTimeout)
                        failures.Add(new TimeoutException("The isolated SSH daemon process did not terminate within ten seconds."));
                    else if (wait == WaitFailed)
                        failures.Add(new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Windows could not wait for the isolated SSH daemon process."));
                }
            }
            else if (wait == WaitFailed)
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not inspect the isolated SSH daemon process."));
            }
        }

        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("The isolated SSH process tree did not terminate cleanly.", failures);
    }

    private bool HasExactListener(string expectedAddress, uint expectedProcessId)
    {
        if (expectedProcessId == 0) return false;
        var expected = ParseTailscaleIpv4(expectedAddress, "target");
        foreach (var listener in EnumerateIpv4Listeners())
        {
            if (listener.ProcessId == expectedProcessId
                && listener.Port == RemoteAdministrationProtocol.SshPort
                && listener.Address.Equals(expected))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<TcpOwnerListener> EnumerateIpv4Listeners()
    {
        uint size = 0;
        var first = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            AfInet,
            TcpTableOwnerPidListener,
            0);
        if (first != ErrorInsufficientBuffer && first != Success)
            throw new Win32Exception((int)first, "Windows could not size the TCP owner table.");

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var result = GetExtendedTcpTable(
                buffer,
                ref size,
                order: false,
                AfInet,
                TcpTableOwnerPidListener,
                0);
            if (result != Success)
                throw new Win32Exception((int)result, "Windows could not read the TCP owner table.");

            var count = Marshal.ReadInt32(buffer);
            if (count is < 0 or > 65535)
                throw new InvalidDataException("Windows returned an invalid TCP listener count.");
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rows = new List<TcpOwnerListener>(count);
            var current = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(current);
                var address = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
                var port = unchecked((ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF)));
                rows.Add(new TcpOwnerListener(address, port, row.OwningProcessId));
                current = IntPtr.Add(current, rowSize);
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private async Task WriteAuthorizedKeysAsync(
        SupervisorState state,
        IReadOnlyList<SupervisorLease> active,
        CancellationToken cancellationToken)
    {
        var lines = active.Select(lease =>
            $"expiry-time=\"{lease.ExpiresAt.UtcDateTime:yyyyMMddHHmmss}Z\",from=\"{state.CoordinatorAddress}\",no-agent-forwarding,no-port-forwarding,no-X11-forwarding,no-user-rc {lease.PublicKey} opticon-session:{lease.SessionId}");
        var value = active.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        await WriteAtomicTextAsync(_authorizedKeysPath, value, cancellationToken);
        await RestrictAuthorizedKeysAsync(_authorizedKeysPath, cancellationToken);
    }

    private string BuildSshdConfig(SupervisorState state)
    {
        static string ConfigPath(string value) => value.Replace('\\', '/');
        return string.Join("\n",
        [
            $"Port {RemoteAdministrationProtocol.SshPort}",
            "AddressFamily inet",
            $"ListenAddress {state.TargetAddress}",
            $"HostKey \"{ConfigPath(_hostKeyPath)}\"",
            $"AuthorizedKeysFile \"{ConfigPath(_authorizedKeysPath)}\"",
            "PubkeyAuthentication yes",
            "PasswordAuthentication no",
            "ChallengeResponseAuthentication no",
            "AuthenticationMethods publickey",
            "PermitEmptyPasswords no",
            $"AllowUsers {RemoteAdministrationProtocol.SshAccountName.ToLowerInvariant()}@{state.CoordinatorAddress}",
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

    private async Task WriteReadyAsync(SupervisorState state, CancellationToken cancellationToken)
    {
        if (_daemonProcessId > int.MaxValue)
            throw new InvalidOperationException("The SSH process identifier is outside the supported range.");
        var ready = new SupervisorReady
        {
            Generation = state.Generation,
            ProcessId = checked((int)_daemonProcessId),
            Address = state.TargetAddress
        };
        var json = JsonSerializer.Serialize(ready, StrictJson) + Environment.NewLine;
        await WriteAtomicTextIfChangedAsync(_readyPath, json, cancellationToken);
        await RestrictSystemOnlyAsync(_readyPath, directory: false, cancellationToken);
    }

    private Task DeleteReadyAsync()
    {
        try
        {
            if (File.Exists(_readyPath)) File.Delete(_readyPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Could not delete SSH readiness marker: " + exception.Message);
        }
        return Task.CompletedTask;
    }

    private async Task FailClosedAsync(bool clearAuthorizedKeys = true)
    {
        // Authentication is cut before waiting for a possibly busy control
        // transaction. If state.lock is damaged or abandoned, a direct fixed-
        // path clear is still attempted; the per-key UTC expiry remains a
        // second, daemon-native cutoff.
        try { await DeleteReadyAsync(); } catch { }
        if (clearAuthorizedKeys)
            await ClearAuthorizedKeysFailClosedAsync();

        await _controlGate.WaitAsync(CancellationToken.None);
        try
        {
            await FailClosedCoreAsync();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    // Caller holds _controlGate.
    private async Task FailClosedCoreAsync()
    {
        // Each containment action is isolated so one damaged resource cannot
        // prevent the remaining controls from closing access.
        try { await DeleteReadyAsync(); } catch { }
        try { await StopDaemonAsync(); } catch (Exception exception) { Console.Error.WriteLine("Could not stop SSH job: " + exception.Message); }
        try { await DeleteFirewallRuleAsync(); } catch (Exception exception) { Console.Error.WriteLine("Could not remove SSH firewall rule: " + exception.Message); }
        try { EnsureManagedAccount(enabled: false); } catch (Exception exception) { Console.Error.WriteLine("Could not disable SSH account: " + exception.Message); }
        _configuredGeneration = -1;
        _observedTerminationGeneration = -1;
        _observedActiveSessionIds.Clear();
        _nextInfrastructureAudit = DateTimeOffset.MinValue;
    }

    private async Task ClearAuthorizedKeysFailClosedAsync()
    {
        try
        {
            await using (await AcquireStateLockAsync(CancellationToken.None))
            {
                await WriteAtomicTextAsync(_authorizedKeysPath, string.Empty, CancellationToken.None);
                await RestrictAuthorizedKeysAsync(_authorizedKeysPath, CancellationToken.None);
                return;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Could not clear SSH authorized keys under state lock: " + exception.Message);
        }

        try
        {
            await WriteAtomicTextAsync(_authorizedKeysPath, string.Empty, CancellationToken.None);
            await RestrictAuthorizedKeysAsync(_authorizedKeysPath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Could not clear SSH authorized keys at the fixed fallback path: " + exception.Message);
        }
    }
    private async Task DeleteFirewallRuleAsync()
    {
        if (!File.Exists(_netshPath)) return;
        _ = await WindowsCommand.RunAsync(
            _netshPath,
            ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"],
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
    }

    private void EnsureManagedAccount(bool enabled)
    {
        var existing = ReadLocalUser();
        if (existing is null)
        {
            if (!enabled)
            {
                try { SshDaemonUserContext.RotateUnknownPassword(); } catch { }
                _accountWasEnabledByThisProcess = false;
                return;
            }
            var add = new UserInfo1
            {
                Name = RemoteAdministrationProtocol.SshAccountName,
                Password = CreateUnknownPassword(),
                Privilege = UserPrivilegeUser,
                Comment = ManagedAccountComment,
                Flags = UserFlagScript | UserFlagPasswordCannotChange | UserFlagPasswordNeverExpires | UserFlagAccountDisabled
            };
            var result = NetUserAdd(null, 1, ref add, out _);
            if (result != Success) ThrowNetApi(result, "create the dedicated SSH account");
            existing = ReadLocalUser() ?? throw new InvalidOperationException("The dedicated SSH account was not created.");
        }
        else if (!existing.Value.Comment.Equals(ManagedAccountComment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A local account named {RemoteAdministrationProtocol.SshAccountName} exists but is not owned by Opticon.");
        }

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            .Translate(typeof(NTAccount)).Value;
        var slash = administrators.IndexOf('\\');
        if (slash >= 0) administrators = administrators[(slash + 1)..];
        var member = new LocalGroupMembersInfo3
        {
            DomainAndName = $"{Environment.MachineName}\\{RemoteAdministrationProtocol.SshAccountName}"
        };
        if (enabled)
        {
            var groupResult = NetLocalGroupAddMembers(null, administrators, 3, ref member, 1);
            if (groupResult is not Success and not ErrorMemberInAlias)
                ThrowNetApi(groupResult, "add the dedicated SSH account to Administrators");
        }

        var desiredFlags = existing.Value.Flags
                           | UserFlagScript
                           | UserFlagPasswordCannotChange
                           | UserFlagPasswordNeverExpires;
        desiredFlags = enabled
            ? desiredFlags & ~UserFlagAccountDisabled
            : desiredFlags | UserFlagAccountDisabled;
        var flags = new UserInfo1008 { Flags = desiredFlags };
        var flagsResult = NetUserSetInfo(
            null,
            RemoteAdministrationProtocol.SshAccountName,
            1008,
            ref flags,
            out _);
        if (flagsResult != Success) ThrowNetApi(flagsResult, "set the dedicated SSH account state");

        if (!enabled)
        {
            Exception? cleanupError = null;
            try { SshDaemonUserContext.RotateUnknownPassword(); }
            catch (Exception exception) { cleanupError = exception; }
            var removeResult = NetLocalGroupDelMembers(null, administrators, 3, ref member, 1);
            if (removeResult is not Success and not ErrorMemberNotInAlias)
            {
                cleanupError ??= new InvalidOperationException(
                    $"Windows could not remove the idle SSH account from Administrators (NetAPI error {removeResult}).");
            }
            if (cleanupError is not null) throw cleanupError;
        }
        _accountWasEnabledByThisProcess = enabled;
    }

    private async Task GrantDaemonRuntimeAccessAsync(CancellationToken cancellationToken)
    {
        var accountSid = GetManagedAccountSid();

        await ApplyRuntimeAclAsync(
            _stateDirectory,
            $"*{accountSid}:RX",
            directory: true,
            cancellationToken);
        foreach (var path in new[] { _configPath, _hostKeyPath, _hostKeyPath + ".pub" })
            await ApplyRuntimeAclAsync(path, $"*{accountSid}:R", directory: false, cancellationToken);

        await EnsureDaemonLogAccessAsync(accountSid, cancellationToken);
    }

    private async Task RotateDaemonLogAsync(CancellationToken cancellationToken)
    {
        var accountSid = GetManagedAccountSid();
        if (!File.Exists(_logPath))
        {
            await EnsureDaemonLogAccessAsync(accountSid, cancellationToken);
            return;
        }

        RejectReparsePoint(_logPath, "SSH daemon log");
        var log = new FileInfo(_logPath);
        if (log.Length == 0)
        {
            await EnsureDaemonLogAccessAsync(accountSid, cancellationToken);
            return;
        }

        var archivePath = _logPath + ".1";
        var temporaryPath = archivePath + ".supervisor-new";
        try
        {
            if (File.Exists(archivePath))
            {
                RejectReparsePoint(archivePath, "archived SSH daemon log");
                File.Delete(archivePath);
            }
            if (Directory.Exists(archivePath))
                throw new InvalidDataException("The archived SSH daemon log path is a directory.");
            if (File.Exists(temporaryPath))
            {
                RejectReparsePoint(temporaryPath, "temporary archived SSH daemon log");
                File.Delete(temporaryPath);
            }

            await using (var source = new FileStream(
                             _logPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var retainedBytes = Math.Min(source.Length, MaximumArchivedLogBytes);
                source.Seek(-retainedBytes, SeekOrigin.End);
                var buffer = new byte[64 * 1024];
                var remaining = retainedBytes;
                while (remaining > 0)
                {
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, archivePath);
            await RestrictSystemOnlyAsync(archivePath, directory: false, cancellationToken);
            File.Delete(_logPath);
            await EnsureDaemonLogAccessAsync(accountSid, cancellationToken);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private async Task EnsureDaemonLogAccessAsync(string accountSid, CancellationToken cancellationToken)
    {
        if (!File.Exists(_logPath))
            await WriteAtomicTextAsync(_logPath, string.Empty, cancellationToken);
        RejectReparsePoint(_logPath, "SSH daemon log");
        await ApplyRuntimeAclAsync(_logPath, $"*{accountSid}:(R,W)", directory: false, cancellationToken);
    }

    private static string GetManagedAccountSid() =>
        ((SecurityIdentifier)new NTAccount(
                Environment.MachineName,
                RemoteAdministrationProtocol.SshAccountName)
            .Translate(typeof(SecurityIdentifier))).Value;

    private async Task ApplyRuntimeAclAsync(
        string path,
        string accountGrant,
        bool directory,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var systemGrant = directory ? "*S-1-5-18:(OI)(CI)F" : "*S-1-5-18:F";
        var result = await WindowsCommand.RunAsync(
            _icaclsPath,
            [
                path,
                "/inheritance:r",
                "/grant:r", systemGrant,
                "/grant:r", accountGrant,
                "/setowner", "*S-1-5-18"
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Windows could not apply the exact SSH daemon ACL to {Path.GetFileName(path)}: {result.ErrorDetail}");
    }

    private NativeUser? ReadLocalUser()
    {
        var result = NetUserGetInfo(null, RemoteAdministrationProtocol.SshAccountName, 1, out var buffer);
        if (result == UserNotFound) return null;
        if (result != Success) ThrowNetApi(result, "read the dedicated SSH account");
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

    private async Task RestrictAuthorizedKeysAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var result = await WindowsCommand.RunAsync(
            _icaclsPath,
            [
                path,
                "/inheritance:r",
                "/grant:r", "*S-1-5-18:F",
                "/grant:r", "*S-1-5-32-544:F",
                "/setowner", "*S-1-5-18"
            ],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Windows could not protect {Path.GetFileName(path)} for SYSTEM and Administrators: {result.ErrorDetail}");
    }
    private async Task RestrictSystemOnlyAsync(
        string path,
        bool directory,
        CancellationToken cancellationToken)
    {
        RequireExactSystemExecutable(_icaclsPath, "Windows ACL tool");
        var grant = directory ? "*S-1-5-18:(OI)(CI)F" : "*S-1-5-18:F";
        var result = await WindowsCommand.RunAsync(
            _icaclsPath,
            [path, "/inheritance:r", "/grant:r", grant, "/setowner", "*S-1-5-18"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Windows could not protect {Path.GetFileName(path)} for SYSTEM: {result.ErrorDetail}");
    }

    private static async Task WriteAtomicTextAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) RejectReparsePoint(path, Path.GetFileName(path));
        var temporary = path + ".supervisor-new";
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            var bytes = Utf8NoBom.GetBytes(value);
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static async Task WriteAtomicTextIfChangedAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            RejectReparsePoint(path, Path.GetFileName(path));
            var current = await File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken);
            if (current.Equals(value, StringComparison.Ordinal)) return;
        }
        await WriteAtomicTextAsync(path, value, cancellationToken);
    }

    private void ValidateFixedLayout()
    {
        var expected = Path.GetFullPath(Path.Combine(AppPaths.AgentDataDirectory, "SshAccess"));
        if (!_stateDirectory.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SSH supervisor state path is not fixed under AgentData/SshAccess.");
        var root = Path.GetFullPath(AppPaths.AgentDataDirectory).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!_stateDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SSH supervisor state path escapes AgentData.");
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The protected {description} is a reparse point.");
    }

    private static void RequireExactSystemExecutable(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"The exact System32 {description} is unavailable.", path);
        RejectReparsePoint(path, description);
    }

    private static IPAddress ParseTailscaleIpv4(string value, string description)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException($"The SSH {description} address is not IPv4.");
        var bytes = address.GetAddressBytes();
        if (bytes[0] != 100 || bytes[1] is < 64 or > 127)
            throw new InvalidDataException($"The SSH {description} address is outside 100.64.0.0/10.");
        return address;
    }

    private static void ValidateSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("The SSH lease state contains an invalid session identifier.");
    }

    private static string NormalizeEd25519PublicKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Contains('\r') || value.Contains('\n'))
            throw new InvalidDataException("An SSH public key is not one bounded line.");
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("ssh-ed25519", StringComparison.Ordinal))
            throw new InvalidDataException("Only Ed25519 SSH public keys are accepted.");

        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException exception) { throw new InvalidDataException("An SSH public key has invalid base64.", exception); }
        if (blob.Length is < 4 + 11 + 4 + 32 or > 256)
            throw new InvalidDataException("An SSH public key has an invalid wire length.");
        var offset = 0;
        var type = ReadSshString(blob, ref offset);
        var key = ReadSshString(blob, ref offset);
        if (!type.SequenceEqual("ssh-ed25519"u8) || key.Length != 32 || offset != blob.Length)
            throw new InvalidDataException("An SSH public key is not a canonical Ed25519 wire key.");
        return $"ssh-ed25519 {parts[1]}";
    }

    private static ReadOnlySpan<byte> ReadSshString(byte[] value, ref int offset)
    {
        if (offset > value.Length - sizeof(uint))
            throw new InvalidDataException("An SSH public key is truncated.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (length > int.MaxValue || offset > value.Length - (int)length)
            throw new InvalidDataException("An SSH public key field has an invalid length.");
        var result = value.AsSpan(offset, (int)length);
        offset += (int)length;
        return result;
    }

    private static string CreateUnknownPassword()
    {
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(28));
        return "Aa1!" + random;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static JsonSerializerOptions CreateStrictJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = true
        };
        return options;
    }

    private static void ThrowNetApi(int error, string action) =>
        throw new InvalidOperationException($"Windows could not {action} (NetAPI error {error}).");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await FailClosedAsync();
        _controlGate.Dispose();
        _supervisorLock?.Dispose();
        _supervisorLock = null;
    }

    private sealed class SupervisorState
    {
        public int SchemaVersion { get; set; }
        public long Generation { get; set; }
        public long SessionTerminationGeneration { get; set; }
        public string TargetAddress { get; set; } = string.Empty;
        public string CoordinatorAddress { get; set; } = string.Empty;
        public List<SupervisorLease> Leases { get; set; } = [];
        public List<SupervisorRevocation> Revocations { get; set; } = [];
    }

    private sealed class SupervisorLease
    {
        public string SessionId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class SupervisorRevocation
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
    private readonly record struct TcpOwnerListener(IPAddress Address, ushort Port, uint ProcessId);

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle() : base(ownsHandle: true) { }
        public SafeKernelHandle(IntPtr existing) : base(ownsHandle: true) => SetHandle(existing);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeKernelHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeKernelHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeKernelHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformation information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeKernelHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeKernelHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

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
    private static extern int NetLocalGroupDelMembers(
        string? serverName,
        string groupName,
        int level,
        ref LocalGroupMembersInfo3 buffer,
        int totalEntries);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(
        string? serverName,
        string groupName,
        int level,
        ref LocalGroupMembersInfo3 buffer,
        int totalEntries);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

