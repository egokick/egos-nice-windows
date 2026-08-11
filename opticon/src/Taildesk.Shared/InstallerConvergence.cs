namespace Taildesk.Shared;

/// <summary>
/// The explicit result of an idempotent installer operation.  A caller must
/// verify the operation's postcondition before returning Ready or Repaired.
/// Blocked means that continuing would require an external change, a user
/// decision, or weakening a security boundary.
/// </summary>
public enum InstallerEnsureOutcome
{
    Ready,
    Repaired,
    Blocked
}

public sealed record InstallerEnsureResult(
    string Operation,
    InstallerEnsureOutcome Outcome,
    string Postcondition,
    string? Detail = null)
{
    public static InstallerEnsureResult Ready(string operation, string postcondition) =>
        new(operation, InstallerEnsureOutcome.Ready, postcondition);

    public static InstallerEnsureResult Repaired(string operation, string postcondition, string? detail = null) =>
        new(operation, InstallerEnsureOutcome.Repaired, postcondition, detail);

    public static InstallerEnsureResult Blocked(string operation, string detail) =>
        new(operation, InstallerEnsureOutcome.Blocked, string.Empty, detail);
}

public enum InstallerPreflightScope
{
    Unelevated,
    Elevated
}

public enum InstallerPreflightSeverity
{
    Informational,
    Repair,
    Blocked
}

/// <summary>
/// A single independently-discovered preflight condition.  Keeping findings
/// structured lets Setup show all repairable issues before it mutates the
/// machine, rather than failing at the first missing component.
/// </summary>
public sealed record InstallerPreflightFinding(
    InstallerPreflightScope Scope,
    InstallerPreflightSeverity Severity,
    string Area,
    string Detail,
    string? SuggestedRepair = null);

public sealed class InstallerPreflightReport
{
    private readonly List<InstallerPreflightFinding> _findings = [];

    public IReadOnlyList<InstallerPreflightFinding> Findings => _findings;

    public bool IsBlocked => _findings.Any(finding =>
        finding.Severity == InstallerPreflightSeverity.Blocked);

    public void Add(InstallerPreflightFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrWhiteSpace(finding.Area) || string.IsNullOrWhiteSpace(finding.Detail))
            throw new ArgumentException("Preflight findings need an area and detail.", nameof(finding));
        _findings.Add(finding);
    }

    public IReadOnlyList<string> RepairPlan() => _findings
        .Where(finding => finding.Severity == InstallerPreflightSeverity.Repair)
        .Select(finding => finding.SuggestedRepair ?? finding.Area)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
