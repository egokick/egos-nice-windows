namespace StayActive.Remotes;

/// <summary>
/// Local, display-only labels for a remote device. These values must never be
/// used as device identity, trust, capability, or routing data.
/// </summary>
internal sealed record RemoteDeviceDisplayOverride
{
    internal const int MaxLabelLength = 80;

    private string? _ownerOrUserLabel;
    private string? _location;

    public RemoteDeviceDisplayOverride()
    {
    }

    public RemoteDeviceDisplayOverride(string? ownerOrUserLabel, string? location)
    {
        OwnerOrUserLabel = ownerOrUserLabel;
        Location = location;
    }

    public string? OwnerOrUserLabel
    {
        get => _ownerOrUserLabel;
        set => _ownerOrUserLabel = NormalizeLabel(value);
    }

    public string? Location
    {
        get => _location;
        set => _location = NormalizeLabel(value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => OwnerOrUserLabel is null && Location is null;

    private static string? NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = string.Create(value.Length, value, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                // Replace rather than remove controls so "first\nlast" does not
                // unexpectedly become "firstlast" in the UI.
                destination[index] = char.IsControl(source[index]) ? ' ' : source[index];
            }
        }).Trim();

        if (sanitized.Length == 0)
        {
            return null;
        }

        if (sanitized.Length > MaxLabelLength)
        {
            var length = MaxLabelLength;
            if (char.IsHighSurrogate(sanitized[length - 1])
                && char.IsLowSurrogate(sanitized[length]))
            {
                length--;
            }

            sanitized = sanitized[..length].TrimEnd();
        }

        return sanitized.Length == 0 ? null : sanitized;
    }
}

/// <summary>
/// Decorates fleet snapshots with local labels while leaving the wrapped
/// client's authoritative device and control-plane data untouched.
/// </summary>
internal sealed class LocalDisplayRemoteFleetClient : IRemoteFleetClient
{
    private readonly IRemoteFleetClient _inner;
    private readonly Func<IReadOnlyDictionary<string, RemoteDeviceDisplayOverride>> _getOverrides;

    public LocalDisplayRemoteFleetClient(
        IRemoteFleetClient inner,
        Func<IReadOnlyDictionary<string, RemoteDeviceDisplayOverride>> getOverrides)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _getOverrides = getOverrides ?? throw new ArgumentNullException(nameof(getOverrides));
    }

    public RemoteFleetSnapshot GetCachedSnapshot()
    {
        var snapshot = _inner.GetCachedSnapshot();
        var configuredOverrides = _getOverrides();
        if (configuredOverrides is null || configuredOverrides.Count == 0)
        {
            return snapshot;
        }

        // Re-index with Ordinal even if the caller supplied a case-insensitive
        // dictionary. A display label for one device ID must never bleed into a
        // differently-cased ID.
        var exactOverrides = new Dictionary<string, RemoteDeviceDisplayOverride>(StringComparer.Ordinal);
        foreach (var pair in configuredOverrides)
        {
            if (!string.IsNullOrEmpty(pair.Key) && pair.Value is not null)
            {
                exactOverrides[pair.Key] = pair.Value;
            }
        }

        if (exactOverrides.Count == 0)
        {
            return snapshot;
        }

        var devices = snapshot.Devices
            .Select(device => ApplyOverride(device, exactOverrides))
            .ToArray();

        return snapshot with { Devices = devices };
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        _inner.RefreshAsync(cancellationToken);

    public void Dispose() => _inner.Dispose();

    private static RemoteDevice ApplyOverride(
        RemoteDevice device,
        IReadOnlyDictionary<string, RemoteDeviceDisplayOverride> overrides)
    {
        if (!overrides.TryGetValue(device.Id, out var displayOverride)
            || displayOverride.IsEmpty)
        {
            return device;
        }

        return device with
        {
            OwnerDisplayName = displayOverride.OwnerOrUserLabel ?? device.OwnerDisplayName,
            Location = displayOverride.Location ?? device.Location
        };
    }
}
