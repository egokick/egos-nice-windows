using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal sealed record MaintenanceTargetSummary(
    string DeviceName,
    DeviceRole Role,
    string CoordinatorUrl,
    string CurrentVersion);

internal sealed record MaintenanceExpectedTarget(
    string TailnetDeviceId,
    string TailscaleIp,
    Guid OperationId)
{
    private const string DeviceArgument = "--expected-tailnet-device-id=";
    private const string AddressArgument = "--expected-tailscale-ip=";
    private const string OperationArgument = "--operation-id=";

    public static MaintenanceExpectedTarget Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4
            || !arguments[0].Equals("--maintenance", StringComparison.Ordinal)
            || !arguments[1].StartsWith(DeviceArgument, StringComparison.Ordinal)
            || !arguments[2].StartsWith(AddressArgument, StringComparison.Ordinal)
            || !arguments[3].StartsWith(OperationArgument, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Maintenance requires the fixed selected-device identity, Tailscale address, and operation ID copied by Opticon.");

        var deviceId = arguments[1][DeviceArgument.Length..];
        var address = arguments[2][AddressArgument.Length..];
        var operationText = arguments[3][OperationArgument.Length..];
        if (string.IsNullOrWhiteSpace(deviceId)
            || deviceId.Length > 256
            || deviceId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new InvalidDataException("The selected Tailscale device identity is invalid.");
        if (!RemoteAdministrationProtocol.IsTailscaleIpv4(address))
            throw new InvalidDataException("The selected device has no canonical Tailscale IPv4 address.");
        if (!Guid.TryParseExact(operationText, "N", out var operationId) || operationId == Guid.Empty)
            throw new InvalidDataException("The maintenance operation ID is invalid.");
        return new MaintenanceExpectedTarget(deviceId, address, operationId);
    }
}

internal sealed class MaintenanceBootstrapCoordinator
{
    private const int AgentPort = 45831;
    private const int RustDeskPort = 21118;
    private const string HealthHeader = "X-Opticon-Update-Health";
    private readonly string _bundleDirectory;
    private readonly IProgress<InstallProgress> _progress;
    private readonly MaintenanceExpectedTarget _expectedTarget;

    public MaintenanceBootstrapCoordinator(
        string bundleDirectory,
        IProgress<InstallProgress> progress,
        MaintenanceExpectedTarget expectedTarget)
    {
        _bundleDirectory = Path.GetFullPath(bundleDirectory);
        _progress = progress;
        _expectedTarget = expectedTarget;
    }

    public static async Task<MaintenanceTargetSummary> LoadTargetSummaryAsync(
        MaintenanceExpectedTarget expectedTarget,
        CancellationToken cancellationToken = default)
    {
        var config = await LoadEnrolledConfigAsync(cancellationToken);
        await ValidateExpectedTargetAsync(expectedTarget, config, cancellationToken);
        var executable = InstalledAgentExecutable();
        if (!File.Exists(executable))
            throw new FileNotFoundException("The enrolled Opticon Agent is missing from its stable installation path.", executable);
        var currentVersion = ReadVersion(executable, "installed Agent");
        return new MaintenanceTargetSummary(config.DeviceName, config.Role, config.CoordinatorUrl, currentVersion);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        EnsureElevatedAdministrator();
        MachineStorageSecurity.EnsureOpticonMachineState();
        var setupExecutable = Environment.ProcessPath
                              ?? throw new InvalidOperationException("Windows did not identify the running Setup executable.");
        if (!Path.GetFileName(setupExecutable).Equals("Taildesk.Setup.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(Path.GetFullPath(setupExecutable))!
                .Equals(_bundleDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Maintenance must run from Taildesk.Setup.exe at the root of the extracted signed release bundle.");
        await ProductSigning.VerifyAuthenticodeAsync(setupExecutable, cancellationToken);

        _progress.Report(new InstallProgress(3, "Loading the existing enrolled Agent without changing its identity…"));
        var config = await LoadEnrolledConfigAsync(cancellationToken);
        await ValidateExpectedTargetAsync(_expectedTarget, config, cancellationToken);
        var installedAgent = InstalledAgentExecutable();
        if (!File.Exists(installedAgent))
            throw new FileNotFoundException("The enrolled Opticon Agent is missing from its stable installation path.", installedAgent);
        await ProductSigning.VerifyAuthenticodeAsync(installedAgent, cancellationToken);
        var currentVersion = ReadVersion(installedAgent, "installed Agent");

        _progress.Report(new InstallProgress(8, "Verifying the signed maintenance release identity…"));
        var signedRelease = await LoadSignedReleaseAsync(cancellationToken);
        var manifest = signedRelease.Manifest;
        await ValidateDeclaredRootFileAsync(
            manifest, "Taildesk.Setup.exe", setupExecutable, cancellationToken);
        var setupVersion = ReadVersion(setupExecutable, "running Setup");
        if (!setupVersion.Equals(UpdatePackageVerifier.NormalizeVersion(manifest.Version), StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The signed Setup version {setupVersion} does not match release {manifest.Version}; mixed release contents are refused.");
        var architecture = CurrentArchitecture();
        ValidateReleaseIdentity(manifest, config, architecture, currentVersion);

        var sourceGuardianDirectory = Path.Combine(_bundleDirectory, "Payload", "UpdateGuardian");
        var sourceGuardian = Path.Combine(sourceGuardianDirectory, "Taildesk.UpdateGuardian.exe");
        await ValidateDeclaredPayloadAsync(
            manifest, "Payload/UpdateGuardian/", sourceGuardianDirectory, cancellationToken);
        await ProductSigning.VerifyAuthenticodeAsync(sourceGuardian, cancellationToken);
        ValidateGuardianVersion(sourceGuardian, manifest.MinimumGuardianVersion);

        _progress.Report(new InstallProgress(14, "Checking Tailscale and RustDesk recovery lifelines…"));
        EnsureRecoveryLifelines(config.BindAddress);
        EnsureNoActiveUpdate();

        _progress.Report(new InstallProgress(18, "Preparing Windows OpenSSH before the Agent transaction…"));
        await InstallCoordinator.EnsureOpenSshServerCapabilityAsync(cancellationToken);
        if (config.Role == DeviceRole.ControllerAndManaged)
            await InstallCoordinator.EnsureOpenSshClientCapabilityAsync(cancellationToken);

        // Capability installation is deliberately complete before any update
        // journal can tell the Guardian to stop or replace the legacy Agent.
        EnsureRecoveryLifelines(config.BindAddress);

        _progress.Report(new InstallProgress(27, "Installing the stable signed update Guardian…"));
        await EnsurePrivateUpdateDirectoryAsync(cancellationToken);
        await InstallStableGuardianAsync(manifest, sourceGuardianDirectory, cancellationToken);
        await RequireInstalledGuardianCompatibilityAsync(manifest, sourceGuardian, cancellationToken);
        await InstallGuardianTaskAsync(cancellationToken);

        _progress.Report(new InstallProgress(36, "Adding the local protected Guardian health credential…"));
        await ProtectAgentConfigurationAsync(cancellationToken);
        var healthToken = await EnsureHealthTokenAsync(config, cancellationToken);

        var operationId = _expectedTarget.OperationId;
        var operationDirectory = Path.Combine(AppPaths.UpdateDataDirectory, operationId.ToString("N"));
        MachineStorageSecurity.EnsureRestrictedDirectoryTree(
            AppPaths.MachineDataDirectory, operationDirectory);
        MachineStorageSecurity.RequireRestrictedDirectory(operationDirectory);
        var packagePath = Path.Combine(operationDirectory, "package.zip");
        var stagedAgent = Path.Combine(operationDirectory, "staged-agent");

        _progress.Report(new InstallProgress(43, "Repacking only signed release metadata and Agent files for protected staging…"));
        await BuildAgentPackageAsync(manifest, signedRelease, packagePath, cancellationToken);
        MachineStorageSecurity.SealRestrictedFile(packagePath);
        var packageSize = new FileInfo(packagePath).Length;
        string packageSha256;
        await using (var package = File.OpenRead(packagePath))
            packageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken)).ToLowerInvariant();

        var request = new OpticonUpdateRequest
        {
            OperationId = operationId,
            TargetVersion = manifest.Version,
            Role = config.Role,
            Architecture = architecture,
            DownloadUrl = "https://maintenance-bootstrap.invalid/package.zip",
            PackageSize = packageSize,
            PackageSha256 = packageSha256
        };

        _progress.Report(new InstallProgress(52, "Re-verifying every staged Agent file, hash, publisher, and version…"));
        await UpdatePackageVerifier.VerifyAndExtractAgentAsync(
            packagePath, stagedAgent, request, cancellationToken);

        var journal = new UpdateJournal
        {
            OperationId = operationId,
            Phase = UpdatePhase.ActivationScheduled,
            MaintenanceBootstrap = true,
            CurrentVersion = currentVersion,
            TargetVersion = UpdatePackageVerifier.NormalizeVersion(manifest.Version),
            Role = config.Role,
            Architecture = architecture,
            PackagePath = packagePath,
            PackageSha256 = packageSha256,
            PackageSize = packageSize,
            StagedAgentDirectory = stagedAgent,
            CandidateDirectory = AppPaths.AgentInstallDirectory + ".candidate-" + operationId.ToString("N"),
            RollbackDirectory = AppPaths.AgentInstallDirectory + ".rollback-" + operationId.ToString("N"),
            FailedCandidateDirectory = AppPaths.AgentInstallDirectory + ".failed-" + operationId.ToString("N"),
            BindAddress = config.BindAddress,
            AgentProcessId = 0,
            Message = "Signed one-time maintenance activation is scheduled. Missing candidate health or commit causes automatic rollback."
        };
        using (await UpdateJournalCoordination.AcquireAsync(TimeSpan.FromMinutes(20), cancellationToken))
        {
            // This second check is authoritative: a boot-time Guardian must be
            // completely idle before maintenance replaces the durable journal.
            EnsureNoActiveUpdate();
            var durableConfig = await LoadEnrolledConfigAsync(cancellationToken);
            if (durableConfig.DeviceId != config.DeviceId
                || durableConfig.Role != config.Role
                || !durableConfig.BindAddress.Equals(config.BindAddress, StringComparison.Ordinal)
                || !durableConfig.CoordinatorUrl.Equals(config.CoordinatorUrl, StringComparison.Ordinal))
                throw new InvalidOperationException("The enrolled device identity changed while maintenance was being prepared.");

            await ValidateExpectedTargetAsync(_expectedTarget, durableConfig, cancellationToken);
            healthToken = UpdateHealthTokenStore.Load(
                durableConfig.UpdateHealthTokenProtected, durableConfig.DeviceId);
            await ProductSigning.VerifyAuthenticodeAsync(installedAgent, cancellationToken);
            var durableVersion = ReadVersion(installedAgent, "installed Agent");
            if (!durableVersion.Equals(currentVersion, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The installed Agent changed from {currentVersion} to {durableVersion} while maintenance was being prepared.");
            EnsureRecoveryLifelines(config.BindAddress);

            var scheduledAt = DateTimeOffset.UtcNow;
            journal.StartedAt = scheduledAt;
            journal.UpdatedAt = scheduledAt;
            journal.GuardianClaimedAt = null;
            journal.SshWasListening = false;
            journal.ActivateAfter = scheduledAt.AddSeconds(12);
            journal.CommitDeadline = journal.ActivateAfter.Value.Add(RemoteAdministrationProtocol.UpdateCommitWindow);
            MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.UpdateCommitRequestFile);
            await UpdateJournalPersistence.SaveAsync(journal, cancellationToken: cancellationToken);
        }

        _progress.Report(new InstallProgress(61, "Starting the fail-safe Guardian; the legacy Agent remains the rollback copy…"));
        try
        {
            await StartGuardianAndWaitForPickupAsync(journal.OperationId, cancellationToken);
        }
        catch (Exception startFailure)
        {
            using (await UpdateJournalCoordination.AcquireAsync(TimeSpan.FromMinutes(2), CancellationToken.None))
            {
                var durable = UpdateJournalPersistence.Load();
                if (durable?.OperationId == journal.OperationId
                    && durable.Phase == UpdatePhase.ActivationScheduled)
                {
                    durable.Phase = UpdatePhase.Failed;
                    durable.ActivateAfter = null;
                    durable.GuardianClaimedAt = null;
                    durable.SshWasListening = false;
                    durable.CommitDeadline = null;
                    durable.AgentProcessId = 0;
                    durable.Message =
                        "Windows could not start the update Guardian; the installed Agent was not changed. " +
                        BoundedDiagnostic(startFailure.Message);
                    await UpdateJournalPersistence.SaveAsync(durable, cancellationToken: CancellationToken.None);
                }
            }
            throw new InvalidOperationException(
                "Windows could not start the update Guardian; the installed Agent was not changed and maintenance can be retried.",
                startFailure);
        }

        await ObserveCandidateAndWaitForExternalCommitAsync(journal, config, healthToken, cancellationToken);
        _progress.Report(new InstallProgress(100,
            $"Opticon Agent {journal.TargetVersion} is healthy and committed. Enrollment, Tailscale, RustDesk, routes, credentials, and Admin were unchanged."));
    }

    private static async Task<AgentConfig> LoadEnrolledConfigAsync(CancellationToken cancellationToken)
    {
        MachineStorageSecurity.EnsureOpticonMachineState();
        if (!File.Exists(AppPaths.AgentConfigFile))
            throw new FileNotFoundException("Maintenance mode requires an existing enrolled Opticon Agent.", AppPaths.AgentConfigFile);
        var config = await new MachineJsonFileStore<AgentConfig>(AppPaths.AgentConfigFile)
            .LoadAsync(cancellationToken);
        if (config.DeviceId == Guid.Empty
            || config.CompletedInviteId is null
            || config.PendingInviteId is not null
            || string.IsNullOrWhiteSpace(config.AgentTokenHash)
            || config.SharedRoots.Count == 0
            || config.ApiPort != AgentPort
            || !RemoteAdministrationProtocol.IsTailscaleIpv4(config.BindAddress)
            || !Uri.TryCreate(config.CoordinatorUrl, UriKind.Absolute, out var coordinator)
            || !RemoteAdministrationProtocol.IsTailscaleIpv4(coordinator.Host))
        {
            throw new InvalidOperationException(
                "Maintenance mode is restricted to a fully enrolled Agent with fixed Tailscale IPv4 endpoints and the standard private API port.");
        }
        return config;
    }

    private async Task<SignedRelease> LoadSignedReleaseAsync(CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_bundleDirectory, UpdatePackageVerifier.ManifestEntryName);
        var signaturePath = Path.Combine(_bundleDirectory, UpdatePackageVerifier.SignatureEntryName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
            throw new FileNotFoundException("Maintenance mode must run from an extracted signed Opticon release bundle.");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (manifestBytes.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("The signed release manifest has an invalid size.");
        var signatureLength = new FileInfo(signaturePath).Length;
        if (signatureLength is <= 0 or > 64 * 1024)
            throw new InvalidDataException("The signed release signature has an invalid size.");
        var signatureText = (await File.ReadAllTextAsync(signaturePath, cancellationToken)).Trim();
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException exception) { throw new InvalidDataException("The release signature is malformed.", exception); }
        if (!SourceReleaseSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The release manifest signature is invalid.");
        var manifest = JsonSerializer.Deserialize<OpticonReleaseManifest>(manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The signed release manifest is empty.");
        if (manifest.Files is null)
            throw new InvalidDataException("The signed release manifest has no file list.");
        return new SignedRelease(manifest, manifestBytes, Encoding.UTF8.GetBytes(signatureText));
    }

    private static void ValidateReleaseIdentity(
        OpticonReleaseManifest manifest,
        AgentConfig config,
        string architecture,
        string currentVersion)
    {
        if (manifest.SchemaVersion != 1
            || manifest.SigningProfile != BuildSigningTrust.ProfileName
            || !BuildSigningTrust.IsPublishable
            || manifest.SourceReleaseKeyId != SourceReleaseSigning.KeyId
            || manifest.ProductSignerThumbprint != ProductSigning.CertificateThumbprint
            || manifest.UpdateProtocolVersion != RemoteAdministrationProtocol.UpdateVersion
            || manifest.Role != config.Role
            || !manifest.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The signed release does not match the production trust policy or this enrolled device role, architecture, and update protocol.");
        var current = UpdatePackageVerifier.ParseVersion(currentVersion);
        var target = UpdatePackageVerifier.ParseVersion(manifest.Version);
        if (target < current)
            throw new InvalidOperationException($"Maintenance cannot downgrade the Agent; installed is {currentVersion}, bundle is {manifest.Version}.");
        if (target == current)
        {
            var installedGuardian = Path.Combine(
                AppPaths.UpdateGuardianInstallDirectory,
                "Taildesk.UpdateGuardian.exe");
            if (!File.Exists(installedGuardian)
                || UpdatePackageVerifier.ParseVersion(ReadVersion(installedGuardian, "installed Guardian")) >= target)
                throw new InvalidOperationException(
                    $"Maintenance requires a newer Agent or Guardian; both installed components already match {currentVersion}.");
        }
        _ = UpdatePackageVerifier.ParseVersion(manifest.MinimumGuardianVersion);
    }

    private static async Task ValidateDeclaredRootFileAsync(
        OpticonReleaseManifest manifest,
        string expectedPath,
        string source,
        CancellationToken cancellationToken)
    {
        var files = manifest.Files
            .Where(file => NormalizeBundlePath(file.Path).Equals(expectedPath, StringComparison.Ordinal))
            .ToArray();
        if (files.Length != 1)
            throw new InvalidDataException($"The signed release must declare exactly one {expectedPath}.");
        var declaration = files[0];
        ValidateDeclaredFile(declaration, expectedPath);
        if (!Path.GetFullPath(source).Equals(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, expectedPath),
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(source)
            || new FileInfo(source).Length != declaration.Size)
            throw new InvalidDataException($"The extracted bundle is missing or changed: {expectedPath}");
        await using var stream = File.OpenRead(source);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!hash.Equals(declaration.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The extracted bundle file hash changed: {expectedPath}");
        if (!declaration.SignerThumbprint.Equals(
                ProductSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The signed publisher pin is invalid: {expectedPath}");
    }

    private async Task ValidateDeclaredPayloadAsync(
        OpticonReleaseManifest manifest,
        string prefix,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var files = manifest.Files.Where(file => NormalizeBundlePath(file.Path).StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (files.Length == 0 || files.Select(file => NormalizeBundlePath(file.Path)).Distinct(StringComparer.Ordinal).Count() != files.Length)
            throw new InvalidDataException($"The signed release has no valid {prefix} payload.");
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeBundlePath(file.Path);
            ValidateDeclaredFile(file, normalized);
            var relative = normalized[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var source = Path.GetFullPath(Path.Combine(sourceDirectory, relative));
            EnsureDescendant(sourceDirectory, source);
            if (!File.Exists(source) || new FileInfo(source).Length != file.Size)
                throw new InvalidDataException($"The extracted bundle is missing or changed: {normalized}");
            await using var stream = File.OpenRead(source);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The extracted bundle file hash changed: {normalized}");
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                await ProductSigning.VerifyAuthenticodeAsync(source, cancellationToken);
        }
    }

    private async Task InstallStableGuardianAsync(
        OpticonReleaseManifest manifest,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var destination = AppPaths.UpdateGuardianInstallDirectory;
        var installedExecutable = Path.Combine(destination, "Taildesk.UpdateGuardian.exe");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            if (!File.Exists(installedExecutable))
                throw new InvalidDataException("An incomplete existing Guardian installation blocks maintenance; it was not overwritten.");
            await ProductSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
            await StableGuardianMaintenance.ReconcileSignedReleaseAsync(sourceDirectory, destination, cancellationToken);
            await ProductSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
            ValidateGuardianVersion(installedExecutable, manifest.MinimumGuardianVersion);
            return;
        }

        var temporary = destination + ".installing-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporary);
            var files = manifest.Files
                .Where(file => NormalizeBundlePath(file.Path).StartsWith("Payload/UpdateGuardian/", StringComparison.Ordinal))
                .ToArray();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizeBundlePath(file.Path);
                var relative = normalized["Payload/UpdateGuardian/".Length..].Replace('/', Path.DirectorySeparatorChar);
                var source = Path.GetFullPath(Path.Combine(sourceDirectory, relative));
                var target = Path.GetFullPath(Path.Combine(temporary, relative));
                EnsureDescendant(sourceDirectory, source);
                EnsureDescendant(temporary, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
            }
            await ProductSigning.VerifyAuthenticodeAsync(Path.Combine(temporary, "Taildesk.UpdateGuardian.exe"), cancellationToken);
            // The temporary directory is on the same volume and outside the
            // fixed path. A crash can leave only a disposable temp tree; the
            // stable Guardian path appears in one atomic directory promotion.
            Directory.Move(temporary, destination);
        }
        catch
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            throw;
        }
        await ProductSigning.VerifyAuthenticodeAsync(installedExecutable, cancellationToken);
        ValidateGuardianVersion(installedExecutable, manifest.MinimumGuardianVersion);
    }

    private async Task BuildAgentPackageAsync(
        OpticonReleaseManifest manifest,
        SignedRelease signedRelease,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var agentFiles = manifest.Files
            .Where(file => NormalizeBundlePath(file.Path).StartsWith("Payload/Agent/", StringComparison.Ordinal))
            .ToArray();
        if (agentFiles.Length == 0) throw new InvalidDataException("The signed release has no Agent payload.");
        await using var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteZipEntryAsync(archive, UpdatePackageVerifier.ManifestEntryName, signedRelease.ManifestBytes, cancellationToken);
        await WriteZipEntryAsync(archive, UpdatePackageVerifier.SignatureEntryName, signedRelease.SignatureTextBytes, cancellationToken);
        foreach (var file in agentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeBundlePath(file.Path);
            ValidateDeclaredFile(file, normalized);
            var source = Path.GetFullPath(Path.Combine(_bundleDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
            EnsureDescendant(_bundleDirectory, source);
            if (!File.Exists(source)) throw new FileNotFoundException("A signed Agent payload file is missing.", source);
            var entry = archive.CreateEntry(normalized, CompressionLevel.Optimal);
            await using var target = entry.Open();
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await input.CopyToAsync(target, 1024 * 1024, cancellationToken);
        }
    }

    private async Task ObserveCandidateAndWaitForExternalCommitAsync(
        UpdateJournal journal,
        AgentConfig config,
        string healthToken,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        }) { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = journal.CommitDeadline ?? DateTimeOffset.UtcNow.Add(RemoteAdministrationProtocol.UpdateCommitWindow);
        var healthySamples = 0;
        string lastFailure = "The replacement Agent has not started.";
        while (DateTimeOffset.UtcNow < deadline.Subtract(TimeSpan.FromSeconds(12)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var current = UpdateJournalPersistence.Load();
            if (current?.OperationId != journal.OperationId)
                throw new InvalidDataException("The protected update journal changed to a different operation.");
            if (current.Phase is UpdatePhase.RolledBack or UpdatePhase.Failed)
                throw new InvalidOperationException("The Guardian refused the maintenance candidate: " + current.Message);
            if (current.Phase != UpdatePhase.AwaitingCommit)
            {
                healthySamples = 0;
                continue;
            }
            var health = await CheckCandidateHealthAsync(http, config, journal, healthToken, cancellationToken);
            if (health is null)
            {
                healthySamples++;
                _progress.Report(new InstallProgress(65 + healthySamples * 6,
                    $"Replacement Agent protected health sample {healthySamples}/3 passed…"));
                if (healthySamples >= 3) break;
            }
            else
            {
                healthySamples = 0;
                lastFailure = health;
            }
        }
        if (healthySamples < 3)
            throw new TimeoutException("The replacement Agent did not pass three protected local health samples. The Guardian will roll back by omission. " + lastFailure);

        await ValidateExpectedTargetAsync(_expectedTarget, config, cancellationToken);
        _progress.Report(new InstallProgress(86,
            "Local protected checks passed. Waiting for this Opticon command center's authenticated external confirmation…"));

        var terminalDeadline = deadline.AddMinutes(2);
        while (DateTimeOffset.UtcNow < terminalDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = UpdateJournalPersistence.Load();
            if (current?.OperationId != journal.OperationId)
                throw new InvalidDataException("The protected update journal changed while awaiting external confirmation.");
            if (!current.MaintenanceBootstrap
                || !UpdatePackageVerifier.NormalizeVersion(current.TargetVersion)
                    .Equals(journal.TargetVersion, StringComparison.Ordinal))
                throw new InvalidDataException("The protected maintenance transaction identity changed.");
            if (current.Phase == UpdatePhase.Committed) return;
            if (current.Phase is UpdatePhase.RolledBack or UpdatePhase.Failed)
                throw new InvalidOperationException("Maintenance was not committed: " + current.Message);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException(
            "No authenticated external commit arrived for this operation before the bounded deadline. The Guardian will roll back by omission.");
    }

    private static async Task<string?> CheckCandidateHealthAsync(
        HttpClient http,
        AgentConfig config,
        UpdateJournal journal,
        string healthToken,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureRecoveryLifelines(config.BindAddress);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"http://{config.BindAddress}:{AgentPort}/internal/update-health");
            request.Headers.TryAddWithoutValidation(HealthHeader, healthToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return $"protected Agent health returned HTTP {(int)response.StatusCode}";
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > 64 * 1024) return "protected Agent health response was too large";
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!TryGetGuid(root, "deviceId", out var deviceId) || deviceId != config.DeviceId
                || !TryGetGuid(root, "operationId", out var operationId) || operationId != journal.OperationId
                || !TryGetString(root, "agentVersion", out var version)
                || !UpdatePackageVerifier.NormalizeVersion(version).Equals(journal.TargetVersion, StringComparison.Ordinal)
                || !TryGetString(root, "updatePhase", out var phase) || phase != UpdatePhase.AwaitingCommit.ToString()
                || !TryGetString(root, "bindAddress", out var address) || address != config.BindAddress
                || !TryGetBoolean(root, "rustDeskReady", out var rustDeskReady) || !rustDeskReady)
                return "protected Agent health identity, version, phase, address, or RustDesk state did not match";
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception.Message;
        }
    }

    private static Task<string> EnsureHealthTokenAsync(
        AgentConfig config,
        CancellationToken cancellationToken) =>
        UpdateHealthTokenStore.LoadOrCreateSidecarAsync(
            config.UpdateHealthTokenProtected,
            config.DeviceId,
            cancellationToken: cancellationToken);

    private static async Task ValidateExpectedTargetAsync(
        MaintenanceExpectedTarget expected,
        AgentConfig config,
        CancellationToken cancellationToken)
    {
        if (!config.BindAddress.Equals(expected.TailscaleIp, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This maintenance command was copied for a different Opticon device address.");

        var tailscale = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tailscale", "tailscale.exe");
        if (!File.Exists(tailscale))
            throw new FileNotFoundException(
                "The fixed Program Files Tailscale CLI is unavailable; selected-device identity cannot be verified.", tailscale);
        tailscale = RequireFixedExecutable(
            tailscale, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var status = await ProcessRunner.RunAsync(
            tailscale, ["status", "--json"], TimeSpan.FromSeconds(30), cancellationToken,
            environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
        if (!status.Succeeded || string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new InvalidOperationException("Tailscale could not verify the selected device identity.");
        if (status.StandardOutput.Length > 1024 * 1024)
            throw new InvalidDataException("Tailscale returned an unexpectedly large identity response.");

        using var document = JsonDocument.Parse(status.StandardOutput, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        if (!root.TryGetProperty("BackendState", out var backendState)
            || !string.Equals(backendState.GetString(), "Running", StringComparison.Ordinal)
            || !root.TryGetProperty("Self", out var self)
            || self.ValueKind != JsonValueKind.Object
            || !self.TryGetProperty("ID", out var idProperty)
            || idProperty.ValueKind != JsonValueKind.String
            || !string.Equals(idProperty.GetString(), expected.TailnetDeviceId, StringComparison.Ordinal)
            || !self.TryGetProperty("TailscaleIPs", out var addressProperty)
            || addressProperty.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "This maintenance command does not match the active Tailscale device identity.");

        var ipv4 = addressProperty.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString() ?? string.Empty)
            .Where(RemoteAdministrationProtocol.IsTailscaleIpv4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ipv4.Length != 1 || !ipv4[0].Equals(expected.TailscaleIp, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This maintenance command does not match the active Tailscale IPv4 identity.");
    }

    private static async Task RequireInstalledGuardianCompatibilityAsync(
        OpticonReleaseManifest manifest,
        string sourceGuardian,
        CancellationToken cancellationToken)
    {
        const string prefix = "Payload/UpdateGuardian/";
        var installedRoot = AppPaths.UpdateGuardianInstallDirectory;
        var installedGuardian = Path.Combine(installedRoot, "Taildesk.UpdateGuardian.exe");
        var installed = Directory.EnumerateFiles(installedRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(installedRoot, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
        var sourceVersion = UpdatePackageVerifier.ParseVersion(ReadVersion(sourceGuardian, "release Guardian"));
        var installedVersion = UpdatePackageVerifier.ParseVersion(ReadVersion(installedGuardian, "installed Guardian"));
        var minimumVersion = UpdatePackageVerifier.ParseVersion(manifest.MinimumGuardianVersion);
        if (installedVersion != sourceVersion)
        {
            if (installedVersion >= minimumVersion
                && installed.Count == 1
                && installed.ContainsKey("Taildesk.UpdateGuardian.exe"))
                return;
            if (installedVersion < minimumVersion)
                throw new InvalidOperationException(
                    $"The installed stable Guardian {installedVersion} predates this release's required Guardian {minimumVersion} and was not overwritten. " +
                    "Use attended stable-Guardian maintenance before updating the Agent.");
            throw new InvalidOperationException(
                $"The stable Guardian {installedVersion} has companion files this signed release cannot attest. " +
                "Use attended stable-Guardian maintenance before updating the Agent.");
        }

        var declared = manifest.Files
            .Where(file => NormalizeBundlePath(file.Path).StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                file => NormalizeBundlePath(file.Path)[prefix.Length..],
                StringComparer.OrdinalIgnoreCase);
        if (declared.Count == 0)
            throw new InvalidDataException("The signed release declares no update Guardian payload.");

        if (declared.Count != installed.Count
            || declared.Keys.Except(installed.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidOperationException(
                "The existing stable Guardian contents differ from the signed release and were not overwritten. " +
                "Use attended stable-Guardian maintenance before updating the Agent.");

        foreach (var (relative, declaration) in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = installed[relative];
            if (new FileInfo(path).Length != declaration.Size)
                throw new InvalidOperationException(
                    $"The existing stable Guardian file has the wrong size: {relative}");
            await using var input = File.OpenRead(path);
            var actualHash = await SHA256.HashDataAsync(input, cancellationToken);
            var expectedHash = Convert.FromHexString(declaration.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidOperationException(
                    $"The existing stable Guardian file differs from the signed release: {relative}");
        }
    }

    private static Task ProtectAgentConfigurationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MachineStorageSecurity.EnsureOpticonMachineState();
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.AgentDataDirectory);
        MachineStorageSecurity.RequireRestrictedFileIfExists(AppPaths.AgentConfigFile);
        return Task.CompletedTask;
    }

    private static Task EnsurePrivateUpdateDirectoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MachineStorageSecurity.EnsureOpticonMachineState();
        MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.UpdateDataDirectory);
        return Task.CompletedTask;
    }

    private static async Task StartGuardianAndWaitForPickupAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        // A full Guardian may legitimately wait two minutes behind a finishing
        // watchdog/full invocation. Setup must outlive that mutex contract.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2.5);
        string lastError = string.Empty;
        UpdateGuardianStartupDiagnostics.Clear();
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = await ProcessRunner.RunAsync(
                RequireSystemExecutable("schtasks.exe"), ["/Run", "/TN", RemoteAdministrationProtocol.GuardianTaskName],
                TimeSpan.FromSeconds(10), cancellationToken,
                environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
            if (!started.Succeeded)
                lastError = string.Join(" ", started.StandardOutput.Trim(), started.StandardError.Trim()).Trim();

            var observationDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (DateTimeOffset.UtcNow < observationDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var durable = UpdateJournalPersistence.Load();
                if (durable?.OperationId != operationId)
                    throw new InvalidDataException("The protected update journal changed while the Guardian was starting.");
                if (durable.Phase is UpdatePhase.Failed or UpdatePhase.RolledBack)
                    throw new InvalidOperationException("The Guardian refused the maintenance candidate: " + durable.Message);
                if (durable.Phase != UpdatePhase.ActivationScheduled
                    || durable.GuardianClaimedAt is not null)
                    return;
                var failure = UpdateGuardianStartupDiagnostics.Read();
                if (failure is not null
                    && failure.Mode.Equals("full", StringComparison.Ordinal)
                    && (failure.OperationId == Guid.Empty || failure.OperationId == operationId))
                    throw new InvalidOperationException(
                        "The scheduled Guardian exited before claiming maintenance: " +
                        BoundedDiagnostic(failure.Error));
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        var taskState = await ProcessRunner.RunAsync(
            RequireSystemExecutable("schtasks.exe"),
            ["/Query", "/TN", RemoteAdministrationProtocol.GuardianTaskName, "/V", "/FO", "LIST"],
            TimeSpan.FromSeconds(15), cancellationToken,
            environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
        var taskDetail = BoundedDiagnostic(
            string.Join(" ", taskState.StandardOutput.Trim(), taskState.StandardError.Trim()).Trim());

        throw new InvalidOperationException(
            "The fail-safe Guardian did not pick up the scheduled maintenance transaction after its bounded mutex wait. " +
            BoundedDiagnostic(lastError) + " Task Scheduler: " + taskDetail);
    }

    private static string BoundedDiagnostic(string value)
    {
        value = string.Join(" ", value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (value.Length == 0) return "No additional diagnostic was reported.";
        return value.Length <= 2_000 ? value : value[..2_000];
    }

    private static async Task InstallGuardianTaskAsync(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe");
        var bootTask = await ProcessRunner.RunAsync(
            RequireSystemExecutable("schtasks.exe"),
            [
                "/Create", "/TN", RemoteAdministrationProtocol.GuardianTaskName,
                "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST",
                "/TR", $"\"{executable}\"", "/F"
            ],
            TimeSpan.FromSeconds(30), cancellationToken,
            environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
        if (!bootTask.Succeeded)
            throw new InvalidOperationException("Windows could not create the fail-safe maintenance Guardian task: " + bootTask.StandardError.Trim());

        var watchdogCommand = $"\"{executable}\" {RemoteAdministrationProtocol.GuardianWatchdogArgument}";
        var watchdogTask = await ProcessRunner.RunAsync(
            RequireSystemExecutable("schtasks.exe"),
            [
                "/Create", "/TN", RemoteAdministrationProtocol.GuardianWatchdogTaskName,
                "/SC", "MINUTE", "/MO", "1", "/RU", "SYSTEM", "/RL", "HIGHEST",
                "/TR", watchdogCommand, "/F"
            ],
            TimeSpan.FromSeconds(30), cancellationToken,
            environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
        if (!watchdogTask.Succeeded)
            throw new InvalidOperationException(
                "Windows could not create the fail-safe maintenance Guardian watchdog task: " +
                watchdogTask.StandardError.Trim());

        var bootSettings =
            "$boot=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable " +
            "-RestartCount 20 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Seconds 0) " +
            "-MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianTaskName}' -Settings $boot | Out-Null";
        var watchdogSettings =
            "$watchdog=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew; " +
            $"Set-ScheduledTask -TaskName '{RemoteAdministrationProtocol.GuardianWatchdogTaskName}' -Settings $watchdog | Out-Null";
        var settingsCommand = bootSettings + "; " + watchdogSettings;
        var configured = await ProcessRunner.RunAsync(
            RequireSystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted", "-Command", settingsCommand],
            TimeSpan.FromSeconds(30), cancellationToken,
            environment: BuildPrivilegedEnvironment(), clearEnvironment: true);
        if (!configured.Succeeded)
            throw new InvalidOperationException(
                "Windows could not apply fail-safe maintenance Guardian recovery/watchdog task settings.");
    }

    private static void EnsureNoActiveUpdate()
    {
        UpdateJournal? existing;
        try { existing = UpdateJournalPersistence.Load(); }
        catch (Exception exception) { throw new InvalidDataException("The existing protected update journal cannot be read.", exception); }
        if (existing is not null && existing.Phase is not UpdatePhase.None and not UpdatePhase.Failed and not UpdatePhase.RolledBack)
            throw new InvalidOperationException(
                $"Maintenance cannot replace update transaction {existing.OperationId:N}, which is {existing.Phase}.");
    }

    private static void EnsureRecoveryLifelines(string bindAddress)
    {
        if (!RemoteAdministrationProtocol.IsTailscaleIpv4(bindAddress)
            || !IPAddress.TryParse(bindAddress, out var expected)
            || !NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(address => address.Address.Equals(expected)))
            throw new InvalidOperationException("The enrolled Tailscale IPv4 address is not assigned to an active local interface.");
        var rustDesk = Process.GetProcessesByName("rustdesk");
        try
        {
            if (rustDesk.Length == 0
                || !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == RustDeskPort))
                throw new InvalidOperationException("RustDesk process and TCP 21118 must be healthy before maintenance.");
        }
        finally { foreach (var process in rustDesk) process.Dispose(); }
    }

    private static void ValidateGuardianVersion(string executable, string minimumVersion)
    {
        var installed = ReadVersion(executable, "update Guardian");
        if (UpdatePackageVerifier.ParseVersion(installed) < UpdatePackageVerifier.ParseVersion(minimumVersion))
            throw new InvalidOperationException(
                $"The existing signed Guardian is {installed}, but this release requires {minimumVersion}. It was not overwritten.");
    }

    private static void ValidateDeclaredFile(OpticonReleaseFile file, string normalized)
    {
        if (!file.Path.Equals(normalized, StringComparison.Ordinal)
            || normalized.Length == 0
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':')
            || file.Size < 0
            || file.Sha256.Length != 64
            || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The signed release contains unsafe file metadata.");
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !file.SignerThumbprint.Equals(ProductSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The signed release executable publisher pin is invalid: {normalized}");
    }

    private static async Task WriteZipEntryAsync(
        ZipArchive archive,
        string name,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var target = entry.Open();
        await target.WriteAsync(bytes, cancellationToken);
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryGetString(root, name, out var text) && Guid.TryParse(text, out value);
    }

    private static bool TryGetBoolean(JsonElement root, string name, out bool value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static void EnsureElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Maintenance mode must be started with Windows administrator approval.");
    }

    private static string InstalledAgentExecutable() =>
        Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");

    private static string CurrentArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"Maintenance does not support Windows {RuntimeInformation.OSArchitecture}.")
    };

    private static string ReadVersion(string executable, string description)
    {
        var version = UpdatePackageVerifier.NormalizeVersion(
            FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? string.Empty);
        _ = UpdatePackageVerifier.ParseVersion(version);
        return version.Length > 0 ? version : throw new InvalidDataException($"The {description} has no valid product version.");
    }

    private static string NormalizeBundlePath(string value) => value.Replace('\\', '/').TrimStart('/');

    private static void EnsureDescendant(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A signed maintenance payload path escapes the extracted bundle.");
    }

    private static string RequireSystemExecutable(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException("A fixed Windows system executable path is invalid.");
        var system = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System));
        return RequireFixedExecutable(Path.Combine(system, relativePath), system);
    }

    private static string RequireFixedExecutable(string path, string allowedRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full))
            throw new FileNotFoundException("A fixed privileged executable is missing or outside its trusted root.", full);

        var current = full;
        while (true)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A fixed privileged executable path contains a reparse point.");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidDataException("A fixed privileged executable escaped its trusted root.");
        }
        if ((File.GetAttributes(full) & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("The fixed privileged executable path is a directory.", full);
        return full;
    }

    private static IReadOnlyDictionary<string, string?> BuildPrivilegedEnvironment()
    {
        var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var system = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System));
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows),
            ["ProgramData"] = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ["ProgramFiles"] = programFiles,
            ["ProgramW6432"] = programFiles,
            ["CommonProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            ["ComSpec"] = RequireSystemExecutable("cmd.exe"),
            ["PATH"] = string.Join(Path.PathSeparator, system, windows, Path.Combine(system, "Wbem")),
            ["PATHEXT"] = ".COM;.EXE",
            ["PSModulePath"] = Path.Combine(system, "WindowsPowerShell", "v1.0", "Modules"),
            ["TEMP"] = AppPaths.UpdateDataDirectory,
            ["TMP"] = AppPaths.UpdateDataDirectory
        };
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            environment["ProgramFiles(x86)"] = Path.GetFullPath(programFilesX86);
        return environment;
    }

    private sealed record SignedRelease(
        OpticonReleaseManifest Manifest,
        byte[] ManifestBytes,
        byte[] SignatureTextBytes);
}
