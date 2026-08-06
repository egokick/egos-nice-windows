using System.Text.Json;

namespace Taildesk.Shared;

public static class UpdateHealthTokenStore
{
    private const int SidecarSchemaVersion = 1;
    private const int MaximumSidecarBytes = 64 * 1024;

    public static string Load(
        string? configuredProtectedToken,
        Guid expectedDeviceId,
        string? sidecarPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredProtectedToken))
            return UnprotectAndValidate(configuredProtectedToken);
        if (expectedDeviceId == Guid.Empty)
            throw new InvalidDataException("The local update health credential has no device identity.");
        return LoadSidecar(expectedDeviceId, sidecarPath ?? AppPaths.UpdateHealthTokenSidecarFile);
    }

    public static string LoadFromAgentConfigFile(string? sidecarPath = null)
    {
        using var stream = new FileStream(
            AppPaths.AgentConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });

        string? configuredProtectedToken = null;
        if (TryGetProperty(document.RootElement, "updateHealthTokenProtected", out var tokenProperty))
        {
            if (tokenProperty.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                throw new InvalidDataException("The Agent update health credential has an invalid type.");
            configuredProtectedToken = tokenProperty.ValueKind == JsonValueKind.String
                ? tokenProperty.GetString()
                : null;
        }
        if (!string.IsNullOrWhiteSpace(configuredProtectedToken))
            return UnprotectAndValidate(configuredProtectedToken);

        if (!TryGetProperty(document.RootElement, "deviceId", out var deviceProperty)
            || deviceProperty.ValueKind != JsonValueKind.String
            || !Guid.TryParse(deviceProperty.GetString(), out var deviceId)
            || deviceId == Guid.Empty)
            throw new InvalidDataException("The Agent configuration has no valid device identity for its update health sidecar.");
        return Load(null, deviceId, sidecarPath);
    }

    public static async Task<string> LoadOrCreateSidecarAsync(
        string? configuredProtectedToken,
        Guid expectedDeviceId,
        string? sidecarPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(configuredProtectedToken))
            return UnprotectAndValidate(configuredProtectedToken);
        if (expectedDeviceId == Guid.Empty)
            throw new InvalidDataException("The local update health credential has no device identity.");

        var path = Path.GetFullPath(sidecarPath ?? AppPaths.UpdateHealthTokenSidecarFile);
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("The update health sidecar has no parent directory.");
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("The protected update directory must exist before creating its health credential.");
        if (File.Exists(path)) return Load(null, expectedDeviceId, path);

        var envelope = new UpdateHealthTokenSidecar
        {
            DeviceId = expectedDeviceId,
            TokenProtected = SecretProtector.Protect(
                SecurityHelpers.CreateToken(), SecretScope.LocalMachine)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        if (bytes.Length is <= 0 or > MaximumSidecarBytes)
            throw new InvalidDataException("The protected update health sidecar has an invalid size.");

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path))
            {
                // Another protected process won the write-once creation race.
            }
            return Load(null, expectedDeviceId, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string LoadSidecar(Guid expectedDeviceId, string path)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
            throw new FileNotFoundException("The Agent has no protected local update health credential.", path);
        if (information.Length is <= 0 or > MaximumSidecarBytes)
            throw new InvalidDataException("The protected update health sidecar has an invalid size.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        UpdateHealthTokenSidecar envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<UpdateHealthTokenSidecar>(stream, JsonDefaults.Options)
                       ?? throw new InvalidDataException("The protected update health sidecar is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The protected update health sidecar is malformed.", exception);
        }
        if (envelope.SchemaVersion != SidecarSchemaVersion
            || envelope.DeviceId != expectedDeviceId
            || string.IsNullOrWhiteSpace(envelope.TokenProtected))
            throw new InvalidDataException("The protected update health sidecar does not match this device.");
        return UnprotectAndValidate(envelope.TokenProtected);
    }

    private static string UnprotectAndValidate(string protectedToken)
    {
        string token;
        try { token = SecretProtector.Unprotect(protectedToken, SecretScope.LocalMachine); }
        catch (Exception exception)
        {
            throw new InvalidDataException("The local update health credential could not be unprotected.", exception);
        }
        if (token.Length is < 32 or > 4096 || token.Any(char.IsControl))
            throw new InvalidDataException("The local update health credential is invalid.");
        return token;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private sealed class UpdateHealthTokenSidecar
    {
        public int SchemaVersion { get; set; } = SidecarSchemaVersion;
        public Guid DeviceId { get; set; }
        public string TokenProtected { get; set; } = string.Empty;
    }
}
