namespace Taildesk.Shared;

public static class HeadscaleRoutes
{
    public static IReadOnlyList<string> ExitNode { get; } = Array.AsReadOnly(["0.0.0.0/0", "::/0"]);
}
