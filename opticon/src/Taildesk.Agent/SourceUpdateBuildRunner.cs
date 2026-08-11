using System.Security.Cryptography;
using Taildesk.Shared;

namespace Taildesk.Agent;

/// <summary>
/// Runs the signed source archive's fixed build entrypoint with a deliberately
/// sparse process environment.  This is not a generic script runner: every
/// path, SDK, archive pin, and build output location is supplied by the
/// verified source-update transaction.
/// </summary>
internal sealed class SourceUpdateBuildRunner
{
    public async Task BuildAsync(
        SourceArchiveManifest manifest,
        SourceUpdateRequest request,
        string operationDirectory,
        string sourceDirectory,
        string sourceArchive,
        string outputDirectory,
        string attestationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        SourceUpdatePackageVerifier.ValidateRequest(request);
        var operationRoot = Path.GetFullPath(operationDirectory);
        var sourceRoot = RequireChild(operationRoot, sourceDirectory, "source extraction directory");
        var archive = RequireChild(operationRoot, sourceArchive, "source archive");
        var output = RequireChild(operationRoot, outputDirectory, "source build output directory");
        var attestation = RequireChild(operationRoot, attestationPath, "source build attestation");
        MachineStorageSecurity.RequireRestrictedDirectory(operationRoot);
        RequireNoReparseTraversal(operationRoot, sourceRoot);
        RequireNoReparseTraversal(operationRoot, output);
        if (!Directory.Exists(sourceRoot) || !File.Exists(archive) || !Directory.Exists(output))
            throw new InvalidDataException("The protected source update stage is incomplete.");
        if (Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("The protected source build output directory is not empty.");
        if (File.Exists(attestation) || Directory.Exists(attestation))
            throw new InvalidDataException("The protected source build attestation path is already occupied.");

        await VerifyBuildScriptAsync(manifest, sourceRoot, cancellationToken);
        var dotnet = await RequireCompatibleSdkAsync(request, operationRoot, cancellationToken);
        var environment = BuildSanitizedEnvironment(operationRoot, dotnet);
        var powershell = RequireSystemPowerShell();
        var script = SourceUpdatePackageVerifier.ExpectedBuildScriptPath(sourceRoot);
        var result = await ProcessRunner.RunAsync(
            powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", script,
                "-SourceRoot", sourceRoot,
                "-SourceArchive", archive,
                "-SourceVersion", request.TargetVersion,
                "-SourceSha256", request.SourceSha256.ToLowerInvariant(),
                "-SourceManifestSha256", request.SourceManifestSha256.ToLowerInvariant(),
                "-SourceManifestKeyId", request.SourceManifestKeyId,
                "-SigningProfile", request.SigningProfile,
                "-SourceReleaseCertificateBase64", manifest.SourceReleaseCertificateBase64,
                "-ProductSignerThumbprint", request.ProductSignerThumbprint,
                "-ProductSigningCertificateBase64", manifest.ProductSigningCertificateBase64,
                "-SdkVersion", request.SdkVersion,
                "-RuntimeVersion", request.RuntimeVersion,
                "-TargetRuntime", request.TargetRuntime,
                "-Role", request.Role.ToString(),
                "-DotnetPath", dotnet,
                "-OutputDirectory", output,
                "-AttestationPath", attestation],
            TimeSpan.FromMinutes(45), cancellationToken, environment: environment, clearEnvironment: true);
        if (!result.Succeeded)
        {
            var detail = string.Join(Environment.NewLine, result.StandardError, result.StandardOutput).Trim();
            if (detail.Length > 4096) detail = detail[..4096] + "…";
            throw new InvalidOperationException(
                "The exact verified source archive could not build its Agent and Guardian locally." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }
        MachineStorageSecurity.SealRestrictedFile(attestation);
        await SourceUpdatePackageVerifier.VerifyBuiltOutputAsync(attestation, output, request, cancellationToken);
    }

    private static async Task VerifyBuildScriptAsync(
        SourceArchiveManifest manifest,
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var file = manifest.Files.SingleOrDefault(item =>
            item.Path.Replace('\\', '/').Equals(SourceUpdateProtocol.SourceBuildScriptName, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The signed source manifest lacks the fixed source-update build entrypoint.");
        if (file.Size is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The signed source-update build entrypoint has an invalid size.");
        var script = SourceUpdatePackageVerifier.ExpectedBuildScriptPath(sourceRoot);
        await using var stream = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != file.Size)
            throw new InvalidDataException("The extracted source-update build entrypoint size changed.");
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(file.Sha256)))
            throw new InvalidDataException("The extracted source-update build entrypoint hash changed.");
    }

    private static async Task<string> RequireCompatibleSdkAsync(
        SourceUpdateRequest request,
        string protectedRoot,
        CancellationToken cancellationToken)
    {
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var dotnet = Path.GetFullPath(Path.Combine(programFiles, "dotnet", "dotnet.exe"));
        RequireChild(programFiles, dotnet, "fixed .NET SDK host");
        RequireNoReparseTraversal(programFiles, dotnet);
        if (!File.Exists(dotnet))
            throw new FileNotFoundException($"A .NET SDK matching {request.SdkVersion} is required but dotnet.exe is missing.", dotnet);
        var environment = BuildSanitizedEnvironment(protectedRoot, dotnet);
        var sdks = await ProcessRunner.RunAsync(dotnet, ["--list-sdks"], TimeSpan.FromSeconds(30),
            cancellationToken, environment: environment, clearEnvironment: true);
        if (!sdks.Succeeded || !DotNetSdkPolicy.InventoryContainsAcceptedSdk(sdks.StandardOutput))
            throw new InvalidOperationException(
                $"A stable .NET SDK matching {request.SdkVersion} is required for a source update.");
        return dotnet;
    }

    private static IReadOnlyDictionary<string, string?> BuildSanitizedEnvironment(string protectedRoot, string dotnet)
    {
        var root = Path.GetFullPath(protectedRoot);
        var system32 = Path.GetFullPath(Environment.SystemDirectory);
        var systemRoot = Path.GetFullPath(system32 + "\\..");
        var systemDrive = Path.GetPathRoot(systemRoot)?.TrimEnd(Path.DirectorySeparatorChar)
                          ?? throw new InvalidOperationException("Windows has no fixed system drive.");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dotnetRoot = Path.GetDirectoryName(dotnet)!;
        var sandbox = Path.Combine(root, "build-sandbox");
        var cliHome = Path.Combine(sandbox, "dotnet-home");
        var appDataRoaming = Path.Combine(sandbox, "appdata-roaming");
        var appDataLocal = Path.Combine(sandbox, "appdata-local");
        var pluginsCache = Path.Combine(sandbox, "nuget-plugins-cache");
        var directories = new[]
        {
            sandbox,
            Path.Combine(sandbox, "temp"),
            cliHome,
            appDataRoaming,
            appDataLocal,
            Path.Combine(sandbox, "nuget-packages"),
            Path.Combine(sandbox, "nuget-http-cache"),
            pluginsCache,
            Path.Combine(sandbox, "msbuild-user-extensions")
        };
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            RequireNoReparseTraversal(root, directory);
        }
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["SystemDrive"] = systemDrive,
            ["ProgramData"] = programData,
            ["ALLUSERSPROFILE"] = programData,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["COMSPEC"] = Path.Combine(system32, "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PATH"] = string.Join(Path.PathSeparator,
                system32,
                Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0"),
                dotnetRoot),
            ["DOTNET_ROOT"] = dotnetRoot,
            ["TEMP"] = Path.Combine(sandbox, "temp"),
            ["TMP"] = Path.Combine(sandbox, "temp"),
            ["DOTNET_CLI_HOME"] = cliHome,
            ["USERPROFILE"] = cliHome,
            ["HOME"] = cliHome,
            ["HOMEDRIVE"] = systemDrive,
            ["HOMEPATH"] = cliHome[systemDrive.Length..],
            ["APPDATA"] = appDataRoaming,
            ["LOCALAPPDATA"] = appDataLocal,
            ["NUGET_PACKAGES"] = Path.Combine(sandbox, "nuget-packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(sandbox, "nuget-http-cache"),
            ["NUGET_PLUGINS_CACHE_PATH"] = pluginsCache,
            ["NUGET_AUDIT"] = "false",
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["NUGET_CERT_REVOCATION_MODE"] = "online",
            ["MSBuildUserExtensionsPath"] = Path.Combine(sandbox, "msbuild-user-extensions"),
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
            ["DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"] = "1",
            ["MSBuildEnableWorkloadResolver"] = "false",
            ["PSModulePath"] = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "Modules"),
            ["POWERSHELL_TELEMETRY_OPTOUT"] = "1"
        };
    }

    private static string RequireSystemPowerShell()
    {
        var system = Path.GetFullPath(Environment.SystemDirectory);
        var powershell = Path.GetFullPath(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        RequireChild(system, powershell, "fixed Windows PowerShell host");
        RequireNoReparseTraversal(system, powershell);
        if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell is unavailable.", powershell);
        return powershell;
    }

    private static string RequireChild(string root, string path, string description)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {description} escaped its fixed root.");
        return full;
    }

    private static void RequireNoReparseTraversal(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!current.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A source build path escaped its fixed root.");
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A source build path contains a reparse point.");
            }
            if (current.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidDataException("A source build path escaped its fixed root.");
        }
    }
}
