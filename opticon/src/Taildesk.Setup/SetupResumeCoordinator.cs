using System.Xml.Linq;
using Taildesk.Shared;

namespace Taildesk.Setup;

/// <summary>
/// Stores only the minimum information needed to resume a verified setup after
/// Windows completes a required reboot. The invitation fragment never appears
/// in a command line or scheduled-task XML; it is DPAPI-protected inside the
/// restricted SetupStaging state instead.
/// </summary>
internal sealed class SetupResumeState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid InviteId { get; set; }
    public string InvitePath { get; set; } = string.Empty;
    public string SourceAttestationPath { get; set; } = string.Empty;
    public string SetupExecutable { get; set; } = string.Empty;
    public string InviteKeyProtected { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed record SetupResumeContext(
    Guid InviteId,
    string InvitePath,
    string SourceAttestationPath,
    string SetupExecutable,
    string InviteKey);

internal static class SetupResumeCoordinator
{
    private const string ResumeTaskName = "Taildesk Setup Resume";
    private const string ResumeTaskDescription =
        "Resumes the protected Opticon installer after a required Windows reboot.";

    internal static async Task ScheduleAsync(
        SetupResumeContext context,
        CancellationToken cancellationToken)
    {
        ValidateContext(context);
        var store = new MachineJsonFileStore<SetupResumeState>(AppPaths.SetupResumeFile);
        await store.SaveAsync(new SetupResumeState
        {
            InviteId = context.InviteId,
            InvitePath = context.InvitePath,
            SourceAttestationPath = context.SourceAttestationPath,
            SetupExecutable = context.SetupExecutable,
            InviteKeyProtected = SecretProtector.Protect(context.InviteKey, SecretScope.LocalMachine),
            CreatedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

        var taskXml = BuildTaskXml(context.SetupExecutable);
        var taskFile = Path.Combine(AppPaths.SetupStagingDirectory, $"setup-resume-{Guid.NewGuid():N}.xml");
        try
        {
            await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                taskFile, System.Text.Encoding.UTF8.GetBytes(taskXml), cancellationToken);
            var result = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                ["/Create", "/TN", ResumeTaskName, "/XML", taskFile, "/F"],
                TimeSpan.FromSeconds(30), cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    "Windows could not schedule the protected Setup reboot continuation: " +
                    (result.StandardError + " " + result.StandardOutput).Trim());
        }
        finally
        {
            MachineStorageSecurity.DeleteRestrictedFileIfExists(taskFile);
        }
    }

    internal static async Task<SetupResumeContext?> LoadForCurrentProcessAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(AppPaths.SetupResumeFile)) return null;
        var state = await new MachineJsonFileStore<SetupResumeState>(AppPaths.SetupResumeFile)
            .LoadAsync(cancellationToken);
        if (state.SchemaVersion != 1 || state.InviteId == Guid.Empty
            || state.CreatedAtUtc < DateTimeOffset.UtcNow.AddDays(-7)
            || string.IsNullOrWhiteSpace(state.InvitePath)
            || string.IsNullOrWhiteSpace(state.SourceAttestationPath)
            || string.IsNullOrWhiteSpace(state.SetupExecutable)
            || string.IsNullOrWhiteSpace(state.InviteKeyProtected))
            throw new InvalidDataException("The protected Setup reboot continuation state is invalid.");

        var context = new SetupResumeContext(
            state.InviteId,
            Path.GetFullPath(state.InvitePath),
            Path.GetFullPath(state.SourceAttestationPath),
            Path.GetFullPath(state.SetupExecutable),
            SecretProtector.Unprotect(state.InviteKeyProtected, SecretScope.LocalMachine));
        ValidateContext(context);
        var running = Path.GetFullPath(Environment.ProcessPath
                                       ?? throw new InvalidOperationException("The resumed Setup path is unavailable."));
        if (!running.Equals(context.SetupExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reboot continuation did not start the recorded protected Setup executable.");
        return context;
    }

    internal static async Task ClearAsync(CancellationToken cancellationToken)
    {
        var task = await ProcessRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            ["/Delete", "/TN", ResumeTaskName, "/F"],
            TimeSpan.FromSeconds(30), cancellationToken);
        if (!task.Succeeded && task.ExitCode != 1)
            throw new InvalidOperationException(
                "Windows could not remove the protected Setup reboot continuation task: " +
                (task.StandardError + " " + task.StandardOutput).Trim());
        MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.SetupResumeFile);
    }

    private static void ValidateContext(SetupResumeContext context)
    {
        if (context.InviteId == Guid.Empty || string.IsNullOrWhiteSpace(context.InviteKey))
            throw new InvalidDataException("The Setup reboot continuation has no invitation identity.");
        if (!File.Exists(context.InvitePath) || !File.Exists(context.SourceAttestationPath)
            || !File.Exists(context.SetupExecutable))
            throw new FileNotFoundException("A protected Setup reboot continuation file is missing.");
        var releaseDirectory = Path.GetDirectoryName(context.SetupExecutable)
                               ?? throw new InvalidDataException("The Setup reboot continuation has no release directory.");
        HostedBootstrapper.RequireProtectedHandoff(context.InvitePath, releaseDirectory);
        HostedBootstrapper.RequireNoReparseTraversal(releaseDirectory, context.SourceAttestationPath);
    }

    private static string BuildTaskXml(string setupExecutable)
    {
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(task + "Task", new XAttribute("version", "1.4"),
                new XElement(task + "RegistrationInfo",
                    new XElement(task + "Description", ResumeTaskDescription)),
                new XElement(task + "Triggers",
                    new XElement(task + "BootTrigger",
                        new XElement(task + "Enabled", "true"),
                        new XElement(task + "Delay", "PT30S")),
                    // The boot trigger covers an unattended restart. The
                    // logon trigger retries if profile discovery correctly
                    // deferred while Windows had no interactive session yet.
                    new XElement(task + "LogonTrigger",
                        new XElement(task + "Enabled", "true"),
                        new XElement(task + "Delay", "PT15S"))),
                new XElement(task + "Principals",
                    new XElement(task + "Principal", new XAttribute("id", "Author"),
                        new XElement(task + "UserId", "S-1-5-18"),
                        new XElement(task + "LogonType", "ServiceAccount"),
                        new XElement(task + "RunLevel", "HighestAvailable"))),
                new XElement(task + "Settings",
                    new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(task + "DisallowStartIfOnBatteries", "false"),
                    new XElement(task + "StopIfGoingOnBatteries", "false"),
                    new XElement(task + "StartWhenAvailable", "true"),
                    new XElement(task + "Enabled", "true"),
                    new XElement(task + "ExecutionTimeLimit", "PT0S")),
                new XElement(task + "Actions", new XAttribute("Context", "Author"),
                    new XElement(task + "Exec",
                        new XElement(task + "Command", Path.GetFullPath(setupExecutable)),
                        new XElement(task + "Arguments", "--resume")))));
        return document.ToString(SaveOptions.DisableFormatting);
    }
}

internal sealed class SetupRebootRequiredException : InvalidOperationException
{
    public SetupRebootRequiredException(string message) : base(message) { }
}

internal sealed class OpenSshRebootRequiredException : InvalidOperationException
{
    public OpenSshRebootRequiredException(string message) : base(message) { }
}
