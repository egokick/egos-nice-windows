namespace Taildesk.Shared;

public static class DotNetSdkPolicy
{
    public const string SignedPolicy = "10.*.*";
    public const string GlobalJsonFloor = "10.0.100";
    public const string GlobalJsonRollForward = "latestMinor";

    public static bool IsAcceptedVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Count(character => character == '.') != 2
            || !Version.TryParse(value, out var version))
            return false;
        return version.Major == 10 && version.Minor >= 0 && version.Build >= 0;
    }

    public static bool InventoryContainsAcceptedSdk(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .Any(IsAcceptedVersion);
    }
}
