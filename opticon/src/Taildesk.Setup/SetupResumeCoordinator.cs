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
        try
        {
            var taskXml = BuildTaskXml(context.SetupExecutable);
            var taskFile = Path.Combine(
                AppPaths.SetupStagingDirectory, $"setup-resume-{Guid.NewGuid():N}.xml");
            try
            {
                await MachineStorageSecurity.WriteRestrictedFileAtomicAsync(
                    taskFile, System.Text.Encoding.UTF8.GetBytes(taskXml), cancellationToken);
                Exception? xmlRegistrationError = null;
                try
                {
                    var result = await RunTaskToolAsync(
                        ["/Create", "/TN", ResumeTaskName, "/XML", taskFile, "/F"],
                        cancellationToken);
                    EnsureTaskToolSuccess(
                        result, "Windows refused the protected Setup reboot-continuation XML");
                    await RequireResumeTaskAsync(context.SetupExecutable, cancellationToken);
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    xmlRegistrationError = exception;
                }

                try
                {
                    // Keep a non-XML compatibility path. It carries no secret:
                    // --resume loads the DPAPI-protected key from restricted state.
                    var command = $"\"{Path.GetFullPath(context.SetupExecutable)}\" --resume";
                    var fallback = await RunTaskToolAsync(
                        ["/Create", "/TN", ResumeTaskName, "/SC", "ONLOGON", "/DELAY", "0000:15",
                            "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", command, "/F"],
                        cancellationToken);
                    EnsureTaskToolSuccess(
                        fallback, "Windows could not create the protected Setup reboot continuation");
                    await ApplyFallbackSettingsAsync(cancellationToken);
                    await RequireResumeTaskAsync(context.SetupExecutable, cancellationToken);
                }
                catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "Windows rejected both protected Setup reboot-continuation registration methods.",
                        new AggregateException(xmlRegistrationError!, fallbackError));
                }
            }
            finally
            {
                MachineStorageSecurity.DeleteRestrictedFileIfExists(taskFile);
            }
        }
        catch (Exception registrationError)
        {
            try
            {
                await RemoveFailedRegistrationAsync();
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    "Setup could not register or completely remove its reboot continuation.",
                    new AggregateException(registrationError, cleanupError));
            }
            throw;
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

    private static Task<ProcessResult> RunTaskToolAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
        Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
        arguments,
        TimeSpan.FromSeconds(30),
        cancellationToken);

    private static void EnsureTaskToolSuccess(ProcessResult result, string message)
    {
        if (result.Succeeded) return;
        var detail = (result.StandardError + " " + result.StandardOutput).Trim();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}");
    }

    private static async Task ApplyFallbackSettingsAsync(
        CancellationToken cancellationToken)
    {
        const string script =
            "$ErrorActionPreference='Stop';" +
            "$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries " +
            "-DontStopIfGoingOnBatteries -StartWhenAvailable " +
            "-ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew;" +
            "Set-ScheduledTask -TaskPath '\\' -TaskName 'Taildesk Setup Resume' " +
            "-Settings $settings | Out-Null";
        var result = await ProcessRunner.RunAsync(
            Path.Combine(
                Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Restricted",
                "-Command", script],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        EnsureTaskToolSuccess(
            result, "Windows could not apply the Setup continuation reliability settings");
    }

    private static async Task RemoveFailedRegistrationAsync()
    {
        Exception? stateCleanupError = null;
        Exception? taskCleanupError = null;
        try
        {
            MachineStorageSecurity.DeleteRestrictedFileIfExists(AppPaths.SetupResumeFile);
        }
        catch (Exception exception)
        {
            stateCleanupError = exception;
        }

        try
        {
            var task = await RunTaskToolAsync(
                ["/Delete", "/TN", ResumeTaskName, "/F"], CancellationToken.None);
            if (!task.Succeeded && task.ExitCode != 1)
                throw new InvalidOperationException(
                    "Windows could not remove the incomplete Setup reboot continuation task: " +
                    (task.StandardError + " " + task.StandardOutput).Trim());
        }
        catch (Exception exception)
        {
            taskCleanupError = exception;
        }

        if (stateCleanupError is not null || taskCleanupError is not null)
            throw new AggregateException(
                "The failed Setup reboot continuation could not be completely removed.",
                new[] { stateCleanupError, taskCleanupError }.OfType<Exception>());
    }

    private static async Task RequireResumeTaskAsync(
        string setupExecutable,
        CancellationToken cancellationToken)
    {
        var query = await RunTaskToolAsync(
            ["/Query", "/TN", ResumeTaskName, "/XML"], cancellationToken);
        EnsureTaskToolSuccess(query, "Windows did not retain the Setup reboot continuation");
        var xml = query.StandardOutput.TrimStart('\uFEFF', '\r', '\n', ' ');
        if (xml.Length is <= 0 or > 256 * 1024)
            throw new InvalidDataException("The Setup reboot-continuation task XML has an invalid size.");
        using var reader = System.Xml.XmlReader.Create(
            new StringReader(xml),
            new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256 * 1024
            });
        var document = XDocument.Load(reader, LoadOptions.None);
        XNamespace task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root;
        var actions = root?.Element(task + "Actions")?.Elements().ToArray() ?? [];
        var principals = root?.Element(task + "Principals")?.Elements(task + "Principal").ToArray() ?? [];
        var triggers = root?.Element(task + "Triggers")?.Elements().ToArray() ?? [];
        var exec = actions.Length == 1 ? actions[0] : null;
        var principal = principals.Length == 1 ? principals[0] : null;
        var trigger = triggers.Length == 1 ? triggers[0] : null;
        var settings = root?.Element(task + "Settings");
        var logonType = principal?.Element(task + "LogonType")?.Value;
        if (root?.Name != task + "Task"
            || actions.Length != 1 || exec?.Name != task + "Exec"
            || root.Element(task + "Actions")?.Attribute("Context")?.Value != "Author"
            || principals.Length != 1
            || triggers.Length != 1 || trigger?.Name != task + "LogonTrigger"
            || trigger?.Element(task + "Enabled")?.Value != "true"
            || trigger?.Element(task + "Delay")?.Value != "PT15S"
            || !string.Equals(
                exec?.Element(task + "Command")?.Value,
                Path.GetFullPath(setupExecutable),
                StringComparison.OrdinalIgnoreCase)
            || exec?.Element(task + "Arguments")?.Value != "--resume"
            || principal?.Element(task + "UserId")?.Value != "S-1-5-18"
            || (!string.IsNullOrEmpty(logonType)
                && !logonType.Equals("ServiceAccount", StringComparison.Ordinal))
            || principal?.Element(task + "RunLevel")?.Value != "HighestAvailable"
            || settings?.Element(task + "MultipleInstancesPolicy")?.Value != "IgnoreNew"
            || settings.Element(task + "DisallowStartIfOnBatteries")?.Value != "false"
            || settings.Element(task + "StopIfGoingOnBatteries")?.Value != "false"
            || settings.Element(task + "StartWhenAvailable")?.Value != "true"
            || settings.Element(task + "AllowStartOnDemand")?.Value != "true"
            || settings.Element(task + "Enabled")?.Value != "true"
            || settings.Element(task + "RunOnlyIfNetworkAvailable")?.Value != "false"
            || settings.Element(task + "ExecutionTimeLimit")?.Value != "PT2H")
            throw new InvalidDataException(
                "The Setup reboot continuation does not match its exact SYSTEM task contract.");
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
                    new XElement(task + "LogonTrigger",
                        new XElement(task + "Enabled", "true"),
                        new XElement(task + "Delay", "PT15S"))),
                new XElement(task + "Principals",
                    new XElement(task + "Principal", new XAttribute("id", "Author"),
                        new XElement(task + "UserId", "S-1-5-18"),
                        // ServiceAccount is a Task Scheduler API enum value,
                        // not a legal task-XML LogonType value.  SYSTEM is a
                        // service account by virtue of its well-known SID;
                        // Windows' own exported SYSTEM tasks omit LogonType.
                        new XElement(task + "RunLevel", "HighestAvailable"))),
                new XElement(task + "Settings",
                    new XElement(task + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(task + "DisallowStartIfOnBatteries", "false"),
                    new XElement(task + "StopIfGoingOnBatteries", "false"),
                    new XElement(task + "StartWhenAvailable", "true"),
                    new XElement(task + "AllowStartOnDemand", "true"),
                    new XElement(task + "Enabled", "true"),
                    new XElement(task + "RunOnlyIfNetworkAvailable", "false"),
                    // Bound an abandoned session-0 continuation so a later
                    // attended logon can retry, while leaving ample time for
                    // slow Windows Installer and enrollment repair phases.
                    new XElement(task + "ExecutionTimeLimit", "PT2H")),
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
