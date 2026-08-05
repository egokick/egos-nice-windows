namespace Taildesk.Shared;

public static class AppPaths
{
    public static string AdminDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Taildesk", "Admin");

    public static string AdminConfigFile => Path.Combine(AdminDataDirectory, "admin.json");

    public static string AgentDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk", "Agent");

    public static string AgentConfigFile => Path.Combine(AgentDataDirectory, "agent.json");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk");
}
