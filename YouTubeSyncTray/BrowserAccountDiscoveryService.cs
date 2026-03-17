using System.Text;
using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class BrowserAccountDiscoveryService
{
    private readonly KnownLibraryScopeStore? _knownLibraryScopeStore;

    public BrowserAccountDiscoveryService(KnownLibraryScopeStore? knownLibraryScopeStore = null)
    {
        _knownLibraryScopeStore = knownLibraryScopeStore;
    }

    public IReadOnlyList<BrowserAccountOption> DiscoverAccounts(AppSettings settings)
    {
        settings.Normalize();

        var options = new List<BrowserAccountOption>();
        foreach (var candidate in GetCandidateProfiles(settings))
        {
            foreach (var option in DiscoverAccounts(candidate.Browser, candidate.Profile))
            {
                options.Add(option);
            }
        }

        if (_knownLibraryScopeStore is not null)
        {
            foreach (var scope in _knownLibraryScopeStore.LoadScopes())
            {
                if (string.IsNullOrWhiteSpace(scope.BrowserAccountKey))
                {
                    continue;
                }

                options.Add(KnownLibraryScopeStore.CreateBrowserAccountOption(scope));
            }
        }

        return options
            .GroupBy(option => option.AccountKey, StringComparer.Ordinal)
            .Select(SelectPreferredOption)
            .OrderBy(option => option.Browser != settings.BrowserCookies)
            .ThenBy(option => !string.Equals(option.Profile, settings.BrowserProfile, StringComparison.OrdinalIgnoreCase))
            .ThenBy(option => option.AuthUserIndex)
            .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public BrowserAccountOption? ResolveSelectedAccount(AppSettings settings)
    {
        var accounts = DiscoverAccounts(settings);
        if (accounts.Count == 0)
        {
            return null;
        }

        var explicitAccountKey = settings.SelectedBrowserAccountKey;
        if (string.IsNullOrWhiteSpace(explicitAccountKey)
            && !string.IsNullOrWhiteSpace(settings.SelectedAccountKey)
            && !settings.SelectedAccountKey.StartsWith("yt|", StringComparison.Ordinal))
        {
            explicitAccountKey = settings.SelectedAccountKey;
        }

        if (!string.IsNullOrWhiteSpace(explicitAccountKey))
        {
            var explicitSelection = accounts.FirstOrDefault(option =>
                string.Equals(option.AccountKey, explicitAccountKey, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(explicitSelection.AccountKey))
            {
                return explicitSelection;
            }

            return BuildFallbackSelectedAccount(explicitAccountKey);
        }

        var preferredAccount = accounts.FirstOrDefault(option =>
            option.Browser == settings.BrowserCookies
            && string.Equals(option.Profile, settings.BrowserProfile, StringComparison.OrdinalIgnoreCase)
            && option.AuthUserIndex == 0);
        if (!string.IsNullOrWhiteSpace(preferredAccount.AccountKey))
        {
            return preferredAccount;
        }

        var preferredProfile = accounts.FirstOrDefault(option =>
            option.Browser == settings.BrowserCookies
            && string.Equals(option.Profile, settings.BrowserProfile, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferredProfile.AccountKey))
        {
            return preferredProfile;
        }

        return accounts[0];
    }

    public int ResolveSelectedAuthUserIndex(AppSettings settings)
    {
        var selected = ResolveSelectedAccount(settings);
        return selected?.AuthUserIndex ?? 0;
    }

    internal IReadOnlyList<BrowserAccountOption> DiscoverAccounts(BrowserCookieSource browser, string profile)
    {
        profile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
        if (!ChromiumBrowserLocator.TryGetProfilePreferencesPath(browser, profile, out var preferencesPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            var accountInfo = ParseAccountInfo(document.RootElement);
            var gaiaAccounts = TryReadGaiaCookieAccounts(document.RootElement);

            var sourceAccounts = gaiaAccounts.Count > 0
                ? gaiaAccounts
                : accountInfo
                    .Select(info => new BrowserAccountIdentity(
                        info.AccountId,
                        info.Email,
                        info.DisplayName,
                        info.GivenName,
                        info.AvatarUrl))
                    .ToList();

            if (sourceAccounts.Count == 0)
            {
                return [];
            }

            var accountInfoById = accountInfo
                .Where(info => !string.IsNullOrWhiteSpace(info.AccountId))
                .ToDictionary(info => info.AccountId, StringComparer.OrdinalIgnoreCase);
            var accountInfoByEmail = accountInfo
                .Where(info => !string.IsNullOrWhiteSpace(info.Email))
                .ToDictionary(info => info.Email, StringComparer.OrdinalIgnoreCase);

            var options = new List<BrowserAccountOption>();
            for (var index = 0; index < sourceAccounts.Count; index++)
            {
                var account = sourceAccounts[index];
                if (!TryMergeAccountInfo(account, accountInfoById, accountInfoByEmail, out var merged))
                {
                    merged = account;
                }

                var displayName = GetDisplayName(merged);
                var email = merged.Email?.Trim() ?? string.Empty;
                var accountId = merged.AccountId?.Trim() ?? string.Empty;
                if (displayName.Length == 0 && email.Length == 0 && accountId.Length == 0)
                {
                    continue;
                }

                options.Add(new BrowserAccountOption(
                    AccountKey: BuildAccountKey(browser, profile, accountId, email, index),
                    Browser: browser,
                    BrowserName: ChromiumBrowserLocator.GetDisplayName(browser),
                    Profile: profile,
                    AuthUserIndex: index,
                    AccountId: accountId,
                    Email: email,
                    DisplayName: displayName,
                    AvatarUrl: merged.AvatarUrl?.Trim() ?? string.Empty));
            }

            return options;
        }
        catch
        {
            return [];
        }
    }

    internal static BrowserAccountOption? BuildFallbackSelectedAccount(string? accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            return null;
        }

        var normalizedAccountKey = accountKey.Trim();
        var parts = normalizedAccountKey.Split('|', 3, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !Enum.TryParse(parts[0], ignoreCase: true, out BrowserCookieSource browser))
        {
            return null;
        }

        var profile = parts[1];
        var identity = parts.Length == 3 ? parts[2] : string.Empty;
        var email = identity.Contains('@', StringComparison.Ordinal) ? identity : string.Empty;
        var displayName = string.IsNullOrWhiteSpace(identity) ? "Saved browser account" : identity;

        return new BrowserAccountOption(
            normalizedAccountKey,
            browser,
            ChromiumBrowserLocator.GetDisplayName(browser),
            string.IsNullOrWhiteSpace(profile) ? "Default" : profile,
            0,
            identity,
            email,
            displayName,
            string.Empty);
    }

    internal static IReadOnlyList<BrowserAccountIdentity> ParseListAccountsBinaryData(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return [];
        }

        try
        {
            var payload = Convert.FromBase64String(encoded.Trim());
            var accounts = new List<BrowserAccountIdentity>();
            var offset = 0;
            while (offset < payload.Length)
            {
                var tag = ReadVarint(payload, ref offset);
                var fieldNumber = (int)(tag >> 3);
                var wireType = (int)(tag & 0x7);
                if (fieldNumber == 1 && wireType == 2)
                {
                    var length = checked((int)ReadVarint(payload, ref offset));
                    var end = Math.Min(payload.Length, offset + length);
                    accounts.Add(ParseAccountMessage(payload.AsSpan(offset, end - offset)));
                    offset = end;
                    continue;
                }

                SkipField(payload, ref offset, wireType);
            }

            return accounts
                .Where(account =>
                    !string.IsNullOrWhiteSpace(account.Email)
                    || !string.IsNullOrWhiteSpace(account.DisplayName)
                    || !string.IsNullOrWhiteSpace(account.AccountId))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<(BrowserCookieSource Browser, string Profile)> GetCandidateProfiles(AppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var browsers = ChromiumBrowserLocator.GetInstalledBrowsers();
        if (browsers.Count == 0)
        {
            browsers = ChromiumBrowserLocator.GetManagedBrowsers();
        }

        foreach (var browser in browsers)
        {
            foreach (var profile in new[] { settings.BrowserProfile }
                         .Concat(ChromiumBrowserLocator.EnumerateProfiles(browser))
                         .Concat(["Default"]))
            {
                if (string.IsNullOrWhiteSpace(profile))
                {
                    continue;
                }

                var normalizedProfile = profile.Trim();
                var key = $"{browser}|{normalizedProfile}";
                if (seen.Add(key))
                {
                    yield return (browser, normalizedProfile);
                }
            }
        }
    }

    private static BrowserAccountOption SelectPreferredOption(IGrouping<string, BrowserAccountOption> group)
    {
        return group
            .OrderByDescending(option => !string.IsNullOrWhiteSpace(option.Email))
            .ThenByDescending(option => !string.IsNullOrWhiteSpace(option.DisplayName))
            .ThenBy(option => option.AuthUserIndex)
            .First();
    }

    private static List<AccountInfoEntry> ParseAccountInfo(JsonElement root)
    {
        var results = new List<AccountInfoEntry>();
        if (!root.TryGetProperty("account_info", out var accountInfoElement)
            || accountInfoElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in accountInfoElement.EnumerateArray())
        {
            results.Add(new AccountInfoEntry(
                AccountId: GetString(item, "gaia"),
                Email: GetString(item, "email"),
                DisplayName: GetString(item, "full_name"),
                GivenName: GetString(item, "given_name"),
                AvatarUrl: GetString(item, "picture_url")));
        }

        return results;
    }

    private static IReadOnlyList<BrowserAccountIdentity> TryReadGaiaCookieAccounts(JsonElement root)
    {
        if (!root.TryGetProperty("gaia_cookie", out var gaiaCookieElement)
            || gaiaCookieElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var encoded = GetString(gaiaCookieElement, "last_list_accounts_binary_data");
        return ParseListAccountsBinaryData(encoded);
    }

    private static bool TryMergeAccountInfo(
        BrowserAccountIdentity source,
        IReadOnlyDictionary<string, AccountInfoEntry> byId,
        IReadOnlyDictionary<string, AccountInfoEntry> byEmail,
        out BrowserAccountIdentity merged)
    {
        if (!string.IsNullOrWhiteSpace(source.AccountId)
            && byId.TryGetValue(source.AccountId, out var infoById))
        {
            merged = new BrowserAccountIdentity(
                AccountId: source.AccountId,
                Email: FirstNonEmpty(source.Email, infoById.Email),
                DisplayName: FirstNonEmpty(source.DisplayName, infoById.DisplayName),
                GivenName: FirstNonEmpty(source.GivenName, infoById.GivenName),
                AvatarUrl: FirstNonEmpty(source.AvatarUrl, infoById.AvatarUrl));
            return true;
        }

        if (!string.IsNullOrWhiteSpace(source.Email)
            && byEmail.TryGetValue(source.Email, out var infoByEmail))
        {
            merged = new BrowserAccountIdentity(
                AccountId: FirstNonEmpty(source.AccountId, infoByEmail.AccountId),
                Email: source.Email,
                DisplayName: FirstNonEmpty(source.DisplayName, infoByEmail.DisplayName),
                GivenName: FirstNonEmpty(source.GivenName, infoByEmail.GivenName),
                AvatarUrl: FirstNonEmpty(source.AvatarUrl, infoByEmail.AvatarUrl));
            return true;
        }

        merged = source;
        return false;
    }

    private static BrowserAccountIdentity ParseAccountMessage(ReadOnlySpan<byte> message)
    {
        var displayName = string.Empty;
        var email = string.Empty;
        var accountId = string.Empty;
        var givenName = string.Empty;

        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            switch (fieldNumber, wireType)
            {
                case (2, 2):
                    displayName = ReadString(message, ref offset);
                    break;
                case (3, 2):
                    email = ReadString(message, ref offset);
                    break;
                case (10, 2):
                    accountId = ReadString(message, ref offset);
                    break;
                case (16, 2):
                    givenName = ReadString(message, ref offset);
                    break;
                default:
                    SkipField(message, ref offset, wireType);
                    break;
            }
        }

        return new BrowserAccountIdentity(
            AccountId: accountId,
            Email: email,
            DisplayName: displayName,
            GivenName: givenName,
            AvatarUrl: string.Empty);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> buffer, ref int offset)
    {
        ulong value = 0;
        var shift = 0;
        while (offset < buffer.Length)
        {
            var current = buffer[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidOperationException("Invalid protobuf varint.");
            }
        }

        throw new InvalidOperationException("Unexpected end of protobuf payload.");
    }

    private static void SkipField(ReadOnlySpan<byte> buffer, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(buffer, ref offset);
                return;
            case 2:
                var length = checked((int)ReadVarint(buffer, ref offset));
                offset = Math.Min(buffer.Length, offset + length);
                return;
            default:
                throw new InvalidOperationException($"Unsupported protobuf wire type '{wireType}'.");
        }
    }

    private static string ReadString(ReadOnlySpan<byte> buffer, ref int offset)
    {
        var length = checked((int)ReadVarint(buffer, ref offset));
        if (length < 0 || offset + length > buffer.Length)
        {
            throw new InvalidOperationException("Invalid protobuf string length.");
        }

        var value = Encoding.UTF8.GetString(buffer.Slice(offset, length));
        offset += length;
        return value;
    }

    private static string BuildAccountKey(
        BrowserCookieSource browser,
        string profile,
        string accountId,
        string email,
        int authUserIndex)
    {
        var identity = !string.IsNullOrWhiteSpace(accountId)
            ? accountId
            : !string.IsNullOrWhiteSpace(email)
                ? email.ToLowerInvariant()
                : $"authuser-{authUserIndex}";
        return $"{browser}|{profile}|{identity}";
    }

    private static string GetDisplayName(BrowserAccountIdentity account)
    {
        return FirstNonEmpty(account.DisplayName, account.GivenName, account.Email, account.AccountId);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }

    private readonly record struct AccountInfoEntry(string AccountId, string Email, string DisplayName, string GivenName, string AvatarUrl);
}

internal readonly record struct BrowserAccountOption(
    string AccountKey,
    BrowserCookieSource Browser,
    string BrowserName,
    string Profile,
    int AuthUserIndex,
    string AccountId,
    string Email,
    string DisplayName,
    string AvatarUrl)
{
    public string Label
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
            if (!string.IsNullOrWhiteSpace(Email)
                && !string.Equals(name, Email, StringComparison.OrdinalIgnoreCase))
            {
                name = $"{name} ({Email})";
            }

            return $"{name} - {BrowserName} / {Profile}";
        }
    }
}

internal readonly record struct BrowserAccountIdentity(
    string AccountId,
    string Email,
    string DisplayName,
    string GivenName,
    string AvatarUrl);
