namespace Taildesk.Shared;

public static class PrivateStorage
{
    public static string InviteDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opticon", "Invitations");

    public static string ValidateInviteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose a local invitation output folder.");
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        var cloudRoots = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .ToArray();
        if (cloudRoots.Any(root => IsWithin(fullPath, root))
            || fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Opticon invitations cannot be stored in OneDrive or another OneDrive-backed folder.");
        }
        return fullPath;
    }

    public static bool IsOneDrivePath(string path)
    {
        try { _ = ValidateInviteDirectory(path); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
