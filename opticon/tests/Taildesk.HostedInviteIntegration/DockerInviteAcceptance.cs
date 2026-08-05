using System.Text;
using System.Text.Json;
using Taildesk.Admin;
using Taildesk.Shared;

internal static class DockerInviteAcceptance
{
    private const string ImageName = "opticon-invite-acceptance:local";

    public static async Task BuildAsync()
    {
        var repository = FindRepository();
        var context = Path.Combine(repository, "tests", "Opticon.InviteAcceptance.Docker");
        Console.WriteLine("Building the isolated Opticon invitation-acceptance container...");
        await RunDockerAsync(["build", "--pull", "--tag", ImageName, context], TimeSpan.FromMinutes(10));
    }

    public static async Task VerifyAsync(InviteBundleResult invitation)
    {
        var repository = FindRepository();
        var manifest = await LoadManifestAsync(Path.Combine(repository, "fly-headscale", "artifacts", "manifest.json"));
        var bundle = manifest.Single(item => item.Product == "OpticonBundle" && item.Role == nameof(DeviceRole.ManagedOnly));
        var dependencies = manifest.Where(item => item.Product is "Tailscale" or "RustDesk").ToArray();
        if (dependencies.Count(item => item.Architecture == "x64") != 2)
            throw new InvalidDataException("The release manifest must declare exactly two x64 setup dependencies.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "OpticonDockerAcceptance", Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(tempRoot, "input");
        var outputDirectory = Path.Combine(tempRoot, "output");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var input = new
            {
                invitationUrl = invitation.InvitationUrl,
                expectedRole = "managedOnly",
                bundle,
                dependencies,
                invitationCertificateBase64 = Convert.ToBase64String(InvitationSigning.PinnedCertificate.RawData),
                invitationCertificateSha1 = InvitationSigning.CertificateThumbprint
            };
            await File.WriteAllTextAsync(Path.Combine(inputDirectory, "input.json"),
                JsonSerializer.Serialize(input, JsonDefaults.Options), new UTF8Encoding(false));

            Console.WriteLine("Accepting and validating the disposable invitation inside Docker...");
            await RunDockerAsync([
                "run", "--rm", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges",
                "--pids-limit=64", "--memory=384m", "--cpus=2", "--network=bridge",
                "--mount", $"type=bind,source={inputDirectory},target=/run/opticon-input,readonly",
                "--mount", $"type=bind,source={outputDirectory},target=/run/opticon-output",
                ImageName
            ], TimeSpan.FromMinutes(15));

            using var resultDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "result.json")));
            var result = resultDocument.RootElement;
            if (result.GetProperty("status").GetString() != "passed"
                || result.GetProperty("deviceName").GetString() != invitation.Record.DeviceName
                || result.GetProperty("role").GetString() != "managedOnly"
                || result.GetProperty("bundle").GetString() != bundle.File
                || result.GetProperty("dependenciesChecked").GetInt32() != 2
                || result.GetProperty("negativeTestsPassed").GetInt32() != 2)
                throw new InvalidDataException("The container returned an incomplete or mismatched acceptance result.");

            Console.WriteLine("Verifying Authenticode on the exact files downloaded from Fly...");
            await InvitationSigning.VerifyAuthenticodeAsync(Path.Combine(outputDirectory, "Taildesk.Setup.exe"));
            await InvitationSigning.VerifyAuthenticodeAsync(Path.Combine(outputDirectory, "Taildesk.Agent.exe"));
            foreach (var dependency in dependencies.Where(item => item.Architecture == "x64"))
                await VerifyPublisherAsync(Path.Combine(outputDirectory, dependency.File), dependency.SignerThumbprint);
            Console.WriteLine("PASS exact live Opticon executables and dependency MSIs have their pinned publisher signatures.");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public static async Task VerifyRemovedAsync(string invitationUrl)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(invitationUrl.Split('#')[0]);
        if ((int)response.StatusCode is not (404 or 410))
            throw new InvalidDataException($"The deleted test invitation remained public (HTTP {(int)response.StatusCode}).");
        Console.WriteLine("PASS the disposable public invitation URL was removed.");
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "fly-headscale", "artifacts", "manifest.json"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The Opticon repository root was not found.");
    }

    private static async Task<Artifact[]> LoadManifestAsync(string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return document.RootElement.GetProperty("artifacts").EnumerateArray().Select(item => new Artifact(
            item.GetProperty("product").GetString() ?? string.Empty,
            item.GetProperty("version").GetString() ?? string.Empty,
            item.TryGetProperty("role", out var role) ? role.GetString() ?? string.Empty : string.Empty,
            item.GetProperty("architecture").GetString() ?? string.Empty,
            item.GetProperty("file").GetString() ?? string.Empty,
            item.GetProperty("size").GetInt64(),
            item.GetProperty("sha256").GetString() ?? string.Empty,
            item.TryGetProperty("signerThumbprint", out var signer) ? signer.GetString() ?? string.Empty : string.Empty)).ToArray();
    }

    private static async Task RunDockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var process = await ProcessRunner.RunAsync("docker", arguments, timeout);
        if (!process.Succeeded)
            throw new InvalidOperationException($"Docker failed (exit {process.ExitCode}): {process.StandardError.Trim()} {process.StandardOutput.Trim()}".Trim());
        if (!string.IsNullOrWhiteSpace(process.StandardOutput)) Console.WriteLine(process.StandardOutput.Trim());
    }

    private static async Task VerifyPublisherAsync(string path, string expectedThumbprint)
    {
        if (string.IsNullOrWhiteSpace(expectedThumbprint)) throw new InvalidDataException("A dependency publisher thumbprint is missing.");
        var pathBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        var command = $"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pathBase64}'));$s=Get-AuthenticodeSignature -LiteralPath $p;if($s.Status -ne 'Valid' -or -not $s.SignerCertificate){{exit 9}};$s.SignerCertificate.Thumbprint";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var process = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], TimeSpan.FromMinutes(2));
        if (!process.Succeeded || !process.StandardOutput.Trim().Equals(expectedThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Path.GetFileName(path)} did not have its pinned valid publisher signature.");
    }

    private sealed record Artifact(string Product, string Version, string Role, string Architecture,
        string File, long Size, string SHA256, string SignerThumbprint);
}
