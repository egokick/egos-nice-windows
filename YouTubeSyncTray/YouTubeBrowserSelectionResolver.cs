namespace YouTubeSyncTray;

internal static class YouTubeBrowserSelectionResolver
{
    public static BrowserAccountOption? ResolvePreferredBrowserAccount(
        string? selectedYouTubeAccountKey,
        IReadOnlyList<YouTubeAccountOption> youTubeAccounts,
        IReadOnlyList<BrowserAccountOption> browserAccounts,
        IReadOnlyList<KnownLibraryScopeRecord> knownScopes,
        BrowserAccountOption? currentBrowserAccount = null)
    {
        if (browserAccounts.Count == 0 || string.IsNullOrWhiteSpace(selectedYouTubeAccountKey))
        {
            return currentBrowserAccount;
        }

        var normalizedYouTubeAccountKey = selectedYouTubeAccountKey.Trim();
        var selectedYouTubeAccount = youTubeAccounts.FirstOrDefault(option =>
            string.Equals(option.AccountKey, normalizedYouTubeAccountKey, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(selectedYouTubeAccount.AccountKey))
        {
            var linkedAccountIds = GetLinkedBrowserAccountIds(selectedYouTubeAccount);
            if (linkedAccountIds.Count > 0)
            {
                var identityMatch = browserAccounts
                    .Where(option => MatchesAnyLinkedAccountId(option, linkedAccountIds))
                    .OrderByDescending(option => HasMatchingScope(knownScopes, option.AccountKey, normalizedYouTubeAccountKey))
                    .ThenByDescending(option => MatchesBrowserAccount(option, currentBrowserAccount))
                    .ThenBy(option => option.AuthUserIndex)
                    .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(identityMatch.AccountKey))
                {
                    return identityMatch;
                }
            }
        }

        var scopedBrowserAccount = knownScopes
            .Where(scope =>
                scope.IsAvailableOnDisk
                && string.Equals(scope.YouTubeAccountKey, normalizedYouTubeAccountKey, StringComparison.Ordinal))
            .OrderByDescending(scope => scope.LastSuccessfulSyncAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(scope => scope.LastSeenAtUtc)
            .Select(scope => browserAccounts.FirstOrDefault(option =>
                string.Equals(option.AccountKey, scope.BrowserAccountKey, StringComparison.Ordinal)))
            .FirstOrDefault(option => !string.IsNullOrWhiteSpace(option.AccountKey));
        if (!string.IsNullOrWhiteSpace(scopedBrowserAccount.AccountKey))
        {
            return scopedBrowserAccount;
        }

        return currentBrowserAccount;
    }

    internal static IReadOnlyList<string> GetLinkedBrowserAccountIds(YouTubeAccountOption account)
    {
        if (string.IsNullOrWhiteSpace(account.DatasyncId))
        {
            return [];
        }

        var linkedAccountIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in account.DatasyncId.Split("||", StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(segment)
                || string.Equals(segment, account.PageId, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(segment))
            {
                linkedAccountIds.Add(segment);
            }
        }

        return linkedAccountIds;
    }

    private static bool MatchesAnyLinkedAccountId(BrowserAccountOption option, IReadOnlyList<string> linkedAccountIds)
    {
        foreach (var linkedAccountId in linkedAccountIds)
        {
            if (string.Equals(option.AccountId, linkedAccountId, StringComparison.Ordinal)
                || string.Equals(option.Email, linkedAccountId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMatchingScope(
        IReadOnlyList<KnownLibraryScopeRecord> knownScopes,
        string browserAccountKey,
        string youTubeAccountKey)
    {
        return knownScopes.Any(scope =>
            scope.IsAvailableOnDisk
            && string.Equals(scope.BrowserAccountKey, browserAccountKey, StringComparison.Ordinal)
            && string.Equals(scope.YouTubeAccountKey, youTubeAccountKey, StringComparison.Ordinal));
    }

    private static bool MatchesBrowserAccount(BrowserAccountOption option, BrowserAccountOption? currentBrowserAccount)
    {
        return currentBrowserAccount.HasValue
            && string.Equals(option.AccountKey, currentBrowserAccount.Value.AccountKey, StringComparison.Ordinal);
    }
}
