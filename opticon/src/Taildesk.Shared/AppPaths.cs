namespace Taildesk.Shared;

public static class AppPaths
{
    public static string AdminDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Taildesk", "Admin");

    public static string AdminConfigFile => Path.Combine(AdminDataDirectory, "admin.json");

    public static string AgentDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk", "Agent");

    public static string AgentConfigFile => Path.Combine(AgentDataDirectory, "agent.json");

    public static string UpdateDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk", "Update");

    public static string UpdateJournalFile => Path.Combine(UpdateDataDirectory, "state.json");

    public static string UpdateHealthTokenSidecarFile =>
        Path.Combine(UpdateDataDirectory, "update-health-token.json");

    public static string UpdateCoordinationLockFile => Path.Combine(UpdateDataDirectory, "transaction.lock");

    public static string UpdateCommitRequestFile => Path.Combine(UpdateDataDirectory, "commit-request.json");

    public static string SshDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk", "Ssh");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk");

    public static string AgentInstallDirectory => Path.Combine(InstallDirectory, "Agent");

    public static string UpdateGuardianInstallDirectory => Path.Combine(InstallDirectory, "UpdateGuardian");
}
