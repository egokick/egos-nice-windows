namespace Taildesk.Shared;

public sealed class PathGuard
{
    private readonly Dictionary<string, string> _roots;

    public PathGuard(IReadOnlyDictionary<string, string> roots)
    {
        _roots = roots.ToDictionary(
            pair => pair.Key,
            pair => Path.GetFullPath(Environment.ExpandEnvironmentVariables(pair.Value)).TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RootDto> GetRoots() => _roots
        .Select(pair => new RootDto { Id = pair.Key, DisplayName = pair.Key, PathHint = pair.Value })
        .OrderBy(root => root.DisplayName)
        .ToList();

    public string Resolve(string rootId, string? relativePath, bool mustExist = true)
    {
        if (!_roots.TryGetValue(rootId, out var root))
        {
            throw new UnauthorizedAccessException("That shared root is not available.");
        }

        var suppliedPath = relativePath ?? string.Empty;
        if (Path.IsPathRooted(suppliedPath)
            || suppliedPath.StartsWith("\\\\", StringComparison.Ordinal)
            || suppliedPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || suppliedPath.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || suppliedPath.Contains(':'))
        {
            throw new UnauthorizedAccessException("Absolute, device, UNC, and alternate-stream paths are not allowed.");
        }

        var safeRelative = suppliedPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, safeRelative));
        var prefix = root + Path.DirectorySeparatorChar;

        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The requested path leaves the shared root.");
        }

        RejectReparseTraversal(root, candidate);
        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new FileNotFoundException("The requested path does not exist.");
        }

        return candidate;
    }

    private static void RejectReparseTraversal(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        if (HasReparsePoint(current))
        {
            throw new UnauthorizedAccessException("Shared roots cannot be links or junctions.");
        }

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && HasReparsePoint(current))
            {
                throw new UnauthorizedAccessException("Links and junctions are not followed in shared roots.");
            }
        }
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
