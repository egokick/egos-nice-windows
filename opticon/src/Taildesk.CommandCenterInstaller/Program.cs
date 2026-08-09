using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Taildesk.Shared;

namespace Taildesk.CommandCenterInstaller;

internal sealed class CommandCenterPackageManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string SigningProfile { get; set; } = string.Empty;
    public string SourceReleaseKeyId { get; set; } = string.Empty;
    public string ProductSignerThumbprint { get; set; } = string.Empty;
    public bool DevelopmentOnly { get; set; }
    public List<CommandCenterPackageFile> Files { get; set; } = [];
}

internal sealed class CommandCenterPackageFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal static class Program
{
    private const string WrapperName = "Install-Opticon.exe";
    private const string ManifestName = "command-center.manifest.json";
    private const string SignatureName = "command-center.manifest.sig";
    private const string InstallerResource = "Taildesk.CommandCenterInstaller.Install-CommandCenter.ps1";
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Regex VersionPattern =
        new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern =
        new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly string RunningVersion = GetRunningVersion();
    private static readonly string[] AllowedPayloadPaths =
    [
        "App/Opticon.exe",
        "App/Cli/opticon.exe",
        "App/Tools/Taildesk.RouteKeeper.exe",
        "App/Payload/Setup/Taildesk.Setup.exe",
        "App/Payload/Agent/Taildesk.Agent.exe",
        "App/Payload/Admin/Opticon.exe",
        "App/Payload/Admin/Cli/opticon.exe",
        "App/Payload/Admin/Tools/Taildesk.RouteKeeper.exe",
        "App/Payload/UpdateGuardian/Taildesk.UpdateGuardian.exe"
    ];

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        string? staging = null;
        try
        {
            var controllerOnlyRepair = ParseArguments(args);
            var executable = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not provide the installer path."));
            if (!Path.GetFileName(executable).Equals(WrapperName, StringComparison.Ordinal))
                throw new InvalidDataException($"The signed installer must retain its exact {WrapperName} filename.");
            await ProductSigning.VerifyAuthenticodeAsync(executable);

            var packageRoot = Path.GetDirectoryName(executable)
                ?? throw new InvalidOperationException("The command-center package directory is unavailable.");
            ValidatePackageRoot(packageRoot);
            var manifest = await ReadAndVerifyManifestAsync(packageRoot);

            staging = CreateProtectedStagingDirectory();
            await CopyVerifiedPayloadAsync(packageRoot, staging, manifest);
            var installer = await ExtractInstallerAsync(staging);
            var exitCode = await RunInstallerAsync(
                installer, staging, manifest.DevelopmentOnly, controllerOnlyRepair);
            if (exitCode != 0)
                throw new InvalidOperationException($"The protected Opticon installer returned {exitCode}.");

            MessageBox.Show(
                controllerOnlyRepair
                    ? "The Opticon command center repair completed successfully."
                    : "The Opticon command center was installed successfully.",
                "Opticon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Opticon refused to install this package.\n\n" + exception.GetBaseException().Message,
                "Opticon secure installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (staging is not null)
            {
                try { MachineStorageSecurity.DeleteRestrictedDirectory(staging); }
                catch { }
            }
        }
    }

    private static bool ParseArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return false;
        if (args.Count == 1 && args[0].Equals("--controller-only-repair", StringComparison.Ordinal))
            return true;
        throw new ArgumentException("Only --controller-only-repair is supported.");
    }

    private static void ValidatePackageRoot(string packageRoot)
    {
        var fullRoot = Path.GetFullPath(packageRoot);
        RejectReparsePoint(fullRoot, "package directory");
        var actual = Directory.EnumerateFileSystemEntries(fullRoot)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        var expected = new HashSet<string>(
            [WrapperName, ManifestName, SignatureName, "App"], StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException("The command-center package root contains missing or undeclared entries.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullRoot))
            RejectReparsePoint(entry, "package entry");
        RejectReparseTree(Path.Combine(fullRoot, "App"));
    }

    private static async Task<CommandCenterPackageManifest> ReadAndVerifyManifestAsync(string packageRoot)
    {
        var manifestBytes = await ReadLockedAsync(
            Path.Combine(packageRoot, ManifestName), MaximumManifestBytes, minimumBytes: 2);
        var signature = await ReadLockedAsync(
            Path.Combine(packageRoot, SignatureName), maximumBytes: 4096, minimumBytes: 128);
        if (!SourceReleaseSigning.Verify(manifestBytes, signature))
            throw new InvalidDataException("The command-center package manifest signature is invalid.");

        var manifest = JsonSerializer.Deserialize<CommandCenterPackageManifest>(
                           manifestBytes, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The command-center package manifest is empty.");
        if (manifest.SchemaVersion != 1
            || !VersionPattern.IsMatch(manifest.Version)
            || !manifest.Version.Equals(RunningVersion, StringComparison.Ordinal)
            || manifest.Files.Count != AllowedPayloadPaths.Length)
            throw new InvalidDataException("The command-center package manifest metadata is invalid.");
        if (!manifest.SigningProfile.Equals(BuildSigningTrust.ProfileName, StringComparison.Ordinal)
            || !manifest.SourceReleaseKeyId.Equals(SourceReleaseSigning.KeyId, StringComparison.Ordinal)
            || !manifest.ProductSignerThumbprint.Equals(ProductSigning.CertificateThumbprint, StringComparison.Ordinal)
            || manifest.DevelopmentOnly == BuildSigningTrust.IsProduction)
            throw new InvalidDataException("The command-center package trust metadata does not match this installer.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var item in manifest.Files)
        {
            var relative = NormalizePayloadPath(item.Path);
            if (!paths.Add(relative)
                || item.Size <= 0
                || item.Size > MaximumPackageBytes
                || !Sha256Pattern.IsMatch(item.Sha256))
                throw new InvalidDataException($"The package manifest entry is invalid: {relative}.");
            total = checked(total + item.Size);
            if (total > MaximumPackageBytes)
                throw new InvalidDataException("The command-center package exceeds its size limit.");
        }
        if (!paths.SetEquals(AllowedPayloadPaths))
            throw new InvalidDataException("The command-center manifest is not the exact product payload allowlist.");
        return manifest;
    }

    private static async Task CopyVerifiedPayloadAsync(
        string packageRoot,
        string staging,
        CommandCenterPackageManifest manifest)
    {
        var sourceApp = Path.GetFullPath(Path.Combine(packageRoot, "App"));
        var destinationApp = Path.Combine(staging, "App");
        Directory.CreateDirectory(destinationApp);

        foreach (var item in manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var relative = NormalizePayloadPath(item.Path);
            var source = ResolveContainedPath(packageRoot, relative);
            RejectReparseTraversal(packageRoot, source);
            var destination = ResolveContainedPath(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyAndVerifyAsync(source, destination, item.Size, item.Sha256);
            RequirePayloadVersion(destination, manifest.Version);
            await ProductSigning.VerifyAuthenticodeAsync(destination);
        }

        var actualFiles = Directory.EnumerateFiles(sourceApp, "*", SearchOption.AllDirectories)
            .Select(path => "App/" + Path.GetRelativePath(sourceApp, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualFiles.SetEquals(AllowedPayloadPaths))
            throw new InvalidDataException("The command-center App directory contains missing or undeclared files.");

        var expectedDirectories = AllowedPayloadPaths
            .SelectMany(path => ParentDirectories(path))
            .ToHashSet(StringComparer.Ordinal);
        var actualDirectories = Directory.EnumerateDirectories(sourceApp, "*", SearchOption.AllDirectories)
            .Select(path => "App/" + Path.GetRelativePath(sourceApp, path).Replace('\\', '/'))
            .Append("App")
            .ToHashSet(StringComparer.Ordinal);
        if (!actualDirectories.SetEquals(expectedDirectories))
            throw new InvalidDataException("The command-center App directory contains undeclared directories.");
    }

    private static IEnumerable<string> ParentDirectories(string path)
    {
        var current = path;
        while ((current = current.Contains('/') ? current[..current.LastIndexOf('/')] : string.Empty).Length > 0)
            yield return current;
    }

    private static string NormalizePayloadPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (!normalized.StartsWith("App/", StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or "..")
            || normalized.Contains(':')
            || normalized.StartsWith('/'))
            throw new InvalidDataException("The command-center package contains an unsafe path.");
        return normalized;
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(
            fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A command-center package path escaped its fixed root.");
        return full;
    }

    private static async Task CopyAndVerifyAsync(
        string source,
        string destination,
        long expectedSize,
        string expectedHash)
    {
        RejectReparsePoint(source, "package file");
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != expectedSize)
            throw new InvalidDataException($"Package file size mismatch: {Path.GetFileName(source)}.");

        try
        {
            await using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long remaining = expectedSize;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0) throw new EndOfStreamException("A package file ended before its signed size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
            if (await input.ReadAsync(buffer.AsMemory(0, 1)) != 0)
                throw new InvalidDataException("A package file exceeds its signed size.");
            await output.FlushAsync();
            output.Flush(flushToDisk: true);
            var actualHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash, Convert.FromHexString(expectedHash)))
                throw new InvalidDataException($"Package file hash mismatch: {Path.GetFileName(source)}.");
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            throw;
        }
    }

    private static void RequirePayloadVersion(string path, string expectedVersion)
    {
        var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty;
        var match = Regex.Match(
            productVersion, "^([0-9]+\\.[0-9]+\\.[0-9]+)(?:[.+-]|$)",
            RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups[1].Value.Equals(expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Payload version mismatch: {Path.GetFileName(path)} is not {expectedVersion}.");
    }

    private static async Task<string> ExtractInstallerAsync(string staging)
    {
        var destination = Path.Combine(staging, "Install-CommandCenter.ps1");
        await using var resource = typeof(Program).Assembly.GetManifestResourceStream(InstallerResource)
            ?? throw new InvalidOperationException("The protected installer payload is missing.");
        await using (var output = new FileStream(
                         destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await resource.CopyToAsync(output);
            await output.FlushAsync();
            output.Flush(flushToDisk: true);
        }
        MachineStorageSecurity.SealRestrictedFile(destination);
        return destination;
    }

    private static async Task<int> RunInstallerAsync(
        string installer,
        string staging,
        bool developmentOnly,
        bool controllerOnlyRepair)
    {
        var windows = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        RejectReparsePoint(windows, "Windows directory");
        var system32 = Path.Combine(windows, "System32");
        var powerShell = Path.Combine(
            system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        RejectReparseTraversal(windows, powerShell);
        if (!File.Exists(powerShell))
            throw new FileNotFoundException("Windows PowerShell is unavailable.", powerShell);

        var temp = MachineStorageSecurity.CreateRestrictedChildDirectory(staging, "temp-");
        var start = new ProcessStartInfo
        {
            FileName = powerShell,
            WorkingDirectory = staging,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("RemoteSigned");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(installer);
        start.ArgumentList.Add("-ExpectedCodeSigningThumbprint");
        start.ArgumentList.Add(ProductSigning.CertificateThumbprint);
        start.ArgumentList.Add("-ExpectedSourceReleaseKeyId");
        start.ArgumentList.Add(SourceReleaseSigning.KeyId);
        start.ArgumentList.Add("-BootstrapProcessId");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-DevelopmentOnly");
        start.ArgumentList.Add(developmentOnly ? "1" : "0");
        if (controllerOnlyRepair) start.ArgumentList.Add("-ControllerOnlyRepair");

        start.Environment.Clear();
        SetEnvironment(start, "SystemRoot", windows);
        SetEnvironment(start, "WINDIR", windows);
        SetEnvironment(start, "ProgramData",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        SetEnvironment(start, "ProgramFiles",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        SetEnvironment(start, "ProgramFiles(x86)",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        SetEnvironment(start, "ComSpec", Path.Combine(system32, "cmd.exe"));
        SetEnvironment(start, "PATH", string.Join(
            Path.PathSeparator, system32, Path.Combine(system32, "Wbem"),
            Path.GetDirectoryName(powerShell)!));
        SetEnvironment(start, "PATHEXT", ".COM;.EXE");
        SetEnvironment(start, "PSModulePath",
            Path.Combine(system32, "WindowsPowerShell", "v1.0", "Modules"));
        SetEnvironment(start, "TEMP", temp);
        SetEnvironment(start, "TMP", temp);
        SetEnvironment(start, "POWERSHELL_TELEMETRY_OPTOUT", "1");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows could not start the protected Opticon installer.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void SetEnvironment(ProcessStartInfo start, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) start.Environment[key] = value;
    }

    private static string CreateProtectedStagingDirectory()
    {
        var common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        RejectReparsePoint(common, "ProgramData");
        var staging = Path.Combine(
            common, "OpticonSecureInstall-" + Guid.NewGuid().ToString("N"));
        MachineStorageSecurity.EnsureRestrictedDirectoryTree(staging);
        return staging;
    }

    private static async Task<byte[]> ReadLockedAsync(
        string path,
        int maximumBytes,
        int minimumBytes)
    {
        RejectReparsePoint(path, "package metadata file");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < minimumBytes || stream.Length > maximumBytes)
            throw new InvalidDataException("A package metadata file has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static void RejectReparseTraversal(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(path);
        if (!current.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A package path escaped its root.");
        while (!current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            RejectReparsePoint(current, "package path");
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("A package path has no parent.");
        }
        RejectReparsePoint(fullRoot, "package root");
    }

    private static void RejectReparseTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var directory))
        {
            RejectReparsePoint(directory, "package directory");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(entry, "package entry");
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} is a reparse point: {path}");
    }

    private static string GetRunningVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The installer assembly version is unavailable.");
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
