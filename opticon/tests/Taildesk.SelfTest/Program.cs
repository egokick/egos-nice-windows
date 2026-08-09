using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Taildesk.Shared;

if (args.Length == 2 && args[0].Equals("--verify-authenticode", StringComparison.Ordinal))
{
    await InvitationSigning.VerifyAuthenticodeAsync(args[1]);
    Console.WriteLine("PASS  pinned Authenticode signature and file digest");
    return;
}
if (args.Length != 0)
    throw new ArgumentException("Taildesk.SelfTest accepts only --verify-authenticode <path>.");

var tests = new (string Name, Action Body)[]
{
    ("tokens are random and hash comparisons work", TestTokens),
    ("invitations default to fourteen days with a bounded extension policy", TestInvitationPolicy),
    ("invite JSON round-trips without losing role", TestInviteRoundTrip),
    ("signed invitation container detects payload tampering", TestInviteContainer),
    ("hosted invitation encryption rejects wrong keys and tampering", TestHostedInvite),
    ("invitation storage rejects OneDrive", TestPrivateStorage),
    ("dependency downloads are version and hash pinned", TestDependencyPins),
    ("Tailscale enrollment resets stale settings before applying invitation policy", TestTailscaleEnrollmentArguments),
    ("process runner supports commands with inherited standard handles", TestProcessRunnerWithoutCapture),
    ("process runner applies its deadline to inherited output handles", TestProcessRunnerStreamDeadline),
    ("RustDesk managed-host hardening is complete and idempotent", TestRustDeskHardening),
    ("RustDesk virtual-display privacy is opt-in", TestRustDeskVirtualDisplayDefault),
    ("RustDesk remote sessions pass the saved password to the native connection command", TestRustDeskRemoteSessionLaunch),
    ("RustDesk installer configures every Windows service profile before validation", TestRustDeskInstallerProfiles),
    ("controller registry contains no permanent credentials", TestControllerRegistryShape),
    ("remote administration contracts reject unpinned or unsafe updates", TestRemoteAdministrationProtocol),
    ("release distribution keeps signed bundles private and CloudFront-addressed", TestReleaseDistributionDesign),
    ("OpenSSH recovery is fixed-path, Windows-compatible, and independently supervised", TestOpenSshRecoveryDesign),
    ("runtime tailnet policy keeps administrative SSH hub-only", TestTailnetSshPolicy),
    ("update journal writes round-trip through atomic persistence", TestUpdateJournalPersistence),
    ("uploads permit huge files but retain bounded resource controls", TestUploadPolicy),
    ("cancelled uploads retain an authenticated byte offset and resume", TestResumableUpload),
    ("path guard permits a child and blocks traversal", TestPathGuard),
    ("path guard exposes every ready local device volume", TestLocalVolumeRoots),
    ("exit-node approval contains both internet default routes", TestExitNodeApprovalRoutes),
    ("private HTTP transport bypasses proxies and redirects", TestDirectHttpTransport),
    ("enrollment retries accept only the exact committed identity", TestEnrollmentReplayPolicy),
    ("credential rotation survives an ambiguous response", TestCredentialRotationState),
    ("failed durable collection mutations roll back in memory", TestDurableCollectionMutation),
    ("guarded path leases prevent component replacement", TestPathLease),
    ("WPF style templates match their control target types", TestWpfStyleTemplateTargets),
    ("WPF controls keep explicit accessible foreground/background pairs", TestWpfContrastContract),
    ("file browser offers direct paths and list or thumbnail views", TestFileBrowserContract),
    ("device rows expose a persisted rename action", TestDeviceRenameContract),
    ("DPAPI current-user and machine scopes round-trip", TestDpapi)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Environment.ExitCode = 1;
    Console.Error.WriteLine($"{failures.Count} Taildesk self-test(s) failed.");
}

static void TestTokens()
{
    var first = SecurityHelpers.CreateToken();
    var second = SecurityHelpers.CreateToken();
    Assert(first.Length >= 40, "token is too short");
    Assert(first != second, "two tokens unexpectedly matched");
    Assert(SecurityHelpers.FixedTimeEquals(SecurityHelpers.HashToken(first), SecurityHelpers.HashToken(first)), "equal hashes did not match");
    Assert(!SecurityHelpers.FixedTimeEquals(SecurityHelpers.HashToken(first), SecurityHelpers.HashToken(second)), "different hashes matched");
}

static void TestInvitationPolicy()
{
    var lifetime = InvitationPolicy.CreateDefaultExpiry() - DateTimeOffset.UtcNow;
    Assert(lifetime > TimeSpan.FromDays(13.99) && lifetime <= TimeSpan.FromDays(14), "default invitation lifetime is not fourteen days");
    Assert(InvitationPolicy.MaximumLifetimeDays == 365, "maximum invitation lifetime changed unexpectedly");
    Assert(InvitationPolicy.IsSupportedPayloadSchema(InvitationPolicy.LegacyBundleSchemaVersion), "legacy invitation schema must remain installable");
    Assert(InvitationPolicy.IsSupportedPayloadSchema(InvitationPolicy.HostedLinkSchemaVersion), "hosted invitation schema must be installable");
    Assert(!InvitationPolicy.IsSupportedPayloadSchema(1) && !InvitationPolicy.IsSupportedPayloadSchema(4), "unknown invitation schemas must be rejected");
}
static void TestInviteRoundTrip()
{
    var invite = new InvitePayload
    {
        InviteId = Guid.NewGuid(),
        DeviceName = "Workshop PC",
        Role = DeviceRole.ControllerAndManaged,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        InviteSecret = SecurityHelpers.CreateToken(),
        TailscaleAuthKey = "tskey-auth-test",
        HeadscaleLoginUrl = "https://taildesk-control.example.test",
        AgentToken = SecurityHelpers.CreateToken(),
        RustDeskPassword = SecurityHelpers.CreateHumanPassword(),
        ControllerToken = SecurityHelpers.CreateToken(),
        CoordinatorUrl = "http://100.100.100.100:45830",
        ExpectedTailnet = "example.test",
        AllowedRoots = ["Documents", "Pictures"]
    };
    var json = JsonSerializer.Serialize(invite, JsonDefaults.Options);
    var copy = JsonSerializer.Deserialize<InvitePayload>(json, JsonDefaults.Options);
    Assert(copy?.InviteId == invite.InviteId, "invite id changed");
    Assert(copy?.Role == DeviceRole.ControllerAndManaged, "role changed");
    Assert(copy?.AgentToken == invite.AgentToken, "agent token changed");
    Assert(copy?.HeadscaleLoginUrl == invite.HeadscaleLoginUrl, "Headscale login URL changed");
    Assert(copy?.ExpectedTailnet == invite.ExpectedTailnet, "expected tailnet changed");
    Assert(copy?.AllowedRoots.SequenceEqual(invite.AllowedRoots) == true, "shared roots changed");
}

static void TestInviteContainer()
{
    var temporary = Path.Combine(Path.GetTempPath(), "taildesk-invite-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var launcher = Path.Combine(temporary, "launcher.exe");
        var source = Path.Combine(temporary, "source");
        var archive = Path.Combine(temporary, "payload.zip");
        var invitation = Path.Combine(temporary, "invite.exe");
        var extracted = Path.Combine(temporary, "extracted");
        File.WriteAllBytes(launcher, [0x4d, 0x5a, 0x01, 0x02]);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "proof.txt"), "taildesk-one-click");
        ZipFile.CreateFromDirectory(source, archive);
        using var rsa = RSA.Create(3072);
        InviteContainer.CreateAsync(launcher, archive, invitation, signer: data =>
            rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)).GetAwaiter().GetResult();
        using (var stream = new FileStream(invitation, FileMode.Append, FileAccess.Write)) stream.Write(new byte[512]);
        InviteContainer.ExtractAsync(invitation, extracted, verifier: rsa).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(extracted, "proof.txt")) == "taildesk-one-click", "one-file invite payload changed");

        var tampered = Path.Combine(temporary, "tampered.exe");
        File.Copy(invitation, tampered);
        using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = new FileInfo(launcher).Length + 4;
            var original = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(original ^ 0xff));
        }
        AssertThrows<InvalidDataException>(() => InviteContainer.ExtractAsync(
            tampered, Path.Combine(temporary, "tampered-output"), verifier: rsa).GetAwaiter().GetResult());
    }
    finally
    {
        Directory.Delete(temporary, true);
    }
}

static void TestHostedInvite()
{
    var invite = new InvitePayload { InviteId = Guid.NewGuid(), DeviceName = "Hosted PC", Role = DeviceRole.ManagedOnly,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), InviteSecret = SecurityHelpers.CreateToken() };
    using var rsa = RSA.Create(3072);
    var envelope = HostedInviteFile.CreateSigned(invite, data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    var key = SecurityHelpers.CreateToken(32);
    var encrypted = HostedInviteFile.Encrypt(key, envelope);
    var decrypted = HostedInviteFile.Decrypt(key, encrypted);
    var copy = HostedInviteFile.ReadSigned(decrypted, (data, signature) => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    Assert(copy.InviteId == invite.InviteId, "hosted invitation changed during encryption");
    AssertThrows<InvalidDataException>(() => HostedInviteFile.Decrypt(SecurityHelpers.CreateToken(32), encrypted));
    encrypted[^1] ^= 0xff;
    AssertThrows<InvalidDataException>(() => HostedInviteFile.Decrypt(key, encrypted));
}
static void TestPrivateStorage()
{
    AssertThrows<InvalidOperationException>(() => PrivateStorage.ValidateInviteDirectory(
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users", "Someone", "OneDrive", "Opticon")));
    var local = PrivateStorage.ValidateInviteDirectory(PrivateStorage.InviteDirectory);
    Assert(local.Contains(Path.Combine("Opticon", "Invitations"), StringComparison.OrdinalIgnoreCase), "default invitation directory is not local Opticon storage");
}

static void TestDependencyPins()
{
    foreach (var artifact in DependencyArtifacts.All)
    {
        Assert(artifact.Version.Length > 0, $"{artifact.Product} has no pinned version");
        Assert(artifact.Sha256.Length == 64 && artifact.Sha256.All(Uri.IsHexDigit), $"{artifact.Product} has no SHA-256 pin");
        Assert(artifact.Size > 0, $"{artifact.Product} has no size pin");
        Assert(artifact.PrimaryUrl.StartsWith(DependencyArtifacts.FlyArtifactBase, StringComparison.Ordinal), "Fly is not the primary artifact source");
        Assert(!artifact.PrimaryUrl.Contains("latest", StringComparison.OrdinalIgnoreCase), "primary URL uses latest");
        Assert(!artifact.FallbackUrl.Contains("latest", StringComparison.OrdinalIgnoreCase), "fallback URL uses latest");
        Assert(artifact.PrimaryUrl.EndsWith(artifact.FileName, StringComparison.Ordinal), "primary filename changed");
        Assert(artifact.FallbackUrl.EndsWith(artifact.FileName, StringComparison.Ordinal), "fallback filename changed");
    }
}
static void TestTailscaleEnrollmentArguments()
{
    var arguments = TailscaleCommandLine.BuildEnrollmentArguments(
        "https://headscale.example.test", "tskey-auth-test", "managed-pc");
    Assert(arguments[0] == "up", "Tailscale enrollment must use the up command");
    Assert(arguments.Contains("--reset", StringComparer.Ordinal),
        "Tailscale enrollment must reset stale non-default settings from a partial installation");
    Assert(arguments.Contains("--force-reauth", StringComparer.Ordinal),
        "Tailscale enrollment must replace an expired partial-installation session without calling logout");
    Assert(arguments.Contains("--accept-dns=false", StringComparer.Ordinal) && arguments.Contains("--accept-routes=false", StringComparer.Ordinal),
        "Tailscale enrollment must reapply Opticon route and DNS policy after reset");
}
static void TestProcessRunnerWithoutCapture()
{
    if (!OperatingSystem.IsWindows()) return;
    var result = ProcessRunner.RunAsync("cmd.exe", ["/d", "/c", "echo ignored"],
        TimeSpan.FromSeconds(5), captureOutput: false).GetAwaiter().GetResult();
    Assert(result.Succeeded, "uncaptured command failed");
    Assert(result.StandardOutput.Length == 0 && result.StandardError.Length == 0,
        "uncaptured command unexpectedly retained redirected output");
}

static void TestProcessRunnerStreamDeadline()
{
    if (!OperatingSystem.IsWindows()) return;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    AssertThrows<TimeoutException>(() => ProcessRunner.RunAsync(
        "cmd.exe",
        ["/d", "/c", "start \"\" /b cmd.exe /d /c \"ping 127.0.0.1 -n 6 > nul\""],
        TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult());
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
        "the inherited output handle was allowed to outlive the process deadline");
}
static void TestRustDeskHardening()
{
    const string original = "rendezvous_server = 'public.example'\r\n[options]\r\ndirect-server = 'N'\r\nunknown = 'preserved'\r\n";
    var hardened = RustDeskConfiguration.HardenManagedHost(original);
    Assert(RustDeskConfiguration.IsManagedHostHardened(hardened), "hardened configuration should verify");
    Assert(hardened == RustDeskConfiguration.HardenManagedHost(hardened), "hardening should be idempotent");
    Assert(hardened.Contains("direct-server = 'Y'", StringComparison.Ordinal), "direct server must be enabled");
    Assert(hardened.Contains("enable-privacy-mode = 'Y'", StringComparison.Ordinal), "managed targets must permit privacy mode");
    Assert(hardened.Contains("whitelist = ','", StringComparison.Ordinal), "RustDesk must not receive an unsupported CIDR whitelist; Windows Firewall enforces the tailnet range");
    Assert(hardened.Contains("unknown = 'preserved'", StringComparison.Ordinal), "unmanaged options must be preserved");

    const string peer = "privacy_mode = false\r\n[options]\r\nunknown = 'preserved'\r\n";
    var privacyEnabled = RustDeskConfiguration.ConfigurePeerPrivacyMode2(peer, true);
    Assert(privacyEnabled.Contains("privacy_mode = true", StringComparison.Ordinal), "Mode 2 must enable privacy for the selected peer");
    Assert(privacyEnabled.Contains("privacy-mode-impl-key = 'privacy_mode_impl_virtual_display'", StringComparison.Ordinal), "Mode 2 must select RustDesk's virtual display implementation");
    Assert(privacyEnabled.Contains("unknown = 'preserved'", StringComparison.Ordinal), "peer options must be preserved");
    Assert(privacyEnabled == RustDeskConfiguration.ConfigurePeerPrivacyMode2(privacyEnabled, true), "peer privacy configuration should be idempotent");
    var privacyDisabled = RustDeskConfiguration.ConfigurePeerPrivacyMode2(privacyEnabled, false);
    Assert(privacyDisabled.Contains("privacy_mode = false", StringComparison.Ordinal), "the per-device toggle must disable privacy for the selected peer");
}

static string ReadSource(params string[] parts)
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
    }

    throw new InvalidOperationException($"Source file was not found: {Path.Combine(parts)}");
}

static void TestRustDeskRemoteSessionLaunch()
{
    var launcher = ReadSource("src", "Taildesk.Admin", "RustDeskSessionLauncher.cs");
    var connectArgument = launcher.IndexOf("start.ArgumentList.Add(\"--connect\")", StringComparison.Ordinal);
    var passwordArgument = launcher.IndexOf("start.ArgumentList.Add(\"--password\")", StringComparison.Ordinal);
    var passwordValue = launcher.IndexOf("start.ArgumentList.Add(password)", StringComparison.Ordinal);
    Assert(connectArgument >= 0 && passwordArgument > connectArgument && passwordValue > passwordArgument,
        "RustDesk remote launch must provide the saved password through its native connection command");
    Assert(launcher.Contains("WorkingDirectory = executableDirectory", StringComparison.Ordinal),
        "RustDesk must not inherit and lock Opticon's installed command-center directory");
}

static void TestRustDeskVirtualDisplayDefault()
{
    Assert(!new DeviceRecord().PrivacyMode2Enabled,
        "new devices must not require a RustDesk virtual display for ordinary remote sessions");

    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    Assert(viewModel.Contains("Config.PrivacyMode2ByDevice.TryGetValue(device.Id, out var enabled) && enabled", StringComparison.Ordinal),
        "a device must enable virtual-display privacy explicitly");
}

static void TestRustDeskInstallerProfiles()
{
    string? sourcePath = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Taildesk.Setup", "InstallerServices.cs");
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        if (sourcePath is not null) break;
    }
    if (sourcePath is null) throw new InvalidOperationException("Taildesk.Setup InstallerServices.cs was not found.");
    var source = File.ReadAllText(sourcePath);
    Assert(source.Contains("ServiceProfiles\", \"LocalService", StringComparison.Ordinal), "LocalService RustDesk profile is not hardened");
    Assert(source.Contains("ServiceProfiles\", \"NetworkService", StringComparison.Ordinal), "NetworkService RustDesk profile is not hardened");
    Assert(source.Contains("System32\", \"config\", \"systemprofile", StringComparison.Ordinal), "SYSTEM RustDesk profile is not hardened");
    Assert(source.Contains("taskkill.exe", StringComparison.Ordinal), "stale RustDesk child processes are not cleared");
    var configureIndex = source.IndexOf("await ConfigureRustDeskAsync(rustDesk", StringComparison.Ordinal);
    var listenerIndex = source.IndexOf("WaitForListeningPortAsync(21118", StringComparison.Ordinal);
    Assert(configureIndex >= 0 && listenerIndex > configureIndex, "RustDesk listener is checked before configuration");
}
static void TestControllerRegistryShape()
{
    var json = JsonSerializer.Serialize(new ControllerDeviceDto(), JsonDefaults.Options);
    Assert(!json.Contains("agentToken", StringComparison.OrdinalIgnoreCase), "controller registry exposes the agent token field");
    Assert(!json.Contains("rustDeskPassword", StringComparison.OrdinalIgnoreCase), "controller registry exposes the RustDesk password field");
}
static void TestRemoteAdministrationProtocol()
{
    Assert(RemoteAdministrationProtocol.SshPort == 45832, "the isolated SSH port changed unexpectedly");
    Assert(RemoteAdministrationProtocol.UpdateVersion == 1, "the guarded update protocol changed without a migration");
    Assert(RemoteAdministrationProtocol.MaximumSshSession == TimeSpan.FromHours(8), "SSH maximum lease is not bounded to eight hours");
    Assert(RemoteAdministrationProtocol.UpdateCommitWindow <= TimeSpan.FromMinutes(5), "update commit window is too long");
    Assert(UpdatePackageVerifier.NormalizeVersion("1.2.3.0") == "1.2.3", "four-part Windows file version was not canonicalized");
    Assert(RemoteAdministrationProtocol.IsTailscaleIpv4("100.64.0.1"), "canonical Tailscale IPv4 was rejected");
    Assert(RemoteAdministrationProtocol.IsTailscaleIpv4("100.127.255.254"), "upper Tailscale IPv4 was rejected");
    Assert(!RemoteAdministrationProtocol.IsTailscaleIpv4("100.128.0.1"), "address beyond Tailscale CGNAT range was accepted");
    Assert(!RemoteAdministrationProtocol.IsTailscaleIpv4("::ffff:100.64.0.1"), "IPv4-mapped IPv6 bypassed strict Tailscale validation");
    Assert(!RemoteAdministrationProtocol.IsTailscaleIpv4("::6464:1"), "native IPv6 bypassed strict Tailscale validation");
    Assert(new OpticonReleaseManifest().MinimumGuardianVersion == RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion,
        "the release Guardian floor must match the watchdog compatibility contract");
    Assert(!RemoteAdministrationProtocol.SupportsGuardianWatchdog(new Version(1, 1, 1))
           && RemoteAdministrationProtocol.SupportsGuardianWatchdog(new Version(1, 1, 2))
           && RemoteAdministrationProtocol.SupportsGuardianWatchdog(new Version(1, 1, 20)),
        "Guardian watchdog compatibility must use the contract floor rather than the current Setup version");

    var futureConfig = JsonSerializer.Deserialize<AgentConfig>(
        "{\"schemaVersion\":2,\"futureEnrollmentField\":{\"value\":7}}", JsonDefaults.Options)
        ?? throw new InvalidDataException("extended Agent config did not deserialize");
    var futureConfigJson = JsonSerializer.Serialize(futureConfig, JsonDefaults.Options);
    Assert(futureConfigJson.Contains("\"futureEnrollmentField\"", StringComparison.Ordinal),
        "an atomic maintenance config save would discard unknown enrolled fields");

    var request = new OpticonUpdateRequest
    {
        OperationId = Guid.NewGuid(),
        TargetVersion = "1.2.3",
        Role = DeviceRole.ManagedOnly,
        Architecture = "x64",
        DownloadUrl = "https://opticon.example.test/opticon-bundle-1.2.3-managed-win-x64.zip",
        PackageSize = 4096,
        PackageSha256 = new string('a', 64)
    };
    UpdatePackageVerifier.ValidateRequest(request);
    var requestJson = JsonSerializer.Serialize(request, JsonDefaults.Options);
    Assert(!requestJson.Contains("maintenanceBootstrap", StringComparison.OrdinalIgnoreCase),
        "an Agent API update request can opt into the privileged legacy maintenance bypass");

    request.DownloadUrl = "http://opticon.example.test/release.zip";
    AssertThrows<InvalidDataException>(() => UpdatePackageVerifier.ValidateRequest(request));
    request.DownloadUrl = "https://user:secret@opticon.example.test/release.zip";
    AssertThrows<InvalidDataException>(() => UpdatePackageVerifier.ValidateRequest(request));
    request.DownloadUrl = "https://opticon.example.test/release.zip";
    request.Architecture = "x86";
    AssertThrows<InvalidDataException>(() => UpdatePackageVerifier.ValidateRequest(request));

    var sshRequestJson = JsonSerializer.Serialize(new SshAccessRequest
    {
        PublicKey = "ssh-ed25519 AAAA",
        RequestedLifetimeSeconds = 3600,
        ExpiresAt = DateTimeOffset.Parse("2030-01-01T04:00:00Z")
    }, JsonDefaults.Options);
    using var sshRequestDocument = JsonDocument.Parse(sshRequestJson);
    Assert(sshRequestDocument.RootElement.GetProperty("requestedLifetimeSeconds").GetInt32() == 3600,
        "the target-relative SSH lease duration did not serialize");

    var sshJson = JsonSerializer.Serialize(new SshAccessResponse
    {
        SessionId = "lease_123",
        Host = "100.64.0.25",
        CreatedAt = DateTimeOffset.Parse("2030-01-01T12:00:00+09:00"),
        ExpiresAt = DateTimeOffset.Parse("2030-01-01T12:30:00+09:00"),
        HostPublicKey = "ssh-ed25519 AAAA"
    }, JsonDefaults.Options);
    var ssh = JsonSerializer.Deserialize<SshAccessResponse>(sshJson, JsonDefaults.Options);
    Assert(ssh?.SessionId == "lease_123" && ssh.Host == "100.64.0.25" && ssh.CreatedAt is not null,
        "SSH lease identity, host, and target-relative timing did not round-trip");

    var targetCreatedAt = DateTimeOffset.Parse("2030-01-01T12:00:00+09:00");
    Assert(RemoteAdministrationProtocol.IsSshLeaseWithinRequestedLifetime(
            targetCreatedAt, targetCreatedAt.AddHours(1), TimeSpan.FromHours(1)),
        "a target-relative SSH lease equal to the requested duration was rejected");
    Assert(!RemoteAdministrationProtocol.IsSshLeaseWithinRequestedLifetime(
            targetCreatedAt, targetCreatedAt.AddHours(1).AddSeconds(1), TimeSpan.FromHours(1)),
        "an SSH lease longer than requested was accepted");
    Assert(!RemoteAdministrationProtocol.IsSshLeaseWithinRequestedLifetime(
            targetCreatedAt, targetCreatedAt, TimeSpan.FromHours(1)),
        "an already-expired SSH lease was accepted");
}
static void TestReleaseDistributionDesign()
{
    DirectoryInfo? root = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Taildesk.sln"))) { root = directory; break; }
        }
        if (root is not null) break;
    }
    Assert(root is not null, "could not find the Opticon source root for release-distribution checks");
    string Read(params string[] parts) => File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    var template = Read("infrastructure", "aws", "opticon-release-distribution.yaml");
    var publisher = Read("fly-headscale", "scripts", "Publish-OpticonBundles.ps1");
    var gateway = Read("fly-headscale", "gateway", "main.go");
    var client = Read("src", "Taildesk.Admin", "OpticonReleaseClient.cs");
    var agent = Read("src", "Taildesk.Agent", "UpdateManager.cs");
    var hostedBootstrap = Read("src", "Taildesk.Setup", "HostedBootstrap.cs");
    var setupWindow = Read("src", "Taildesk.Setup", "MainWindow.xaml.cs");
    Assert(template.Contains("BucketOwnerEnforced", StringComparison.Ordinal)
           && template.Contains("BlockPublicPolicy: true", StringComparison.Ordinal)
           && template.Contains("DenyInsecureTransport", StringComparison.Ordinal)
           && template.Contains("OriginAccessControl", StringComparison.Ordinal)
           && template.Contains("ResponseHeadersPolicyId: 60669652-455b-4ae9-85a4-c4c02393f86c", StringComparison.Ordinal)
           && template.Contains("TLSv1.2_2021", StringComparison.Ordinal),
        "CloudFront infrastructure no longer enforces the private TLS-only S3 boundary");
    Assert(publisher.Contains("--checksum-algorithm", StringComparison.Ordinal)
           && publisher.Contains("--metadata", StringComparison.Ordinal)
           && publisher.Contains("sha256=$hash", StringComparison.Ordinal)
           && publisher.Contains("--checksum-mode", StringComparison.Ordinal)
           && publisher.Contains("Add-Type -AssemblyName System.Net.Http", StringComparison.Ordinal)
           && publisher.Contains("max_concurrent_requests = 20", StringComparison.Ordinal)
           && publisher.Contains("Invoke-CloudFrontVerification", StringComparison.Ordinal)
           && publisher.Contains("FullStreamVerified", StringComparison.Ordinal)
           && publisher.Contains("Publish-ManifestAtomically", StringComparison.Ordinal)
           && !publisher.Contains("flyctl deploy", StringComparison.Ordinal)
           && publisher.Contains("Refusing to overwrite immutable", StringComparison.Ordinal),
        "publisher no longer enforces immutable S3 upload, bounded CloudFront readback, and atomic manifest publication");
    Assert(gateway.Contains("validCloudFrontDownloadURL", StringComparison.Ordinal)
           && gateway.Contains("bundleDownloadURL", StringComparison.Ordinal)
           && gateway.Contains("releaseManifestAdmin", StringComparison.Ordinal)
           && gateway.Contains("writeFileAtomically", StringComparison.Ordinal)
           && client.Contains(".cloudfront.net", StringComparison.Ordinal),
        "manifest clients do not tightly validate CloudFront download URLs");
    Assert(client.Contains("GuardianApiBootstrapVersion", StringComparison.Ordinal)
           && client.Contains("candidate.Version == current", StringComparison.Ordinal)
           && client.Contains("installedGuardian < candidate.Version", StringComparison.Ordinal)
           && client.Contains("RequiresGuardianReconciliation", StringComparison.Ordinal),
        "release selection must offer authenticated Guardian reconciliation after a watchdog-capable Agent reaches the same version");
    Assert(agent.Contains("UseProxy = false", StringComparison.Ordinal)
           && agent.Contains("AllowAutoRedirect = false", StringComparison.Ordinal)
           && agent.Contains("CheckCertificateRevocationList = true", StringComparison.Ordinal),
        "Agent release downloader does not retain the required direct HTTPS behavior");
    Assert(hostedBootstrap.Contains("start.Environment[InvitePathEnvironmentVariable]", StringComparison.Ordinal)
           && hostedBootstrap.Contains("start.Environment[InviteKeyEnvironmentVariable]", StringComparison.Ordinal)
           && hostedBootstrap.Contains("IsPublishedBootstrap", StringComparison.Ordinal)
           && setupWindow.Contains("GetEnvironmentVariable(HostedBootstrapper.InvitePathEnvironmentVariable)", StringComparison.Ordinal)
           && setupWindow.Contains("SetEnvironmentVariable(HostedBootstrapper.InviteKeyEnvironmentVariable, null)", StringComparison.Ordinal)
           && setupWindow.Contains("new InstallCoordinator(_invite!, AppContext.BaseDirectory", StringComparison.Ordinal)
           && !setupWindow.Contains("new InstallCoordinator(_invite!, Path.GetDirectoryName(_invitePath)", StringComparison.Ordinal)
           && setupWindow.Contains("private-key-redacted", StringComparison.Ordinal)
           && setupWindow.Contains("DetailsExpander.IsExpanded = true", StringComparison.Ordinal),
        "hosted bootstrap handoff, executable-relative payload lookup, or redacted persistent Setup diagnostics regressed");
    Assert(gateway.Contains("await fetch(", StringComparison.Ordinal)
           && gateway.Contains("URL.createObjectURL(blob)", StringComparison.Ordinal)
           && gateway.Contains("buildBootstrapStarterCommand", StringComparison.Ordinal)
           && gateway.Contains("Get-AuthenticodeSignature", StringComparison.Ordinal)
           && gateway.Contains("connect-src ", StringComparison.Ordinal),
        "invitation download no longer creates a direct or hash/signature-verified compatibility bootstrap handoff");
}
static void TestTailnetSshPolicy()
{
    string? sourcePath = null;
    foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(root);
        while (directory is not null)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("src", "Taildesk.Admin", "TailnetPolicy.cs"),
                         Path.Combine("opticon", "src", "Taildesk.Admin", "TailnetPolicy.cs")
                     })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) { sourcePath = candidate; break; }
            }
            if (sourcePath is not null) break;
            directory = directory.Parent;
        }
        if (sourcePath is not null) break;
    }
    if (sourcePath is null) throw new InvalidOperationException("Taildesk.Admin TailnetPolicy.cs was not found.");
    var source = File.ReadAllText(sourcePath);
    Assert(source.Contains("\"ip\": [\"tcp:45832\"]", StringComparison.Ordinal), "runtime policy does not grant the isolated SSH port");
    Assert(source.Contains("\"tag:taildesk-managed:45832\"", StringComparison.Ordinal), "runtime policy does not test managed-device SSH denial");
    Assert(source.Contains("\"tag:taildesk-controller:45832\"", StringComparison.Ordinal), "runtime policy does not test controller SSH denial");
}
static void TestUpdateJournalPersistence()
{
    var directory = Path.Combine(Path.GetTempPath(), "opticon-update-journal-test-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "state.json");
    try
    {
        var journal = new UpdateJournal
        {
            OperationId = Guid.NewGuid(),
            Phase = UpdatePhase.Ready,
            MaintenanceBootstrap = true,
            SshWasListening = true,
            GuardianClaimedAt = DateTimeOffset.UtcNow,
            CurrentVersion = "1.0.0",
            TargetVersion = "1.1.0",
            StartedAt = DateTimeOffset.UtcNow,
            RollbackDirectory = Path.Combine(directory, "agent.rollback"),
            Message = "verified"
        };
        UpdateJournalPersistence.SaveAsync(journal, path).GetAwaiter().GetResult();
        var loaded = UpdateJournalPersistence.Load(path);
        Assert(loaded?.OperationId == journal.OperationId && loaded.Phase == UpdatePhase.Ready, "atomic update journal did not round-trip");
        Assert(loaded!.MaintenanceBootstrap, "maintenance-only Guardian state was not durable");
        Assert(loaded.ToStatus().MaintenanceBootstrap,
            "maintenance-only Guardian state was not exposed through authenticated status");
        Assert(loaded.SshWasListening && loaded.GuardianClaimedAt == journal.GuardianClaimedAt,
            "Guardian pickup and SSH lifeline requirements were not durable");
        Assert(loaded.UpdatedAt >= journal.StartedAt, "journal persistence did not stamp its durable update time");
        Assert(!loaded.ToStatus().RollbackAvailable,
            "a planned rollback path was reported as a physical rollback copy");
        Directory.CreateDirectory(loaded.RollbackDirectory);
        Assert(loaded.ToStatus().RollbackAvailable,
            "a physical rollback copy was not exposed through authenticated status");
        Directory.Delete(loaded.RollbackDirectory);
        Assert(!loaded.ToStatus().RollbackAvailable,
            "a consumed rollback copy remained exposed through authenticated status");

        var lockPath = Path.Combine(directory, "transaction.lock");
        using var lease = UpdateJournalCoordination.AcquireAsync(
            TimeSpan.FromSeconds(1), path: lockPath).GetAwaiter().GetResult();
        AssertThrows<TimeoutException>(() =>
            UpdateJournalCoordination.AcquireAsync(
                TimeSpan.FromMilliseconds(150), path: lockPath).GetAwaiter().GetResult());
    }
    finally
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }
}
static void TestUploadPolicy()
{
    var config = new AgentConfig();
    Assert(config.MaxUploadBytes >= 20L * 1024 * 1024 * 1024, "uploads are capped below 20 GiB");
    Assert(config.MaxUploadBytes == 256L * 1024 * 1024 * 1024, "default maximum upload is not the reviewed 256 GiB limit");
    Assert(config.MaxConcurrentUploads is >= 1 and <= 2, "concurrent upload bound is unsafe");
    Assert(config.MinimumFreeSpaceBytes >= 5L * 1024 * 1024 * 1024, "free-space reserve is too small");
    Assert(config.MaxUploadDurationMinutes <= 24 * 60, "upload lifetime is unbounded");
    var transfers = ReadSource("src", "Taildesk.Admin", "TransferManager.cs");
    var browser = ReadSource("src", "Taildesk.Admin", "FileManagerWindow.xaml.cs");
    var agentClient = ReadSource("src", "Taildesk.Admin", "AgentClient.cs");
    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    var transferView = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    Assert(transfers.Contains("public void Resume(TransferRow row)", StringComparison.Ordinal)
           && transfers.Contains("row.Cancellation?.Cancel()", StringComparison.Ordinal)
           && transfers.Contains("row.Id", StringComparison.Ordinal),
        "the application transfer manager must own cancellation and resumable transfer identity");
    Assert(browser.Contains("StartDownload", StringComparison.Ordinal)
           && browser.Contains("StartUpload", StringComparison.Ordinal)
           && !browser.Contains("_transfers.DownloadAsync", StringComparison.Ordinal)
           && !browser.Contains("_transfers.UploadAsync", StringComparison.Ordinal),
        "the file browser must start application-owned transfers instead of awaiting window-owned transfers");
    Assert(agentClient.Contains("RangeHeaderValue(offset, null)", StringComparison.Ordinal)
           && agentClient.Contains("files/upload-status", StringComparison.Ordinal)
           && agentClient.Contains("HttpStatusCode.NotFound", StringComparison.Ordinal)
           && agentProgram.Contains("GetUploadStatus", StringComparison.Ordinal)
           && agentProgram.Contains("UploadLegacyAsync", StringComparison.Ordinal),
        "downloads and uploads must negotiate retained byte offsets when resumed");
    Assert(transferView.Contains("Header=\"Resume\"", StringComparison.Ordinal)
           && transferView.Contains("PreviewMouseRightButtonDown", StringComparison.Ordinal),
        "the Transfers page must expose row-targeted right-click resume");
}

static void TestResumableUpload()
{
    var directory = Path.Combine(Path.GetTempPath(), "opticon-resume-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var expected = "resumable-transfer-payload"u8.ToArray();
        var partial = Path.Combine(directory, ".taildesk-upload-test.partial");
        using (var prefix = new MemoryStream(expected[..9]))
        {
            try
            {
                ResumableTransferFile.AppendToLengthAsync(
                        partial, prefix, 0, expected.Length, 1024 * 1024, CancellationToken.None)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("The deliberately incomplete upload unexpectedly completed.");
            }
            catch (IOException exception)
            {
                Assert(exception.Message.Contains("ended before", StringComparison.Ordinal),
                    "the incomplete upload failed for an unexpected reason");
            }
        }

        var offset = ResumableTransferFile.GetValidatedLength(partial, expected.Length);
        Assert(offset == 9,
            "the Agent did not retain the exact resumable offset");
        using (var remainder = new MemoryStream(expected[9..]))
        {
            ResumableTransferFile.AppendToLengthAsync(
                    partial, remainder, offset, expected.Length, 1024 * 1024, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        Assert(File.ReadAllBytes(partial).SequenceEqual(expected),
            "the resumed upload did not reproduce the original bytes");
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}
static void TestPathGuard()
{
    var temporary = Path.Combine(Path.GetTempPath(), "taildesk-selftest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var child = Path.Combine(temporary, "child");
        Directory.CreateDirectory(child);
        var guard = new PathGuard(new Dictionary<string, string> { ["test"] = temporary });
        Assert(guard.Resolve("test", "child") == Path.GetFullPath(child), "valid child did not resolve");
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", ".."));
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", "file.txt:stream", mustExist: false));
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", "C:\\Windows", mustExist: false));
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", "\\\\server\\share", mustExist: false));
    }
    finally
    {
        Directory.Delete(temporary, true);
    }
}

static void TestLocalVolumeRoots()
{
    if (!OperatingSystem.IsWindows()) return;
    var guard = new PathGuard(new Dictionary<string, string>(), includeLocalVolumes: true);
    var roots = guard.GetRoots();
    foreach (var drive in DriveInfo.GetDrives())
    {
        try
        {
            if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.NoRootDirectory) continue;
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(drive.RootDirectory.FullName));
            Assert(roots.Any(candidate =>
                    Path.TrimEndingDirectorySeparator(candidate.PathHint).Equals(expected, StringComparison.OrdinalIgnoreCase)),
                $"ready local volume {expected} was not exposed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    var systemRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(Environment.SystemDirectory)!);
    var systemVolume = roots.SingleOrDefault(candidate =>
        Path.TrimEndingDirectorySeparator(candidate.PathHint).Equals(systemRoot, StringComparison.OrdinalIgnoreCase));
    Assert(systemVolume is not null, "the Windows system volume was not exposed");
    if (systemVolume is null) return;
    Assert(systemVolume.Id.StartsWith("drive-", StringComparison.OrdinalIgnoreCase), "the local volume has no stable drive root id");
    var relativeSystemDirectory = Path.GetRelativePath(systemVolume.PathHint, Environment.SystemDirectory);
    Assert(guard.Resolve(systemVolume.Id, relativeSystemDirectory).Equals(
            Path.GetFullPath(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase),
        "a directory outside the legacy profile roots could not be resolved");
}

static void TestFileBrowserContract()
{
    var xamlPath = FindSourceFile("src", "Taildesk.Admin", "FileManagerWindow.xaml");
    var document = XDocument.Load(xamlPath);
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    var names = document.Descendants()
        .Select(element => element.Attribute(x + "Name")?.Value)
        .Where(name => name is not null)
        .ToHashSet(StringComparer.Ordinal);
    Assert(names.Contains("PathText"), "the remote directory address bar is missing");
    Assert(names.Contains("ShowThumbnailsCheck"), "the list/thumbnail toggle is missing");
    Assert(names.Contains("FileGrid") && names.Contains("ThumbnailList"), "both file browser views must be present");

    var fileGrid = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "FileGrid");
    var thumbnailList = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "ThumbnailList");
    Assert(fileGrid.Attribute("SelectionMode")?.Value == "Extended"
           && thumbnailList.Attribute("SelectionMode")?.Value == "Extended",
        "list and thumbnail views must both support Ctrl/Shift multi-selection");

    var browser = ReadSource("src", "Taildesk.Admin", "FileManagerWindow.xaml.cs");
    Assert(browser.Contains("SelectedItems", StringComparison.Ordinal)
           && browser.Contains("PlanFolderDownloadsAsync", StringComparison.Ordinal)
           && browser.Contains("_transfers.StartDownload", StringComparison.Ordinal)
           && browser.Contains("Directory.CreateDirectory(localDirectory)", StringComparison.Ordinal)
           && browser.IndexOf("foreach (var download in batch.Downloads)", StringComparison.Ordinal)
              > browser.IndexOf("PlanFolderDownloadsAsync", StringComparison.Ordinal),
        "multi-download must queue selected files and recursively preserve selected folder trees");

    var transfers = ReadSource("src", "Taildesk.Admin", "TransferManager.cs");
    Assert(transfers.Contains("MaximumConcurrentTransfers", StringComparison.Ordinal)
           && transfers.Contains("_transferSlots.WaitAsync", StringComparison.Ordinal),
        "large multi-download batches must use bounded transfer concurrency");

    var agentConfig = ReadSource("src", "Taildesk.Shared", "AgentConfiguration.cs");
    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    Assert(agentConfig.Contains("ExposeAllLocalVolumes", StringComparison.Ordinal)
           && agentProgram.Contains("new PathGuard(config.SharedRoots, config.ExposeAllLocalVolumes)", StringComparison.Ordinal),
        "the Agent must expose every ready local volume to the location dropdown");
}

static void TestDeviceRenameContract()
{
    var xaml = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    Assert(xaml.Contains("Header=\"Rename device\"", StringComparison.Ordinal)
           && xaml.Contains("PreviewMouseRightButtonDown=\"DeviceGrid_PreviewMouseRightButtonDown\"", StringComparison.Ordinal)
           && xaml.Contains("Click=\"RenameDevice_Click\"", StringComparison.Ordinal),
        "the device grid must select a right-clicked row and expose Rename device");

    var window = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml.cs");
    Assert(window.Contains("_viewModel.RenameDeviceAsync(device, prompt.Value)", StringComparison.Ordinal),
        "the Rename device menu action must submit the selected device and entered name");

    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    var renameStart = viewModel.IndexOf("public async Task RenameDeviceAsync", StringComparison.Ordinal);
    var renameEnd = viewModel.IndexOf("public async Task ChangeRoleAsync", renameStart, StringComparison.Ordinal);
    Assert(renameStart >= 0 && renameEnd > renameStart, "the persisted device rename operation is missing");
    var rename = viewModel[renameStart..renameEnd];
    Assert(rename.Contains("registered.Name = normalized", StringComparison.Ordinal)
           && rename.Contains("await _state.SaveAsync(cancellationToken)", StringComparison.Ordinal)
           && rename.Contains("ReplaceDevices(Config.Devices)", StringComparison.Ordinal),
        "device rename must update, persist, and refresh the primary registry");
}

static void TestExitNodeApprovalRoutes()
{
    Assert(HeadscaleRoutes.ExitNode.Count == 2, "exit-node approval must contain exactly two default routes");
    Assert(HeadscaleRoutes.ExitNode.Contains("0.0.0.0/0", StringComparer.Ordinal), "IPv4 default route is missing");
    Assert(HeadscaleRoutes.ExitNode.Contains("::/0", StringComparer.Ordinal), "IPv6 default route is missing");
    Assert(!HeadscaleRoutes.ExitNode.Any(string.IsNullOrWhiteSpace), "exit-node approval contains an empty route");
}

static void TestDirectHttpTransport()
{
    using var handler = DirectHttp.CreateHandler();
    Assert(!handler.UseProxy, "private HTTP transport inherited the system proxy");
    Assert(!handler.AllowAutoRedirect, "private HTTP transport follows redirects");
}

static void TestEnrollmentReplayPolicy()
{
    var secret = SecurityHelpers.CreateToken();
    var device = new DeviceRecord
    {
        TailnetDeviceId = "node-42",
        TailscaleIp = "100.90.0.42",
        HostName = "WORKSHOP",
        DnsName = "workshop.example.ts.net",
        OperatingSystem = "Windows",
        AgentVersion = "1.2.3"
    };
    var invite = new InviteRecord
    {
        Id = Guid.NewGuid(),
        InviteSecretHash = SecurityHelpers.HashToken(secret),
        RedeemedAt = DateTimeOffset.UtcNow,
        EnrolledDeviceId = device.Id
    };
    var request = new EnrollmentRequest
    {
        InviteId = invite.Id,
        InviteSecret = secret,
        TailnetDeviceId = device.TailnetDeviceId,
        TailscaleIp = device.TailscaleIp,
        HostName = device.HostName,
        DnsName = device.DnsName,
        OperatingSystem = device.OperatingSystem,
        AgentVersion = device.AgentVersion
    };

    Assert(EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, device, request), "the exact committed retry was rejected");
    request.TailscaleIp = "100.90.0.43";
    Assert(!EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, device, request), "a different Tailscale address was accepted");
    request.TailscaleIp = device.TailscaleIp;
    request.InviteSecret = SecurityHelpers.CreateToken();
    Assert(!EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, device, request), "a different invitation secret was accepted");
}

static void TestCredentialRotationState()
{
    var oldToken = SecurityHelpers.CreateToken();
    var newToken = SecurityHelpers.CreateToken();
    var password = SecurityHelpers.CreateHumanPassword();
    var operationId = Guid.NewGuid();
    var started = DateTimeOffset.UtcNow;
    var config = new AgentConfig { AgentTokenHash = SecurityHelpers.HashToken(oldToken) };

    CredentialRotationState.Begin(config, operationId, newToken, password, started);
    Assert(CredentialRotationState.IsExactAppliedRotation(config, operationId, newToken, password), "the applied operation was not replayable");
    Assert(CredentialRotationState.CanAuthenticate(config, newToken, false, started), "the new token was not active");
    Assert(CredentialRotationState.CanAuthenticate(config, oldToken, true, started), "the prior token could not retry the exact rotation");
    Assert(!CredentialRotationState.CanAuthenticate(config, oldToken, false, started), "the prior token retained general API access");
    Assert(!CredentialRotationState.CanAuthenticate(
            config, oldToken, true, started.Add(CredentialRotationState.PreviousTokenGracePeriod).AddSeconds(1)),
        "the prior token survived its bounded replay window");
    Assert(!CredentialRotationState.IsExactAppliedRotation(config, operationId, newToken, password + "x"), "a changed retry payload was accepted");

    var durable = JsonSerializer.Deserialize<AgentConfig>(
        JsonSerializer.Serialize(config, JsonDefaults.Options), JsonDefaults.Options)
        ?? throw new InvalidDataException("credential rotation state did not deserialize");
    Assert(CredentialRotationState.IsExactAppliedRotation(durable, operationId, newToken, password), "durable pending rotation was not recoverable");
    CredentialRotationState.Commit(durable, operationId);
    CredentialRotationState.Commit(durable, operationId);
    Assert(!CredentialRotationState.CanAuthenticate(durable, oldToken, true, started), "the prior token survived commit");
    Assert(CredentialRotationState.CanAuthenticate(durable, newToken, false, started), "commit retired the new token");
}

static void TestDurableCollectionMutation()
{
    var values = new List<string>();
    using var gate = new SemaphoreSlim(1, 1);
    AssertThrows<InvalidOperationException>(() => DurableCollectionMutation.AddAsync(
        values, "ghost", gate, _ => throw new InvalidOperationException("simulated persistence failure")).GetAwaiter().GetResult());
    Assert(values.Count == 0, "a failed persistence operation left a ghost record in memory");
    DurableCollectionMutation.AddAsync(values, "durable", gate, _ => Task.CompletedTask).GetAwaiter().GetResult();
    Assert(values.SequenceEqual(["durable"]), "a successful durable collection mutation was lost");
}

static void TestPathLease()
{
    if (!OperatingSystem.IsWindows()) return;
    var temporary = Path.Combine(Path.GetTempPath(), "taildesk-path-lease-" + Guid.NewGuid().ToString("N"));
    var child = Path.Combine(temporary, "child");
    var moved = Path.Combine(temporary, "moved");
    Directory.CreateDirectory(child);
    File.WriteAllText(Path.Combine(child, "proof.txt"), "guarded");
    try
    {
        var guard = new PathGuard(new Dictionary<string, string> { ["test"] = temporary });
        using (var lease = guard.Acquire("test", "child"))
        {
            Assert(lease.IsDirectory, "directory lease did not identify its target");
            Directory.Move(child, moved);
            try { Directory.CreateDirectory(child); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            using var created = lease.CreateFile("leased.txt");
            using (var writer = new StreamWriter(created.OpenWriteStream(), leaveOpen: false))
                writer.Write("original-directory");
            created.RenameTo(lease, "promoted.txt");
            Assert(File.Exists(Path.Combine(moved, "promoted.txt")), "relative create or rename escaped to the replacement pathname");
            Assert(!File.Exists(Path.Combine(child, "promoted.txt")), "relative create or rename used the replacement pathname");
            Assert(lease.Enumerate().Any(entry => entry.Name == "promoted.txt" && entry.Size > 0),
                "handle-based enumeration did not observe the promoted file");

            using (var partial = lease.OpenOrCreateFile(".taildesk-upload-test.partial"))
            {
                using var output = partial.OpenWriteStream();
                output.Write("first"u8);
            }
            using (var resumed = lease.OpenOrCreateFile(".taildesk-upload-test.partial"))
            {
                Assert(resumed.Length == 5, "guarded resumable file lost its retained byte offset");
                using (var output = resumed.OpenWriteStream())
                {
                    output.Position = resumed.Length;
                    output.Write("-second"u8);
                }
                resumed.RenameTo(lease, "resumed.txt");
            }
            Assert(File.ReadAllText(Path.Combine(moved, "resumed.txt")) == "first-second",
                "guarded resumable append or promotion changed the payload");
        }
        if (Directory.Exists(child)) Directory.Delete(child, true);
        Directory.Move(moved, child);

        var readParentMoved = false;
        using (var stream = guard.Acquire("test", "child\\proof.txt", readFile: true).OpenReadStream())
        {
            try { Directory.Move(child, moved); readParentMoved = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (readParentMoved)
            {
                try
                {
                    Directory.CreateDirectory(child);
                    File.WriteAllText(Path.Combine(child, "proof.txt"), "replacement");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert(reader.ReadToEnd() == "guarded", "guarded read returned the wrong file");
        }
        if (readParentMoved)
        {
            if (Directory.Exists(child)) Directory.Delete(child, true);
            Directory.Move(moved, child);
        }

        using (var deleteLease = guard.Acquire("test", "child\\proof.txt", delete: true))
            deleteLease.Delete();
        Assert(!File.Exists(Path.Combine(child, "proof.txt")), "handle-based deletion did not remove the verified file");
    }
    finally
    {
        try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
    }
}

static void TestDpapi()
{
    if (!OperatingSystem.IsWindows()) return;
    var secret = SecurityHelpers.CreateToken();
    var user = SecretProtector.Protect(secret, SecretScope.CurrentUser);
    var machine = SecretProtector.Protect(secret, SecretScope.LocalMachine);
    Assert(SecretProtector.Unprotect(user, SecretScope.CurrentUser) == secret, "current-user DPAPI failed");
    Assert(SecretProtector.Unprotect(machine, SecretScope.LocalMachine) == secret, "machine DPAPI failed");
}

static void TestWpfStyleTemplateTargets()
{
    string? xamlPath = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Taildesk.Admin", "App.xaml");
            if (File.Exists(candidate))
            {
                xamlPath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        if (xamlPath is not null) break;
    }
    if (xamlPath is null) throw new InvalidOperationException("Taildesk.Admin App.xaml was not found.");
    var document = XDocument.Load(xamlPath);
    foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "Style"))
    {
        var styleTarget = NormalizeTargetType(style.Attribute("TargetType")?.Value ?? string.Empty);
        if (styleTarget.Length == 0) continue;
        foreach (var template in style.Descendants().Where(element => element.Name.LocalName == "ControlTemplate"))
        {
            var templateTarget = NormalizeTargetType(template.Attribute("TargetType")?.Value ?? string.Empty);
            if (templateTarget.Length == 0) continue;
            Assert(templateTarget == styleTarget, $"{styleTarget} style contains a {templateTarget} control template");
        }
    }
}

static void TestWpfContrastContract()
{
    var adminPath = FindSourceFile("src", "Taildesk.Admin", "App.xaml");
    var setupPath = FindSourceFile("src", "Taildesk.Setup", "App.xaml");
    var admin = XDocument.Load(adminPath);
    var setup = XDocument.Load(setupPath);
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

    static string TargetType(XElement style) =>
        NormalizeTargetType(style.Attribute("TargetType")?.Value ?? string.Empty);
    static bool HasDirectSetter(XElement style, string property) =>
        style.Elements().Any(element => element.Name.LocalName == "Setter"
                                        && element.Attribute("Property")?.Value == property);
    static XElement RequireImplicitStyle(XDocument document, XNamespace xaml, string targetType)
    {
        var style = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Style"
            && TargetType(element) == targetType
            && element.Attribute(xaml + "Key") is null);
        return style ?? throw new InvalidOperationException($"{targetType} has no implicit application style");
    }
    static void RequireColorPair(XElement style, string label)
    {
        Assert(HasDirectSetter(style, "Background") && HasDirectSetter(style, "Foreground"),
            $"{label} must set both Background and Foreground");
    }
    static void RequireTriggerColorPair(XElement style, string property, string value, string label)
    {
        var trigger = style.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Trigger"
            && element.Attribute("Property")?.Value == property
            && element.Attribute("Value")?.Value == value);
        Assert(trigger is not null
               && HasDirectSetter(trigger, "Background")
               && HasDirectSetter(trigger, "Foreground"),
            $"{label} must replace both Background and Foreground");
    }

    var adminText = File.ReadAllText(adminPath);
    var setupText = File.ReadAllText(setupPath);
    Assert(!adminText.Contains("SystemColors", StringComparison.Ordinal)
           && !setupText.Contains("SystemColors", StringComparison.Ordinal),
        "fixed dark WPF canvases must not consume independently changing Windows theme colors");

    foreach (var targetType in new[]
             {
                 "Button", "TextBox", "PasswordBox", "ComboBox", "CheckBox", "ListBox",
                 "ListBoxItem", "ContextMenu", "MenuItem", "DataGrid", "DataGridColumnHeader",
             })
    {
        var style = RequireImplicitStyle(admin, x, targetType);
        RequireColorPair(style, targetType);
    }
    foreach (var targetType in new[] { "DataGridCell", "DataGridRow" })
    {
        Assert(HasDirectSetter(RequireImplicitStyle(admin, x, targetType), "Foreground"),
            $"{targetType} must set Foreground; its background is supplied by DataGrid row/alternation states");
    }

    foreach (var templatedType in new[] { "Button", "ComboBox", "CheckBox" })
    {
        var style = RequireImplicitStyle(admin, x, templatedType);
        Assert(style.Descendants().Any(element =>
                element.Name.LocalName == "ControlTemplate" && TargetType(element) == templatedType),
            $"{templatedType} must own its template instead of inheriting Windows theme text colors");
    }

    var primaryButton = admin.Descendants().FirstOrDefault(element =>
        element.Name.LocalName == "Style" && element.Attribute(x + "Key")?.Value == "PrimaryButton")
        ?? throw new InvalidOperationException("PrimaryButton style was not found");
    RequireTriggerColorPair(primaryButton, "IsMouseOver", "True", "PrimaryButton hover");
    RequireTriggerColorPair(primaryButton, "IsPressed", "True", "PrimaryButton pressed");
    RequireTriggerColorPair(primaryButton, "IsEnabled", "False", "PrimaryButton disabled");

    var pairedStates = new[]
    {
        (Type: "ListBoxItem", Property: "IsMouseOver", Value: "True"),
        (Type: "ListBoxItem", Property: "IsSelected", Value: "True"),
        (Type: "MenuItem", Property: "IsHighlighted", Value: "True"),
        (Type: "DataGridColumnHeader", Property: "IsMouseOver", Value: "True"),
        (Type: "DataGridColumnHeader", Property: "IsPressed", Value: "True"),
        (Type: "DataGridCell", Property: "IsSelected", Value: "True"),
        (Type: "DataGridRow", Property: "IsSelected", Value: "True")
    };
    foreach (var state in pairedStates)
        RequireTriggerColorPair(RequireImplicitStyle(admin, x, state.Type), state.Property, state.Value,
            $"{state.Type} {state.Property}");

    var setupButton = RequireImplicitStyle(setup, x, "Button");
    RequireColorPair(setupButton, "Setup Button");
    Assert(setupButton.Descendants().Any(element =>
            element.Name.LocalName == "ContentPresenter"
            && element.Attributes().Any(attribute => attribute.Name.LocalName is "Foreground" or "TextElement.Foreground")),
        "Setup button content must bind the explicit foreground into its template");
    RequireTriggerColorPair(setupButton, "IsMouseOver", "True", "Setup button hover");
    RequireTriggerColorPair(setupButton, "IsPressed", "True", "Setup button pressed");

    var brushes = admin.Descendants()
        .Where(element => element.Name.LocalName == "SolidColorBrush" && element.Attribute(x + "Key") is not null)
        .ToDictionary(element => element.Attribute(x + "Key")!.Value,
            element => element.Attribute("Color")?.Value ?? string.Empty, StringComparer.Ordinal);
    foreach (var pair in new[]
             {
                 (Foreground: "TextBrush", Background: "WindowBrush"),
                 (Foreground: "TextBrush", Background: "RailBrush"),
                 (Foreground: "TextBrush", Background: "PanelBrush"),
                 (Foreground: "TextBrush", Background: "PanelAltBrush"),
                 (Foreground: "TextBrush", Background: "ControlBrush"),
                 (Foreground: "TextBrush", Background: "ControlHoverBrush"),
                 (Foreground: "TextBrush", Background: "ControlPressedBrush"),
                 (Foreground: "TextBrush", Background: "InputBrush"),
                 (Foreground: "TextBrush", Background: "SelectionBrush"),
                 (Foreground: "MutedBrush", Background: "WindowBrush"),
                 (Foreground: "MutedBrush", Background: "PanelBrush"),
                 (Foreground: "OnAccentBrush", Background: "AccentBrush"),
                 (Foreground: "OnAccentBrush", Background: "AccentHoverBrush"),
                 (Foreground: "OnAccentBrush", Background: "AccentPressedBrush"),
                 (Foreground: "OnLightSurfaceBrush", Background: "LightSurfaceBrush"),
                 (Foreground: "OnLightSurfaceBrush", Background: "LightSurfaceHoverBrush")
             })
    {
        var ratio = ContrastRatio(brushes[pair.Foreground], brushes[pair.Background]);
        Assert(ratio >= 4.5,
            $"{pair.Foreground} on {pair.Background} has only {ratio:F2}:1 contrast");
    }
}

static string FindSourceFile(params string[] parts)
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
    }
    throw new InvalidOperationException($"Source file was not found: {Path.Combine(parts)}");
}

static double ContrastRatio(string foreground, string background)
{
    static double Luminance(string color)
    {
        var hex = color.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        Assert(hex.Length == 6, $"unsupported color value: {color}");
        static double Channel(string value)
        {
            var component = Convert.ToInt32(value, 16) / 255d;
            return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(hex[..2]) + 0.7152 * Channel(hex[2..4]) + 0.0722 * Channel(hex[4..6]);
    }

    var foregroundLuminance = Luminance(foreground);
    var backgroundLuminance = Luminance(background);
    return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05)
           / (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
}

static void TestOpenSshRecoveryDesign()
{
    DirectoryInfo? root = new(AppContext.BaseDirectory);
    while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Taildesk.Agent")))
        root = root.Parent;
    if (root is null) throw new InvalidOperationException("Opticon source root was not found.");

    string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    var launcher = ReadSource("src", "Taildesk.Admin", "SshSessionLauncher.cs");
    Assert(launcher.Contains("System32", StringComparison.Ordinal)
           && launcher.Contains("OpenSSH", StringComparison.Ordinal),
        "SSH launcher must resolve the Windows System32 OpenSSH client");
    Assert(!launcher.Contains("FindOnPath", StringComparison.Ordinal),
        "SSH launcher must not execute a PATH-resolved client");
    Assert(launcher.Contains("WorkingDirectory = Path.GetDirectoryName(privateKeyPath)", StringComparison.Ordinal),
        "interactive SSH must not inherit and lock Opticon's installed command-center directory");
    Assert(launcher.Contains("new LoopbackSshRelay(grant.Host, DedicatedPort)", StringComparison.Ordinal)
           && launcher.Contains("new TcpListener(IPAddress.Loopback, 0)", StringComparison.Ordinal)
           && launcher.Contains("target.ConnectAsync(_targetHost, _targetPort", StringComparison.Ordinal)
           && launcher.Contains("connectionHost ?? grant.Host", StringComparison.Ordinal),
        "SSH must traverse a per-lease loopback relay so endpoint VPN policy cannot block the hardened child client from the Tailscale peer");
    var updateCoordinator = ReadSource("src", "Taildesk.Admin", "RemoteDeviceUpdateCoordinator.cs");
    Assert(updateCoordinator.Contains("GetUpdateStatusAsync(device, agentToken", StringComparison.Ordinal)
           && updateCoordinator.Contains("Update failed safely:", StringComparison.Ordinal),
        "guarded updates must surface the remote journal while preparation is running and after a safe failure");
    var mainWindow = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml.cs");
    Assert(mainWindow.Contains("RunMaintenanceBootstrapAsync", StringComparison.Ordinal)
           && mainWindow.Contains("$ph.UseProxy=$false", StringComparison.Ordinal)
           && mainWindow.Contains("requiresAttendedMaintenance", StringComparison.Ordinal)
           && mainWindow.Contains("requires update guardian", StringComparison.Ordinal),
        "legacy download and Guardian-contract failures must offer a direct, pinned, externally observed maintenance recovery path");
    var adminApp = ReadSource("src", "Taildesk.Admin", "App.xaml.cs");
    var incrementalRebuild = File.ReadAllText(Path.Combine(root.FullName, "..", "Taildesk", "rebuild-if-source-changed.ps1"));
    Assert(adminApp.Contains("Taildesk.Admin.ShutdownForUpdate", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Request-InstalledOpticonShutdown", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Taildesk.Admin.ShutdownForUpdate", StringComparison.Ordinal),
        "source-triggered controller rebuilds must request a graceful Command Center shutdown before swapping the installed payload");
    var agentUpdateDownload = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    Assert(agentUpdateDownload.Contains("UseProxy = false", StringComparison.Ordinal)
           && agentUpdateDownload.Contains("Last error:", StringComparison.Ordinal),
        "Agent artifact downloads must bypass ambient proxies and preserve bounded network diagnostics");
    var agentDownloadComplete = agentUpdateDownload.IndexOf("offset == expectedSize", StringComparison.Ordinal);
    var agentDownloadRange = agentUpdateDownload.IndexOf("new RangeHeaderValue(offset", StringComparison.Ordinal);
    var agentDownloadFlush = agentUpdateDownload.IndexOf("await output.FlushAsync", StringComparison.Ordinal);
    var agentDownloadMove = agentUpdateDownload.IndexOf("File.Move(partial, destination", agentDownloadFlush, StringComparison.Ordinal);
    var agentDownloadScopeEnd = agentUpdateDownload.LastIndexOf('}', agentDownloadMove);
    Assert(agentDownloadComplete >= 0 && agentDownloadComplete < agentDownloadRange
           && agentUpdateDownload.Contains("RequestedRangeNotSatisfiable", StringComparison.Ordinal)
           && agentDownloadFlush >= 0 && agentDownloadScopeEnd > agentDownloadFlush
           && agentDownloadMove > agentDownloadScopeEnd,
        "Agent resume must promote a complete partial without an EOF range and dispose its stream before the atomic move");
    var administratorProofIndex = launcher.IndexOf("await VerifyRemoteAdministratorAsync", StringComparison.Ordinal);
    var requestedCommandIndex = launcher.IndexOf("var remoteCommand = options.PowerShellEncodedCommand", StringComparison.Ordinal);
    Assert(administratorProofIndex >= 0 && requestedCommandIndex > administratorProofIndex
           && launcher.Contains("attestation.AdministrativeCapability", StringComparison.Ordinal)
           && launcher.Contains("attestation.IntegrityRid is < 0x3000 or >= 0x4000", StringComparison.Ordinal),
        "every SSH shell or command must pass the signed full-administrator attestation before launch");
    Assert(launcher.Contains("AddressFamily.InterNetwork", StringComparison.Ordinal),
        "SSH launcher must reject IPv6 before comparing target addresses");
    var validationIndex = launcher.IndexOf("ValidateCommandOptions(options)", StringComparison.Ordinal);
    var staleCleanupIndex = launcher.IndexOf("CleanupStaleSessionDirectories()", StringComparison.Ordinal);
    Assert(validationIndex >= 0 && staleCleanupIndex > validationIndex
           && launcher.Contains("options.RemoteCommand.Length > MaximumRemoteCommandCharacters", StringComparison.Ordinal)
           && launcher.Contains("MaximumEncodedPowerShellCharacters = 5600", StringComparison.Ordinal)
           && launcher.Contains("options.RemoteCommand.Contains('\\0')", StringComparison.Ordinal),
        "raw and encoded SSH commands must be bounded and reject NUL before key creation or remote provisioning");

    var agentClient = ReadSource("src", "Taildesk.Admin", "AgentClient.cs");
    Assert(agentClient.Contains("UseProxy = false", StringComparison.Ordinal)
           && agentClient.Contains("AllowAutoRedirect = false", StringComparison.Ordinal),
        "authenticated Agent requests must bypass proxies and refuse redirects");
    var downloadFlush = agentClient.IndexOf("await output.FlushAsync", StringComparison.Ordinal);
    var downloadMove = agentClient.LastIndexOf("File.Move(temporary, localPath", StringComparison.Ordinal);
    var downloadStreamScopeEnd = agentClient.LastIndexOf('}', downloadMove);
    Assert(downloadFlush >= 0 && downloadStreamScopeEnd > downloadFlush && downloadMove > downloadStreamScopeEnd,
        "downloads must flush and dispose the exclusive partial-file stream before atomically promoting it");

    var cli = ReadSource("src", "Taildesk.Cli", "Program.cs");
    Assert(cli.Contains("Volatile.Read(ref interactiveSshAttached)", StringComparison.Ordinal)
           && cli.Contains("_setInteractiveSshAttached(true)", StringComparison.Ordinal)
           && cli.Contains("_setInteractiveSshAttached(false)", StringComparison.Ordinal),
        "the CLI must cancel preflight but deliver Ctrl+C to an attached interactive ssh.exe");

    var systemHealth = ReadSource("src", "Taildesk.Admin", "SystemHealthChecker.cs");
    var nordPowerShell = ReadSource("scripts", "Configure-NordTailscaleSplit.ps1");
    var nordPython = ReadSource("scripts", "Configure-NordTailscaleSplit.py");
    Assert(systemHealth.Contains("\"Admin\", \"Cli\", \"opticon.exe\"", StringComparison.Ordinal)
           && systemHealth.Contains("\"System32\", \"OpenSSH\", \"ssh.exe\"", StringComparison.Ordinal)
           && nordPowerShell.Contains(@"Admin\Cli\opticon.exe", StringComparison.Ordinal)
           && nordPowerShell.Contains(@"System32\OpenSSH\ssh.exe", StringComparison.Ordinal)
           && nordPython.Contains(@"Admin\Cli\opticon.exe", StringComparison.Ordinal)
           && nordPython.Contains("\"System32\", \"OpenSSH\", \"ssh.exe\"", StringComparison.Ordinal),
        "NordVPN split tunneling and drift checks must include the Opticon CLI and exact Windows OpenSSH client");

    var manager = ReadSource("src", "Taildesk.Agent", "SshAccessManager.cs");
    foreach (var unsupported in new[]
             {
                 "\"PidFile ", "\"KbdInteractiveAuthentication ", "\"StrictModes ",
                 "\"X11Forwarding ", "\"PermitTunnel ", "\"PermitUserEnvironment ", "\"PermitUserRC "
             })
        Assert(!manager.Contains(unsupported, StringComparison.Ordinal), $"sshd_config contains unsupported {unsupported}");
    Assert(manager.Contains("AccountName.ToLowerInvariant()", StringComparison.Ordinal),
        "Windows AllowUsers account must be lowercase");
    Assert(manager.Contains("AuthorizedKeysFile \\\"", StringComparison.Ordinal)
           && !manager.Contains("Match Group", StringComparison.OrdinalIgnoreCase)
           && !manager.Contains("administrators_authorized_keys", StringComparison.OrdinalIgnoreCase),
        "isolated sshd must use its global absolute authorized_keys file without the stock Administrators Match override");
    Assert(manager.Contains("*S-1-5-18:F", StringComparison.Ordinal)
           && manager.Contains("*S-1-5-32-544:F", StringComparison.Ordinal),
        "administrator authorized_keys ACL must allow only SYSTEM and built-in Administrators");
    Assert(manager.Contains("RestrictDaemonReadablePathAsync", StringComparison.Ordinal)
           && manager.Contains("/remove:g", StringComparison.Ordinal),
        "Agent SSH preflight must remove legacy named daemon ACEs from host-key inputs");
    Assert(manager.Contains("RequireSystemOpenSshExecutable", StringComparison.Ordinal)
           && !manager.Contains("FindOnPath", StringComparison.Ordinal),
        "SYSTEM SSH binaries must use exact System32 paths");
    Assert(manager.Contains("_schtasksPath", StringComparison.Ordinal)
           && manager.Contains("_netshPath", StringComparison.Ordinal)
           && manager.Contains("_icaclsPath", StringComparison.Ordinal)
           && manager.Contains("NetLocalGroupDelMembers", StringComparison.Ordinal)
           && manager.Contains("ErrorMemberNotInAlias", StringComparison.Ordinal),
        "SYSTEM helpers must use exact paths and the idle SSH account must leave Administrators");
    Assert(manager.Contains("ReadSupervisorFailureAsync", StringComparison.Ordinal)
           && manager.Contains("File.Delete(_failurePath)", StringComparison.Ordinal)
           && manager.Contains("could not start:", StringComparison.Ordinal),
        "Agent SSH provisioning must clear stale diagnostics and surface a new supervisor failure immediately");
    var agentApiProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    Assert(agentApiProgram.Contains("catch (System.ComponentModel.Win32Exception", StringComparison.Ordinal)
           && agentApiProgram.Contains("catch (AggregateException", StringComparison.Ordinal)
           && agentApiProgram.Contains("Unexpected Agent failure", StringComparison.Ordinal),
        "the Agent API must serialize bounded detail for every SSH/Windows failure class");

    var setup = ReadSource("src", "Taildesk.Setup", "InstallerServices.cs");
    Assert(setup.Contains("OpenSSH.Server~~~~0.0.1.0", StringComparison.Ordinal),
        "Setup must preinstall OpenSSH Server while normal control is healthy");
    Assert(setup.Contains("OpenSSH.Client~~~~0.0.1.0", StringComparison.Ordinal),
        "controller-capable Setup must preinstall OpenSSH Client");
    Assert(setup.Contains("internal static async Task EnsureOpenSshServerCapabilityAsync", StringComparison.Ordinal)
           && setup.Contains("internal static async Task EnsureOpenSshClientCapabilityAsync", StringComparison.Ordinal),
        "maintenance mode must be able to invoke both idempotent OpenSSH preflights");

    var adminXaml = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    var remoteButton = adminXaml.IndexOf("Content=\"Remote into\"", StringComparison.Ordinal);
    var sshButton = adminXaml.IndexOf("Content=\"Open SSH\"", StringComparison.Ordinal);
    var browseButton = adminXaml.IndexOf("Content=\"Browse files\"", StringComparison.Ordinal);
    Assert(remoteButton >= 0 && remoteButton < sshButton && sshButton < browseButton,
        "Open SSH must be immediately next to Remote into on the Devices page");

    var adminWindow = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml.cs");
    Assert(adminWindow.Contains("OpenSsh_Click", StringComparison.Ordinal)
           && adminWindow.Contains("_viewModel.LaunchSshAsync(device)", StringComparison.Ordinal),
        "the Devices-page Open SSH button must invoke the selected-device launcher");

    Assert(setup.Contains("RemoteAdministrationProtocol.GuardianWatchdogTaskName", StringComparison.Ordinal)
           && setup.Contains("RemoteAdministrationProtocol.GuardianWatchdogArgument", StringComparison.Ordinal)
           && setup.Contains("RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion", StringComparison.Ordinal)
           && setup.Contains("\"MINUTE\", \"/MO\", \"1\"", StringComparison.Ordinal)
           && setup.Contains("RequireInstalledGuardianWatchdogCompatibilityAsync", StringComparison.Ordinal),
        "fresh Setup must prove Guardian compatibility and install the minute watchdog before enrollment completes");
    var setupGuardianPreflight = setup.IndexOf("await InstallGuardianAsync(guardianPayload", StringComparison.Ordinal);
    Assert(setupGuardianPreflight >= 0
           && setupGuardianPreflight < setup.IndexOf("await EnsureOpenSshServerCapabilityAsync", StringComparison.Ordinal),
        "fresh Setup must prove Guardian compatibility before changing recovery or network state");
    Assert(setup.Contains("SupportsGuardianWatchdog(installedVersion)", StringComparison.Ordinal)
           && !setup.Contains("if (installedVersion < sourceVersion)", StringComparison.Ordinal),
        "fresh Setup must verify the installed Guardian against the watchdog contract after attended maintenance");
    var stableGuardianMaintenance = ReadSource("src", "Taildesk.Shared", "StableGuardianMaintenance.cs");
    Assert(stableGuardianMaintenance.Contains("UpdateJournalCoordination.AcquireAsync", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("InvitationSigning.VerifyAuthenticodeAsync", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("File.Replace(staged, installed, backup", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("File.Replace(backup, installedExecutable, failed", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("GuardianWatchdogArgument", StringComparison.Ordinal)
           && stableGuardianMaintenance.IndexOf("GuardianWatchdogArgument", StringComparison.Ordinal)
              < stableGuardianMaintenance.IndexOf("DeleteWithRetryAsync(backup", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("RequireRecognizedInstalledFiles", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("Guid.TryParseExact", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("FilesMatchAsync", StringComparison.Ordinal),
        "attended Setup must atomically reconcile and roll back a signed stable Guardian while cleaning only recognized transaction residue");
    var setupWatchdogSettings = setup[setup.IndexOf("var watchdogSettings", StringComparison.Ordinal)..setup.IndexOf("var guardianTaskSettings", StringComparison.Ordinal)];
    Assert(!setupWatchdogSettings.Contains("StartWhenAvailable", StringComparison.Ordinal),
        "the recurring watchdog must not queue missed StartWhenAvailable runs");

    var maintenance = ReadSource("src", "Taildesk.Setup", "MaintenanceBootstrapCoordinator.cs");
    Assert(maintenance.Contains("target == current", StringComparison.Ordinal)
           && maintenance.Contains("Maintenance requires a newer Agent or Guardian", StringComparison.Ordinal),
        "attended maintenance must permit a same-release Agent transaction only to repair an older Guardian");
    Assert(maintenance.Contains("Environment.ProcessPath", StringComparison.Ordinal)
           && maintenance.Contains("VerifyAuthenticodeAsync(setupExecutable", StringComparison.Ordinal)
           && maintenance.Contains("setupVersion.Equals", StringComparison.Ordinal),
        "maintenance must pin its running Setup and bind it to the signed release version");
    Assert(maintenance.Contains("MaintenanceExpectedTarget", StringComparison.Ordinal)
           && maintenance.Contains("[\"status\", \"--json\"]", StringComparison.Ordinal)
           && maintenance.Contains("Environment.SpecialFolder.ProgramFiles", StringComparison.Ordinal)
           && maintenance.Contains("expected.TailnetDeviceId", StringComparison.Ordinal)
           && maintenance.Contains("expected.TailscaleIp", StringComparison.Ordinal),
        "maintenance must bind to the copied Tailnet node and exact Tailscale IPv4 before mutation");
    Assert(maintenance.Contains("MaintenanceBootstrap = true", StringComparison.Ordinal)
           && maintenance.Contains("UpdateJournalCoordination.AcquireAsync", StringComparison.Ordinal),
        "legacy preflight bypass must be local-only and journal replacement must be coordinated");
    Assert(maintenance.Contains("EnsureRecoveryLifelines(config.BindAddress)", StringComparison.Ordinal)
           && maintenance.Contains("EnsureOpenSshServerCapabilityAsync", StringComparison.Ordinal),
        "maintenance must establish recovery lifelines before activation");
    Assert(maintenance.Contains("LoadOrCreateSidecarAsync", StringComparison.Ordinal)
           && maintenance.Contains("RequireInstalledGuardianCompatibilityAsync", StringComparison.Ordinal)
           && maintenance.Contains("Directory.EnumerateFiles(installedRoot", StringComparison.Ordinal)
           && maintenance.Contains("declaration.Sha256", StringComparison.Ordinal)
           && !maintenance.Contains("configStore.SaveAsync", StringComparison.Ordinal)
           && !maintenance.Contains("SaveAsync(config", StringComparison.Ordinal),
        "maintenance must use the protected update-health sidecar without rewriting agent.json");
    Assert(maintenance.Contains("Guid.TryParseExact(operationText, \"N\"", StringComparison.Ordinal)
           && maintenance.Contains("var operationId = _expectedTarget.OperationId", StringComparison.Ordinal),
        "Setup must strictly parse and journal the exact command-center operation ID");
    Assert(maintenance.Contains("Replacement Agent protected health sample", StringComparison.Ordinal)
           && maintenance.Contains("ObserveCandidateAndWaitForExternalCommitAsync", StringComparison.Ordinal)
           && !maintenance.Contains("UpdateJournalPersistence.RequestCommitAsync", StringComparison.Ordinal),
        "Setup must keep three protected local samples but have no maintenance commit authority");
    Assert(maintenance.Contains("RemoteAdministrationProtocol.GuardianWatchdogTaskName", StringComparison.Ordinal)
           && maintenance.Contains("RemoteAdministrationProtocol.GuardianWatchdogArgument", StringComparison.Ordinal)
           && maintenance.Contains("\"MINUTE\", \"/MO\", \"1\"", StringComparison.Ordinal),
        "legacy maintenance must install the minute Guardian watchdog before writing ActivationScheduled");
    var maintenanceWatchdogSettings = maintenance[maintenance.IndexOf("var watchdogSettings", StringComparison.Ordinal)..maintenance.IndexOf("var settingsCommand", StringComparison.Ordinal)];
    Assert(!maintenanceWatchdogSettings.Contains("StartWhenAvailable", StringComparison.Ordinal),
        "the maintenance watchdog must not queue missed StartWhenAvailable runs");

    var bundleBuilder = ReadSource("fly-headscale", "scripts", "Build-OpticonBundles.ps1");
    Assert(bundleBuilder.Contains("$setupPath", StringComparison.Ordinal)
           && bundleBuilder.Contains("Get-Item -LiteralPath $setupPath", StringComparison.Ordinal),
        "the signed inner release manifest must include the root Setup executable");
    Assert(bundleBuilder.Contains("[string]$MinimumGuardianVersion = \"1.1.2\"", StringComparison.Ordinal),
        "the hosted release must permit a watchdog-capable Guardian to install the Agent that performs signed Guardian reconciliation");
    var guardianUpdateManager = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    Assert(guardianUpdateManager.Contains("VerifyAndExtractGuardianAsync", StringComparison.Ordinal)
           && guardianUpdateManager.Contains("StableGuardianMaintenance.ReconcileSignedReleaseAsync", StringComparison.Ordinal)
           && guardianUpdateManager.Contains("GuardianWatchdogArgument", StringComparison.Ordinal)
           && guardianUpdateManager.Contains("Close the active Opticon SSH lease", StringComparison.Ordinal)
           && ReadSource("src", "Taildesk.Agent", "Program.cs").Contains("/api/v1/update/guardian", StringComparison.Ordinal)
           && updateCoordinator.Contains("ReconcileGuardianAsync", StringComparison.Ordinal)
           && updateCoordinator.Contains("post-maintenance Agent sample", StringComparison.Ordinal),
        "watchdog-capable Agents must reconcile only the production-signed Guardian and externally attest the result without UAC");
    Assert(adminWindow.Contains("BuildMaintenanceBootstrapCommand(release, device, operationId)", StringComparison.Ordinal)
           && adminWindow.Contains("release-manifest.json", StringComparison.Ordinal)
           && adminWindow.Contains("RSASignaturePadding]::Pss", StringComparison.Ordinal)
           && adminWindow.Contains("Signed Setup SHA-256 mismatch", StringComparison.Ordinal)
           && adminWindow.Contains("InvitationSigning.CertificateThumbprint", StringComparison.Ordinal)
           && adminWindow.Contains("--expected-tailnet-device-id=", StringComparison.Ordinal)
           && adminWindow.Contains("--expected-tailscale-ip=", StringComparison.Ordinal)
           && adminWindow.Contains("--operation-id=", StringComparison.Ordinal),
        "copied maintenance must verify a pinned signed Setup declaration, selected Tailnet identity, and exact operation before UAC");
    var signing = ReadSource("src", "Taildesk.Shared", "InvitationSigning.cs");
    var authenticode = ReadSource("src", "Taildesk.Shared", "AuthenticodeFileVerifier.cs");
    Assert(signing.Contains("AuthenticodeFileVerifier.VerifyPinned", StringComparison.Ordinal)
           && authenticode.Contains("trustResult is not Success and not CertificateUntrustedRoot", StringComparison.Ordinal)
           && authenticode.Contains("FixedTimeEquals(embedded.RawData, expectedSigner.RawData)", StringComparison.Ordinal),
        "runtime Authenticode checks must accept only valid or exact pinned self-signed signatures and reject all other indeterminate results");
    Assert(adminWindow.Contains("if (release.RequiresMaintenanceBootstrap)", StringComparison.Ordinal)
           && adminWindow.Contains("await RunMaintenanceBootstrapAsync(", StringComparison.Ordinal),
        "legacy update selection must delegate to the guarded maintenance flow");
    var flowStart = adminWindow.IndexOf("private async Task RunMaintenanceBootstrapAsync", StringComparison.Ordinal);
    var flowEnd = adminWindow.IndexOf("private DeviceRecord RequireDevice", flowStart, StringComparison.Ordinal);
    Assert(flowStart >= 0 && flowEnd > flowStart, "maintenance command-center flow was not found");
    var maintenanceFlow = adminWindow[flowStart..flowEnd];
    var clipboardIndex = maintenanceFlow.IndexOf("await SetClipboardTextAsync(command)", StringComparison.Ordinal);
    var confirmationIndex = maintenanceFlow.IndexOf("MessageBoxResult.Yes", StringComparison.Ordinal);
    var snapshotIndex = maintenanceFlow.IndexOf("SnapshotMaintenanceSshAsync", StringComparison.Ordinal);
    var remoteIndex = maintenanceFlow.IndexOf("LaunchRemoteControlAsync", StringComparison.Ordinal);
    var observerIndex = maintenanceFlow.IndexOf("ObserveMaintenanceBootstrapAsync", StringComparison.Ordinal);
    Assert(confirmationIndex >= 0 && confirmationIndex < clipboardIndex
           && clipboardIndex < snapshotIndex && snapshotIndex < remoteIndex && remoteIndex < observerIndex,
        "maintenance must confirm before copying, then snapshot SSH, launch RustDesk, and observe the exact operation");
    Assert(adminWindow.Contains("clipboardBusy = unchecked((int)0x800401D0)", StringComparison.Ordinal)
           && adminWindow.Contains("attempt <= 20", StringComparison.Ordinal)
           && adminWindow.Contains("Clipboard.SetDataObject(value, copy: true)", StringComparison.Ordinal)
           && adminWindow.Contains("no maintenance command was started", StringComparison.Ordinal),
        "security-sensitive clipboard handoffs must tolerate transient Windows clipboard ownership and fail clearly before maintenance");

    var remoteUpdates = ReadSource("src", "Taildesk.Admin", "RemoteDeviceUpdateCoordinator.cs");
    Assert(remoteUpdates.Contains("ObserveMaintenanceBootstrapAsync", StringComparison.Ordinal)
           && remoteUpdates.Contains("DateTimeOffset.UtcNow.AddMinutes(30)", StringComparison.Ordinal)
           && remoteUpdates.Contains("update?.OperationId != operationId", StringComparison.Ordinal)
           && remoteUpdates.Contains("!update.MaintenanceBootstrap", StringComparison.Ordinal)
           && remoteUpdates.Contains("CommitUpdateAsync(device, agentToken, operationId", StringComparison.Ordinal),
        "the command center must bound discovery and commit only the exact maintenance operation");
    Assert(remoteUpdates.Contains("status.Architecture, release.Architecture", StringComparison.Ordinal)
           && remoteUpdates.Contains("status.UpdateProtocolVersion == RemoteAdministrationProtocol.UpdateVersion", StringComparison.Ordinal)
           && remoteUpdates.Contains("status.TailscaleIp, device.TailscaleIp", StringComparison.Ordinal)
           && remoteUpdates.Contains("status.TailnetDeviceId, device.TailnetDeviceId", StringComparison.Ordinal)
           && remoteUpdates.Contains("Maintenance external health sample", StringComparison.Ordinal)
           && remoteUpdates.Contains("RemoteAdministrationProtocol.SshPort", StringComparison.Ordinal),
        "maintenance commit must require three exact authenticated external identity and recovery samples");

    var updateHealthStore = ReadSource("src", "Taildesk.Shared", "UpdateHealthTokenStore.cs");
    Assert(updateHealthStore.Contains("SecretScope.LocalMachine", StringComparison.Ordinal)
           && updateHealthStore.Contains("File.Move(temporary, path, false)", StringComparison.Ordinal)
           && updateHealthStore.IndexOf("configuredProtectedToken", StringComparison.Ordinal)
              < updateHealthStore.IndexOf("LoadSidecar", StringComparison.Ordinal),
        "update health resolution must prefer config and create a DPAPI LocalMachine write-once sidecar");

    var guardian = ReadSource("src", "Taildesk.UpdateGuardian", "GuardianRunner.cs");
    Assert(guardian.Contains("CommittedBootHealthWindow = TimeSpan.FromMinutes(6.5)", StringComparison.Ordinal)
           && guardian.Contains("RollbackHealthWindow = TimeSpan.FromMinutes(6.5)", StringComparison.Ordinal)
           && guardian.Contains("new LifelineSnapshot(journal.SshWasListening)", StringComparison.Ordinal),
        "committed boot verification must outlast the Agent's five-minute Tailscale bind wait");
    Assert(guardian.Contains("durable.OperationId != journal.OperationId", StringComparison.Ordinal),
        "a stale Guardian can overwrite a newer durable transaction");
    Assert(guardian.Contains("WaitForLegacyRollbackAsync", StringComparison.Ordinal)
           && guardian.Contains("requiredHealthySamples = 3", StringComparison.Ordinal),
        "maintenance rollback must accept a signed running legacy Agent without its unavailable health endpoint");
    Assert(guardian.Contains("UpdatePhase.Downloading or UpdatePhase.Verifying", StringComparison.Ordinal),
        "interrupted staging must fail durably without blocking later updates");
    Assert(guardian.Contains("watchdogOnly ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(20)", StringComparison.Ordinal)
           && guardian.Contains("journal.Phase is UpdatePhase.None", StringComparison.Ordinal)
           && guardian.Contains("or UpdatePhase.Committed", StringComparison.Ordinal)
           && guardian.Contains("or UpdatePhase.RolledBack", StringComparison.Ordinal)
           && guardian.Contains("or UpdatePhase.Failed", StringComparison.Ordinal),
        "the minute watchdog must use bounded contention and no-op for terminal/non-actionable phases");
    var guardianProgram = ReadSource("src", "Taildesk.UpdateGuardian", "Program.cs");
    Assert(guardianProgram.Contains("RemoteAdministrationProtocol.GuardianWatchdogArgument", StringComparison.Ordinal)
           && guardianProgram.Contains("watchdogOnly ? TimeSpan.Zero : TimeSpan.FromMinutes(2)", StringComparison.Ordinal)
           && guardianProgram.Contains(".RunAsync(watchdogOnly, cancellation.Token)", StringComparison.Ordinal)
           && guardianProgram.Contains(".GetAwaiter()", StringComparison.Ordinal)
           && guardianProgram.Contains(".GetResult()", StringComparison.Ordinal)
           && !guardianProgram.Contains("return await new GuardianRunner", StringComparison.Ordinal),
        "full ONSTART mode must wait through a quick watchdog so boot health cannot be suppressed");

    var updateManager = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    Assert(updateManager.Contains("WaitForGuardianPickupAsync", StringComparison.Ordinal)
           && updateManager.Contains("durable.GuardianClaimedAt is not null", StringComparison.Ordinal),
        "Task Scheduler success must not be mistaken for Guardian transaction pickup");
    Assert(updateManager.Contains("RunGuardianTaskForCommitAsync", StringComparison.Ordinal)
           && updateManager.Contains("durable.OperationId != operationId", StringComparison.Ordinal)
           && updateManager.Contains("UpdatePhase.Committed or UpdatePhase.RolledBack or UpdatePhase.Failed", StringComparison.Ordinal)
           && !updateManager.Contains("allowAlreadyRunning", StringComparison.Ordinal),
        "commit wakeup must require terminal evidence from the exact durable operation");
    var packageVerifier = ReadSource("src", "Taildesk.Shared", "UpdatePackageVerifier.cs");
    Assert(packageVerifier.Contains("Both archive", StringComparison.Ordinal)
           && packageVerifier.IndexOf("await target.FlushAsync", StringComparison.Ordinal)
              < packageVerifier.IndexOf("VerifyAuthenticodeAsync(output", StringComparison.Ordinal),
        "Guardian extraction must close its exclusive output handle before Authenticode reopens the staged executable");
    Assert(manager.Contains("SessionTerminationGeneration++", StringComparison.Ordinal)
           && manager.Contains("terminateAuthenticatedSessions: true", StringComparison.Ordinal),
        "revocation and expiry must durably request termination of already-authenticated SSH shells");

    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    Assert(agentProgram.Split("remote?.AddressFamily != AddressFamily.InterNetwork", StringSplitOptions.None).Length == 3,
        "internal-health and sensitive API authorization must reject mapped/native IPv6 identities");
    Assert(agentProgram.Contains("var updateHealthToken", StringComparison.Ordinal)
           && agentProgram.Contains("FixedTimeEquals(healthHeader, updateHealthToken)", StringComparison.Ordinal),
        "Agent internal health must use the config-first sidecar-capable credential resolver");
    Assert(agentProgram.Contains("RemoteAdministrationProtocol.IsTailscaleIpv4(coordinator.Host)", StringComparison.Ordinal),
        "the configured coordinator authorization identity is not canonical Tailscale IPv4");
    Assert(agentProgram.Contains("SystemRoot = grant.SystemRoot", StringComparison.Ordinal),
        "the authenticated SSH grant must propagate the exact target SystemRoot for automation");
    var guardianHealth = ReadSource("src", "Taildesk.UpdateGuardian", "InternalHealthClient.cs");
    Assert(guardianHealth.Contains("UpdateHealthTokenStore.LoadFromAgentConfigFile()", StringComparison.Ordinal),
        "Guardian internal health must use the same config-first sidecar fallback");
    var maintenanceBootstrap = ReadSource("src", "Taildesk.Setup", "MaintenanceBootstrapCoordinator.cs");
    Assert(maintenanceBootstrap.Contains("AddMinutes(2.5)", StringComparison.Ordinal)
           && maintenanceBootstrap.Contains("UpdateGuardianStartupDiagnostics.Read()", StringComparison.Ordinal)
           && guardianProgram.Contains("UpdateGuardianStartupDiagnostics.TryWrite", StringComparison.Ordinal),
        "Setup pickup must outlive the Guardian mutex wait and surface protected pre-claim startup failures");

    var adminToken = ReadSource("src", "Taildesk.UpdateGuardian", "SshAdminToken.cs");
    Assert(adminToken.Contains("TokenElevationTypeLimited", StringComparison.Ordinal)
           && adminToken.Contains("SecurityMandatoryHighRid", StringComparison.Ordinal)
           && adminToken.Contains("BuiltinAdministratorsSid", StringComparison.Ordinal)
           && adminToken.Contains("ScManagerCreateService", StringComparison.Ordinal)
           && adminToken.Contains("LocalSystemSid", StringComparison.Ordinal),
        "the SSH administrator proof must reject filtered/SYSTEM tokens and prove high-integrity SCM access");
    Assert(adminToken.Contains("Marshal.SizeOf<TokenLinkedToken>()", StringComparison.Ordinal)
           && adminToken.Contains("GetLinkedTokenInformation", StringComparison.Ordinal)
           && !adminToken.Contains("ReadTokenInformation(token, TokenLinkedTokenClass", StringComparison.Ordinal),
        "the fixed TOKEN_LINKED_TOKEN query must not depend on a zero-buffer sizing probe that returns ERROR_BAD_LENGTH on supported Windows builds");
    Assert(adminToken.Contains("GetInt32TokenInformation", StringComparison.Ordinal)
           && adminToken.Contains("const uint expectedLength = sizeof(int)", StringComparison.Ordinal)
           && !adminToken.Contains("var buffer = ReadTokenInformation(token, informationClass, description)", StringComparison.Ordinal),
        "fixed-size token fields must not depend on zero-buffer sizing probes that preserve stale Windows last-error values");
    Assert(adminToken.Contains("TokenAccessLevels.Query | TokenAccessLevels.Duplicate", StringComparison.Ordinal),
        "the in-session SSH proof must retain TOKEN_DUPLICATE while constructing its independent WindowsIdentity");
    var daemonUser = ReadSource("src", "Taildesk.UpdateGuardian", "SshDaemonUserContext.cs");
    Assert(daemonUser.Contains("LogonFullAdministrator", StringComparison.Ordinal)
           && daemonUser.Contains("CreateProcessAsUserW", StringComparison.Ordinal)
           && daemonUser.Contains("ScopedProcessPrivilege.Enable(\"SeBackupPrivilege\")", StringComparison.Ordinal)
           && daemonUser.Contains("ScopedProcessPrivilege.Enable(\"SeRestorePrivilege\")", StringComparison.Ordinal)
           && daemonUser.Contains("ProfileUnloadAttempts", StringComparison.Ordinal),
        "Guardian must create sshd with the full dedicated token and safely load/unload its profile");

    var supervisor = ReadSource("src", "Taildesk.UpdateGuardian", "SshSupervisor.cs");
    Assert(supervisor.Contains("WriteFailureAsync(exception)", StringComparison.Ordinal)
           && supervisor.Contains("supervisor.failure", StringComparison.Ordinal)
           && supervisor.Contains("File.Delete(_failurePath)", StringComparison.Ordinal)
           && supervisor.Contains("WithDaemonLog", StringComparison.Ordinal),
        "the independent SSH supervisor must publish protected failures and clear them after readiness");
    Assert(supervisor.Contains("await supervisor.WriteFailureAsync(exception)", StringComparison.Ordinal)
           && supervisor.IndexOf("await supervisor.WriteFailureAsync(exception)", StringComparison.Ordinal)
              < supervisor.IndexOf("await supervisor.FailClosedAsync()", StringComparison.Ordinal),
        "early SSH supervisor initialization failures must be published before fail-closed cleanup");
    Assert(supervisor.Contains("JobObjectLimitKillOnJobClose", StringComparison.Ordinal)
           && supervisor.Contains("CreateSuspended", StringComparison.Ordinal),
        "stable guardian must own sshd and shells in a kill-on-close job");
    Assert(supervisor.Contains("supervisor.lock", StringComparison.Ordinal)
           && supervisor.Contains("state.lock", StringComparison.Ordinal),
        "supervisor instance and scoped state locks must remain separate");
    Assert(supervisor.Contains("sessionTerminationRequired", StringComparison.Ordinal)
           && supervisor.Contains("state.SessionTerminationGeneration != _observedTerminationGeneration", StringComparison.Ordinal)
           && supervisor.Contains("_observedActiveSessionIds.Except(activeSessionIds).Any()", StringComparison.Ordinal)
           && supervisor.Contains("authorizationSetShrank", StringComparison.Ordinal)
           && supervisor.Contains("await StopDaemonAsync()", StringComparison.Ordinal),
        "the stable supervisor must restart its kill-on-close job whenever a lease is revoked or expires, even while the agent is offline");
    Assert(supervisor.Contains("UtcDateTime:yyyyMMddHHmmss}Z", StringComparison.Ordinal),
        "native authorized-key expiry must use an unambiguous UTC Z timestamp");
    Assert(supervisor.Contains("TerminateJobObject", StringComparison.Ordinal)
           && supervisor.Contains("QueryInformationJobObject", StringComparison.Ordinal)
           && supervisor.Contains("RotateDaemonLogAsync", StringComparison.Ordinal)
           && supervisor.Contains("MaximumArchivedLogBytes", StringComparison.Ordinal)
           && supervisor.Contains("LogLevel INFO", StringComparison.Ordinal),
        "Guardian teardown and SSH logging must remain bounded and fail-closed");
    var runtimeAclStart = supervisor.IndexOf("private async Task GrantDaemonRuntimeAccessAsync", StringComparison.Ordinal);
    var runtimeAclEnd = supervisor.IndexOf("private async Task RotateDaemonLogAsync", runtimeAclStart, StringComparison.Ordinal);
    Assert(runtimeAclStart >= 0 && runtimeAclEnd > runtimeAclStart
           && !supervisor[runtimeAclStart..runtimeAclEnd].Contains("_hostKeyPath", StringComparison.Ordinal)
           && supervisor.Contains("RestrictDaemonReadableAsync", StringComparison.Ordinal)
           && supervisor.Contains("/remove:g", StringComparison.Ordinal),
        "Guardian must let the elevated daemon read host keys through Administrators without a rejected named-user ACE");

    var app = ReadSource("src", "Taildesk.Admin", "App.xaml.cs");
    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    Assert(app.Contains("ShutdownSshSessionsAsync", StringComparison.Ordinal)
           && viewModel.Contains("session.TerminateAsync", StringComparison.Ordinal)
           && viewModel.Contains("RemoteRevocationError", StringComparison.Ordinal)
           && viewModel.Contains("LocalCleanupError", StringComparison.Ordinal),
        "Command Center shutdown must terminate SSH and report remote/local cleanup independently");

    var buildScript = ReadSource("build.ps1");
    var targetReleaseCheck = ReadSource("scripts", "Ensure-OpticonTargetRelease.ps1");
    var buildWorkflow = ReadSource(".github", "workflows", "build-windows.yml");
    var hostedBuild = ReadSource("fly-headscale", "scripts", "Build-OpticonBundles.ps1");
    var installer = ReadSource("installer", "Install-CommandCenter.ps1");
    Assert(buildScript.Contains("The Opticon solution build failed", StringComparison.Ordinal)
           && buildScript.Contains("The Opticon self-tests failed", StringComparison.Ordinal)
           && buildScript.Contains("must contain only the signed opticon.exe", StringComparison.Ordinal)
           && buildScript.Contains("IncludeSourceRevisionInInformationalVersion=false", StringComparison.Ordinal)
           && hostedBuild.Contains("hosted CLI directory must contain only", StringComparison.Ordinal)
           && hostedBuild.Contains("IncludeSourceRevisionInInformationalVersion=false", StringComparison.Ordinal),
        "release packaging must fail on native build/test errors and ship a single signed CLI app");
    Assert(buildScript.Contains("SkipTargetReleaseDeployment", StringComparison.Ordinal)
           && buildScript.Contains("Ensure-OpticonTargetRelease.ps1", StringComparison.Ordinal)
           && buildWorkflow.Contains("-SkipTargetReleaseDeployment", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("Test-CompleteRelease", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("Publish-OpticonBundles.ps1", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("status --porcelain", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("refs/remotes/origin/main", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("DeploymentRequired", StringComparison.Ordinal),
        "operator builds must deploy missing target releases only from clean synchronized main while CI opts out explicitly");
    var sourceControllerUpdater = ReadSource("scripts", "Update-InstalledOpticon.ps1");
    Assert(sourceControllerUpdater.Contains("Install-Opticon.ps1", StringComparison.Ordinal)
           && sourceControllerUpdater.Contains("exclusive lock", StringComparison.Ordinal)
           && !sourceControllerUpdater.Contains("Copy-Item (Join-Path $SourceDirectory '*')", StringComparison.Ordinal),
        "source-triggered controller updates must use the transactional release installer instead of copying over the live UI");
    const string packageBuildLock = ".opticon-package-build.lock";
    const string acquirePackageBuildLock = "$packageBuildLock = Enter-OpticonPackageBuildLock";
    Assert(buildScript.Contains(packageBuildLock, StringComparison.Ordinal)
           && hostedBuild.Contains(packageBuildLock, StringComparison.Ordinal)
           && buildScript.Contains("[IO.FileShare]::None", StringComparison.Ordinal)
           && hostedBuild.Contains("[IO.FileShare]::None", StringComparison.Ordinal)
           && buildScript.IndexOf(acquirePackageBuildLock, StringComparison.Ordinal) < buildScript.IndexOf("dotnet build", StringComparison.Ordinal)
           && hostedBuild.IndexOf(acquirePackageBuildLock, StringComparison.Ordinal) < hostedBuild.IndexOf("dotnet publish", StringComparison.Ordinal)
           && buildScript.LastIndexOf("$packageBuildLock.Dispose()", StringComparison.Ordinal) > buildScript.LastIndexOf("Compress-Archive", StringComparison.Ordinal)
           && hostedBuild.LastIndexOf("$packageBuildLock.Dispose()", StringComparison.Ordinal) > hostedBuild.LastIndexOf("Compress-Archive", StringComparison.Ordinal),
        "standalone and hosted packaging must share one exclusive lock across every build and publication mutation");
    var cliPathIntegration = ReadSource("src", "Taildesk.Admin", "CliPathIntegration.cs");
    var cliProgram = ReadSource("src", "Taildesk.Cli", "Program.cs");
    const string ownershipMarker = ".opticon-controller-owned";
    const string readyMarker = ".opticon-controller-ready";
    const string installLock = ".controller-install.lock";

    Assert(installer.Contains(ownershipMarker, StringComparison.Ordinal)
           && setup.Contains(ownershipMarker, StringComparison.Ordinal)
           && cliPathIntegration.Contains(ownershipMarker, StringComparison.Ordinal)
           && cliProgram.Contains(ownershipMarker, StringComparison.Ordinal),
        "installers, UI, and CLI must share the exact controller ownership marker");
    Assert(installer.Contains(readyMarker, StringComparison.Ordinal)
           && setup.Contains(readyMarker, StringComparison.Ordinal)
           && cliPathIntegration.Contains(readyMarker, StringComparison.Ordinal)
           && cliProgram.Contains(readyMarker, StringComparison.Ordinal)
           && installer.Contains("ControllerReadyMarkerValue)|$version", StringComparison.Ordinal)
           && setup.Contains("ControllerReadyMarkerValue}|{version}", StringComparison.Ordinal)
           && cliPathIntegration.Contains("Assembly.GetExecutingAssembly().GetName().Version", StringComparison.Ordinal)
           && cliProgram.Contains("Assembly.GetExecutingAssembly().GetName().Version", StringComparison.Ordinal),
        "the durable commit marker must bind the on-disk UI/CLI version to the executing UI or CLI generation");

    Assert(installer.Contains(installLock, StringComparison.Ordinal)
           && setup.Contains(installLock, StringComparison.Ordinal)
           && installer.Contains("[IO.FileShare]::None", StringComparison.Ordinal)
           && setup.Contains("FileShare.None", StringComparison.Ordinal)
           && cliPathIntegration.Contains("FileShare.Read", StringComparison.Ordinal)
           && cliProgram.Contains("FileShare.Read", StringComparison.Ordinal),
        "both installers must take the exclusive persistent lock while UI and CLI hold compatible lifetime reader leases");
    Assert(installer.IndexOf("$installLock = Enter-ControllerInstallLock", StringComparison.Ordinal)
               < installer.LastIndexOf("Ensure-OpenSshClientCapability", StringComparison.Ordinal)
           && installer.LastIndexOf("$installLock.Dispose()", StringComparison.Ordinal)
               > installer.IndexOf("Install-OpticonPayloadTransaction", StringComparison.Ordinal)
           && setup.IndexOf("AcquireControllerInstallLockAsync", StringComparison.Ordinal)
               < setup.IndexOf("CaptureControllerConfiguration", StringComparison.Ordinal),
        "exclusive installation locking must cover prerequisite mutation, snapshot, swap, and post-commit configuration");

    Assert(installer.Contains("Assert-InstallDestinationPreflight", StringComparison.Ordinal)
           && installer.Contains("restricted to the canonical directory", StringComparison.Ordinal)
           && installer.Contains("Assert-OwnedOpticonDirectory", StringComparison.Ordinal)
           && setup.Contains("RequireOwnedControllerDirectoryAsync", StringComparison.Ordinal)
           && setup.Contains("legacyExecutables", StringComparison.Ordinal)
           && setup.Contains("contains a reparse point", StringComparison.Ordinal),
        "destructive controller swaps must be canonical, ownership guarded, reparse safe, and verify every legacy executable");
    Assert(installer.Contains("Restore-InterruptedOpticonInstall", StringComparison.Ordinal)
           && installer.Contains("Assert-CommittedOrLegacyOpticonDirectory -Directory $backup", StringComparison.Ordinal)
           && installer.Contains("Move-Item -LiteralPath $backup -Destination $destination", StringComparison.Ordinal)
           && setup.Contains("RequireCommittedOrLegacyControllerDirectoryAsync(backup", StringComparison.Ordinal)
           && setup.Contains("HasExactControllerReadyMarker(destination)", StringComparison.Ordinal)
           && setup.Contains("Directory.Move(backup, destination)", StringComparison.Ordinal),
        "recovery must validate/restore .previous and never discard it for an uncommitted live candidate");
    Assert(installer.IndexOf("& $ConfigureActivatedPayload", StringComparison.Ordinal)
               < installer.IndexOf("Write-ControllerReadyMarker -Directory $destination", StringComparison.Ordinal)
           && setup.IndexOf("await configureActivatedPayload()", StringComparison.Ordinal)
               < setup.IndexOf("WriteControllerReadyMarker(destination)", StringComparison.Ordinal)
           && installer.Contains("Restore-ControllerConfigurationSnapshot", StringComparison.Ordinal)
           && setup.Contains("RestoreControllerConfigurationAsync", StringComparison.Ordinal),
        "ready is written last and a post-swap configuration failure must roll back payload and restorable configuration");

    Assert(installer.Contains("@($destination, $backup)", StringComparison.Ordinal)
           && setup.Contains("RequireInstalledControllerProcessesClosed(destination, backup)", StringComparison.Ordinal)
           && cliPathIntegration.IndexOf("await AcquireControllerLifetimeLeaseAsync", StringComparison.Ordinal)
               < cliPathIntegration.IndexOf("if (runningRetainedInstall)", StringComparison.Ordinal)
           && cliProgram.IndexOf("var lease = new FileStream", StringComparison.Ordinal)
               < cliProgram.IndexOf("if (!await HasExactControllerMarkersAsync", StringComparison.Ordinal),
        "live and .previous UI/CLI generations must be checked under the shared/exclusive lease before use or deletion");

    Assert(cliPathIntegration.Contains("recordedDirectory.Equals(defaultInstalledDirectory", StringComparison.Ordinal)
           && cliPathIntegration.Contains("previous = null; // Never remove an unverified", StringComparison.Ordinal)
           && setup.Contains("previous = null; // Never remove an unverified", StringComparison.Ordinal)
           && installer.Contains("Test-TrustedRecordedOpticonCliPath", StringComparison.Ordinal)
           && installer.Contains("CanonicalControllerInstallDirectory", StringComparison.Ordinal)
           && cliPathIntegration.Contains("if (uiVersion != cliVersion)", StringComparison.Ordinal)
           && setup.Contains("if (uiVersion != cliVersion)", StringComparison.Ordinal)
           && installer.Contains("Assert-MatchingOpticonUiCliVersion", StringComparison.Ordinal),
        "PATH repair must use only the canonical recorded install and exact matching UI/CLI versions");
}

static string NormalizeTargetType(string value) => value.Replace("{x:Type ", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal).Trim();

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"expected {typeof(TException).Name}");
}
