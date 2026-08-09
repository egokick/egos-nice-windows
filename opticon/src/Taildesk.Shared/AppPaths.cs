namespace Taildesk.Shared;

public static class AppPaths
{
    public static string MachineDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Taildesk");

    public static string AdminDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Taildesk", "Admin");

    public static string AdminConfigFile => Path.Combine(AdminDataDirectory, "admin.json");

    public static string ScheduledTransfersFile => Path.Combine(AdminDataDirectory, "scheduled-transfers.json");

    public static string ScheduledTransfersLockFile => Path.Combine(AdminDataDirectory, "scheduled-transfers.lock");

    public static string AgentDataDirectory => Path.Combine(MachineDataDirectory, "Agent");

    public static string AgentConfigFile => Path.Combine(AgentDataDirectory, "agent.json");

    public static string BootstrapHandoffDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpticonBootstrap");

    public static string UpdateDataDirectory => Path.Combine(MachineDataDirectory, "Update");

    public static string UpdateJournalFile => Path.Combine(UpdateDataDirectory, "state.json");

    public static string UpdateHealthTokenSidecarFile =>
        Path.Combine(UpdateDataDirectory, "update-health-token.json");

    public static string UpdateCoordinationLockFile => Path.Combine(UpdateDataDirectory, "transaction.lock");

    public static string UpdateCommitRequestFile => Path.Combine(UpdateDataDirectory, "commit-request.json");

    public static string UpdateGuardianStartupFailureFile =>
        Path.Combine(UpdateDataDirectory, "guardian-startup-failure.json");

    public static string SshDataDirectory => Path.Combine(MachineDataDirectory, "Ssh");

    public static string SshAccessDataDirectory => Path.Combine(AgentDataDirectory, "SshAccess");

    public static string SetupStagingDirectory => Path.Combine(MachineDataDirectory, "SetupStaging");

    public static string AgentInstallTransactionFile =>
        Path.Combine(SetupStagingDirectory, "agent-install-transaction.json");

    public static string AgentInstallTransactionLockFile =>
        Path.Combine(SetupStagingDirectory, "agent-install-transaction.lock");

    public static string GuardianInstallTransactionFile =>
        Path.Combine(SetupStagingDirectory, "guardian-install-transaction.json");

    public static string ControllerHandoffDirectory => Path.Combine(MachineDataDirectory, "ControllerHandoff");

    public static string InstallReceiptFile => Path.Combine(MachineDataDirectory, "install-receipt.json");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk");

    public static string AgentInstallDirectory => Path.Combine(InstallDirectory, "Agent");

    public static string UpdateGuardianInstallDirectory => Path.Combine(InstallDirectory, "UpdateGuardian");

    public static string ControllerBootstrapFile => Path.Combine(ControllerHandoffDirectory, "bootstrap.json");
}
