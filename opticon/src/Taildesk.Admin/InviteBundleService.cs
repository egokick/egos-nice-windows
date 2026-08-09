using System.Security.Cryptography;
using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.Admin;

public sealed record InviteBundleResult(InviteRecord Record, string InvitationUrl);

public sealed class InviteBundleService
{
    private static readonly string[] SupportedRoots = ["Desktop", "Documents", "Downloads", "Pictures", "Videos"];
    private readonly AdminState _state;
    private readonly HeadscaleApiClient _headscale;

    public InviteBundleService(AdminState state, HeadscaleApiClient headscale)
    {
        _state = state;
        _headscale = headscale;
    }

    public async Task<InviteBundleResult> CreateAsync(
        string deviceName,
        DeviceRole role,
        bool advertiseExitNode,
        IReadOnlyCollection<string> allowedRoots,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        if (_state.Config.Mode != AdminMode.Primary)
        {
            throw new InvalidOperationException("Only the primary command center can issue invitations.");
        }
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new ArgumentException("Enter a name for the target machine.", nameof(deviceName));
        }

        ArgumentNullException.ThrowIfNull(allowedRoots);
        var distinctRequestedRoots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedRoots = SupportedRoots
            .Where(root => distinctRequestedRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (selectedRoots.Length == 0)
        {
            throw new ArgumentException("Select at least one shared folder for the target machine.", nameof(allowedRoots));
        }
        if (selectedRoots.Length != distinctRequestedRoots.Length)
        {
            throw new ArgumentException("The invitation contains an unsupported shared folder.", nameof(allowedRoots));
        }

        progress?.Report("Checking the private network...");
        var expectedTailnet = await ReadCurrentTailnetAsync(cancellationToken);
        progress?.Report("Pinning this invitation to the exact authenticated source release...");
        var sourceRelease = await new OpticonSourceReleaseClient().GetCurrentAsync(_state.Config, cancellationToken);
        var expires = InvitationPolicy.CreateDefaultExpiry();
        progress?.Report("Creating the one-use network key...");
        var authKey = await _headscale.CreateInviteKeyAsync(role, advertiseExitNode, $"Opticon invite for {deviceName}", expires, cancellationToken);
        var secret = SecurityHelpers.CreateToken();
        var agentToken = SecurityHelpers.CreateToken();
        var rustDeskPassword = SecurityHelpers.CreateHumanPassword();
        var controllerToken = SecurityHelpers.CreateToken();
        var payload = new InvitePayload
        // Current hosted invitations pin both source and bootstrap bytes.
        {
            SchemaVersion = InvitationPolicy.HostedLinkSchemaVersion,
            InviteId = Guid.NewGuid(),
            DeviceName = deviceName.Trim(),
            Role = role,
            ExpiresAt = expires,
            InviteSecret = secret,
            TailscaleAuthKey = authKey.Key,
            HeadscaleLoginUrl = _state.Config.HeadscaleControlUrl,
            AgentToken = agentToken,
            RustDeskPassword = rustDeskPassword,
            ControllerToken = controllerToken,
            CoordinatorUrl = _state.Config.CoordinatorUrl,
            ExpectedTailnet = expectedTailnet,
            ReleaseVersion = sourceRelease.Version,
            SourceSha256 = sourceRelease.Sha256,
            SourceFile = sourceRelease.File,
            SourceSize = sourceRelease.Size,
            SourceManifestSha256 = sourceRelease.SourceManifestSha256,
            SourceManifestKeyId = sourceRelease.SourceManifestKeyId,
            SigningProfile = sourceRelease.SigningProfile,
            ProductSignerThumbprint = sourceRelease.ProductSignerThumbprint,
            SdkVersion = sourceRelease.SdkVersion,
            RuntimeVersion = sourceRelease.RuntimeVersion,
            TargetRuntimes = sourceRelease.TargetRuntimes.ToArray(),
            BootstrapVersion = sourceRelease.BootstrapVersion,
            BootstrapFile = sourceRelease.BootstrapFile,
            BootstrapSize = sourceRelease.BootstrapSize,
            BootstrapSha256 = sourceRelease.BootstrapSha256,
            BootstrapSignerThumbprint = sourceRelease.BootstrapSignerThumbprint,
            AdvertiseExitNode = advertiseExitNode,
            AllowedRoots = selectedRoots
        };

        var record = new InviteRecord
        {
            Id = payload.InviteId,
            DeviceName = payload.DeviceName,
            Role = role,
            InviteSecretHash = SecurityHelpers.HashToken(secret),
            AgentTokenProtected = SecretProtector.Protect(agentToken),
            RustDeskPasswordProtected = SecretProtector.Protect(rustDeskPassword),
            ControllerTokenProtected = SecretProtector.Protect(controllerToken),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expires,
            AdvertiseExitNode = advertiseExitNode,
            TailscaleKeyId = authKey.Id,
            ReleaseVersion = sourceRelease.Version,
            SourceSha256 = sourceRelease.Sha256,
            SourceFile = sourceRelease.File,
            SourceSize = sourceRelease.Size,
            SourceManifestSha256 = sourceRelease.SourceManifestSha256,
            SourceManifestKeyId = sourceRelease.SourceManifestKeyId,
            SigningProfile = sourceRelease.SigningProfile,
            ProductSignerThumbprint = sourceRelease.ProductSignerThumbprint,
            SdkVersion = sourceRelease.SdkVersion,
            RuntimeVersion = sourceRelease.RuntimeVersion,
            TargetRuntimes = sourceRelease.TargetRuntimes.ToArray(),
            BootstrapVersion = sourceRelease.BootstrapVersion,
            BootstrapFile = sourceRelease.BootstrapFile,
            BootstrapSize = sourceRelease.BootstrapSize,
            BootstrapSha256 = sourceRelease.BootstrapSha256,
            BootstrapSignerThumbprint = sourceRelease.BootstrapSignerThumbprint
        };

        HostedInvitePublication? publication = null;
        try
        {
            progress?.Report("Signing and encrypting the private invitation...");
            var publicId = SecurityHelpers.CreateToken(24);
            var fragmentKey = SecurityHelpers.CreateToken(32);
            var signedEnvelope = HostedInviteFile.CreateSigned(payload);
            var encryptedEnvelope = HostedInviteFile.Encrypt(fragmentKey, signedEnvelope);
            CryptographicOperations.ZeroMemory(signedEnvelope);

            progress?.Report("Publishing the 14-day one-click link...");
            try
            {
                publication = await new HostedInviteClient(_state).PublishAsync(
                    payload, encryptedEnvelope, publicId, fragmentKey, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedEnvelope);
            }

            record.HostedInviteIdHash = publication.IdHash;
            record.HostedUrlProtected = SecretProtector.Protect(publication.Url);
            await DurableCollectionMutation.AddAsync(
                _state.Config.Invites,
                record,
                _state.InviteGate,
                _state.SaveAsync,
                cancellationToken);
            return new InviteBundleResult(record, publication.Url);
        }
        catch
        {
            if (publication is not null)
            {
                try { await new HostedInviteClient(_state).DeleteAsync(publication.IdHash, CancellationToken.None); } catch { }
            }
            try { await _headscale.RevokeKeyAsync(authKey.Id, CancellationToken.None); } catch { }
            throw;
        }
    }

    public async Task<bool> ExtendAsync(
        InviteRecord record,
        int additionalDays,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BuildSigningTrust.RequirePublishable();
        ArgumentNullException.ThrowIfNull(record);
        if (_state.Config.Mode != AdminMode.Primary) throw new InvalidOperationException("Only the primary command center can extend invitations.");
        if (record.RedeemedAt.HasValue) throw new InvalidOperationException("This invitation has already been used and cannot be extended.");
        if (record.IsExpired) throw new InvalidOperationException("This invitation has already expired. Create a new invitation instead.");
        record.PendingTailscaleKeyRevocations ??= [];
        if (!await RevokePendingKeysAsync(record, cancellationToken))
            throw new InvalidOperationException(
                "A superseded Headscale key is still awaiting revocation. Retry after Headscale is reachable before extending this invitation again.");
        if (additionalDays is < 1 or > InvitationPolicy.MaximumLifetimeDays)
            throw new ArgumentOutOfRangeException(nameof(additionalDays), $"Enter between 1 and {InvitationPolicy.MaximumLifetimeDays} days.");
        var newExpiry = record.ExpiresAt.AddDays(additionalDays);
        if (newExpiry > DateTimeOffset.UtcNow.AddDays(InvitationPolicy.MaximumLifetimeDays))
            throw new InvalidOperationException($"An invitation cannot be extended beyond {InvitationPolicy.MaximumLifetimeDays} days from today.");
        var hostedUrl = record.HostedUrl;
        if (!Uri.TryCreate(hostedUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps || link.Fragment.Length < 2)
            throw new InvalidOperationException("This invitation has no active hosted URL to extend.");
        var publicId = link.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        var fragmentKey = link.Fragment[1..];
        if (string.IsNullOrWhiteSpace(publicId)) throw new InvalidDataException("The hosted invitation URL is malformed.");
        var idHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(publicId))).ToLowerInvariant();
        if (!SecurityHelpers.FixedTimeEquals(idHash, record.HostedInviteIdHash))
            throw new InvalidDataException("The hosted invitation URL does not match its local record.");

        var hosted = new HostedInviteClient(_state);
        progress?.Report("Reading and verifying the current invitation...");
        var originalEncrypted = await hosted.DownloadEncryptedAsync(publicId, cancellationToken);
        var signed = HostedInviteFile.Decrypt(fragmentKey, originalEncrypted);
        InvitePayload payload;
        try { payload = HostedInviteFile.ReadSigned(signed); }
        finally { CryptographicOperations.ZeroMemory(signed); }
        if (payload.InviteId != record.Id || payload.Role != record.Role ||
            !SecurityHelpers.FixedTimeEquals(SecurityHelpers.HashToken(payload.InviteSecret), record.InviteSecretHash))
            throw new InvalidDataException("The hosted invitation does not match its command-center record.");

        var oldExpiry = payload.ExpiresAt;
        var oldAuthKey = payload.TailscaleAuthKey;
        var oldKeyId = record.TailscaleKeyId;
        var oldPendingRevocations = record.PendingTailscaleKeyRevocations.ToList();
        CreatedPreAuthKey? replacementKey = null;
        var replacementPublished = false;
        var recordCommitted = false;
        try
        {
            progress?.Report("Rotating the one-use network key...");
            replacementKey = await _headscale.CreateInviteKeyAsync(
                record.Role, record.AdvertiseExitNode, $"Extended Opticon invite for {record.DeviceName}", newExpiry, cancellationToken);
            payload.ExpiresAt = newExpiry;
            payload.TailscaleAuthKey = replacementKey.Key;
            var newSigned = HostedInviteFile.CreateSigned(payload);
            var newEncrypted = HostedInviteFile.Encrypt(fragmentKey, newSigned);
            CryptographicOperations.ZeroMemory(newSigned);
            try
            {
                progress?.Report("Publishing the extended invitation...");
                await hosted.PublishAsync(payload, newEncrypted, publicId, fragmentKey, cancellationToken);
                replacementPublished = true;
            }
            finally { CryptographicOperations.ZeroMemory(newEncrypted); }

            record.ExpiresAt = newExpiry;
            record.TailscaleKeyId = replacementKey.Id;
            if (!string.IsNullOrWhiteSpace(oldKeyId)
                && !record.PendingTailscaleKeyRevocations.Contains(oldKeyId, StringComparer.Ordinal))
                record.PendingTailscaleKeyRevocations.Add(oldKeyId);
            await _state.SaveAsync(cancellationToken);
            recordCommitted = true;
        }
        catch
        {
            if (replacementPublished && !recordCommitted)
            {
                payload.ExpiresAt = oldExpiry;
                payload.TailscaleAuthKey = oldAuthKey;
                try { await hosted.PublishAsync(payload, originalEncrypted, publicId, fragmentKey, CancellationToken.None); } catch { }
                record.ExpiresAt = oldExpiry;
                record.TailscaleKeyId = oldKeyId;
                record.PendingTailscaleKeyRevocations = oldPendingRevocations;
            }
            if (replacementKey is not null && !recordCommitted)
            {
                try { await _headscale.RevokeKeyAsync(replacementKey.Id, CancellationToken.None); } catch { }
            }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(originalEncrypted); }

        return await RevokePendingKeysAsync(record, cancellationToken);
    }

    public async Task<bool> RevokePendingKeysAsync(
        InviteRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.PendingTailscaleKeyRevocations ??= [];
        var original = record.PendingTailscaleKeyRevocations.ToList();
        if (original.Count == 0) return true;

        var remaining = new List<string>();
        foreach (var keyId in original.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal))
        {
            var revoked = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await _headscale.RevokeKeyAsync(keyId, cancellationToken);
                    revoked = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                }
                catch { }
            }
            if (!revoked) remaining.Add(keyId);
        }

        if (remaining.SequenceEqual(original, StringComparer.Ordinal)) return remaining.Count == 0;
        record.PendingTailscaleKeyRevocations = remaining;
        try { await _state.SaveAsync(cancellationToken); }
        catch
        {
            record.PendingTailscaleKeyRevocations = original;
            throw;
        }
        return remaining.Count == 0;
    }
    private static async Task<string> ReadCurrentTailnetAsync(CancellationToken cancellationToken)
    {
        var tailscale = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(tailscale)) throw new FileNotFoundException("Tailscale is not installed on the command center.");
        var result = await ProcessRunner.RunAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(20), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException("Could not read the command center's active tailnet: " + result.StandardError.Trim());
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var tailnet = root.TryGetProperty("CurrentTailnet", out var current) && current.ValueKind == JsonValueKind.Object
            ? ReadString(current, "Name")
            : string.Empty;
        if (string.IsNullOrWhiteSpace(tailnet))
        {
            tailnet = root.TryGetProperty("MagicDNSSuffix", out var suffix) && suffix.ValueKind == JsonValueKind.String
                ? suffix.GetString() ?? string.Empty
                : string.Empty;
        }
        return !string.IsNullOrWhiteSpace(tailnet)
            ? tailnet
            : throw new InvalidOperationException("Tailscale is not signed in to a tailnet on the command center.");
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

}
