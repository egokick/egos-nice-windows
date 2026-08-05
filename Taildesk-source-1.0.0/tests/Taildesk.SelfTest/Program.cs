using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Taildesk.Shared;

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
    ("RustDesk managed-host hardening is complete and idempotent", TestRustDeskHardening),
    ("RustDesk installer configures every Windows service profile before validation", TestRustDeskInstallerProfiles),
    ("controller registry contains no permanent credentials", TestControllerRegistryShape),
    ("uploads permit huge files but retain bounded resource controls", TestUploadPolicy),
    ("path guard permits a child and blocks traversal", TestPathGuard),
    ("WPF style templates match their control target types", TestWpfStyleTemplateTargets),
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
static void TestRustDeskHardening()
{
    const string original = "rendezvous_server = 'public.example'\r\n[options]\r\ndirect-server = 'N'\r\nunknown = 'preserved'\r\n";
    var hardened = RustDeskConfiguration.HardenManagedHost(original);
    Assert(RustDeskConfiguration.IsManagedHostHardened(hardened), "hardened configuration should verify");
    Assert(hardened == RustDeskConfiguration.HardenManagedHost(hardened), "hardening should be idempotent");
    Assert(hardened.Contains("direct-server = 'Y'", StringComparison.Ordinal), "direct server must be enabled");
    Assert(hardened.Contains("whitelist = ','", StringComparison.Ordinal), "RustDesk must not receive an unsupported CIDR whitelist; Windows Firewall enforces the tailnet range");
    Assert(hardened.Contains("unknown = 'preserved'", StringComparison.Ordinal), "unmanaged options must be preserved");
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
static void TestUploadPolicy()
{
    var config = new AgentConfig();
    Assert(config.MaxUploadBytes >= 20L * 1024 * 1024 * 1024, "uploads are capped below 20 GiB");
    Assert(config.MaxUploadBytes == 256L * 1024 * 1024 * 1024, "default maximum upload is not the reviewed 256 GiB limit");
    Assert(config.MaxConcurrentUploads is >= 1 and <= 2, "concurrent upload bound is unsafe");
    Assert(config.MinimumFreeSpaceBytes >= 5L * 1024 * 1024 * 1024, "free-space reserve is too small");
    Assert(config.MaxUploadDurationMinutes <= 24 * 60, "upload lifetime is unbounded");
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
