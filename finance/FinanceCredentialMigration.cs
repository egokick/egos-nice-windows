using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class FinanceCredentialMigration
{
    private const string RedactedMarker = "[redacted]";
    private static readonly Regex LabeledCredentialPattern = new(
        @"(?im)(?<label>\b(?:username|user[ \t]+name|login[ \t]+(?:id|name)|password|passcode|pin)\b[ \t]*(?::|=|\bis\b)[ \t]*)(?!\[redacted\])(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;|\r\n]+)",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);

    public static void Migrate(
        string dataRoot,
        string envPath,
        IFinanceCredentialStore credentialStore)
    {
        VerifyRedactionInvariants();
        var replacements = new List<(string Path, string Content)>();
        var financeDirectory = Path.Combine(dataRoot, "data", "finance");
        var accountsPath = Path.Combine(financeDirectory, "accounts.json");
        var snapshotsPath = Path.Combine(financeDirectory, "snapshots.jsonl");

        PrepareAccountsMigration(accountsPath, credentialStore, replacements);
        PrepareEnvironmentMigration(envPath, credentialStore, replacements);
        PrepareSnapshotMigration(snapshotsPath, credentialStore, replacements);

        foreach (var replacement in replacements)
        {
            FinanceDataFile.WriteTextAtomic(replacement.Path, replacement.Content);
        }
    }

    public static string? RedactCredentialMaterial(
        string? value,
        params FinanceCredential?[] credentials)
    {
        if (value is null)
        {
            return null;
        }

        var redacted = value;
        foreach (var credential in credentials)
        {
            if (credential is null)
            {
                continue;
            }

            redacted = ReplaceExactCredential(redacted, credential.Username, StringComparison.OrdinalIgnoreCase);
            redacted = ReplaceExactCredential(redacted, credential.Password, StringComparison.Ordinal);
        }

        return LabeledCredentialPattern.Replace(
            redacted,
            match => match.Groups["label"].Value + RedactedMarker);
    }

    public static bool ContainsCredentialMaterial(
        string? value,
        params FinanceCredential?[] credentials) =>
        value is not null
        && !string.Equals(value, RedactCredentialMaterial(value, credentials), StringComparison.Ordinal);

    private static void PrepareAccountsMigration(
        string path,
        IFinanceCredentialStore credentialStore,
        ICollection<(string Path, string Content)> replacements)
    {
        var content = ReadOptionalText(path);
        if (content is null)
        {
            return;
        }

        JsonArray accounts;
        try
        {
            accounts = JsonNode.Parse(content) as JsonArray
                ?? throw new FinanceDataException(
                    $"Finance data file '{Path.GetFileName(path)}' must contain a JSON array.");
        }
        catch (FinanceDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new FinanceDataException(
                $"Finance data file '{Path.GetFileName(path)}' is invalid. "
                + "Credential migration did not change it.",
                exception);
        }

        var changed = false;
        foreach (var node in accounts)
        {
            if (node is not JsonObject account)
            {
                throw new FinanceDataException(
                    $"Finance data file '{Path.GetFileName(path)}' contains a non-object account.");
            }

            var accountId = GetString(account, "id");
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new FinanceDataException(
                    $"Finance data file '{Path.GetFileName(path)}' contains an account without an ID.");
            }

            var usernameProperty = FindProperty(account, "username");
            var passwordProperty = FindProperty(account, "password");
            var legacyCredential = ReadLegacyCredentialPair(
                usernameProperty,
                passwordProperty,
                $"account '{accountId}' in '{Path.GetFileName(path)}'");
            var storedCredential = credentialStore.Read(accountId);
            if (legacyCredential is not null)
            {
                storedCredential = VerifyCredentialMigration(accountId, legacyCredential, credentialStore);
            }

            changed |= ScrubJsonCollectorNotes(account, storedCredential, legacyCredential);

            if (usernameProperty is not null)
            {
                account.Remove(usernameProperty.Value.Key);
            }

            if (passwordProperty is not null)
            {
                account.Remove(passwordProperty.Value.Key);
            }

            changed |= usernameProperty is not null || passwordProperty is not null;
        }

        if (changed)
        {
            replacements.Add((path, accounts.ToJsonString(IndentedJson)));
        }
    }

    private static void PrepareEnvironmentMigration(
        string path,
        IFinanceCredentialStore credentialStore,
        ICollection<(string Path, string Content)> replacements)
    {
        var content = ReadOptionalText(path);
        if (content is null)
        {
            return;
        }

        var values = EnvFile.Read(path);
        var accountKeys = values.Keys
            .Select(key => Regex.Match(
                key,
                @"^FINANCE_ACCOUNT_(?<id>\d+)_(?<field>USERNAME|PASSWORD|COLLECTOR_NOTES)$",
                RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .ToList();
        if (accountKeys.Count == 0)
        {
            return;
        }

        var credentials = new Dictionary<string, FinanceCredential?>(StringComparer.OrdinalIgnoreCase);
        foreach (var accountId in accountKeys
                     .Select(match => match.Groups["id"].Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var usernameKey = $"FINANCE_ACCOUNT_{accountId}_USERNAME";
            var passwordKey = $"FINANCE_ACCOUNT_{accountId}_PASSWORD";
            var legacyCredential = ReadLegacyCredentialPair(
                values.ContainsKey(usernameKey),
                values.GetValueOrDefault(usernameKey),
                values.ContainsKey(passwordKey),
                values.GetValueOrDefault(passwordKey),
                $"account '{accountId}' in '{Path.GetFileName(path)}'");
            var storedCredential = credentialStore.Read(accountId);
            if (legacyCredential is not null)
            {
                storedCredential = VerifyCredentialMigration(accountId, legacyCredential, credentialStore);
            }

            credentials[accountId] = storedCredential;
        }

        var sanitized = Regex.Replace(
            content,
            @"(?im)^[ \t]*FINANCE_ACCOUNT_\d+_(?:USERNAME|PASSWORD)[ \t]*=.*(?:\r?\n|$)",
            string.Empty);
        sanitized = ScrubEnvironmentCollectorNotes(sanitized, credentials);
        if (!string.Equals(content, sanitized, StringComparison.Ordinal))
        {
            replacements.Add((path, sanitized));
        }
    }

    private static void PrepareSnapshotMigration(
        string path,
        IFinanceCredentialStore credentialStore,
        ICollection<(string Path, string Content)> replacements)
    {
        var content = ReadOptionalText(path);
        if (content is null)
        {
            return;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var migratedLines = new List<string>(lines.Length);
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                if (index < lines.Length - 1)
                {
                    migratedLines.Add(string.Empty);
                }

                continue;
            }

            JsonObject snapshot;
            try
            {
                snapshot = JsonNode.Parse(line) as JsonObject
                    ?? throw new FinanceDataException(
                        $"Finance data file '{Path.GetFileName(path)}' contains non-object JSON on line {index + 1}.");
            }
            catch (FinanceDataException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new FinanceDataException(
                    $"Finance data file '{Path.GetFileName(path)}' is invalid on line {index + 1}. "
                    + "Credential migration did not change it.",
                    exception);
            }

            var accountsProperty = FindProperty(snapshot, "accounts");
            if (accountsProperty?.Value is JsonArray accounts)
            {
                foreach (var accountNode in accounts)
                {
                    if (accountNode is not JsonObject account)
                    {
                        throw new FinanceDataException(
                            $"Finance data file '{Path.GetFileName(path)}' contains a non-object account "
                            + $"on line {index + 1}.");
                    }

                    var accountId = GetString(account, "id");
                    var storedCredential = string.IsNullOrWhiteSpace(accountId)
                        ? null
                        : credentialStore.Read(accountId);
                    changed |= ScrubSnapshotAccount(account, storedCredential);
                }
            }

            migratedLines.Add(snapshot.ToJsonString(LineJson));
        }

        if (changed)
        {
            replacements.Add((path, string.Join(Environment.NewLine, migratedLines) + Environment.NewLine));
        }
    }

    private static FinanceCredential? ReadLegacyCredentialPair(
        KeyValuePair<string, JsonNode?>? usernameProperty,
        KeyValuePair<string, JsonNode?>? passwordProperty,
        string source) =>
        ReadLegacyCredentialPair(
            usernameProperty is not null,
            usernameProperty is null ? null : GetStringValue(usernameProperty.Value.Value),
            passwordProperty is not null,
            passwordProperty is null ? null : GetStringValue(passwordProperty.Value.Value),
            source);

    private static FinanceCredential? ReadLegacyCredentialPair(
        bool hasUsernameProperty,
        string? usernameValue,
        bool hasPasswordProperty,
        string? passwordValue,
        string source)
    {
        if (!hasUsernameProperty && !hasPasswordProperty)
        {
            return null;
        }

        var username = usernameValue ?? string.Empty;
        var password = passwordValue ?? string.Empty;
        var hasUsername = !string.IsNullOrWhiteSpace(username);
        var hasPassword = password.Length > 0;
        if (!hasUsername && !hasPassword)
        {
            return null;
        }

        if (!hasUsernameProperty || !hasPasswordProperty || !hasUsername || !hasPassword)
        {
            throw new FinanceDataException(
                $"Legacy credentials for {source} are incomplete. No plaintext finance files were changed.");
        }

        return new FinanceCredential(username.Trim(), password);
    }

    private static bool ScrubSnapshotAccount(
        JsonObject account,
        FinanceCredential? storedCredential)
    {
        var usernameProperty = FindProperty(account, "username");
        var passwordProperty = FindProperty(account, "password");
        var usernameHint = usernameProperty is null
            ? string.Empty
            : GetStringValue(usernameProperty.Value.Value);
        var passwordHint = passwordProperty is null
            ? string.Empty
            : GetStringValue(passwordProperty.Value.Value);
        var snapshotHints = string.IsNullOrWhiteSpace(usernameHint) && string.IsNullOrWhiteSpace(passwordHint)
            ? null
            : new FinanceCredential(usernameHint, passwordHint);

        var changed = ScrubJsonCollectorNotes(account, storedCredential, snapshotHints);
        changed |= RemoveProperty(account, "username");
        changed |= RemoveProperty(account, "password");
        return changed;
    }

    private static void VerifyRedactionInvariants()
    {
        var credential = new FinanceCredential("legacy-user", "secret-value");
        const string instructionFixture = "Password: secret-value. Keep these unrelated instructions.";
        const string expectedInstruction = "Password: [redacted]. Keep these unrelated instructions.";
        if (!string.Equals(
                RedactCredentialMaterial(instructionFixture, credential),
                expectedInstruction,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Credential redaction did not preserve unrelated instructions.");
        }

        var snapshotFixture = new JsonObject
        {
            ["id"] = "fixture-account",
            ["username"] = "legacy-user",
            ["collectorNotes"] = "Username: legacy-user. Keep this snapshot instruction."
        };
        if (!ScrubSnapshotAccount(snapshotFixture, credential)
            || FindProperty(snapshotFixture, "username") is not null
            || !string.Equals(
                GetString(snapshotFixture, "collectorNotes"),
                "Username: [redacted]. Keep this snapshot instruction.",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Username-only snapshot credential redaction failed its fixture check.");
        }
    }

    private static bool ScrubJsonCollectorNotes(
        JsonObject account,
        params FinanceCredential?[] credentials)
    {
        var notesProperty = FindProperty(account, "collectorNotes");
        if (notesProperty is null || notesProperty.Value.Value is null)
        {
            return false;
        }

        var original = GetStringValue(notesProperty.Value.Value);
        var redacted = RedactCredentialMaterial(original, credentials) ?? string.Empty;
        if (string.Equals(original, redacted, StringComparison.Ordinal))
        {
            return false;
        }

        account[notesProperty.Value.Key] = redacted;
        return true;
    }

    private static string ScrubEnvironmentCollectorNotes(
        string content,
        IReadOnlyDictionary<string, FinanceCredential?> credentials)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Regex.Split(content, "\r?\n");
        var collectorNotesLine = new Regex(
            @"^(?<prefix>[ \t]*FINANCE_ACCOUNT_(?<id>\d+)_COLLECTOR_NOTES[ \t]*=[ \t]*)(?<value>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var index = 0; index < lines.Length; index++)
        {
            var match = collectorNotesLine.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            credentials.TryGetValue(match.Groups["id"].Value, out var credential);
            var original = match.Groups["value"].Value;
            var redacted = RedactCredentialMaterial(original, credential) ?? string.Empty;
            if (!string.Equals(original, redacted, StringComparison.Ordinal))
            {
                lines[index] = match.Groups["prefix"].Value + redacted;
            }
        }

        return string.Join(newline, lines);
    }

    private static string ReplaceExactCredential(
        string value,
        string? credentialValue,
        StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(credentialValue))
        {
            return value;
        }

        return value.Replace(credentialValue, RedactedMarker, comparison);
    }

    private static FinanceCredential VerifyCredentialMigration(
        string accountId,
        FinanceCredential legacyCredential,
        IFinanceCredentialStore credentialStore)
    {
        var stored = credentialStore.Read(accountId);
        if (stored is null)
        {
            credentialStore.Write(accountId, legacyCredential);
            stored = credentialStore.Read(accountId)
                ?? throw new FinanceCredentialStoreException(
                    $"Windows Credential Manager did not retain credentials for account '{accountId}'.");
        }

        if (!CredentialsMatch(stored, legacyCredential))
        {
            throw new FinanceCredentialStoreException(
                $"Windows Credential Manager already contains different credentials for account '{accountId}'. "
                + "No plaintext finance files were changed.");
        }

        return stored;
    }

    private static bool CredentialsMatch(FinanceCredential left, FinanceCredential right)
    {
        if (!string.Equals(left.Username, right.Username, StringComparison.Ordinal))
        {
            return false;
        }

        var leftBytes = Encoding.Unicode.GetBytes(left.Password);
        var rightBytes = Encoding.Unicode.GetBytes(right.Password);
        try
        {
            return leftBytes.Length == rightBytes.Length
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static string? ReadOptionalText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FinanceDataException(
                $"Finance data file '{Path.GetFileName(path)}' could not be read for credential migration. "
                + "The file was left unchanged.",
                exception);
        }
    }

    private static KeyValuePair<string, JsonNode?>? FindProperty(JsonObject value, string name)
    {
        foreach (var property in value)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private static bool RemoveProperty(JsonObject value, string name)
    {
        var property = FindProperty(value, name);
        return property is not null && value.Remove(property.Value.Key);
    }

    private static string? GetString(JsonObject value, string name)
    {
        var property = FindProperty(value, name);
        return property is null ? null : GetStringValue(property.Value.Value);
    }

    private static string GetStringValue(JsonNode? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        try
        {
            return value.GetValue<string>() ?? string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            throw new FinanceDataException("A legacy finance credential value is not a JSON string.", exception);
        }
    }
}
