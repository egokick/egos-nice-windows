using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal static class SimpleDeviceInstaller
{
    private const string Origin = "https://taildesk-egokick-control.fly.dev";
    private const string AgentServiceName = "OpticonAgent";
    private static readonly string[] HistoricalTasks =
    [
        "Taildesk Agent", "Taildesk Update Guardian", "Taildesk Update Guardian Watchdog",
        "Taildesk SSH Supervisor", "Taildesk Fly Route", "Opticon Command Center",
        "Taildesk Setup Resume"
    ];
    private static readonly string[] FirewallRules =
    [
        "Opticon Agent (Tailscale only)", "Taildesk Agent (Tailscale only)",
        "Opticon RustDesk (Tailscale only)", "RustDesk Direct (Tailscale only)",
        "RustDesk External IPv4 Block", "RustDesk External IPv6 Block"
    ];

    internal static async Task<int> RunAsync(
        SourceBootstrapRequest bootstrap,
        Action<string> report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var staging = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpticonBootstrap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            report("Verifying invitation...");
            var launcher = Environment.ProcessPath
                           ?? throw new InvalidOperationException("The Opticon installer path is unavailable.");
            if (bootstrap.LauncherSha256 is not null)
                await RequireFileHashAsync(launcher, new FileInfo(launcher).Length, bootstrap.LauncherSha256, cancellationToken);
            await ProductSigning.VerifyAuthenticodeAsync(launcher, cancellationToken);

            using var http = DirectHttp.CreateClient(TimeSpan.FromMinutes(20));
            var invitePath = Path.Combine(staging, "invite.tdinvite");
            await HostedBootstrapper.DownloadAsync(http,
                $"{Origin}/opticon/i/{Uri.EscapeDataString(bootstrap.PublicId)}/invite.tdinvite",
                invitePath, expectedSize: null, maximumSize: 64 * 1024, expectedHash: null);
            var encrypted = await File.ReadAllBytesAsync(invitePath, cancellationToken);
            var signedEnvelope = HostedInviteFile.Decrypt(bootstrap.PrivateKey, encrypted);
            InvitePayload invite;
            try { invite = HostedInviteFile.ReadSigned(signedEnvelope); }
            finally { CryptographicOperations.ZeroMemory(signedEnvelope); }
            ValidateInvitation(invite);

            var bundlePath = Path.Combine(staging, invite.BundleFile);
            await HostedBootstrapper.DownloadAsync(http, invite.BundleDownloadUrl, bundlePath,
                invite.BundleSize, 512L * 1024 * 1024, invite.BundleSha256);
            var release = Path.Combine(staging, "release");
            await ExtractAndVerifyBundleAsync(bundlePath, release, invite, cancellationToken);

            report("Resetting Opticon...");
            await ResetOpticonAsync(cancellationToken);
            MachineStorageSecurity.EnsureOpticonMachineState();

            report("Connecting private network...");
            var architecture = RuntimeInformation.OSArchitecture;
            var tailscaleArtifact = DependencyArtifacts.Tailscale(architecture);
            var tailscaleInstaller = await DownloadDependencyAsync(http, staging, tailscaleArtifact, cancellationToken);
            await InstallMsiAsync(tailscaleInstaller, "Tailscale", cancellationToken);
            var tailscale = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Tailscale", "tailscale.exe");
            if (!File.Exists(tailscale)) throw new FileNotFoundException("Tailscale did not install.", tailscale);
            var tailscaleIp = await TryReadExpectedTailscaleIpAsync(tailscale, invite, cancellationToken);
            if (tailscaleIp is null)
            {
                _ = await ProcessRunner.RunAsync(tailscale, ["logout"], TimeSpan.FromSeconds(30), cancellationToken);
                var joined = await ProcessRunner.RunAsync(tailscale,
                    TailscaleCommandLine.BuildEnrollmentArguments(
                        invite.HeadscaleLoginUrl, invite.TailscaleAuthKey,
                        TailscaleCommandLine.NormalizeHostName(invite.DeviceName, Environment.MachineName)),
                    TimeSpan.FromMinutes(2), cancellationToken);
                tailscaleIp = await TryReadExpectedTailscaleIpAsync(tailscale, invite, cancellationToken);
                if (!joined.Succeeded && tailscaleIp is null)
                    RequireSuccess(joined, "Tailscale could not join the Opticon network");
                tailscaleIp ??= await ReadTailscaleIpAsync(tailscale, cancellationToken);
            }
            RequireSuccess(await ProcessRunner.RunAsync(tailscale,
                    ["set", $"--advertise-exit-node={invite.AdvertiseExitNode.ToString().ToLowerInvariant()}"],
                    TimeSpan.FromSeconds(30), cancellationToken),
                "Tailscale could not apply exit-node advertisement policy");

            report("Preparing secure recovery...");
            await InstallCoordinator.EnsureOpenSshServerCapabilityAsync(cancellationToken);
            if (invite.Role == DeviceRole.ControllerAndManaged)
                await InstallCoordinator.EnsureOpenSshClientCapabilityAsync(cancellationToken);

            report("Installing remote access...");
            var rustDeskArtifact = DependencyArtifacts.RustDesk(architecture);
            var rustDeskInstaller = await DownloadDependencyAsync(http, staging, rustDeskArtifact, cancellationToken);
            await InstallMsiAsync(rustDeskInstaller, "RustDesk", cancellationToken);
            var rustDesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "RustDesk", "rustdesk.exe");
            if (!File.Exists(rustDesk)) throw new FileNotFoundException("RustDesk did not install.", rustDesk);
            var service = await ProcessRunner.RunAsync("sc.exe", ["query", "RustDesk"],
                TimeSpan.FromSeconds(15), cancellationToken);
            if (!service.Succeeded)
                RequireSuccess(await ProcessRunner.RunAsync(rustDesk, ["--install-service"],
                    TimeSpan.FromSeconds(30), cancellationToken, captureOutput: false),
                    "RustDesk could not install its service");
            // RustDesk 1.4.x can keep its password helper resident after the
            // configuration has been applied. Do not wait on that resident
            // process as though it were an ordinary short-lived command.
            ProcessRunner.StartDetached(rustDesk, ["--password", invite.RustDeskPassword]);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            RustDeskServiceProfileStore.HardenAll();
            RequireSuccess(await ProcessRunner.RunAsync("sc.exe", ["config", "RustDesk", "start=", "auto"],
                TimeSpan.FromSeconds(15), cancellationToken), "RustDesk could not be set to automatic startup");
            _ = await ProcessRunner.RunAsync("sc.exe", ["stop", "RustDesk"], TimeSpan.FromSeconds(30), cancellationToken);
            RequireSuccess(await ProcessRunner.RunAsync("sc.exe", ["start", "RustDesk"],
                TimeSpan.FromSeconds(30), cancellationToken), "RustDesk could not start");

            report("Installing Opticon Agent and recovery guardian...");
            var sourceAgent = Path.Combine(release, "Payload", "Agent", "Taildesk.Agent.exe");
            var sourceGuardian = Path.Combine(release, "Payload", "UpdateGuardian", "Taildesk.UpdateGuardian.exe");
            var sourceUninstaller = Path.Combine(release, "Payload", "Uninstall", "Uninstall-Opticon.exe");
            await InstallGuardianAsync(sourceGuardian, cancellationToken);
            Directory.CreateDirectory(AppPaths.AgentInstallDirectory);
            var installedAgent = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
            File.Copy(sourceAgent, installedAgent, overwrite: false);
            var installedUninstaller = Path.Combine(AppPaths.InstallDirectory, "Uninstall-Opticon.exe");
            File.Copy(sourceUninstaller, installedUninstaller, overwrite: false);

            var config = new AgentConfig
            {
                DeviceName = invite.DeviceName,
                Role = invite.Role,
                BindAddress = tailscaleIp,
                AgentTokenHash = SecurityHelpers.HashToken(invite.AgentToken),
                MediaSigningKeyProtected = SecretProtector.Protect(SecurityHelpers.CreateToken(), SecretScope.LocalMachine),
                UpdateHealthTokenProtected = SecretProtector.Protect(SecurityHelpers.CreateToken(), SecretScope.LocalMachine),
                CoordinatorUrl = invite.CoordinatorUrl,
                PendingInviteId = invite.InviteId,
                PendingInviteSecretProtected = SecretProtector.Protect(invite.InviteSecret, SecretScope.LocalMachine),
                AdvertiseExitNode = invite.AdvertiseExitNode,
                SharedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExposeAllLocalVolumes = false,
                ControllerShortcutPaths = []
            };
            await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).SaveAsync(config, cancellationToken);

            await ReplaceFirewallRulesAsync(tailscaleIp, installedAgent, rustDesk, cancellationToken);
            await CreateAgentServiceAsync(installedAgent, cancellationToken);
            RegisterUninstaller(installedUninstaller, invite.ReleaseVersion);
            RequireSuccess(await ProcessRunner.RunAsync("sc.exe", ["start", AgentServiceName],
                TimeSpan.FromSeconds(30), cancellationToken), "The Opticon Agent service could not start");
            await WaitForAgentHealthAsync(tailscaleIp, invite.AgentToken, cancellationToken);

            report("Confirming enrollment...");
            await WaitForEnrollmentAsync(invite.InviteId, cancellationToken);
            report("Connected. This machine is ready.");
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Opticon installation failed: {exception.Message}", exception);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static void ValidateInvitation(InvitePayload invite)
    {
        if (invite.SchemaVersion != InvitationPolicy.HostedLinkSchemaVersion
            || invite.InstallProtocol != InvitationPolicy.BinaryInstallProtocol
            || invite.InviteId == Guid.Empty || invite.ExpiresAt <= DateTimeOffset.UtcNow
            || string.IsNullOrWhiteSpace(invite.InviteSecret)
            || string.IsNullOrWhiteSpace(invite.TailscaleAuthKey)
            || string.IsNullOrWhiteSpace(invite.HeadscaleLoginUrl)
            || string.IsNullOrWhiteSpace(invite.ExpectedTailnet)
            || string.IsNullOrWhiteSpace(invite.AgentToken)
            || string.IsNullOrWhiteSpace(invite.RustDeskPassword)
            || string.IsNullOrWhiteSpace(invite.DeviceName)
            || !Uri.TryCreate(invite.CoordinatorUrl, UriKind.Absolute, out var coordinator)
            || coordinator.Scheme is not ("http" or "https")
            || invite.BundleSize <= 0 || invite.BundleSize > 512L * 1024 * 1024
            || invite.BundleSha256.Length != 64 || invite.BundleSha256.Any(c => !Uri.IsHexDigit(c))
            || !invite.BundleFile.Equals(
                $"opticon-bundle-{invite.ReleaseVersion}-{(invite.Role == DeviceRole.ManagedOnly ? "managed" : "controller")}-win-x64.zip",
                StringComparison.Ordinal)
            || invite.BundleArchitecture != "x64"
            || !IsImmutableBundleUrl(invite.BundleDownloadUrl, invite.ReleaseVersion, invite.BundleFile))
            throw new InvalidDataException("The signed invitation does not contain a complete current binary installation request.");
    }

    private static bool IsImmutableBundleUrl(string value, string version, string file) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath == $"/opticon/releases/{version}/{Uri.EscapeDataString(file)}";

    private static async Task ExtractAndVerifyBundleAsync(
        string archivePath,
        string destination,
        InvitePayload invite,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length != 6) throw new InvalidDataException("The Opticon device bundle has an invalid file count.");
        var entries = files.ToDictionary(entry => Normalize(entry.FullName), StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "Taildesk.Setup.exe", "Payload/Agent/Taildesk.Agent.exe",
            "Payload/UpdateGuardian/Taildesk.UpdateGuardian.exe",
            "Payload/Uninstall/Uninstall-Opticon.exe", "release-manifest.json", "release-manifest.sig"
        };
        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("The Opticon device bundle is not the minimal declared payload.");
        var manifestBytes = await ReadEntryAsync(entries["release-manifest.json"], 1024 * 1024, cancellationToken);
        var signatureText = System.Text.Encoding.UTF8.GetString(
            await ReadEntryAsync(entries["release-manifest.sig"], 16 * 1024, cancellationToken)).Trim();
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch { throw new InvalidDataException("The device bundle signature is malformed."); }
        if (!SourceReleaseSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The device bundle signature is invalid.");
        var manifest = JsonSerializer.Deserialize<OpticonReleaseManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The device bundle manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Version != invite.ReleaseVersion
            || manifest.Role != invite.Role || manifest.Architecture != invite.BundleArchitecture
            || manifest.SigningProfile != BuildSigningTrust.ProfileName
            || manifest.SourceReleaseKeyId != SourceReleaseSigning.KeyId
            || manifest.ProductSignerThumbprint != ProductSigning.CertificateThumbprint)
            throw new InvalidDataException("The device bundle identity does not match the invitation.");
        if (!manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected.Where(path => path.EndsWith(".exe", StringComparison.Ordinal))))
            throw new InvalidDataException("The signed device bundle file allowlist is invalid.");

        foreach (var file in manifest.Files)
        {
            var path = Normalize(file.Path);
            var entry = entries[path];
            if (entry.Length != file.Size || file.Size <= 0 || file.Sha256.Length != 64)
                throw new InvalidDataException($"The device bundle declaration is invalid: {path}.");
            var output = SafeDestination(destination, path);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var input = entry.Open())
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await input.CopyToAsync(target, cancellationToken);
            await RequireFileHashAsync(output, file.Size, file.Sha256, cancellationToken);
            await ProductSigning.VerifyAuthenticodeAsync(output, cancellationToken);
        }
    }

    private static async Task ResetOpticonAsync(CancellationToken cancellationToken)
    {
        _ = await ProcessRunner.RunAsync("sc.exe", ["stop", AgentServiceName], TimeSpan.FromSeconds(20), cancellationToken);
        foreach (var task in HistoricalTasks)
        {
            _ = await ProcessRunner.RunAsync("schtasks.exe", ["/End", "/TN", task], TimeSpan.FromSeconds(15), cancellationToken);
            _ = await ProcessRunner.RunAsync("schtasks.exe", ["/Delete", "/TN", task, "/F"], TimeSpan.FromSeconds(15), cancellationToken);
        }
        StopProcessesFromRoot(AppPaths.InstallDirectory);
        _ = await ProcessRunner.RunAsync("sc.exe", ["delete", AgentServiceName], TimeSpan.FromSeconds(20), cancellationToken);
        await WaitForServiceDeletionAsync(AgentServiceName, cancellationToken);
        DeleteFixedRoot(AppPaths.InstallDirectory);
        DeleteFixedRoot(AppPaths.MachineDataDirectory);
        DeleteFixedRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonProvenance"));
    }

    private static async Task WaitForServiceDeletionAsync(string serviceName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var query = await ProcessRunner.RunAsync(
                "sc.exe", ["query", serviceName], TimeSpan.FromSeconds(10), cancellationToken);
            if (!query.Succeeded) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new InvalidOperationException($"Windows did not remove the {serviceName} service within 15 seconds.");
    }

    private static void StopProcessesFromRoot(string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var image = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                    if (!image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch { }
            }
        }
    }

    private static void DeleteFixedRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) && !File.Exists(full)) return;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The fixed Opticon root is a reparse point and was not removed: {full}");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true); else File.Delete(full);
    }

    private static async Task<string> DownloadDependencyAsync(
        HttpClient http, string staging, DependencyArtifact artifact, CancellationToken cancellationToken)
    {
        var path = Path.Combine(staging, artifact.FileName);
        await HostedBootstrapper.DownloadAsync(http, artifact.PrimaryUrl, path, artifact.Size,
            artifact.Size, artifact.Sha256);
#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no X509CertificateLoader replacement.
        using var signer = X509CertificateLoader.LoadCertificate(
            X509Certificate.CreateFromSignedFile(path).GetRawCertData());
#pragma warning restore SYSLIB0057
        if (!string.Equals(signer.Thumbprint, artifact.ExpectedSignerThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{artifact.Product} has an unexpected Authenticode signer.");
        await BoundWindowsProductSignatureVerifier.VerifyPinnedInstallerAsync(path, signer,
            requireWindowsTrustedChain: true, cancellationToken);
        return path;
    }

    private static async Task InstallMsiAsync(string path, string product, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("msiexec.exe", ["/i", path, "/qn", "/norestart"],
            TimeSpan.FromMinutes(5), cancellationToken);
        if (result.ExitCode is not (0 or 3010)) RequireSuccess(result, $"{product} installation failed");
    }

    private static async Task<string> ReadTailscaleIpAsync(string tailscale, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var result = await ProcessRunner.RunAsync(tailscale, ["ip", "-4"], TimeSpan.FromSeconds(10), cancellationToken);
            var candidate = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (result.Succeeded && IPAddress.TryParse(candidate, out var ip)
                                 && RemoteAdministrationProtocol.IsTailscaleIpv4(ip.ToString())) return ip.ToString();
            await Task.Delay(1000, cancellationToken);
        }
        throw new InvalidOperationException("Tailscale did not provide a private IPv4 address.");
    }

    private static async Task<string?> TryReadExpectedTailscaleIpAsync(
        string tailscale,
        InvitePayload invite,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                tailscale, ["status", "--json"], TimeSpan.FromSeconds(15), cancellationToken);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (!root.TryGetProperty("BackendState", out var state)
                || !string.Equals(state.GetString(), "Running", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("Self", out var self)) return null;
            var tailnet = root.TryGetProperty("CurrentTailnet", out var current)
                          && current.ValueKind == JsonValueKind.Object
                          && current.TryGetProperty("Name", out var name)
                ? name.GetString() ?? string.Empty
                : root.TryGetProperty("MagicDNSSuffix", out var suffix)
                    ? suffix.GetString() ?? string.Empty
                    : string.Empty;
            if (!tailnet.Equals(invite.ExpectedTailnet, StringComparison.OrdinalIgnoreCase)) return null;
            var tags = self.TryGetProperty("Tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
                ? tagArray.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray()
                : [];
            var expectedTag = invite.Role == DeviceRole.ControllerAndManaged
                ? "tag:taildesk-controller" : "tag:taildesk-managed";
            var oppositeTag = invite.Role == DeviceRole.ControllerAndManaged
                ? "tag:taildesk-managed" : "tag:taildesk-controller";
            if (!tags.Contains(expectedTag, StringComparer.OrdinalIgnoreCase)
                || tags.Contains(oppositeTag, StringComparer.OrdinalIgnoreCase)
                || tags.Contains("tag:taildesk-exit", StringComparer.OrdinalIgnoreCase) != invite.AdvertiseExitNode)
                return null;
            var dnsName = self.TryGetProperty("DNSName", out var dns) ? dns.GetString() ?? string.Empty
                : self.TryGetProperty("HostName", out var host) ? host.GetString() ?? string.Empty : string.Empty;
            var label = dnsName.TrimEnd('.').Split('.', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.Equals(label,
                    TailscaleCommandLine.NormalizeHostName(invite.DeviceName, Environment.MachineName),
                    StringComparison.OrdinalIgnoreCase)) return null;
            if (!self.TryGetProperty("TailscaleIPs", out var addresses)
                || addresses.ValueKind != JsonValueKind.Array) return null;
            return addresses.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                .FirstOrDefault(value => IPAddress.TryParse(value, out var ip)
                                         && RemoteAdministrationProtocol.IsTailscaleIpv4(ip.ToString()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task ReplaceFirewallRulesAsync(
        string tailscaleIp, string agent, string rustDesk, CancellationToken cancellationToken)
    {
        foreach (var name in FirewallRules)
            _ = await ProcessRunner.RunAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={name}"],
                TimeSpan.FromSeconds(15), cancellationToken);
        RequireSuccess(await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=Opticon Agent (Tailscale only)",
                "dir=in", "action=allow", "protocol=TCP", "localport=45831", $"localip={tailscaleIp}",
                "remoteip=100.64.0.0/10", $"program={agent}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(20), cancellationToken), "The Agent firewall rule could not be installed");
        RequireSuccess(await ProcessRunner.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=Opticon RustDesk (Tailscale only)",
                "dir=in", "action=allow", "protocol=TCP", "localport=21118", $"localip={tailscaleIp}",
                "remoteip=100.64.0.0/10", $"program={rustDesk}", "profile=any", "enable=yes"],
            TimeSpan.FromSeconds(20), cancellationToken), "The RustDesk firewall rule could not be installed");
        foreach (var (name, remote) in new[]
                 {
                     ("RustDesk External IPv4 Block", "0.0.0.0-100.63.255.255,100.128.0.0-255.255.255.255"),
                     ("RustDesk External IPv6 Block", "::/1,8000::/1")
                 })
            RequireSuccess(await ProcessRunner.RunAsync("netsh.exe",
                ["advfirewall", "firewall", "add", "rule", $"name={name}", "dir=out",
                    "action=block", $"remoteip={remote}", $"program={rustDesk}", "profile=any", "enable=yes"],
                TimeSpan.FromSeconds(20), cancellationToken), "A RustDesk isolation rule could not be installed");
    }

    private static async Task CreateAgentServiceAsync(string executable, CancellationToken cancellationToken)
    {
        var command = $"\"{Path.GetFullPath(executable)}\" --service";
        RequireSuccess(await ProcessRunner.RunAsync("sc.exe",
            ["create", AgentServiceName, "binPath=", command, "start=", "auto", "obj=", "LocalSystem",
                "DisplayName=", "Opticon Agent"], TimeSpan.FromSeconds(30), cancellationToken),
            "Windows could not create the Opticon Agent service");
        RequireSuccess(await ProcessRunner.RunAsync("sc.exe",
            ["failure", AgentServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"],
            TimeSpan.FromSeconds(20), cancellationToken), "Windows could not configure Agent service recovery");
    }

    private static async Task InstallGuardianAsync(string source, CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("The signed Update Guardian payload is missing.", source);
        await ProductSigning.VerifyAuthenticodeAsync(source, cancellationToken);
        var version = UpdatePackageVerifier.ParseVersion(UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(source).ProductVersion ?? string.Empty));
        if (!RemoteAdministrationProtocol.SupportsGuardianWatchdog(version))
            throw new InvalidDataException(
                $"The signed Update Guardian {version} does not support the required watchdog contract.");

        Directory.CreateDirectory(AppPaths.UpdateGuardianInstallDirectory);
        var installed = Path.Combine(AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe");
        File.Copy(source, installed, overwrite: false);
        await ProductSigning.VerifyAuthenticodeAsync(installed, cancellationToken);

        RequireSuccess(await ProcessRunner.RunAsync("schtasks.exe",
                ["/Create", "/TN", RemoteAdministrationProtocol.GuardianTaskName,
                    "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", $"\"{installed}\"", "/F"],
                TimeSpan.FromSeconds(30), cancellationToken),
            "Windows could not create the fail-safe Update Guardian task");
        RequireSuccess(await ProcessRunner.RunAsync("schtasks.exe",
                ["/Create", "/TN", RemoteAdministrationProtocol.GuardianWatchdogTaskName,
                    "/SC", "MINUTE", "/MO", "1", "/RU", "SYSTEM", "/RL", "HIGHEST",
                    "/TR", $"\"{installed}\" {RemoteAdministrationProtocol.GuardianWatchdogArgument}", "/F"],
                TimeSpan.FromSeconds(30), cancellationToken),
            "Windows could not create the Update Guardian watchdog task");

        var taskSettings =
            "$boot=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-StartWhenAvailable -RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) " +
            "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianTaskName}' -Settings $boot | Out-Null; " +
            "$watchdog=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianWatchdogTaskName}' -Settings $watchdog | Out-Null";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        RequireSuccess(await ProcessRunner.RunAsync(powershell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", taskSettings],
                TimeSpan.FromSeconds(30), cancellationToken),
            "Windows could not configure the Update Guardian recovery tasks");
    }

    private static void RegisterUninstaller(string executable, string version)
    {
        using var root = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Opticon", writable: true);
        root.SetValue("DisplayName", "Opticon");
        root.SetValue("DisplayVersion", version);
        root.SetValue("Publisher", "Opticon");
        root.SetValue("UninstallString", $"\"{executable}\"");
        root.SetValue("QuietUninstallString", $"\"{executable}\" --quiet");
        root.SetValue("NoModify", 1, RegistryValueKind.DWord);
        root.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static async Task WaitForAgentHealthAsync(
        string ip,
        string agentToken,
        CancellationToken cancellationToken)
    {
        using var client = DirectHttp.CreateClient(TimeSpan.FromSeconds(3));
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}:45831/healthz");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", agentToken);
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(1000, cancellationToken);
        }
        throw new TimeoutException("The Opticon Agent service did not become healthy within 30 seconds.");
    }

    private static async Task WaitForEnrollmentAsync(Guid inviteId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                try
                {
                    var state = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile).LoadAsync(timeout.Token);
                    if (state.CompletedInviteId == inviteId && state.PendingInviteId is null) return;
                }
                catch (IOException) { }
                await Task.Delay(1000, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The command center did not confirm enrollment within 60 seconds.");
        }
    }

    private static async Task RequireFileHashAsync(
        string path, long size, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != size) throw new InvalidDataException($"File size mismatch: {Path.GetFileName(path)}.");
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash)))
            throw new InvalidDataException($"File hash mismatch: {Path.GetFileName(path)}.");
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximum, CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > maximum) throw new InvalidDataException("A bundle metadata entry has an invalid size.");
        await using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        await input.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static string Normalize(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The device bundle contains an unsafe path.");
        return normalized;
    }

    private static string SafeDestination(string root, string relative)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A device bundle path escaped its staging root.");
        return output;
    }

    private static void RequireSuccess(ProcessResult result, string operation)
    {
        if (result.Succeeded) return;
        var detail = (result.StandardError + " " + result.StandardOutput).Trim();
        if (detail.Length > 600) detail = detail[..600];
        throw new InvalidOperationException($"{operation} (exit {result.ExitCode}){(detail.Length == 0 ? "." : ": " + detail)}");
    }
}
