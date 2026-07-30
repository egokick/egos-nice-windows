using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public sealed record FinanceCredential(string Username, string Password);

public interface IFinanceCredentialStore
{
    FinanceCredential? Read(string accountId);
    void Write(string accountId, FinanceCredential credential);
    bool Delete(string accountId);
    bool Exists(string accountId);
}

public sealed class FinanceCredentialStoreException : Exception
{
    public FinanceCredentialStoreException(string message)
        : base(message)
    {
    }

    public FinanceCredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WindowsFinanceCredentialStore : IFinanceCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 2560;
    private const string ProfileTargetPrefix = "EgosNiceWindows.Finance.Profile/";
    private const string LegacyTargetPrefix = "EgosNiceWindows.Finance.Account/";
    private static readonly object CredentialSync = new();
    private readonly string _targetPrefix;

    public WindowsFinanceCredentialStore(string profileIdentity)
    {
        if (string.IsNullOrWhiteSpace(profileIdentity))
        {
            throw new ArgumentException("A finance profile identity is required.", nameof(profileIdentity));
        }

        var canonicalIdentity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileIdentity))
            .ToUpperInvariant();
        var identityBytes = Encoding.UTF8.GetBytes(canonicalIdentity);
        try
        {
            var hash = SHA256.HashData(identityBytes);
            _targetPrefix = ProfileTargetPrefix + Convert.ToHexString(hash.AsSpan(0, 16)) + "/Account/";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityBytes);
        }
    }

    public FinanceCredential? Read(string accountId)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        var scopedTarget = BuildScopedTargetName(normalizedAccountId);
        var legacyTarget = BuildLegacyTargetName(normalizedAccountId);
        lock (CredentialSync)
        {
            var scopedCredential = ReadTarget(scopedTarget, accountId, "read profile-scoped");
            var legacyCredential = ReadTarget(legacyTarget, accountId, "read legacy");
            if (scopedCredential is not null)
            {
                if (legacyCredential is null)
                {
                    return scopedCredential;
                }

                if (!CredentialsMatch(scopedCredential, legacyCredential))
                {
                    throw new FinanceCredentialStoreException(
                        $"Profile-scoped and legacy credentials differ for account '{accountId}'. "
                        + "Neither credential was deleted.");
                }

                DeleteTarget(legacyTarget, accountId, "delete verified legacy");
                return scopedCredential;
            }

            if (legacyCredential is null)
            {
                return null;
            }

            WriteTarget(scopedTarget, accountId, legacyCredential);
            var verifiedCredential = ReadTarget(scopedTarget, accountId, "verify migrated profile-scoped")
                ?? throw new FinanceCredentialStoreException(
                    $"Windows Credential Manager did not retain the migrated credential for account '{accountId}'. "
                    + "The legacy credential was not deleted.");
            if (!CredentialsMatch(verifiedCredential, legacyCredential))
            {
                TryDeleteTarget(scopedTarget);
                throw new FinanceCredentialStoreException(
                    $"Windows Credential Manager could not verify the migrated credential for account '{accountId}'. "
                    + "The legacy credential was not deleted.");
            }

            DeleteTarget(legacyTarget, accountId, "delete verified legacy");
            return verifiedCredential;
        }
    }

    public void Write(string accountId, FinanceCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var normalizedAccountId = NormalizeAccountId(accountId);
        var scopedTarget = BuildScopedTargetName(normalizedAccountId);
        var legacyTarget = BuildLegacyTargetName(normalizedAccountId);
        lock (CredentialSync)
        {
            WriteTarget(scopedTarget, accountId, credential);
            var verifiedCredential = ReadTarget(scopedTarget, accountId, "verify profile-scoped write")
                ?? throw new FinanceCredentialStoreException(
                    $"Windows Credential Manager did not retain credentials for account '{accountId}'.");
            if (!CredentialsMatch(verifiedCredential, credential))
            {
                TryDeleteTarget(scopedTarget);
                throw new FinanceCredentialStoreException(
                    $"Windows Credential Manager could not verify credentials for account '{accountId}'.");
            }

            // An explicit write is the authoritative replacement. Only remove an
            // older legacy target after the scoped value matches the requested pair.
            DeleteTarget(legacyTarget, accountId, "delete legacy after verified write");
        }
    }

    public bool Delete(string accountId)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        var scopedTarget = BuildScopedTargetName(normalizedAccountId);
        var legacyTarget = BuildLegacyTargetName(normalizedAccountId);
        lock (CredentialSync)
        {
            var removed = false;
            FinanceCredentialStoreException? scopedFailure = null;
            FinanceCredentialStoreException? legacyFailure = null;
            try
            {
                removed |= DeleteTarget(scopedTarget, accountId, "delete profile-scoped");
            }
            catch (FinanceCredentialStoreException exception)
            {
                scopedFailure = exception;
            }

            try
            {
                removed |= DeleteTarget(legacyTarget, accountId, "delete legacy");
            }
            catch (FinanceCredentialStoreException exception)
            {
                legacyFailure = exception;
            }

            if (scopedFailure is not null || legacyFailure is not null)
            {
                var failures = new[] { scopedFailure, legacyFailure }
                    .Where(exception => exception is not null)
                    .Cast<Exception>()
                    .ToArray();
                throw new FinanceCredentialStoreException(
                    $"Windows Credential Manager could not delete every credential namespace for account '{accountId}'.",
                    new AggregateException(failures));
            }

            return removed;
        }
    }

    public bool Exists(string accountId) => Read(accountId) is not null;

    private static FinanceCredential? ReadTarget(string target, string accountId, string operation)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw CreateStoreException(operation, accountId, error);
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var username = native.UserName ?? string.Empty;
            if (native.CredentialBlobSize == 0 || native.CredentialBlob == nint.Zero)
            {
                return new FinanceCredential(username, string.Empty);
            }

            if (native.CredentialBlobSize > MaximumCredentialBlobBytes
                || native.CredentialBlobSize % sizeof(char) != 0)
            {
                throw new FinanceCredentialStoreException(
                    "Windows Credential Manager returned an invalid finance credential blob.");
            }

            var passwordBytes = new byte[checked((int)native.CredentialBlobSize)];
            try
            {
                Marshal.Copy(native.CredentialBlob, passwordBytes, 0, passwordBytes.Length);
                return new FinanceCredential(username, Encoding.Unicode.GetString(passwordBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void WriteTarget(string target, string accountId, FinanceCredential credential)
    {
        var passwordBytes = Encoding.Unicode.GetBytes(credential.Password ?? string.Empty);
        if (passwordBytes.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            throw new ArgumentException(
                $"The password for account '{accountId}' exceeds the Windows Credential Manager size limit.",
                nameof(credential));
        }

        var passwordPointer = nint.Zero;
        try
        {
            if (passwordBytes.Length > 0)
            {
                passwordPointer = Marshal.AllocCoTaskMem(passwordBytes.Length);
                Marshal.Copy(passwordBytes, 0, passwordPointer, passwordBytes.Length);
            }

            var native = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = passwordPointer,
                Persist = CredPersistLocalMachine,
                UserName = credential.Username ?? string.Empty
            };

            if (!CredWrite(ref native, 0))
            {
                throw CreateStoreException("write", accountId, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (passwordPointer != nint.Zero)
            {
                var zeroes = new byte[passwordBytes.Length];
                Marshal.Copy(zeroes, 0, passwordPointer, zeroes.Length);
                CryptographicOperations.ZeroMemory(zeroes);
                Marshal.FreeCoTaskMem(passwordPointer);
            }

            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool DeleteTarget(string target, string accountId, string operation)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw CreateStoreException(operation, accountId, error);
    }

    private static void TryDeleteTarget(string target)
    {
        try
        {
            CredDelete(target, CredTypeGeneric, 0);
        }
        catch
        {
            // Verification failures leave the legacy target untouched. Cleanup of
            // an unverified scoped target is best-effort and never hides that error.
        }
    }

    private static string NormalizeAccountId(string accountId)
    {
        var normalized = accountId?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A finance account ID is required.", nameof(accountId));
        }

        return normalized;
    }

    private string BuildScopedTargetName(string normalizedAccountId) =>
        _targetPrefix + Uri.EscapeDataString(normalizedAccountId);

    private static string BuildLegacyTargetName(string normalizedAccountId) =>
        LegacyTargetPrefix + Uri.EscapeDataString(normalizedAccountId);

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

    private static FinanceCredentialStoreException CreateStoreException(
        string operation,
        string accountId,
        int errorCode)
    {
        var nativeMessage = new Win32Exception(errorCode).Message;
        return new FinanceCredentialStoreException(
            $"Windows Credential Manager could not {operation} credentials for account '{accountId}' "
            + $"(Windows error {errorCode}: {nativeMessage}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credentialPointer);
}
