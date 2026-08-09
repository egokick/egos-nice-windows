using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Taildesk.Shared;

/// <summary>
/// Applies the managed-host policy only to RustDesk's fixed Windows service
/// profiles. Every directory and file is opened relative to a held Windows-root
/// handle with reparse traversal disabled; existing files must also have exactly
/// one hard link. No interactive-user profile path is accepted or written here.
/// </summary>
public static class RustDeskServiceProfileStore
{
    private const int MaximumConfigBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[][] ProfileComponents =
    [
        ["ServiceProfiles", "LocalService", "AppData", "Roaming"],
        ["ServiceProfiles", "NetworkService", "AppData", "Roaming"],
        ["System32", "config", "systemprofile", "AppData", "Roaming"]
    ];

    public static void HardenAll()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("RustDesk service-profile hardening requires Windows.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new DirectoryNotFoundException("The fixed Windows directory is unavailable.");

        using var windowsRoot = NativePath.OpenAbsoluteRoot(Path.GetFullPath(windows));
        foreach (var profile in ProfileComponents)
        {
            using var roaming = OpenDirectoryTree(windowsRoot, profile);
            using var rustDesk = NativePath.OpenRelative(
                roaming, "RustDesk", readFile: false, writeFile: false, delete: false,
                requireDirectory: true, NativePath.FileOpenIf);
            using var config = NativePath.OpenRelative(
                rustDesk, "config", readFile: false, writeFile: false, delete: false,
                requireDirectory: true, NativePath.FileOpenIf);
            HardenConfigDirectory(config);
        }
    }

    private static SafeFileHandle OpenDirectoryTree(SafeFileHandle root, IReadOnlyList<string> components)
    {
        SafeFileHandle? current = null;
        try
        {
            foreach (var component in components)
            {
                var parent = current ?? root;
                var next = NativePath.OpenRelative(
                    parent, component, readFile: false, writeFile: false, delete: false,
                    requireDirectory: true, NativePath.FileOpenIf);
                current?.Dispose();
                current = next;
            }
            return current ?? throw new InvalidOperationException("The fixed RustDesk service profile is empty.");
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    private static void HardenConfigDirectory(SafeFileHandle config)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RustDesk2.toml"
        };
        foreach (var entry in NativePath.Enumerate(config))
        {
            if (!entry.Name.StartsWith("RustDesk", StringComparison.OrdinalIgnoreCase)
                || !entry.Name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.IsDirectory || entry.IsReparsePoint)
                throw new UnauthorizedAccessException(
                    "A RustDesk service-profile configuration entry is not a regular file.");
            names.Add(entry.Name);
        }

        foreach (var name in names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            HardenConfigFile(config, name);
    }

    private static void HardenConfigFile(SafeFileHandle directory, string name)
    {
        using var handle = NativePath.OpenRelative(
            directory, name, readFile: true, writeFile: true, delete: false,
            requireDirectory: false, NativePath.FileOpenIf, exclusive: true);
        using var stream = new FileStream(
            NativePath.Duplicate(handle), FileAccess.ReadWrite, 64 * 1024, isAsync: false);
        if (stream.Length < 0 || stream.Length > MaximumConfigBytes)
            throw new InvalidDataException("A RustDesk service-profile configuration is too large.");

        var existingBytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(existingBytes);
        var existing = existingBytes.Length == 0 ? string.Empty : StrictUtf8.GetString(existingBytes);
        if (existing.Length > 0 && existing[0] == '\uFEFF') existing = existing[1..];
        var hardened = RustDeskConfiguration.HardenManagedHost(existing);
        var hardenedBytes = StrictUtf8.GetBytes(hardened);
        if (hardenedBytes.Length is <= 0 or > MaximumConfigBytes)
            throw new InvalidDataException("The hardened RustDesk configuration has an invalid size.");

        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(hardenedBytes);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        var verifiedBytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(verifiedBytes);
        var verified = StrictUtf8.GetString(verifiedBytes);
        if (!RustDeskConfiguration.IsManagedHostHardened(verified))
            throw new InvalidDataException("RustDesk service-profile hardening did not verify after write.");
    }
}
