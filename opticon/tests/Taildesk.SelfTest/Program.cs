using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Taildesk.Shared;

if (args.Length == 2 && args[0].Equals("--verify-authenticode", StringComparison.Ordinal))
{
    await ProductSigning.VerifyAuthenticodeAsync(args[1]);
    Console.WriteLine("PASS  pinned Authenticode signature and file digest");
    return;
}
if (args.Length != 0)
    throw new ArgumentException("Taildesk.SelfTest accepts only --verify-authenticode <path>.");

var tests = new (string Name, Action Body)[]
{
    ("tokens are random and hash comparisons work", TestTokens),
    ("invitations default to fourteen days with a bounded extension policy", TestInvitationPolicy),
    ("invite JSON round-trips without losing role", TestInviteRoundTrip),
    ("signed invitation container detects payload tampering", TestInviteContainer),
    ("hosted invitation encryption rejects wrong keys and tampering", TestHostedInvite),
    ("invitation storage rejects OneDrive", TestPrivateStorage),
    ("dependency downloads are version and hash pinned", TestDependencyPins),
    ("Tailscale enrollment resets stale settings before applying invitation policy", TestTailscaleEnrollmentArguments),
    ("process runner supports commands with inherited standard handles", TestProcessRunnerWithoutCapture),
    ("process runner applies its deadline to inherited output handles", TestProcessRunnerStreamDeadline),
    ("machine install crash recovery is exact-source bound and roll-forward only", TestMachineInstallCrashRecovery),
    ("installer convergence records verified repairs and plans every discovered repair", TestInstallerConvergenceContracts),
    ("RustDesk managed-host hardening is complete and idempotent", TestRustDeskHardening),
    ("RustDesk virtual-display privacy is opt-in", TestRustDeskVirtualDisplayDefault),
    ("RustDesk remote sessions pass the saved password to the native connection command", TestRustDeskRemoteSessionLaunch),
    ("RustDesk installer configures every Windows service profile before validation", TestRustDeskInstallerProfiles),
    ("controller registry contains no permanent credentials", TestControllerRegistryShape),
    ("remote administration contracts reject unpinned or unsafe updates", TestRemoteAdministrationProtocol),
    ("source-only updates pin one archive, seal local build output, and retain Guardian rollback", TestSourceUpdateRuntime),
    ("Setup accepts an exact trusted user profile root and an absent transaction journal", TestSetupPreflightContracts),
    ("source-built Agent and Guardian paths remain trusted through atomic promotion", TestSourceUpdateProvenanceMappings),
    ("remote update polling distinguishes Agent restarts from caller cancellation", TestRemoteUpdatePollingRecovery),
    ("legacy machine state cannot cross the protected update boundary unattended", TestLegacyMachineStateUpdateGate),
    ("the signed 1.1.41 bridge is the only legacy ACL and trust exception", TestLegacyMachineStateBridgeSafety),
    ("release distribution keeps signed bundles private and CloudFront-addressed", TestReleaseDistributionDesign),
    ("OpenSSH recovery is fixed-path, Windows-compatible, and independently supervised", TestOpenSshRecoveryDesign),
    ("runtime tailnet policy keeps administrative SSH hub-only", TestTailnetSshPolicy),
    ("update journal contracts round-trip through protected atomic persistence", TestUpdateJournalPersistence),
    ("uploads permit huge files but retain bounded resource controls", TestUploadPolicy),
    ("cancelled uploads retain an authenticated byte offset and resume", TestResumableUpload),
    ("scheduled transfers parse standard cron and calculate the next local occurrence", TestScheduledTransferCron),
    ("scheduled transfer file filters are bounded and predictable", TestScheduledTransferFilters),
    ("scheduled transfers expose UI history/retry and a complete CLI surface", TestScheduledTransferSurface),
    ("CLI invitation creation and cancellation share the UI lifecycle", TestInviteCliSurface),
    ("scheduled transfer retention and destructive operations fail closed", TestScheduledTransferSecurityPolicy),
    ("Admin media, local-file, and invitation trust boundaries are enforced", TestAdminTrustBoundarySource),
    ("path guard permits a child and blocks traversal", TestPathGuard),
    ("path guard limits SYSTEM file access to configured non-system roots", TestLocalVolumeRoots),
    ("Agent endpoint capabilities are case-insensitive and segment-exact", TestAgentEndpointPolicy),
    ("exit-node approval contains both internet default routes", TestExitNodeApprovalRoutes),
    ("private HTTP transport bypasses proxies and redirects", TestDirectHttpTransport),
    ("enrollment retries accept only the exact committed identity", TestEnrollmentReplayPolicy),
    ("credential rotation survives an ambiguous response", TestCredentialRotationState),
    ("failed durable collection mutations roll back in memory", TestDurableCollectionMutation),
    ("guarded path leases prevent component replacement", TestPathLease),
    ("WPF style templates match their control target types", TestWpfStyleTemplateTargets),
    ("WPF contrast audit covers every text surface and control state", TestWpfContrastContract),
    ("file browser offers direct paths and list or thumbnail views", TestFileBrowserContract),
    ("device rows expose a persisted rename action", TestDeviceRenameContract),
    ("device rows expose online duration and five-minute cached battery telemetry", TestDeviceTelemetryContract),
    ("DPAPI current-user and machine scopes round-trip", TestDpapi)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Environment.ExitCode = 1;
    Console.Error.WriteLine($"{failures.Count} Taildesk self-test(s) failed.");
}

static void TestTokens()
{
    var first = SecurityHelpers.CreateToken();
    var second = SecurityHelpers.CreateToken();
    Assert(first.Length >= 40, "token is too short");
    Assert(first != second, "two tokens unexpectedly matched");
    Assert(SecurityHelpers.FixedTimeEquals(SecurityHelpers.HashToken(first), SecurityHelpers.HashToken(first)), "equal hashes did not match");
    Assert(!SecurityHelpers.FixedTimeEquals(SecurityHelpers.HashToken(first), SecurityHelpers.HashToken(second)), "different hashes matched");
}

static void TestMachineInstallCrashRecovery()
{
    var binding = new SourceInstallationBinding(
        new string('a', 32),
        Guid.NewGuid(),
        new string('b', 64),
        new string('c', 64),
        new string('d', 64));
    var journal = MachineInstallTransactionPersistence.Create(binding);
    Assert(journal.Phase == MachineInstallTransactionPhase.Prepared,
        "a new machine-install journal did not start before machine mutation");
    Assert(!MachineInstallTransactionPersistence.RequiresNetworkRollForward(journal),
        "a prepared transaction incorrectly bypassed Tailscale reauthentication consent");

    foreach (var phase in Enum.GetValues<MachineInstallTransactionPhase>()
                 .OrderBy(value => (int)value)
                 .Skip(1))
    {
        MachineInstallTransactionPersistence.Advance(journal, phase);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(journal, JsonDefaults.Options);
        var recovered = JsonSerializer.Deserialize<MachineInstallTransactionJournal>(
                            serialized, JsonDefaults.Options)
                        ?? throw new InvalidOperationException("serialized machine-install recovery was empty");
        MachineInstallTransactionPersistence.RequireMatches(recovered, binding);
        Assert(recovered.Phase == phase, $"machine-install crash recovery lost phase {phase}");
        Assert(MachineInstallTransactionPersistence.RequiresNetworkRollForward(recovered)
               == (phase >= MachineInstallTransactionPhase.NetworkEnrollmentStarted),
            $"machine-install phase {phase} chose the wrong reauthentication policy");
        journal = recovered;
    }

    AssertThrows<InvalidDataException>(() =>
        MachineInstallTransactionPersistence.Advance(journal, MachineInstallTransactionPhase.Prepared));
    AssertThrows<InvalidDataException>(() =>
        MachineInstallTransactionPersistence.Advance(journal, (MachineInstallTransactionPhase)999));
    AssertThrows<InvalidDataException>(() =>
        MachineInstallTransactionPersistence.RequireMatches(
            journal, binding with { TransactionId = new string('e', 32) }));
    AssertThrows<InvalidDataException>(() =>
        MachineInstallTransactionPersistence.RequireMatches(
            journal, binding with { InviteId = Guid.NewGuid() }));
    AssertThrows<InvalidDataException>(() =>
        MachineInstallTransactionPersistence.RequireMatches(
            journal, binding with { SourceSha256 = new string('f', 64) }));
    AssertThrows<InvalidDataException>(() =>
        MachineInstallTransactionPersistence.RequireMatches(
            journal, binding with { SourceManifestSha256 = new string('0', 64) }));

    var installer = ReadSource("src", "Taildesk.Setup", "InstallerServices.cs");
    var provenance = ReadSource("src", "Taildesk.Shared", "SourceBuildProvenance.cs");
    var journalStart = installer.IndexOf(
        "await EnsureMachineInstallTransactionAsync(sourceBinding", StringComparison.Ordinal);
    var tailscaleMutation = installer.IndexOf(
        "var tailscaleResult = await EnsureTailscaleInstalledAsync", StringComparison.Ordinal);
    var enrollmentJournal = installer.IndexOf(
        "MachineInstallTransactionPhase.NetworkEnrollmentStarted", StringComparison.Ordinal);
    var enrollmentMutation = installer.IndexOf(
        "TailscaleCommandLine.BuildEnrollmentArguments", StringComparison.Ordinal);
    var remoteJournal = installer.IndexOf(
        "MachineInstallTransactionPhase.RemoteConfigurationStarted", StringComparison.Ordinal);
    var remoteMutation = installer.IndexOf(
        "await ConfigureRustDeskAsync(rustDesk", StringComparison.Ordinal);
    var receiptVerified = installer.IndexOf(
        "The protected enrollment success receipt did not verify after commit.", StringComparison.Ordinal);
    var journalCleared = installer.IndexOf(
        "await CompleteMachineInstallTransactionAsync(cancellationToken)", StringComparison.Ordinal);
    var provenanceCommitted = installer.IndexOf(
        "SourceBuildProvenance.CommitActiveInstallation()", StringComparison.Ordinal);
    Assert(journalStart >= 0 && tailscaleMutation > journalStart,
        "the protected machine-install journal is not durable before Tailscale installation");
    Assert(enrollmentJournal >= 0 && enrollmentMutation > enrollmentJournal,
        "Tailscale auth-key consumption is not preceded by a durable roll-forward phase");
    Assert(remoteJournal >= 0 && remoteMutation > remoteJournal,
        "RustDesk password/service mutation is not preceded by a durable recovery phase");
    Assert(receiptVerified >= 0 && journalCleared > receiptVerified
           && provenanceCommitted > journalCleared,
        "machine-install recovery is not retained through exact enrollment receipt validation");
    Assert(provenance.Contains("MachineInstallTransactionPersistence.Load() is not null", StringComparison.Ordinal)
           && provenance.Contains("pending source trust was preserved for roll-forward recovery", StringComparison.Ordinal),
        "source provenance can still be discarded while external machine effects require recovery");
}

static void TestInstallerConvergenceContracts()
{
    var ready = InstallerEnsureResult.Ready("EnsureGuardianAsync", "Guardian verified.");
    var repaired = InstallerEnsureResult.Repaired("EnsureAgentAsync", "Agent verified.", "Promoted generation.");
    var blocked = InstallerEnsureResult.Blocked("EnsureTailnetEnrollmentAsync", "Reauthentication required.");
    Assert(ready.Outcome == InstallerEnsureOutcome.Ready
           && repaired.Outcome == InstallerEnsureOutcome.Repaired
           && blocked.Outcome == InstallerEnsureOutcome.Blocked
           && string.IsNullOrEmpty(blocked.Postcondition),
        "installer ensure operations do not expose the Ready/Repaired/Blocked contract");

    var report = new InstallerPreflightReport();
    report.Add(new InstallerPreflightFinding(
        InstallerPreflightScope.Unelevated,
        InstallerPreflightSeverity.Repair,
        "SDK",
        "Pinned SDK is absent.",
        "Install pinned .NET SDK."));
    report.Add(new InstallerPreflightFinding(
        InstallerPreflightScope.Elevated,
        InstallerPreflightSeverity.Repair,
        "OpenSSH",
        "Capability is absent.",
        "Install OpenSSH capability."));
    report.Add(new InstallerPreflightFinding(
        InstallerPreflightScope.Elevated,
        InstallerPreflightSeverity.Blocked,
        "Firewall",
        "Policy disables Windows Firewall."));
    Assert(report.IsBlocked
           && report.RepairPlan().SequenceEqual(
               ["Install pinned .NET SDK.", "Install OpenSSH capability."], StringComparer.Ordinal),
        "preflight did not retain every discovered repair and blocked condition");

    var binding = new SourceInstallationBinding(
        new string('a', 32), Guid.NewGuid(), new string('b', 64),
        new string('c', 64), new string('d', 64));
    var journal = MachineInstallTransactionPersistence.Create(binding);
    MachineInstallTransactionPersistence.RecordVerifiedRepair(
        journal, "EnsureProtectedStorageAsync", repaired: true,
        "Protected storage is canonical.", "SetupStaging");
    MachineInstallTransactionPersistence.RecordPreviousComponentVersion(
        journal, "Tailscale", "1.82.5");
    MachineInstallTransactionPersistence.RecordTailscaleDecision(
        journal, reauthenticationApproved: true, nodeIdentity: "node-42");
    MachineInstallTransactionPersistence.RecordRebootState(
        journal, rebootPending: true, operation: "EnsureOpenSshAsync");
    var recovered = JsonSerializer.Deserialize<MachineInstallTransactionJournal>(
                        JsonSerializer.Serialize(journal, JsonDefaults.Options), JsonDefaults.Options)
                    ?? throw new InvalidOperationException("convergence journal did not deserialize");
    MachineInstallTransactionPersistence.RequireMatches(recovered, binding);
    Assert(recovered.SchemaVersion == 2
           && recovered.RepairsAttempted.Contains("EnsureProtectedStorageAsync", StringComparer.Ordinal)
           && recovered.ResourcesChangedByOpticon.Contains("SetupStaging", StringComparer.Ordinal)
           && recovered.PreviousComponentVersions["Tailscale"] == "1.82.5"
           && recovered.TailscaleReauthenticationApproved
           && recovered.TailscaleNodeIdentity == "node-42"
           && recovered.RebootPending
           && recovered.CurrentOperation == "EnsureOpenSshAsync",
        "the protected convergence journal lost repair, ownership, version, node, or reboot state");

    var installer = ReadSource("src", "Taildesk.Setup", "InstallerServices.cs");
    var bootstrap = ReadSource("src", "Taildesk.Setup", "SourceBootstrapInstaller.cs");
    var provenance = ReadSource("src", "Taildesk.Shared", "SourceBuildProvenance.cs");
    var resume = ReadSource("src", "Taildesk.Setup", "SetupResumeCoordinator.cs");
    var profile = ReadSource("src", "Taildesk.Setup", "InteractiveUserProfile.cs");
    var agent = ReadSource("src", "Taildesk.Agent", "Program.cs");
    foreach (var operation in new[]
             {
                 "EnsureBuildEnvironmentAsync", "EnsureProtectedStorageAsync",
                 "EnsureInteractiveUserProfileAsync", "EnsurePayloadVerifiedAsync",
                 "EnsureGuardianAsync", "EnsureOpenSshAsync",
                 "EnsureTailscaleInstalledAsync", "EnsureTailnetEnrollmentAsync",
                 "EnsureFirewallPolicyAsync", "EnsureRustDeskAsync",
                 "EnsureAgentAsync", "EnsureEnrollmentCommittedAsync"
             })
        Assert(installer.Contains(operation, StringComparison.Ordinal),
            $"convergent Setup phase is missing: {operation}");
    Assert(installer.Contains("MachineInstallTransactionPersistence.RecordTailscaleDecision", StringComparison.Ordinal)
           && installer.Contains("TryReadTailscaleStatusAsync", StringComparison.Ordinal)
           && installer.Contains("IsOpticonOwnedAgentTask", StringComparison.Ordinal)
           && installer.Contains("group=Opticon", StringComparison.Ordinal)
           && !installer.Contains("\"name=all\", $\"program={rustDesk}\"", StringComparison.Ordinal)
           && installer.Contains("IsExactFirewallConfigurationAsync", StringComparison.Ordinal),
        "installer does not retain the required Tailscale, task, or owned-firewall convergence behavior");
    Assert(bootstrap.Contains("DependencyArtifacts.DotNetSdk", StringComparison.Ordinal)
           && bootstrap.Contains("InstallPinnedSdkAsync", StringComparison.Ordinal)
           && bootstrap.Contains("CompatibleSdkIsReadyAsync", StringComparison.Ordinal),
        "missing .NET SDK repair is not pinned and automatically reverified");
    Assert(provenance.Contains("EnsureRecoverableStore", StringComparison.Ordinal)
           && provenance.Contains("StoreRecoveryOutcome.Normalized", StringComparison.Ordinal)
           && provenance.Contains(".untrusted-", StringComparison.Ordinal),
        "regenerable source provenance does not normalize safe ACL drift or quarantine tainted bytes");
    Assert(resume.Contains("MachineJsonFileStore<SetupResumeState>", StringComparison.Ordinal)
           && resume.Contains("SecretProtector.Protect", StringComparison.Ordinal)
           && resume.Contains("--resume", StringComparison.Ordinal)
           && resume.Contains("BootTrigger", StringComparison.Ordinal)
           && resume.Contains("LogonTrigger", StringComparison.Ordinal)
           && !resume.Contains("new XElement(task + \"Arguments\", context.InviteKey)", StringComparison.Ordinal),
        "reboot continuation does not keep invite secrets out of task arguments or retry after logon");
    Assert(profile.Contains("ResolveFinalDirectoryTarget", StringComparison.Ordinal)
           && profile.Contains("WTSGetActiveConsoleSessionId", StringComparison.Ordinal)
           && profile.Contains("return full;", StringComparison.Ordinal),
        "interactive profile resolution does not support missing folders, redirects, and reboot resume");
    Assert(!agent.Contains("config.SharedRoots.Count == 0", StringComparison.Ordinal),
        "an Agent with no optional shared folders still exits instead of providing remote recovery");
}

static void TestScheduledTransferCron()
{
    var utc = TimeZoneInfo.Utc;
    var start = new DateTimeOffset(2026, 8, 9, 14, 37, 42, TimeSpan.Zero);
    Assert(CronSchedule.Parse("* * * * *").GetNextOccurrence(start, utc)
           == new DateTimeOffset(2026, 8, 9, 14, 38, 0, TimeSpan.Zero), "every-minute cron skipped a minute");
    Assert(CronSchedule.Parse("0 * * * *").GetNextOccurrence(start, utc)
           == new DateTimeOffset(2026, 8, 9, 15, 0, 0, TimeSpan.Zero), "hourly cron chose the wrong hour");
    Assert(CronSchedule.Parse("30 9 * * MON").GetNextOccurrence(start, utc)
           == new DateTimeOffset(2026, 8, 10, 9, 30, 0, TimeSpan.Zero), "named weekly cron chose the wrong day");
    Assert(CronSchedule.Parse("*/15 8-10 * JAN,MAR 1-5").IsMatch(new DateTime(2026, 3, 2, 9, 45, 0)),
        "cron lists, steps, ranges, or names did not match");
    AssertThrows<InvalidDataException>(() => CronSchedule.Parse("0 12 * *"));
    AssertThrows<InvalidDataException>(() => CronSchedule.Parse("61 12 * * *"));
}

static void TestScheduledTransferFilters()
{
    var definition = new ScheduledTransferDefinition
    {
        Name = "Invoices", DeviceId = Guid.NewGuid(), LocalFolder = Path.GetTempPath(), RemoteRoot = "Documents",
        Filter = ScheduledTransferFilter.Extension, FilterPattern = "pdf", CronExpression = "0 9 * * *", TimeZoneId = TimeZoneInfo.Utc.Id
    };
    ScheduledTransferRules.Validate(definition);
    Assert(definition.FilterPattern == ".pdf", "extension filter was not normalized");
    Assert(ScheduledTransferRules.Matches(definition, "2026/invoice.PDF"), "extension filter should ignore case");
    Assert(!ScheduledTransferRules.Matches(definition, "2026/invoice.pdf.exe"), "extension filter matched a suffix instead of an extension");
    definition.Filter = ScheduledTransferFilter.Regex;
    definition.FilterPattern = @"^2026/invoice-[0-9]+\.csv$";
    ScheduledTransferRules.Validate(definition);
    Assert(ScheduledTransferRules.Matches(definition, "2026/invoice-42.csv"), "regex did not match the relative path");
    Assert(!ScheduledTransferRules.Matches(definition, "archive/invoice-42.csv"), "regex ignored its relative-path anchor");
    definition.FilterPattern = "[";
    AssertThrows<InvalidDataException>(() => ScheduledTransferRules.Validate(definition));
}

static void TestScheduledTransferSurface()
{
    var main = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    var editor = ReadSource("src", "Taildesk.Admin", "ScheduledTransferEditorWindow.xaml");
    var cli = ReadSource("src", "Taildesk.Cli", "ScheduledTransferCli.cs");
    var engine = ReadSource("src", "Taildesk.Admin", "ScheduledTransferEngine.cs");
    Assert(main.Contains("Scheduled transfers", StringComparison.Ordinal) && main.Contains("RUN HISTORY", StringComparison.Ordinal)
           && main.Contains("Retry selected failure", StringComparison.Ordinal), "scheduled transfer UI or retry history is missing");
    Assert(editor.Contains("Every minute", StringComparison.Ordinal) && editor.Contains("Every hour", StringComparison.Ordinal)
           && editor.Contains("Every day", StringComparison.Ordinal) && editor.Contains("Every week", StringComparison.Ordinal)
           && editor.Contains("Custom cron", StringComparison.Ordinal), "friendly cron choices are incomplete");
    foreach (var command in new[] { "list", "add", "edit", "run", "enable", "disable", "remove", "history", "retry" })
        Assert(cli.Contains($"\"{command}\"", StringComparison.Ordinal), $"schedule CLI is missing {command}");
    var proof = engine.IndexOf("RequireSameDigest(sourceBefore, remoteDestinationDigest", StringComparison.Ordinal);
    var confirmation = engine.IndexOf("result.TransferConfirmed = true", proof, StringComparison.Ordinal);
    var handleDelete = engine.IndexOf("source.Delete()", confirmation, StringComparison.Ordinal);
    Assert(proof >= 0 && confirmation > proof && handleDelete > confirmation
           && !engine.Contains("File.Delete(localSource)", StringComparison.Ordinal)
           && !engine.Contains("_agents.DeleteAsync", StringComparison.Ordinal),
        "Move must prove identical bytes and delete the open local source handle; pathname remote deletion must remain disabled");
}

static void TestInviteCliSurface()
{
    var cli = ReadSource("src", "Taildesk.Cli", "Program.cs");
    var service = ReadSource("src", "Taildesk.Admin", "InviteBundleService.cs");
    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    Assert(cli.Contains("\"invite\" or \"invites\"", StringComparison.Ordinal)
           && cli.Contains("\"create\" => await RunInviteCreateAsync", StringComparison.Ordinal)
           && cli.Contains("\"list\" => await RunInviteListAsync", StringComparison.Ordinal)
           && cli.Contains("\"cancel\" or \"revoke\"", StringComparison.Ordinal)
           && cli.Contains("new InviteBundleService(state, new HeadscaleApiClient(state))", StringComparison.Ordinal),
        "the CLI must expose create/list/cancel and reuse the production invitation service");
    Assert(cli.Contains("roots.AddRange([\"Desktop\", \"Documents\", \"Downloads\", \"Pictures\", \"Videos\"])", StringComparison.Ordinal)
           && cli.Contains("Invitation cancellation requires --yes", StringComparison.Ordinal)
           && cli.Contains("all five standard profile folders are shared", StringComparison.Ordinal)
           && cli.Contains("is a secret and is printed only by invite create", StringComparison.Ordinal),
        "the CLI invitation defaults, destructive confirmation, or secret-output contract regressed");

    var cancel = service.IndexOf("Task<InviteCancellationResult> CancelAsync", StringComparison.Ordinal);
    var revokeKey = service.IndexOf("_headscale.RevokeKeyAsync", cancel, StringComparison.Ordinal);
    var deleteHosted = service.IndexOf("HostedInviteClient(_state).DeleteAsync", revokeKey, StringComparison.Ordinal);
    var expire = service.IndexOf("record.ExpiresAt = DateTimeOffset.UtcNow", deleteHosted, StringComparison.Ordinal);
    Assert(cancel >= 0 && revokeKey > cancel && deleteHosted > revokeKey && expire > deleteHosted,
        "shared cancellation must revoke the network key, remove the hosted link, then durably expire the record");
    Assert(viewModel.Contains("InviteBundleService(_state, _headscale).CancelAsync", StringComparison.Ordinal),
        "the UI and CLI must share one invitation cancellation implementation");
}

static void TestScheduledTransferSecurityPolicy()
{
    var source = new ScheduledTransferRun
    {
        Id = Guid.NewGuid(),
        State = ScheduledTransferRunState.Failed,
        Files =
        [
            new ScheduledTransferFileResult
            {
                RelativePath = "report.bin", State = ScheduledTransferFileState.Failed,
                TransferConfirmed = true, SourceSha256 = "source-proof", DestinationSha256 = "destination-proof"
            }
        ]
    };
    var running = new ScheduledTransferRun
    {
        Id = Guid.NewGuid(), State = ScheduledTransferRunState.Running, RetryOfRunId = source.Id
    };
    var document = new ScheduledTransferDocument
    {
        Schedules = [new ScheduledTransferDefinition { ActiveRunId = running.Id }],
        History = Enumerable.Range(0, ScheduledTransferHistoryPolicy.MaximumRuns + 25)
            .Select(_ => new ScheduledTransferRun { Id = Guid.NewGuid(), State = ScheduledTransferRunState.Succeeded })
            .Concat([running, source])
            .ToList()
    };
    ScheduledTransferHistoryPolicy.Trim(document);
    Assert(document.History.Any(item => item.Id == running.Id), "active scheduled run was evicted by history trimming");
    Assert(document.History.Any(item => item.Id == source.Id), "an active retry's source record was evicted by history trimming");

    var copied = source.Files[0].Copy();
    source.Files[0].SourceSha256 = "changed";
    Assert(copied.SourceSha256 == "source-proof" && copied.DestinationSha256 == "destination-proof",
        "retry proof was not copied into an immutable candidate snapshot");

    var unsafeMove = new ScheduledTransferDefinition
    {
        Name = "Unsafe download Move", DeviceId = Guid.NewGuid(), Direction = ScheduledTransferDirection.Download,
        Mode = ScheduledTransferMode.Move, LocalFolder = Path.GetTempPath(), RemoteRoot = "Documents",
        CronExpression = "0 * * * *", TimeZoneId = TimeZoneInfo.Utc.Id
    };
    AssertThrows<InvalidDataException>(() => ScheduledTransferRules.Validate(unsafeMove));
}

static void TestAdminTrustBoundarySource()
{
    var agent = ReadSource("src", "Taildesk.Admin", "AgentClient.cs");
    var browser = ReadSource("src", "Taildesk.Admin", "FileManagerWindow.xaml.cs");
    var guarded = ReadSource("src", "Taildesk.Admin", "GuardedLocalTransferFile.cs");
    var engine = ReadSource("src", "Taildesk.Admin", "ScheduledTransferEngine.cs");
    var store = ReadSource("src", "Taildesk.Admin", "ScheduledTransferStore.cs");
    var invites = ReadSource("src", "Taildesk.Admin", "InviteBundleService.cs");

    Assert(agent.Contains("UriKind.Relative", StringComparison.Ordinal)
           && agent.Contains("resolved.Host.Equals(expected.Host", StringComparison.Ordinal)
           && agent.Contains("resolved.AbsolutePath.Equals(\"/api/v1/media\"", StringComparison.Ordinal),
        "Agent media links are not restricted to the authenticated Agent origin and endpoint");
    Assert(browser.Contains("GetMediaBytesAsync", StringComparison.Ordinal)
           && browser.Contains("thumbnail.StreamSource = stream", StringComparison.Ordinal)
           && browser.Contains("PreviewExtensions", StringComparison.Ordinal)
           && browser.Contains("DownloadToRootAsync", StringComparison.Ordinal)
           && !browser.Contains("Process.Start(new ProcessStartInfo(uri.AbsoluteUri)", StringComparison.Ordinal),
        "media still reaches WPF or the shell through an Agent-controlled network URI or handler");
    Assert(agent.Contains("GuardedLocalTransferTarget.Create", StringComparison.Ordinal)
           && agent.Contains("unexpected partial response to a full-file download", StringComparison.Ordinal)
           && !agent.Contains("taildesk-partial", StringComparison.Ordinal)
           && !agent.Contains("RangeHeaderValue(offset", StringComparison.Ordinal),
        "downloads still combine unverifiable retained bytes with a new remote response");
    Assert(guarded.Contains("PathGuard", StringComparison.Ordinal)
           && guarded.Contains("CreateFile(temporaryName)", StringComparison.Ordinal)
           && guarded.Contains("RenameTo(_directory", StringComparison.Ordinal)
           && guarded.Contains("SetFileInformationByHandle", StringComparison.Ordinal),
        "local transfers are not created, promoted, and deleted through guarded handles");
    Assert(engine.Contains("run.RetryCandidates", StringComparison.Ordinal)
           && engine.Contains("RequireRecordedProof", StringComparison.Ordinal)
           && store.Contains("ScheduledTransferHistoryPolicy.Trim", StringComparison.Ordinal),
        "scheduled retries do not carry durable candidates/proofs through bounded history");

    var pendingWrite = invites.IndexOf("record.PendingTailscaleKeyRevocations.Add(oldKeyId)", StringComparison.Ordinal);
    var durableSave = invites.IndexOf("await _state.SaveAsync(cancellationToken)", pendingWrite, StringComparison.Ordinal);
    var revoke = invites.IndexOf("return await RevokePendingKeysAsync(record", durableSave, StringComparison.Ordinal);
    Assert(pendingWrite >= 0 && durableSave > pendingWrite && revoke > durableSave,
        "the superseded invitation key ID is not persisted before revocation is attempted");

    var record = new InviteRecord { PendingTailscaleKeyRevocations = ["key-old"] };
    var roundTrip = JsonSerializer.Deserialize<InviteRecord>(
        JsonSerializer.Serialize(record, JsonDefaults.Options), JsonDefaults.Options);
    Assert(roundTrip?.PendingTailscaleKeyRevocations.SequenceEqual(["key-old"]) == true,
        "pending invitation-key revocations do not survive durable serialization");
}

static void TestDeviceTelemetryContract()
{
    var device = new DeviceRecord
    {
        State = DeviceConnectionState.Online,
        OnlineSince = DateTimeOffset.UtcNow.AddDays(-2).AddHours(-3),
        BatteryPercentage = 74
    };
    Assert(device.OnlineTime.StartsWith("2d 3h", StringComparison.Ordinal), "online duration is not formatted as days and hours");
    Assert(device.BatteryLife == "74%", "battery percentage is not formatted for the device grid");
    device.State = DeviceConnectionState.Offline;
    Assert(device.OnlineTime == "—" && device.BatteryLife == "—", "offline telemetry should not look current");
    device.State = DeviceConnectionState.Online;
    device.BatteryPercentage = null;
    Assert(device.BatteryLife == "—", "machines without batteries should show an em dash");

    var battery = ReadSource("src", "Taildesk.Agent", "BatteryStatusProvider.cs");
    var runtime = ReadSource("src", "Taildesk.Agent", "AgentRuntime.cs");
    var window = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    Assert(battery.Contains("TimeSpan.FromMinutes(5)", StringComparison.Ordinal)
           && battery.Contains("GetSystemPowerStatus", StringComparison.Ordinal)
           && battery.Contains("now < _nextPollAt", StringComparison.Ordinal),
        "battery telemetry is not cached behind a five-minute Windows probe");
    Assert(runtime.Contains("Environment.TickCount64", StringComparison.Ordinal)
           && runtime.Contains("BatteryPercentage = _battery.GetBatteryPercentage()", StringComparison.Ordinal),
        "Agent status does not expose OS uptime and battery telemetry");
    Assert(window.Contains("Header=\"ONLINE TIME\"", StringComparison.Ordinal)
           && window.Contains("Header=\"BATTERY LIFE\"", StringComparison.Ordinal),
        "the Devices grid is missing its online-time or battery-life column");
}

static void TestInvitationPolicy()
{
    var lifetime = InvitationPolicy.CreateDefaultExpiry() - DateTimeOffset.UtcNow;
    Assert(lifetime > TimeSpan.FromDays(13.99) && lifetime <= TimeSpan.FromDays(14), "default invitation lifetime is not fourteen days");
    Assert(InvitationPolicy.MaximumLifetimeDays == 365, "maximum invitation lifetime changed unexpectedly");
    Assert(InvitationPolicy.IsSupportedPayloadSchema(InvitationPolicy.LegacyBundleSchemaVersion), "legacy invitation schema must remain parseable for history");
    Assert(InvitationPolicy.IsSupportedPayloadSchema(InvitationPolicy.PreviousHostedLinkSchemaVersion), "previous hosted schema must remain parseable for history");
    Assert(InvitationPolicy.IsSupportedPayloadSchema(InvitationPolicy.PreviousSourceBuildSchemaVersion), "previous source schema must remain parseable for history");
    Assert(InvitationPolicy.IsSupportedPayloadSchema(InvitationPolicy.PreviousBootstrapPinnedSourceBuildSchemaVersion), "bootstrap-pinned source schema must remain parseable for history");
    Assert(InvitationPolicy.IsInstallablePayloadSchema(InvitationPolicy.HostedLinkSchemaVersion), "current schema must be installable");
    Assert(!InvitationPolicy.IsInstallablePayloadSchema(InvitationPolicy.PreviousSourceBuildSchemaVersion), "schema 4 must be historical-only after bootstrap pinning");
    Assert(!InvitationPolicy.IsInstallablePayloadSchema(InvitationPolicy.PreviousBootstrapPinnedSourceBuildSchemaVersion), "schema 5 must be historical-only after source-only migration");
    Assert(InvitationPolicy.SourceInstallProtocol == "source-v1", "the source-only invitation protocol changed unexpectedly");
    Assert(!InvitationPolicy.IsSupportedPayloadSchema(1) && !InvitationPolicy.IsSupportedPayloadSchema(7), "unknown invitation schemas must be rejected");
}
static void TestInviteRoundTrip()
{
    var invite = new InvitePayload
    {
        InviteId = Guid.NewGuid(),
        DeviceName = "Workshop PC",
        Role = DeviceRole.ControllerAndManaged,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        InviteSecret = SecurityHelpers.CreateToken(),
        TailscaleAuthKey = "tskey-auth-test",
        HeadscaleLoginUrl = "https://taildesk-control.example.test",
        AgentToken = SecurityHelpers.CreateToken(),
        RustDeskPassword = SecurityHelpers.CreateHumanPassword(),
        ControllerToken = SecurityHelpers.CreateToken(),
        CoordinatorUrl = "http://100.100.100.100:45830",
        ExpectedTailnet = "example.test",
        AllowedRoots = ["Documents", "Pictures"]
    };
    var json = JsonSerializer.Serialize(invite, JsonDefaults.Options);
    var copy = JsonSerializer.Deserialize<InvitePayload>(json, JsonDefaults.Options);
    Assert(copy?.InviteId == invite.InviteId, "invite id changed");
    Assert(copy?.Role == DeviceRole.ControllerAndManaged, "role changed");
    Assert(copy?.AgentToken == invite.AgentToken, "agent token changed");
    Assert(copy?.HeadscaleLoginUrl == invite.HeadscaleLoginUrl, "Headscale login URL changed");
    Assert(copy?.ExpectedTailnet == invite.ExpectedTailnet, "expected tailnet changed");
    Assert(copy?.AllowedRoots.SequenceEqual(invite.AllowedRoots) == true, "shared roots changed");
    Assert(copy?.InstallProtocol == InvitationPolicy.SourceInstallProtocol, "source-only install protocol changed");
}

static void TestInviteContainer()
{
    var temporary = Path.Combine(Path.GetTempPath(), "taildesk-invite-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var launcher = Path.Combine(temporary, "launcher.exe");
        var source = Path.Combine(temporary, "source");
        var archive = Path.Combine(temporary, "payload.zip");
        var invitation = Path.Combine(temporary, "invite.exe");
        var extracted = Path.Combine(temporary, "extracted");
        File.WriteAllBytes(launcher, [0x4d, 0x5a, 0x01, 0x02]);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "proof.txt"), "taildesk-one-click");
        ZipFile.CreateFromDirectory(source, archive);
        using var rsa = RSA.Create(3072);
        InviteContainer.CreateAsync(launcher, archive, invitation, signer: data =>
            rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)).GetAwaiter().GetResult();
        using (var stream = new FileStream(invitation, FileMode.Append, FileAccess.Write)) stream.Write(new byte[512]);
        InviteContainer.ExtractAsync(invitation, extracted, verifier: rsa).GetAwaiter().GetResult();
        Assert(File.ReadAllText(Path.Combine(extracted, "proof.txt")) == "taildesk-one-click", "one-file invite payload changed");

        var tampered = Path.Combine(temporary, "tampered.exe");
        File.Copy(invitation, tampered);
        using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = new FileInfo(launcher).Length + 4;
            var original = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(original ^ 0xff));
        }
        AssertThrows<InvalidDataException>(() => InviteContainer.ExtractAsync(
            tampered, Path.Combine(temporary, "tampered-output"), verifier: rsa).GetAwaiter().GetResult());
    }
    finally
    {
        Directory.Delete(temporary, true);
    }
}

static void TestHostedInvite()
{
    var invite = new InvitePayload { InviteId = Guid.NewGuid(), DeviceName = "Hosted PC", Role = DeviceRole.ManagedOnly,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), InviteSecret = SecurityHelpers.CreateToken() };
    using var rsa = RSA.Create(3072);
    var envelope = HostedInviteFile.CreateSigned(invite, data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    var key = SecurityHelpers.CreateToken(32);
    var encrypted = HostedInviteFile.Encrypt(key, envelope);
    var decrypted = HostedInviteFile.Decrypt(key, encrypted);
    var copy = HostedInviteFile.ReadSigned(decrypted, (data, signature) => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    Assert(copy.InviteId == invite.InviteId, "hosted invitation changed during encryption");
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(invite, JsonDefaults.Options);
    AssertThrows<InvalidDataException>(() => HostedInviteFile.ReadSigned(
        plaintext, (data, signature) => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    Assert(string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(AppPaths.BootstrapHandoffDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))),
            StringComparison.OrdinalIgnoreCase),
        "the elevated invitation handoff root must be a direct child of ProgramData");
    AssertThrows<InvalidDataException>(() => HostedInviteFile.Decrypt(SecurityHelpers.CreateToken(32), encrypted));
    encrypted[^1] ^= 0xff;
    AssertThrows<InvalidDataException>(() => HostedInviteFile.Decrypt(key, encrypted));
}
static void TestPrivateStorage()
{
    AssertThrows<InvalidOperationException>(() => PrivateStorage.ValidateInviteDirectory(
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users", "Someone", "OneDrive", "Opticon")));
    var local = PrivateStorage.ValidateInviteDirectory(PrivateStorage.InviteDirectory);
    Assert(local.Contains(Path.Combine("Opticon", "Invitations"), StringComparison.OrdinalIgnoreCase), "default invitation directory is not local Opticon storage");
}

static void TestDependencyPins()
{
    foreach (var artifact in DependencyArtifacts.All)
    {
        Assert(artifact.Version.Length > 0, $"{artifact.Product} has no pinned version");
        Assert(artifact.Sha256.Length == 64 && artifact.Sha256.All(Uri.IsHexDigit), $"{artifact.Product} has no SHA-256 pin");
        Assert(artifact.Size > 0, $"{artifact.Product} has no size pin");
        Assert(artifact.PrimaryUrl.StartsWith(DependencyArtifacts.FlyArtifactBase, StringComparison.Ordinal), "Fly is not the primary artifact source");
        Assert(!artifact.PrimaryUrl.Contains("latest", StringComparison.OrdinalIgnoreCase), "primary URL uses latest");
        Assert(!artifact.FallbackUrl.Contains("latest", StringComparison.OrdinalIgnoreCase), "fallback URL uses latest");
        Assert(artifact.PrimaryUrl.EndsWith(artifact.FileName, StringComparison.Ordinal), "primary filename changed");
        Assert(artifact.FallbackUrl.EndsWith(artifact.FileName, StringComparison.Ordinal), "fallback filename changed");
    }
}
static void TestTailscaleEnrollmentArguments()
{
    var arguments = TailscaleCommandLine.BuildEnrollmentArguments(
        "https://headscale.example.test", "tskey-auth-test", "managed-pc");
    Assert(arguments[0] == "up", "Tailscale enrollment must use the up command");
    Assert(arguments.Contains("--reset", StringComparer.Ordinal),
        "Tailscale enrollment must reset stale non-default settings from a partial installation");
    Assert(arguments.Contains("--force-reauth", StringComparer.Ordinal),
        "Tailscale enrollment must replace an expired partial-installation session without calling logout");
    Assert(arguments.Contains("--accept-dns=false", StringComparer.Ordinal) && arguments.Contains("--accept-routes=false", StringComparer.Ordinal),
        "Tailscale enrollment must reapply Opticon route and DNS policy after reset");
}
static void TestProcessRunnerWithoutCapture()
{
    if (!OperatingSystem.IsWindows()) return;
    var result = ProcessRunner.RunAsync("cmd.exe", ["/d", "/c", "echo ignored"],
        TimeSpan.FromSeconds(5), captureOutput: false).GetAwaiter().GetResult();
    Assert(result.Succeeded, "uncaptured command failed");
    Assert(result.StandardOutput.Length == 0 && result.StandardError.Length == 0,
        "uncaptured command unexpectedly retained redirected output");
}

static void TestProcessRunnerStreamDeadline()
{
    if (!OperatingSystem.IsWindows()) return;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    AssertThrows<TimeoutException>(() => ProcessRunner.RunAsync(
        "cmd.exe",
        ["/d", "/c", "start \"\" /b cmd.exe /d /c \"ping 127.0.0.1 -n 6 > nul\""],
        TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult());
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
        "the inherited output handle was allowed to outlive the process deadline");
}
static void TestRustDeskHardening()
{
    const string original = "rendezvous_server = 'public.example'\r\n[options]\r\ndirect-server = 'N'\r\nunknown = 'preserved'\r\n";
    var hardened = RustDeskConfiguration.HardenManagedHost(original);
    Assert(RustDeskConfiguration.IsManagedHostHardened(hardened), "hardened configuration should verify");
    Assert(hardened == RustDeskConfiguration.HardenManagedHost(hardened), "hardening should be idempotent");
    Assert(hardened.Contains("direct-server = 'Y'", StringComparison.Ordinal), "direct server must be enabled");
    Assert(hardened.Contains("enable-privacy-mode = 'Y'", StringComparison.Ordinal), "managed targets must permit privacy mode");
    Assert(hardened.Contains("whitelist = ','", StringComparison.Ordinal), "RustDesk must not receive an unsupported CIDR whitelist; Windows Firewall enforces the tailnet range");
    Assert(hardened.Contains("unknown = 'preserved'", StringComparison.Ordinal), "unmanaged options must be preserved");

    const string peer = "privacy_mode = false\r\n[options]\r\nunknown = 'preserved'\r\n";
    var privacyEnabled = RustDeskConfiguration.ConfigurePeerPrivacyMode2(peer, true);
    Assert(privacyEnabled.Contains("privacy_mode = true", StringComparison.Ordinal), "Mode 2 must enable privacy for the selected peer");
    Assert(privacyEnabled.Contains("privacy-mode-impl-key = 'privacy_mode_impl_virtual_display'", StringComparison.Ordinal), "Mode 2 must select RustDesk's virtual display implementation");
    Assert(privacyEnabled.Contains("unknown = 'preserved'", StringComparison.Ordinal), "peer options must be preserved");
    Assert(privacyEnabled == RustDeskConfiguration.ConfigurePeerPrivacyMode2(privacyEnabled, true), "peer privacy configuration should be idempotent");
    var privacyDisabled = RustDeskConfiguration.ConfigurePeerPrivacyMode2(privacyEnabled, false);
    Assert(privacyDisabled.Contains("privacy_mode = false", StringComparison.Ordinal), "the per-device toggle must disable privacy for the selected peer");
}

static string ReadSource(params string[] parts)
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
    }

    throw new InvalidOperationException($"Source file was not found: {Path.Combine(parts)}");
}

static void TestRustDeskRemoteSessionLaunch()
{
    var launcher = ReadSource("src", "Taildesk.Admin", "RustDeskSessionLauncher.cs");
    var connectArgument = launcher.IndexOf("start.ArgumentList.Add(\"--connect\")", StringComparison.Ordinal);
    var passwordArgument = launcher.IndexOf("start.ArgumentList.Add(\"--password\")", StringComparison.Ordinal);
    var passwordValue = launcher.IndexOf("start.ArgumentList.Add(password)", StringComparison.Ordinal);
    Assert(connectArgument >= 0 && passwordArgument > connectArgument && passwordValue > passwordArgument,
        "RustDesk remote launch must provide the saved password through its native connection command");
    Assert(launcher.Contains("WorkingDirectory = executableDirectory", StringComparison.Ordinal),
        "RustDesk must not inherit and lock Opticon's installed command-center directory");
}

static void TestRustDeskVirtualDisplayDefault()
{
    Assert(!new DeviceRecord().PrivacyMode2Enabled,
        "new devices must not require a RustDesk virtual display for ordinary remote sessions");

    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    Assert(viewModel.Contains("Config.PrivacyMode2ByDevice.TryGetValue(device.Id, out var enabled) && enabled", StringComparison.Ordinal),
        "a device must enable virtual-display privacy explicitly");
}

static void TestRustDeskInstallerProfiles()
{
    string? sourcePath = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Taildesk.Setup", "InstallerServices.cs");
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        if (sourcePath is not null) break;
    }
    if (sourcePath is null) throw new InvalidOperationException("Taildesk.Setup InstallerServices.cs was not found.");
    var source = File.ReadAllText(sourcePath);
    var profileStore = ReadSource("src", "Taildesk.Shared", "RustDeskServiceProfileStore.cs");
    Assert(profileStore.Contains("ServiceProfiles\", \"LocalService", StringComparison.Ordinal), "LocalService RustDesk profile is not hardened");
    Assert(profileStore.Contains("ServiceProfiles\", \"NetworkService", StringComparison.Ordinal), "NetworkService RustDesk profile is not hardened");
    Assert(profileStore.Contains("System32\", \"config\", \"systemprofile", StringComparison.Ordinal), "SYSTEM RustDesk profile is not hardened");
    Assert(profileStore.Contains("NativePath.OpenRelative", StringComparison.Ordinal)
           && profileStore.Contains("exclusive: true", StringComparison.Ordinal)
           && source.Contains("RustDeskServiceProfileStore.HardenAll()", StringComparison.Ordinal)
           && !source.Contains("taskkill.exe", StringComparison.Ordinal),
        "RustDesk profiles are not hardened through held no-follow handles or still use a name-wide process kill");
    var configureIndex = source.IndexOf("await ConfigureRustDeskAsync(rustDesk", StringComparison.Ordinal);
    var firewallIndex = source.IndexOf("await ConfigureFirewallAsync(snapshot.Ip, expectedRustDesk", StringComparison.Ordinal);
    var listenerIndex = source.IndexOf("WaitForListeningExecutableAsync(", configureIndex, StringComparison.Ordinal);
    Assert(firewallIndex >= 0 && configureIndex > firewallIndex && listenerIndex > configureIndex
           && source.Contains("RequireFirewallProfilesSecureAsync", StringComparison.Ordinal)
           && source.Contains("DefaultInboundAction.ToString() -ne 'Block'", StringComparison.Ordinal)
           && source.Contains("AssertExactFirewallConfigurationAsync", StringComparison.Ordinal)
           && source.Contains("group=Opticon", StringComparison.Ordinal)
           && source.Contains("IsExactFirewallConfigurationAsync", StringComparison.Ordinal),
        "RustDesk can start before exact firewall isolation and enabled default-block profiles are verified");
    Assert(source.Contains("InstalledDependencyMatchesAsync", StringComparison.Ordinal)
           && source.Contains("RequireFixedProgramFilesExecutable", StringComparison.Ordinal)
           && source.Contains("RequireInstallerSignatureAsync(full", StringComparison.Ordinal)
           && source.Contains("CryptographicOperations.FixedTimeEquals", StringComparison.Ordinal)
           && !source.Contains("StartsWith(artifact.Version", StringComparison.Ordinal)
           && source.Contains("GetExtendedTcpTable", StringComparison.Ordinal)
           && source.Contains("process.MainModule?.FileName", StringComparison.Ordinal),
        "existing vendor executables must pass exact fixed-path, version, publisher, timestamp, and listener-owner checks before privileged reuse");
}
static void TestControllerRegistryShape()
{
    var json = JsonSerializer.Serialize(new ControllerDeviceDto(), JsonDefaults.Options);
    Assert(!json.Contains("agentToken", StringComparison.OrdinalIgnoreCase), "controller registry exposes the agent token field");
    Assert(!json.Contains("rustDeskPassword", StringComparison.OrdinalIgnoreCase), "controller registry exposes the RustDesk password field");
}
static void TestRemoteAdministrationProtocol()
{
    Assert(RemoteAdministrationProtocol.SshPort == 45832, "the isolated SSH port changed unexpectedly");
    Assert(RemoteAdministrationProtocol.UpdateVersion == 1, "the guarded update protocol changed without a migration");
    Assert(RemoteAdministrationProtocol.MaximumSshSession == TimeSpan.FromHours(8), "SSH maximum lease is not bounded to eight hours");
    Assert(RemoteAdministrationProtocol.UpdateCommitWindow <= TimeSpan.FromMinutes(5), "update commit window is too long");
    Assert(UpdatePackageVerifier.NormalizeVersion("1.2.3.0") == "1.2.3", "four-part Windows file version was not canonicalized");
    Assert(RemoteAdministrationProtocol.IsTailscaleIpv4("100.64.0.1"), "canonical Tailscale IPv4 was rejected");
    Assert(RemoteAdministrationProtocol.IsTailscaleIpv4("100.127.255.254"), "upper Tailscale IPv4 was rejected");
    Assert(!RemoteAdministrationProtocol.IsTailscaleIpv4("100.128.0.1"), "address beyond Tailscale CGNAT range was accepted");
    Assert(!RemoteAdministrationProtocol.IsTailscaleIpv4("::ffff:100.64.0.1"), "IPv4-mapped IPv6 bypassed strict Tailscale validation");
    Assert(!RemoteAdministrationProtocol.IsTailscaleIpv4("::6464:1"), "native IPv6 bypassed strict Tailscale validation");
    Assert(new OpticonReleaseManifest().MinimumGuardianVersion == RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion,
        "the release Guardian floor must match the watchdog compatibility contract");
    Assert(!RemoteAdministrationProtocol.SupportsGuardianWatchdog(new Version(1, 1, 1))
           && RemoteAdministrationProtocol.SupportsGuardianWatchdog(new Version(1, 1, 2))
           && RemoteAdministrationProtocol.SupportsGuardianWatchdog(new Version(1, 1, 20)),
        "Guardian watchdog compatibility must use the contract floor rather than the current Setup version");

    var futureConfig = JsonSerializer.Deserialize<AgentConfig>(
        "{\"schemaVersion\":2,\"futureEnrollmentField\":{\"value\":7}}", JsonDefaults.Options)
        ?? throw new InvalidDataException("extended Agent config did not deserialize");
    var futureConfigJson = JsonSerializer.Serialize(futureConfig, JsonDefaults.Options);
    Assert(futureConfigJson.Contains("\"futureEnrollmentField\"", StringComparison.Ordinal),
        "an atomic maintenance config save would discard unknown enrolled fields");

    var request = new OpticonUpdateRequest
    {
        OperationId = Guid.NewGuid(),
        TargetVersion = "1.2.3",
        Role = DeviceRole.ManagedOnly,
        Architecture = "x64",
        DownloadUrl = "https://opticon.example.test/opticon-bundle-1.2.3-managed-win-x64.zip",
        PackageSize = 4096,
        PackageSha256 = new string('a', 64)
    };
    UpdatePackageVerifier.ValidateRequest(request);
    var requestJson = JsonSerializer.Serialize(request, JsonDefaults.Options);
    Assert(!requestJson.Contains("maintenanceBootstrap", StringComparison.OrdinalIgnoreCase),
        "an Agent API update request can opt into the privileged legacy maintenance bypass");

    request.DownloadUrl = "http://opticon.example.test/release.zip";
    AssertThrows<InvalidDataException>(() => UpdatePackageVerifier.ValidateRequest(request));
    request.DownloadUrl = "https://user:secret@opticon.example.test/release.zip";
    AssertThrows<InvalidDataException>(() => UpdatePackageVerifier.ValidateRequest(request));
    request.DownloadUrl = "https://opticon.example.test/release.zip";
    request.Architecture = "x86";
    AssertThrows<InvalidDataException>(() => UpdatePackageVerifier.ValidateRequest(request));

    var sshRequestJson = JsonSerializer.Serialize(new SshAccessRequest
    {
        PublicKey = "ssh-ed25519 AAAA",
        RequestedLifetimeSeconds = 3600,
        ExpiresAt = DateTimeOffset.Parse("2030-01-01T04:00:00Z")
    }, JsonDefaults.Options);
    using var sshRequestDocument = JsonDocument.Parse(sshRequestJson);
    Assert(sshRequestDocument.RootElement.GetProperty("requestedLifetimeSeconds").GetInt32() == 3600,
        "the target-relative SSH lease duration did not serialize");

    var sshJson = JsonSerializer.Serialize(new SshAccessResponse
    {
        SessionId = "lease_123",
        Host = "100.64.0.25",
        CreatedAt = DateTimeOffset.Parse("2030-01-01T12:00:00+09:00"),
        ExpiresAt = DateTimeOffset.Parse("2030-01-01T12:30:00+09:00"),
        HostPublicKey = "ssh-ed25519 AAAA"
    }, JsonDefaults.Options);
    var ssh = JsonSerializer.Deserialize<SshAccessResponse>(sshJson, JsonDefaults.Options);
    Assert(ssh?.SessionId == "lease_123" && ssh.Host == "100.64.0.25" && ssh.CreatedAt is not null,
        "SSH lease identity, host, and target-relative timing did not round-trip");

    var targetCreatedAt = DateTimeOffset.Parse("2030-01-01T12:00:00+09:00");
    Assert(RemoteAdministrationProtocol.IsSshLeaseWithinRequestedLifetime(
            targetCreatedAt, targetCreatedAt.AddHours(1), TimeSpan.FromHours(1)),
        "a target-relative SSH lease equal to the requested duration was rejected");
    Assert(!RemoteAdministrationProtocol.IsSshLeaseWithinRequestedLifetime(
            targetCreatedAt, targetCreatedAt.AddHours(1).AddSeconds(1), TimeSpan.FromHours(1)),
        "an SSH lease longer than requested was accepted");
    Assert(!RemoteAdministrationProtocol.IsSshLeaseWithinRequestedLifetime(
            targetCreatedAt, targetCreatedAt, TimeSpan.FromHours(1)),
        "an already-expired SSH lease was accepted");
}

static void TestRemoteUpdatePollingRecovery()
{
    var coordinator = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Admin", "RemoteDeviceUpdateCoordinator.cs"));

    Assert(coordinator.Contains("IsRecoverableRemoteFailure", StringComparison.Ordinal)
           && coordinator.Contains("!cancellationToken.IsCancellationRequested", StringComparison.Ordinal)
           && coordinator.Contains("exception is not InvalidDataException", StringComparison.Ordinal),
        "remote update polling must preserve an explicit caller cancellation while safely retrying transient Agent failures");
    Assert(coordinator.Contains("Activation delivery is indeterminate; polling the Guardian's durable terminal state.", StringComparison.Ordinal)
           && coordinator.Contains("Commit delivery is indeterminate; polling the Guardian's durable terminal state.", StringComparison.Ordinal)
           && coordinator.Contains("Maintenance commit delivery is indeterminate; polling the exact durable terminal state.", StringComparison.Ordinal)
           && coordinator.Contains("rollbackTerminal.Phase is UpdatePhase.RolledBack or UpdatePhase.Failed", StringComparison.Ordinal)
           && coordinator.Contains("The guardian restored the previous Agent; Tailscale and remote control were left untouched.", StringComparison.Ordinal),
        "ambiguous activation, commit, and maintenance responses must recover the exact durable terminal update result");
}

static void TestSourceUpdateRuntime()
{
    Assert(SourceUpdateProtocol.Version == 1
           && SourceUpdateProtocol.RequiredSdkVersion == DotNetSdkPolicy.SignedPolicy
           && DotNetSdkPolicy.SignedPolicy == "10.*.*"
           && SourceUpdateProtocol.RequiredRuntimeVersion == "10.0.10"
           && SourceUpdateProtocol.MinimumGuardianVersion == "1.2.0",
        "the source update protocol no longer pins its SDK family, output runtime, and Guardian floor");
    Assert(DotNetSdkPolicy.IsAcceptedVersion("10.0.100")
           && DotNetSdkPolicy.IsAcceptedVersion("10.0.302")
           && DotNetSdkPolicy.IsAcceptedVersion("10.7.900")
           && !DotNetSdkPolicy.IsAcceptedVersion("9.0.999")
           && !DotNetSdkPolicy.IsAcceptedVersion("11.0.100")
           && !DotNetSdkPolicy.IsAcceptedVersion("10.0.100-preview.1")
           && DotNetSdkPolicy.InventoryContainsAcceptedSdk("9.0.999 [x]\n10.0.302 [y]")
           && !DotNetSdkPolicy.InventoryContainsAcceptedSdk("9.0.999 [x]\n11.0.100 [y]"),
        "the stable .NET 10 SDK wildcard policy accepted an invalid major or rejected a valid 10.x SDK");

    var createDirectorySecurity = typeof(SourceBuildProvenance).GetMethod(
        "CreateRestrictedDirectorySecurity",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The provenance ACL factory is missing.");
    var requireDirectorySecurity = typeof(SourceBuildProvenance).GetMethod(
        "RequireRestrictedSecurity",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The provenance ACL validator is missing.");
    var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
    const InheritanceFlags directoryInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    var safeReadOnlyAcl = (DirectorySecurity)createDirectorySecurity.Invoke(null, null)!;
    safeReadOnlyAcl.AddAccessRule(new FileSystemAccessRule(
        worldSid,
        FileSystemRights.Read,
        directoryInheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    safeReadOnlyAcl.AddAccessRule(new FileSystemAccessRule(
        worldSid,
        FileSystemRights.Write,
        directoryInheritance,
        PropagationFlags.None,
        AccessControlType.Deny));
    requireDirectorySecurity.Invoke(null, new object[] { safeReadOnlyAcl, true });

    var unsafeWritableAcl = (DirectorySecurity)createDirectorySecurity.Invoke(null, null)!;
    unsafeWritableAcl.AddAccessRule(new FileSystemAccessRule(
        worldSid,
        FileSystemRights.Write,
        directoryInheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    var rejectedUntrustedWriter = false;
    try
    {
        requireDirectorySecurity.Invoke(null, new object[] { unsafeWritableAcl, true });
    }
    catch (TargetInvocationException exception)
        when (exception.InnerException is UnauthorizedAccessException)
    {
        rejectedUntrustedWriter = true;
    }
    Assert(rejectedUntrustedWriter,
        "the provenance ACL validator accepted mutation rights outside SYSTEM and Administrators");

    var journal = new UpdateJournal
    {
        SchemaVersion = 2,
        DeliveryMode = UpdateDeliveryMode.SourceArchive,
        OperationId = Guid.NewGuid(),
        SourceFile = "opticon-source-1.2.0.zip",
        SourceBuildOutputDirectory = "C:\\ProgramData\\Opticon\\updates\\source-build",
        SourceBuildAttestationPath = "C:\\ProgramData\\Opticon\\updates\\source-build-attestation.json"
    };
    var journalJson = JsonSerializer.Serialize(journal, JsonDefaults.Options);
    Assert(journalJson.Contains("\"deliveryMode\": \"sourceArchive\"", StringComparison.Ordinal)
           && journalJson.Contains("sourceBuildAttestationPath", StringComparison.Ordinal),
        "the protected source update journal does not persist its delivery mode and build attestation path");

    var verifier = ReadSource("src", "Taildesk.Shared", "SourceUpdatePackageVerifier.cs");
    var runner = ReadSource("src", "Taildesk.Agent", "SourceUpdateBuildRunner.cs");
    var manager = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    var guardian = ReadSource("src", "Taildesk.UpdateGuardian", "GuardianRunner.cs");
    var provenance = ReadSource("src", "Taildesk.Shared", "SourceBuildProvenance.cs");
    var buildScript = ReadSource("source-package", "Build-OpticonUpdateFromSource.ps1");
    Assert(verifier.Contains("RequireImmutableCloudFrontUrl", StringComparison.Ordinal)
           && verifier.Contains("CloudFrontHostPattern", StringComparison.Ordinal)
           && verifier.Contains("SourceReleaseSigning.Verify", StringComparison.Ordinal)
           && verifier.Contains("VerifyBuiltOutputAsync", StringComparison.Ordinal)
           && verifier.Contains("RegisterVerifiedSourceUpdateAsync", StringComparison.Ordinal),
        "source updates must reject arbitrary URLs and verify both signed source and the sealed local build attestation");
    Assert(runner.Contains("UseProxy", StringComparison.Ordinal) == false
           && runner.Contains("clearEnvironment: true", StringComparison.Ordinal)
           && runner.Contains("DOTNET_MULTILEVEL_LOOKUP", StringComparison.Ordinal)
           && runner.Contains("NUGET_PACKAGES", StringComparison.Ordinal)
           && runner.Contains("USERPROFILE", StringComparison.Ordinal)
           && runner.Contains("APPDATA", StringComparison.Ordinal)
           && runner.Contains("LOCALAPPDATA", StringComparison.Ordinal)
           && runner.Contains("NUGET_PLUGINS_CACHE_PATH", StringComparison.Ordinal)
           && runner.Contains("DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK", StringComparison.Ordinal)
           && runner.Contains("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", StringComparison.Ordinal)
           && runner.Contains("SourceUpdateProtocol.SourceBuildScriptName", StringComparison.Ordinal),
        "the source build runner must use a fixed entrypoint with an isolated SDK/NuGet environment");
    Assert(manager.Contains("PrepareSourceAsync", StringComparison.Ordinal)
           && manager.Contains("ReconcileSourceGuardianAsync", StringComparison.Ordinal)
           && manager.Contains("SourceUpdatePackageVerifier.VerifyAndExtractAsync", StringComparison.Ordinal)
           && manager.Contains("_sourceBuild.BuildAsync", StringComparison.Ordinal)
           && manager.Contains("RequireCommittedSourceJournal", StringComparison.Ordinal),
        "the Agent must stage source builds and promote the matching Guardian only after the Agent commits");
    Assert(agentProgram.Contains("/api/v1/update/source/prepare", StringComparison.Ordinal)
           && agentProgram.Contains("/api/v1/update/source/guardian", StringComparison.Ordinal),
        "the Agent does not expose the authenticated source prepare and Guardian reconciliation routes");
    Assert(guardian.Contains("journal.DeliveryMode == UpdateDeliveryMode.SourceArchive", StringComparison.Ordinal)
           && guardian.Contains("VerifyArchiveAsync", StringComparison.Ordinal)
           && guardian.Contains("VerifyBuiltOutputAsync", StringComparison.Ordinal)
           && guardian.Contains("CopyVerifiedComponentAsync", StringComparison.Ordinal),
        "the Guardian must independently reverify the source archive and every attested Agent byte before swapping it");
    Assert(provenance.Contains("RegisterVerifiedSourceUpdateAsync", StringComparison.Ordinal)
           && provenance.Contains("SourceBuildOutputDirectory", StringComparison.Ordinal)
           && provenance.Contains("Payload", StringComparison.Ordinal),
        "source-built Agent and Guardian paths are not protected by persistent machine provenance");
    Assert(buildScript.Contains("Payload\\Agent", StringComparison.Ordinal)
           && buildScript.Contains("Payload\\UpdateGuardian", StringComparison.Ordinal)
           && buildScript.Contains("opticon-offline", StringComparison.Ordinal)
           && buildScript.Contains("source build attestation", StringComparison.Ordinal),
        "the fixed source build script must build both rollback-aware components with an offline package feed");
}

static void TestSetupPreflightContracts()
{
    var requireNoReparseTraversal = typeof(Taildesk.Setup.InteractiveUserProfile).GetMethod(
        "RequireNoReparseTraversal",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The Setup user-profile path validator is missing.");
    var root = Directory.CreateTempSubdirectory("opticon-profile-test-").FullName;
    try
    {
        requireNoReparseTraversal.Invoke(null, new object[] { root, root });
        requireNoReparseTraversal.Invoke(null, new object[] { root, Path.Combine(root, "missing-child") });
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    var persistence = ReadSource("src", "Taildesk.Shared", "AgentInstallTransactionPersistence.cs");
    Assert(persistence.Contains("if (!File.Exists(AppPaths.AgentInstallTransactionFile))", StringComparison.Ordinal)
           && persistence.IndexOf(
               "if (!File.Exists(AppPaths.AgentInstallTransactionFile))",
               StringComparison.Ordinal) < persistence.IndexOf(
               "MachineStorageSecurity.RequireRestrictedDirectory(AppPaths.SetupStagingDirectory)",
               StringComparison.Ordinal),
        "an absent Agent transaction journal still requires a not-yet-created SetupStaging directory");

    var pendingFileCheck = typeof(SourceBuildProvenance).GetMethod(
        "HasPendingFilesOutsideInstalledGenerations",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The stale source-provenance recovery check is missing.");
    var pendingPath = Path.Combine(Path.GetTempPath(), "opticon-pending-" + Guid.NewGuid().ToString("N"));
    File.WriteAllText(pendingPath, "pending");
    try
    {
        var pendingHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pendingPath))).ToLowerInvariant();
        var pendingFile = new InstalledSourceFile
        {
            Path = pendingPath,
            Size = new FileInfo(pendingPath).Length,
            Sha256 = pendingHash
        };
        var pendingGeneration = new InstalledSourceGeneration { Files = [pendingFile] };
        var noInstalledGeneration = (bool)pendingFileCheck.Invoke(
            null,
            new object[] { pendingGeneration, Array.Empty<InstalledSourceGeneration>() })!;
        var alreadyInstalledGeneration = (bool)pendingFileCheck.Invoke(
            null,
            new object[]
            {
                pendingGeneration,
                new[] { new InstalledSourceGeneration { Files = [pendingFile] } }
            })!;
        Assert(noInstalledGeneration && !alreadyInstalledGeneration,
            "stale source provenance recovery cannot distinguish uncommitted files from an installed generation");
    }
    finally
    {
        File.Delete(pendingPath);
    }
}

static void TestSourceUpdateProvenanceMappings()
{
    var operationId = Guid.NewGuid();
    var operationDirectory = Path.Combine(AppPaths.UpdateDataDirectory, operationId.ToString("N"));
    var sourceBuild = Path.Combine(operationDirectory, "source-build");
    var journal = new UpdateJournal
    {
        SchemaVersion = 2,
        DeliveryMode = UpdateDeliveryMode.SourceArchive,
        OperationId = operationId,
        SourceBuildOutputDirectory = sourceBuild
    };
    var resolver = typeof(SourceBuildProvenance).GetMethod(
        "ResolveCanonicalTrustPaths",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(string), typeof(UpdateJournal)],
        modifiers: null)
        ?? throw new InvalidOperationException("The source-update provenance resolver is unavailable.");
    string[] Resolve(string path) => ((IEnumerable<string>)(resolver.Invoke(null, [path, journal])
        ?? throw new InvalidOperationException("The source-update provenance resolver returned no paths.")))
        .Select(Path.GetFullPath)
        .ToArray();
    var canonicalAgent = Path.Combine(AppPaths.AgentInstallDirectory, "Taildesk.Agent.exe");
    var canonicalGuardian = Path.Combine(AppPaths.UpdateGuardianInstallDirectory, "Taildesk.UpdateGuardian.exe");
    var stagedAgent = Path.Combine(sourceBuild, "Payload", "Agent", "Taildesk.Agent.exe");
    var stagedGuardian = Path.Combine(sourceBuild, "Payload", "UpdateGuardian", "Taildesk.UpdateGuardian.exe");
    var candidateAgent = AppPaths.AgentInstallDirectory + ".candidate-" + operationId.ToString("N")
        + Path.DirectorySeparatorChar + "Taildesk.Agent.exe";
    var guardianUpgrade = canonicalGuardian + ".upgrade-" + operationId.ToString("N");
    var guardianBackup = canonicalGuardian + ".backup-" + operationId.ToString("N");
    var guardianFailed = canonicalGuardian + ".failed-" + operationId.ToString("N");
    var expectedAgent = Path.GetFullPath(canonicalAgent);
    var expectedGuardian = Path.GetFullPath(canonicalGuardian);
    Assert(Resolve(stagedAgent).Contains(expectedAgent, StringComparer.OrdinalIgnoreCase)
           && Resolve(candidateAgent).Contains(expectedAgent, StringComparer.OrdinalIgnoreCase),
        "the source-built Agent is not trusted while staged or in the Guardian candidate directory");
    Assert(Resolve(stagedGuardian).Contains(expectedGuardian, StringComparer.OrdinalIgnoreCase)
           && Resolve(guardianUpgrade).Contains(expectedGuardian, StringComparer.OrdinalIgnoreCase)
           && Resolve(guardianBackup).Contains(expectedGuardian, StringComparer.OrdinalIgnoreCase)
           && Resolve(guardianFailed).Contains(expectedGuardian, StringComparer.OrdinalIgnoreCase),
        "the source-built Guardian is not trusted through its staged, promoted, and rollback paths");
}

static void TestLegacyMachineStateUpdateGate()
{
    var firstProtected = new Version(1, 1, 39);
    var sourceFloor = UpdatePackageVerifier.ParseVersion(SourceUpdateProtocol.MinimumGuardianVersion);
    Assert(RemoteAdministrationProtocol.MinimumProtectedMachineStateAgentVersion == "1.1.39"
           && RemoteAdministrationProtocol.RequiresLegacyMachineStateMigration(new Version(1, 1, 38), firstProtected)
           && RemoteAdministrationProtocol.RequiresLegacyMachineStateMigration(new Version(1, 0, 0), new Version(1, 1, 40))
           && !RemoteAdministrationProtocol.RequiresLegacyMachineStateMigration(firstProtected, new Version(1, 1, 40))
           && !RemoteAdministrationProtocol.RequiresLegacyMachineStateMigration(new Version(1, 1, 38), new Version(1, 1, 38))
           && SourceUpdateProtocol.MinimumGuardianVersion == "1.2.0"
           && new Version(1, 1, 38) < sourceFloor
           && new Version(1, 2, 0) >= sourceFloor,
        "legacy state remains protected, while the source-only remote-update protocol starts at 1.2.0");

    var releaseClient = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Admin", "OpticonReleaseClient.cs"));
    var agentClient = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Admin", "AgentClient.cs"));
    var coordinator = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Admin", "RemoteDeviceUpdateCoordinator.cs"));
    var mainWindow = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Admin", "MainWindow.xaml.cs"));
    var storage = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Shared", "MachineStorageSecurity.cs"));

    Assert(releaseClient.Contains("manifest.SchemaVersion != 2", StringComparison.Ordinal)
           && releaseClient.Contains("manifest.Artifacts.Count != 1", StringComparison.Ordinal)
           && releaseClient.Contains("ValidateSourceArtifact", StringComparison.Ordinal)
           && releaseClient.Contains("source.Role is not null", StringComparison.Ordinal)
           && releaseClient.Contains("RequiresCleanReinstall = requiresCleanReinstall", StringComparison.Ordinal)
           && releaseClient.Contains("installedAgent < SourceUpdateFloor", StringComparison.Ordinal)
           && releaseClient.Contains("installedGuardian < SourceUpdateFloor", StringComparison.Ordinal),
        "release selection must accept exactly one pinned schema-2 source archive and mark sub-1.2.0 devices for clean reinstall");
    Assert(agentClient.Contains("PrepareSourceUpdateAsync", StringComparison.Ordinal)
           && agentClient.Contains("api/v1/update/source/prepare", StringComparison.Ordinal)
           && agentClient.Contains("TimeSpan.FromMinutes(60)", StringComparison.Ordinal)
           && agentClient.Contains("ReconcileSourceGuardianAsync", StringComparison.Ordinal)
           && agentClient.Contains("api/v1/update/source/guardian", StringComparison.Ordinal),
        "the Admin HTTP client must use the authenticated source prepare and Guardian routes with a build-sized timeout");
    Assert(coordinator.Contains("release.RequiresCleanReinstall", StringComparison.Ordinal)
           && coordinator.Contains("CreateSourceUpdateRequest", StringComparison.Ordinal)
           && coordinator.Contains("SourceUpdatePackageVerifier.ValidateRequest(request)", StringComparison.Ordinal)
           && coordinator.Contains("_agents.PrepareSourceUpdateAsync", StringComparison.Ordinal)
           && coordinator.Contains("CompleteSourceTransactionAsync", StringComparison.Ordinal)
           && coordinator.Contains("_agents.ReconcileSourceGuardianAsync", StringComparison.Ordinal)
           && coordinator.Contains("result.OperationId != sourceRequest.OperationId", StringComparison.Ordinal)
           && !coordinator.Contains("_agents.PrepareUpdateAsync", StringComparison.Ordinal)
           && !coordinator.Contains("IsLegacyMachineStateMigrationBridge", StringComparison.Ordinal),
        "the remote update coordinator must stage only a validated source request and reconcile its Guardian with the same committed operation ID");

    const string cleanGate = "if (release.RequiresCleanReinstall)";
    var gateStart = mainWindow.IndexOf(cleanGate, StringComparison.Ordinal);
    var sourceStart = mainWindow.IndexOf("if (release.SourceRelease is null)", StringComparison.Ordinal);
    Assert(gateStart >= 0
           && sourceStart > gateStart
           && mainWindow[gateStart..sourceStart].Contains("No remote candidate was staged or activated", StringComparison.Ordinal)
           && mainWindow[gateStart..sourceStart].Contains("Opticon 1.1.38 cannot receive a remote source stage", StringComparison.Ordinal)
           && mainWindow[gateStart..sourceStart].Contains("attended clean uninstall and re-enroll", StringComparison.Ordinal)
           && !mainWindow[gateStart..sourceStart].Contains("UpdateDeviceAsync", StringComparison.Ordinal)
           && mainWindow.Contains("Guarded source-built Opticon update", StringComparison.Ordinal)
           && mainWindow.Contains("locally built output is sealed and attested", StringComparison.Ordinal),
        "the Update Opticon UI must never remotely stage legacy devices and must clearly describe local source build attestation for supported devices");

    var ensureEnd = storage.IndexOf("public static bool IsProtectedMachinePath", StringComparison.Ordinal);
    Assert(ensureEnd > 0
           && storage[..ensureEnd].Contains("SshAccessDataDirectory is intentionally excluded", StringComparison.Ordinal)
           && storage[..ensureEnd].Contains("SYSTEM-only or SYSTEM-and-daemon ACL", StringComparison.Ordinal)
           && !storage[..ensureEnd].Contains("AppPaths.SshAccessDataDirectory", StringComparison.Ordinal),
        "generic machine-state ACL validation must leave SSH's dedicated SYSTEM/daemon contract intact");
}

static void TestLegacyMachineStateBridgeSafety()
{
    var retiredSigner = InvitationSigning.CertificateThumbprint;
    var bridge = new Version(1, 1, 41);
    Assert(RemoteAdministrationProtocol.LegacyMachineStateMigrationBridgeVersion == "1.1.41"
           && RemoteAdministrationProtocol.IsLegacyMachineStateMigrationBridge(
               new Version(1, 1, 38), bridge, retiredSigner)
           && !RemoteAdministrationProtocol.IsLegacyMachineStateMigrationBridge(
               new Version(1, 1, 37), bridge, retiredSigner)
           && !RemoteAdministrationProtocol.IsLegacyMachineStateMigrationBridge(
               new Version(1, 1, 38), new Version(1, 1, 40), retiredSigner)
           && !RemoteAdministrationProtocol.IsLegacyMachineStateMigrationBridge(
               new Version(1, 1, 38), bridge, "0000000000000000000000000000000000000000"),
        "the legacy bridge must be restricted to exactly 1.1.38 -> 1.1.41 and the retired signer marker");

    var migration = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Shared", "LegacyMachineStateMigration.cs"));
    var storage = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Shared", "MachineStorageSecurity.cs"));
    var pathGuard = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Shared", "PathGuard.cs"));
    var verifier = File.ReadAllText(FindSourceFile(
        "src", "Taildesk.Shared", "UpdatePackageVerifier.cs"));

    var sealBeforeValidation = migration.IndexOf("SealLegacyStateBeforeValidation(root)", StringComparison.Ordinal);
    var validationAfterSeal = migration.IndexOf("ValidateLegacyState(sealedState)", StringComparison.Ordinal);
    Assert(migration.Contains("BuildSigningTrust.IsLegacyMigrationBuild", StringComparison.Ordinal)
           && migration.Contains("LegacyMachineStateMigrationBridgeVersion", StringComparison.Ordinal)
           && migration.Contains("UpdatePhase.AwaitingCommit", StringComparison.Ordinal)
           && migration.Contains("\"1.1.38\"", StringComparison.Ordinal)
           && migration.Contains("FileFlagOpenReparsePoint", StringComparison.Ordinal)
           && migration.Contains("OpenLegacyChildNoSharing", StringComparison.Ordinal)
           && migration.Contains("NativePath.Enumerate(handle)", StringComparison.Ordinal)
           && migration.Contains("GetSecurityInfo", StringComparison.Ordinal)
           && migration.Contains("SetSecurityInfo", StringComparison.Ordinal)
           && migration.Contains("RequireLegacyProvenanceAcl", StringComparison.Ordinal)
           && migration.Contains("BuiltinUsersSid", StringComparison.Ordinal)
           && migration.Contains("GenericAllRights", StringComparison.Ordinal)
           && migration.Contains("CreatorOwnerSid", StringComparison.Ordinal)
           && migration.Contains("FileSystemRights.Synchronize", StringComparison.Ordinal)
           && migration.Contains("RootLegacyCreateRights", StringComparison.Ordinal)
           && migration.Contains("SealGuardianHeldTransactionLock", StringComparison.Ordinal)
           && migration.Contains("SetNamedSecurityInfo", StringComparison.Ordinal)
           && migration.Contains("GetNamedSecurityInfo", StringComparison.Ordinal)
           && migration.Contains("ExclusiveOpenDeadline", StringComparison.Ordinal)
           && migration.Contains("unknown entry", StringComparison.OrdinalIgnoreCase)
           && migration.Contains("RequireIsolatedSshAccessDirectory", StringComparison.Ordinal)
           && migration.Contains("HasProtectedActiveBridgeJournal", StringComparison.Ordinal)
           && sealBeforeValidation >= 0
           && validationAfterSeal > sealBeforeValidation
           && !migration.Contains("ApplyExactRestricted", StringComparison.Ordinal)
           && !migration.Contains("UpdateJournalPersistence.Load", StringComparison.Ordinal)
           && !migration.Contains("Directory.Delete", StringComparison.Ordinal)
           && !migration.Contains("File.Delete", StringComparison.Ordinal),
        "the bridge must seal each no-share, non-reparse legacy handle before validation, accept only the known root ACL provenance, preserve SSH access, and never delete legacy state");
    Assert(storage.Contains("LegacyMachineStateMigration.MigrateIfRequiredForSignedBridge", StringComparison.Ordinal)
           && storage.Contains("CreateRestrictedDirectorySecurity", StringComparison.Ordinal)
           && storage.Contains("CreateRestrictedFileSecurity", StringComparison.Ordinal)
           && pathGuard.Contains("bool changeSecurity = false", StringComparison.Ordinal)
           && pathGuard.Contains("ReadControl", StringComparison.Ordinal)
           && pathGuard.Contains("WriteDac", StringComparison.Ordinal)
           && pathGuard.Contains("WriteOwner", StringComparison.Ordinal),
        "the bridge must use the ordinary exact ACL descriptors through a same-handle guarded-open API");
    Assert(verifier.Contains("VerifyManifestSignatureAndValidate", StringComparison.Ordinal)
           && verifier.Contains("InvitationSigning.Verify(manifestBytes, signature)", StringComparison.Ordinal)
           && verifier.Contains("IsExactLegacyMigrationBridgeManifest", StringComparison.Ordinal)
           && verifier.Contains("manifest.LegacyMigration", StringComparison.Ordinal)
           && verifier.Contains("expectedSigner = isLegacyMigrationBridge", StringComparison.Ordinal),
        "retired manifest and payload trust must be scoped to the exact signed bridge rather than normal releases");
}

static void TestReleaseDistributionDesign()
{
    DirectoryInfo? root = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Taildesk.sln"))) { root = directory; break; }
        }
        if (root is not null) break;
    }
    Assert(root is not null, "could not find the Opticon source root for release-distribution checks");
    string Read(params string[] parts) => File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    var template = Read("infrastructure", "aws", "opticon-release-distribution.yaml");
    var provision = Read("infrastructure", "aws", "Provision-OpticonReleaseDistribution.ps1");
    var publisher = Read("fly-headscale", "scripts", "Publish-OpticonBundles.ps1");
    var builder = Read("fly-headscale", "scripts", "Build-OpticonBundles.ps1");
    var sourceOnlyPublisher = Read("fly-headscale", "scripts", "Publish-OpticonSourceRelease.ps1");
    var sourceOnlyBuilder = Read("fly-headscale", "scripts", "Build-OpticonSourceRelease.ps1");
    var gateway = Read("fly-headscale", "gateway", "main.go");
    var client = Read("src", "Taildesk.Admin", "OpticonReleaseClient.cs");
    var sourceClient = Read("src", "Taildesk.Admin", "OpticonSourceReleaseClient.cs");
    var agent = Read("src", "Taildesk.Agent", "UpdateManager.cs");
    var hostedBootstrap = Read("src", "Taildesk.Setup", "HostedBootstrap.cs");
    var sourceBootstrap = Read("src", "Taildesk.Setup", "SourceBootstrapInstaller.cs");
    var sdkRequirementDialog = Read("src", "Taildesk.Setup", "SdkRequirementDialog.cs");
    var legacyRemoval = Read("src", "Taildesk.Setup", "LegacyOpticonRemoval.cs");
    var setupPrivilege = Read("src", "Taildesk.Setup", "ScopedProcessPrivilege.cs");
    var sourceInstaller = Read("source-package", "Install-OpticonFromSource.ps1");
    var sourceNuget = Read("source-package", "NuGet.Config");
    var sourceProvenance = Read("src", "Taildesk.Shared", "SourceBuildProvenance.cs");
    var agentInstallJournal = Read("src", "Taildesk.Shared", "AgentInstallTransactionPersistence.cs");
    var setupInstaller = Read("src", "Taildesk.Setup", "InstallerServices.cs");
    var setupWindow = Read("src", "Taildesk.Setup", "MainWindow.xaml.cs");
    Assert(template.Contains("BucketOwnerEnforced", StringComparison.Ordinal)
           && template.Contains("BlockPublicPolicy: true", StringComparison.Ordinal)
           && template.Contains("DenyInsecureTransport", StringComparison.Ordinal)
           && template.Contains("OriginAccessControl", StringComparison.Ordinal)
           && template.Contains("VersioningConfiguration:", StringComparison.Ordinal)
           && template.Contains("Status: Enabled", StringComparison.Ordinal)
           && provision.Contains("versioning is not enabled", StringComparison.Ordinal)
           && template.Contains("ResponseHeadersPolicyId: 60669652-455b-4ae9-85a4-c4c02393f86c", StringComparison.Ordinal)
           && template.Contains("TLSv1.2_2021", StringComparison.Ordinal),
         "CloudFront infrastructure no longer enforces the private TLS-only S3 boundary");
    Assert(sourceOnlyBuilder.Contains("-SourceOnly", StringComparison.Ordinal)
           && sourceOnlyBuilder.Contains("no OpticonBundle or OpticonBootstrap artifact", StringComparison.Ordinal)
           && sourceOnlyPublisher.Contains("SourceOnly = $true", StringComparison.Ordinal)
           && sourceOnlyPublisher.Contains("one immutable object per version", StringComparison.Ordinal)
           && sourceOnlyPublisher.Contains("fixed signed local launcher", StringComparison.Ordinal)
           && publisher.Contains("$buildArguments = @{", StringComparison.Ordinal)
           && publisher.Contains("$buildArguments.SourceOnly = $true", StringComparison.Ordinal)
           && !publisher.Contains("$buildArguments = @(", StringComparison.Ordinal)
           && sourceClient.Contains("manifest.SchemaVersion != 2", StringComparison.Ordinal)
           && sourceClient.Contains("source-only release manifest contains a non-source artifact", StringComparison.Ordinal)
           && gateway.Contains("sourceInstallProtocol", StringComparison.Ordinal)
           && gateway.Contains("SourceLauncherFile", StringComparison.Ordinal)
           && builder.Contains("opticon-source-launcher-$Version.exe", StringComparison.Ordinal),
        "the source-only release channel must publish one signed archive and an embedded fixed launcher");
    Assert(publisher.Contains("--checksum-algorithm", StringComparison.Ordinal)
           && publisher.Contains("--metadata", StringComparison.Ordinal)
           && publisher.Contains("sha256=$hash", StringComparison.Ordinal)
           && publisher.Contains("--checksum-mode", StringComparison.Ordinal)
           && publisher.Contains("Add-Type -AssemblyName System.Net.Http", StringComparison.Ordinal)
           && publisher.Contains("max_concurrent_requests = 20", StringComparison.Ordinal)
           && publisher.Contains("multipart_threshold = 5GB", StringComparison.Ordinal)
           && publisher.Contains("Invoke-CloudFrontVerification", StringComparison.Ordinal)
           && publisher.Contains("FullStreamVerified", StringComparison.Ordinal)
           && publisher.Contains("Publish-ManifestAtomically", StringComparison.Ordinal)
           && publisher.Contains("Assert-OpticonSourceArchive", StringComparison.Ordinal)
           && publisher.Contains("Assert-OpticonBundleArchive", StringComparison.Ordinal)
           && publisher.Contains("Assert-ProductionArtifactTrust", StringComparison.Ordinal)
           && publisher.Contains("OwnerManaged", StringComparison.Ordinal)
           && publisher.Contains("ChecksumSHA256).Equals($expectedChecksum", StringComparison.Ordinal)
           && publisher.Contains("Test-CompositeSha256Checksum", StringComparison.Ordinal)
           && publisher.Contains("$objectExists -and [string]$head.ChecksumType -eq 'COMPOSITE'", StringComparison.Ordinal)
           && publisher.Contains("$total -gt $ExpectedSize", StringComparison.Ordinal)
           && publisher.Contains("Read-PublicManifestBounded", StringComparison.Ordinal)
           && publisher.Contains("[IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($path), $security)", StringComparison.Ordinal)
           && publisher.Contains("exact Microsoft-documented DigiCert RFC3161 endpoint", StringComparison.Ordinal)
           && !publisher.Contains("$LASTEXITCODE -ne 0", StringComparison.Ordinal)
           && !publisher.Contains("flyctl deploy", StringComparison.Ordinal)
           && publisher.Contains("Refusing to overwrite immutable", StringComparison.Ordinal),
        "publisher no longer enforces immutable S3 upload, bounded CloudFront readback, and atomic manifest publication");
    Assert(gateway.Contains("validCloudFrontDownloadURL", StringComparison.Ordinal)
           && gateway.Contains("sourceForInvite", StringComparison.Ordinal)
           && gateway.Contains("validSourceArtifact", StringComparison.Ordinal)
           && gateway.Contains("releaseManifestAdmin", StringComparison.Ordinal)
           && gateway.Contains("writeFileAtomically", StringComparison.Ordinal)
           && gateway.Contains("OPTICON_SIGNING_PROFILE", StringComparison.Ordinal)
           && gateway.Contains("OwnerManaged", StringComparison.Ordinal)
           && client.Contains(".cloudfront.net", StringComparison.Ordinal),
        "manifest clients do not tightly validate CloudFront download URLs");
    Assert(client.Contains("SourceUpdateFloor", StringComparison.Ordinal)
           && client.Contains("manifest.SchemaVersion != 2", StringComparison.Ordinal)
           && client.Contains("manifest.Artifacts.Count != 1", StringComparison.Ordinal)
           && client.Contains("ValidateSourceArtifact", StringComparison.Ordinal)
           && client.Contains("RequireImmutableCloudFrontDownload", StringComparison.Ordinal)
           && client.Contains("RequiresCleanReinstall", StringComparison.Ordinal),
        "release selection must accept only the schema-2 source archive and fail closed to clean reinstall for pre-source devices");
    Assert(agent.Contains("UseProxy = false", StringComparison.Ordinal)
           && agent.Contains("AllowAutoRedirect = false", StringComparison.Ordinal)
           && agent.Contains("CheckCertificateRevocationList = true", StringComparison.Ordinal),
        "Agent release downloader does not retain the required direct HTTPS behavior");
    Assert(hostedBootstrap.Contains("SourceBootstrapInstaller.RunAsync", StringComparison.Ordinal)
           && hostedBootstrap.Contains("IsSourceLauncher", StringComparison.Ordinal)
           && hostedBootstrap.Contains("ParseSourceLaunch", StringComparison.Ordinal)
           && hostedBootstrap.Contains("BoundSourceLauncherPattern", StringComparison.Ordinal)
           && hostedBootstrap.Contains("LauncherSha256", StringComparison.Ordinal)
           && hostedBootstrap.Contains("ProductSigning.VerifyAuthenticodeAsync", StringComparison.Ordinal)
           && !hostedBootstrap.Contains("BootstrapSha256", StringComparison.Ordinal)
           && hostedBootstrap.Contains("BootstrapHandoffDirectory", StringComparison.Ordinal)
           && hostedBootstrap.Contains("CreateRestrictedDirectorySecurity", StringComparison.Ordinal)
           && hostedBootstrap.Contains("RequireRestrictedAcl", StringComparison.Ordinal)
           && hostedBootstrap.Contains("RequireProtectedHandoff", StringComparison.Ordinal)
           && sourceBootstrap.Contains("VerifyLauncherMatchesArchiveAsync", StringComparison.Ordinal)
           && sourceBootstrap.Contains("DownloadPresignedSourceAsync", StringComparison.Ordinal)
           && sourceBootstrap.Contains("TemporaryRedirect", StringComparison.Ordinal)
           && sourceBootstrap.IndexOf("VerifyLauncherMatchesArchiveAsync", StringComparison.Ordinal)
              < sourceBootstrap.IndexOf("LegacyOpticonRemoval.RemoveLegacyInstallationIfPresentAsync", StringComparison.Ordinal)
           && sourceBootstrap.Contains("ResolveSourceArchive", StringComparison.Ordinal)
           && sourceBootstrap.Contains("SourceInstallProtocol", StringComparison.Ordinal)
           && !sourceBootstrap.Contains("MatchesBootstrap", StringComparison.Ordinal)
           && !sourceBootstrap.Contains("BootstrapSha256", StringComparison.Ordinal)
           && setupWindow.Contains("HostedBootstrapper.IsSourceLauncher", StringComparison.Ordinal)
           && Read("src", "Taildesk.Setup", "SourceLauncherPrompt.cs").Contains("ReadInvitationUrl", StringComparison.Ordinal)
           && setupWindow.Contains("GetEnvironmentVariable(HostedBootstrapper.InvitePathEnvironmentVariable)", StringComparison.Ordinal)
           && setupWindow.Contains("SetEnvironmentVariable(HostedBootstrapper.InviteKeyEnvironmentVariable, null)", StringComparison.Ordinal)
           && setupWindow.Contains("Plaintext and legacy local invitation files are no longer accepted", StringComparison.Ordinal)
           && !setupWindow.Contains("DeserializeAsync<InvitePayload>", StringComparison.Ordinal)
           && setupWindow.Contains("new InstallCoordinator(", StringComparison.Ordinal)
           && setupWindow.Contains("_invite!, AppContext.BaseDirectory", StringComparison.Ordinal)
           && !setupWindow.Contains("new InstallCoordinator(_invite!, Path.GetDirectoryName(_invitePath)", StringComparison.Ordinal)
           && setupWindow.Contains("Environment.ExitCode = 1", StringComparison.Ordinal)
           && setupWindow.Contains("MarkAutomaticInstallSucceeded", StringComparison.Ordinal)
           && setupWindow.Contains("FileMode.CreateNew", StringComparison.Ordinal)
           && !setupWindow.Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal)
           && setupWindow.Contains("private-key-redacted", StringComparison.Ordinal)
           && setupWindow.Contains("DetailsExpander.IsExpanded = true", StringComparison.Ordinal),
         "source-only launcher handoff, archive pinning, or redacted persistent Setup diagnostics regressed");
    Assert(legacyRemoval.Contains("The verified invitation authorizes its automatic replacement", StringComparison.Ordinal)
           && !legacyRemoval.Contains("LegacyOpticonRemovalPrompt", StringComparison.Ordinal)
           && !File.Exists(Path.Combine(root!.FullName, "src", "Taildesk.Setup", "LegacyOpticonRemovalPrompt.cs"))
           && legacyRemoval.Contains("QueryTaskIfPresentAsync", StringComparison.Ordinal)
           && !legacyRemoval.Contains("RequireTaskOwnsExactExecutable", StringComparison.Ordinal)
           && legacyRemoval.Contains("RequireRegularDirectoryTree", StringComparison.Ordinal)
           && legacyRemoval.Contains("SealDirectoryTreeForDeletion", StringComparison.Ordinal)
           && legacyRemoval.Contains("FileFlagOpenReparsePoint", StringComparison.Ordinal)
           && legacyRemoval.Contains("SetSecurityInfo", StringComparison.Ordinal)
           && !legacyRemoval.Contains("OwnerSecurityInformation", StringComparison.Ordinal)
           && !legacyRemoval.Contains("WriteOwner", StringComparison.Ordinal)
           && legacyRemoval.Contains("desiredAccess |= WriteDac | FileListDirectory", StringComparison.Ordinal)
           && legacyRemoval.Contains("SealDirectoryDacl(root)", StringComparison.Ordinal)
           && legacyRemoval.Contains("SealDirectoryDacl(entry)", StringComparison.Ordinal)
           && legacyRemoval.Contains("DaclSecurityInformation | ProtectedDaclSecurityInformation", StringComparison.Ordinal)
           && legacyRemoval.Contains("var inheritance = AceFlags.None", StringComparison.Ordinal)
           && legacyRemoval.Contains("Cleanup seals every directory explicitly", StringComparison.Ordinal)
           && legacyRemoval.Contains("DescribeCleanupPath(entry.Path)", StringComparison.Ordinal)
           && legacyRemoval.Contains("using handle access", StringComparison.Ordinal)
           && legacyRemoval.Contains("SetFileInformationByHandle", StringComparison.Ordinal)
            && legacyRemoval.Contains("PinnedDirectoryTree", StringComparison.Ordinal)
           && legacyRemoval.Contains("FileShareDelete", StringComparison.Ordinal)
            && legacyRemoval.Contains("FileReadAttributes | Synchronize", StringComparison.Ordinal)
            && legacyRemoval.Contains("Could not re-observe a pinned Opticon path safely", StringComparison.Ordinal)
            && legacyRemoval.Contains("ScopedProcessPrivilege.Enable(\"SeBackupPrivilege\")", StringComparison.Ordinal)
            && legacyRemoval.Contains("ScopedProcessPrivilege.Enable(\"SeRestorePrivilege\")", StringComparison.Ordinal)
            && legacyRemoval.Contains("var desiredAccess = Delete | FileReadAttributes | Synchronize", StringComparison.Ordinal)
            && !legacyRemoval.Contains("GenericRead", StringComparison.Ordinal)
            && !legacyRemoval.Contains("ReadControl", StringComparison.Ordinal)
            && legacyRemoval.Contains("DescribeCleanupPath(path)", StringComparison.Ordinal)
            && legacyRemoval.IndexOf("DeleteAllPinnedEntries", StringComparison.Ordinal)
               < legacyRemoval.IndexOf("await DeleteTaskAsync", StringComparison.Ordinal)
            && setupPrivilege.Contains("AdjustTokenPrivileges", StringComparison.Ordinal)
            && setupPrivilege.Contains("ErrorNotAllAssigned", StringComparison.Ordinal)
            && !legacyRemoval.Contains("using var observed = OpenPinnedEntry", StringComparison.Ordinal)
            && !legacyRemoval.Contains("Directory.Delete(", StringComparison.Ordinal)
           && !legacyRemoval.Contains("File.Delete(", StringComparison.Ordinal)
           && legacyRemoval.Contains("FileAttributes.ReparsePoint", StringComparison.Ordinal)
           && !legacyRemoval.Contains("UpdatePackageVerifier.NormalizeVersion", StringComparison.Ordinal)
           && !legacyRemoval.Contains("FileVersionInfo", StringComparison.Ordinal)
           && legacyRemoval.Contains("Directory.EnumerateFiles(installDirectory, \"*.exe\", SearchOption.AllDirectories)", StringComparison.Ordinal)
           && legacyRemoval.Contains("image.StartsWith(canonicalRoot", StringComparison.Ordinal)
           && legacyRemoval.Contains("RemoteAdministrationProtocol.AgentTaskName", StringComparison.Ordinal)
           && legacyRemoval.Contains("RemoteAdministrationProtocol.SshSupervisorTaskName", StringComparison.Ordinal)
           && !legacyRemoval.Contains("TailscaleCli", StringComparison.Ordinal)
           && !legacyRemoval.Contains("sc.exe", StringComparison.OrdinalIgnoreCase)
           && !legacyRemoval.Contains("netsh", StringComparison.OrdinalIgnoreCase)
           && legacyRemoval.Contains("Tailscale and RustDesk were left unchanged", StringComparison.Ordinal),
        "existing-version replacement must be signed-launcher authorized, automatic, fixed-task/path bounded, and leave Tailscale/RustDesk untouched");
    Assert(gateway.Contains("serveInvitationSourceLauncher", StringComparison.Ordinal)
           && gateway.Contains("validSourceLauncherMetadata", StringComparison.Ordinal)
           && gateway.Contains("No ZIP extraction or invitation paste is needed", StringComparison.Ordinal)
           && gateway.Contains("install.download='Install-Opticon-", StringComparison.Ordinal)
           && gateway.Contains("location.hash.slice(1)", StringComparison.Ordinal)
           && !gateway.Contains("buildBootstrapStarterCommand", StringComparison.Ordinal)
           && !gateway.Contains("ExecutionPolicy", StringComparison.Ordinal),
        "source-only invitations must deliver one fragment-bound signed launcher without an unsigned script or manual handoff");
    Assert(sourceBootstrap.Contains("clearEnvironment: true", StringComparison.Ordinal)
           && sourceBootstrap.Contains("DirectHttp.CreateClient", StringComparison.Ordinal)
           && sourceBootstrap.Contains("maximumSize: 64 * 1024", StringComparison.Ordinal)
           && !sourceBootstrap.Contains("artifacts/v1/manifest.json", StringComparison.Ordinal)
           && !sourceBootstrap.Contains("UseShellExecute = true", StringComparison.Ordinal)
           && !sourceBootstrap.Contains("Process.Start(new ProcessStartInfo", StringComparison.Ordinal)
           && sourceBootstrap.Contains("MSBuildUserExtensionsPath", StringComparison.Ordinal)
           && sourceBootstrap.Contains("PSModulePath", StringComparison.Ordinal)
           && sourceBootstrap.Contains("USERPROFILE", StringComparison.Ordinal)
           && sourceBootstrap.Contains("APPDATA", StringComparison.Ordinal)
           && sourceBootstrap.Contains("LOCALAPPDATA", StringComparison.Ordinal)
           && sourceBootstrap.Contains("NUGET_PLUGINS_CACHE_PATH", StringComparison.Ordinal)
           && sourceBootstrap.Contains("DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK", StringComparison.Ordinal)
           && sourceBootstrap.Contains("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", StringComparison.Ordinal)
           && sourceBootstrap.Contains("RequireNoReparseTraversal(programFiles, dotnet)", StringComparison.Ordinal)
           && !sourceBootstrap.Contains("ExecutionPolicy\", \"Bypass", StringComparison.Ordinal)
           && sourceInstaller.Contains("ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false", StringComparison.Ordinal)
           && sourceInstaller.Contains("ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false", StringComparison.Ordinal)
           && sourceInstaller.Contains("DirectoryBuildTargetsPath=", StringComparison.Ordinal)
           && sourceInstaller.Contains("UseSharedCompilation=false", StringComparison.Ordinal)
           && sourceInstaller.Contains("nodeReuse:false", StringComparison.Ordinal)
           && sourceInstaller.Contains("ValidateSet('win-x64', 'win-arm64')", StringComparison.Ordinal)
            && sourceBootstrap.Contains("Architecture.Arm64 => \"win-arm64\"", StringComparison.Ordinal)
             && sourceBootstrap.Contains("DotNetSdkPolicy.InventoryContainsAcceptedSdk", StringComparison.Ordinal)
             && !sourceInstaller.Contains("$DotnetPath --list-runtimes", StringComparison.Ordinal)
             && !sourceInstaller.Contains("$DotnetPath --info", StringComparison.Ordinal)
            && sourceBootstrap.Contains("MSBUILDDISABLENODEREUSE", StringComparison.Ordinal)
            && sourceInstaller.Contains("--no-restore", StringComparison.Ordinal)
            && sourceInstaller.Contains("--self-contained', 'true", StringComparison.Ordinal)
            && sourceInstaller.Contains("'10.*.*'", StringComparison.Ordinal)
            && sourceInstaller.Contains("'latestMinor'", StringComparison.Ordinal)
            && sourceInstaller.Contains("ValidateSet('Production','OwnerManaged')", StringComparison.Ordinal)
            && sourceNuget.Contains("opticon-offline", StringComparison.Ordinal)
           && sourceNuget.Contains("./packages", StringComparison.Ordinal)
           && builder.Contains("$offlinePackageDirectory", StringComparison.Ordinal)
           && builder.Contains("microsoft.aspnetcore.app.runtime.win-x64", StringComparison.Ordinal)
           && builder.Contains("microsoft.aspnetcore.app.runtime.win-arm64", StringComparison.Ordinal)
           && builder.Contains("microsoft.netcore.app.host.win-arm64", StringComparison.Ordinal)
           && builder.Contains("microsoft.windows.sdk.net.ref", StringComparison.Ordinal)
           && sourceInstaller.Contains("$setupExitCode -ne 0", StringComparison.Ordinal),
        "the elevated source build does not isolate MSBuild/PowerShell, carry its signed offline feed, or propagate Setup failure");
    Assert(sourceBootstrap.Contains("CompatibleSdkIsReadyAsync", StringComparison.Ordinal)
            && sourceBootstrap.Contains("cancellationToken => CompatibleSdkIsReadyAsync", StringComparison.Ordinal)
            && sdkRequirementDialog.Contains("Setup will detect it and resume automatically", StringComparison.Ordinal)
            && sdkRequirementDialog.Contains("stable .NET SDK matching", StringComparison.Ordinal)
            && sdkRequirementDialog.Contains("DispatcherTimer", StringComparison.Ordinal)
            && sdkRequirementDialog.Contains("TimeSpan.FromSeconds(3)", StringComparison.Ordinal)
            && sdkRequirementDialog.Contains("Content = \"Check now\"", StringComparison.Ordinal)
            && sdkRequirementDialog.Contains("window.DialogResult = true", StringComparison.Ordinal)
            && !sdkRequirementDialog.Contains("Content = \"Retry\"", StringComparison.Ordinal),
        "the .NET 10 SDK prerequisite must resume automatically without turning a completed SDK install into Setup failure");
    Assert(sourceProvenance.Contains("public int SchemaVersion { get; set; } = 5", StringComparison.Ordinal)
            && sourceProvenance.Contains("List<InstalledSourceGeneration> Installed", StringComparison.Ordinal)
            && sourceProvenance.Contains("InstalledSourceGeneration? Pending", StringComparison.Ordinal)
            && sourceProvenance.Contains("PendingTransactionId", StringComparison.Ordinal)
            && sourceProvenance.Contains("PendingInviteCiphertextSha256", StringComparison.Ordinal)
            && sourceProvenance.Contains("AcquireStoreLease", StringComparison.Ordinal)
            && sourceProvenance.Contains("_activeStoreLease", StringComparison.Ordinal)
            && sourceProvenance.Contains("Admin.previous", StringComparison.Ordinal)
            && sourceProvenance.Contains(".rollback-", StringComparison.Ordinal)
            && sourceProvenance.Contains("CommitActiveComponent", StringComparison.Ordinal)
            && sourceProvenance.Contains("store.Pending = null", StringComparison.Ordinal)
             && agentInstallJournal.Contains("PreviousConfig", StringComparison.Ordinal)
             && agentInstallJournal.Contains("PreviousReceipt", StringComparison.Ordinal)
             && agentInstallJournal.Contains("PreviousTaskXml", StringComparison.Ordinal)
             && agentInstallJournal.Contains("TaskStateRestored", StringComparison.Ordinal)
             && setupInstaller.Contains("Directory.Move(destination, rollback)", StringComparison.Ordinal)
            && setupInstaller.Contains("Directory.Move(candidate, destination)", StringComparison.Ordinal)
            && setupInstaller.Contains("RollbackAgentInstallTransactionAsync", StringComparison.Ordinal)
             && setupInstaller.Contains("AgentInstallOperationId", StringComparison.Ordinal)
             && setupInstaller.Contains("CaptureAgentTaskSnapshotAsync", StringComparison.Ordinal)
             && setupInstaller.Contains("RestoreAgentTaskSnapshotAsync", StringComparison.Ordinal)
             && setupInstaller.Contains("The restored Agent task did not restart the prior Agent", StringComparison.Ordinal)
             && setupWindow.Contains("TryRollbackSourceProvenance", StringComparison.Ordinal),
        "failed source reinstalls can replace current provenance or cannot validate exact rollback generations");
    Assert(!builder.Contains("reuseCommandCenterPublish", StringComparison.Ordinal)
           && !builder.Contains("commandCenterPublish", StringComparison.Ordinal)
           && builder.Contains("targetRuntimes = @('win-x64', 'win-arm64')", StringComparison.Ordinal)
           && builder.Contains("safe.directory=$($script:trustedGitRoot.Replace('\\', '/'))", StringComparison.Ordinal)
           && builder.Contains("The production hosted build resolved an unexpected Git root", StringComparison.Ordinal)
           && builder.Contains("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", StringComparison.Ordinal)
           && builder.Contains("DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK", StringComparison.Ordinal)
           && builder.Contains("-p:MSBuildEnableWorkloadResolver=false", StringComparison.Ordinal)
           && builder.Contains("USERPROFILE = $cliHome; HOME = $cliHome", StringComparison.Ordinal)
           && builder.Contains("APPDATA = $isolatedRoamingProfile; LOCALAPPDATA = $isolatedLocalProfile", StringComparison.Ordinal)
           && builder.Contains("$retainedBootstraps", StringComparison.Ordinal)
           && builder.Contains("$retainedSources", StringComparison.Ordinal)
           && builder.Contains("The clean $component publish must contain only", StringComparison.Ordinal),
        "release signing can still reuse ignored publish caches or accept undeclared output files");
}
static void TestTailnetSshPolicy()
{
    string? sourcePath = null;
    foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(root);
        while (directory is not null)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("src", "Taildesk.Admin", "TailnetPolicy.cs"),
                         Path.Combine("opticon", "src", "Taildesk.Admin", "TailnetPolicy.cs")
                     })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) { sourcePath = candidate; break; }
            }
            if (sourcePath is not null) break;
            directory = directory.Parent;
        }
        if (sourcePath is not null) break;
    }
    if (sourcePath is null) throw new InvalidOperationException("Taildesk.Admin TailnetPolicy.cs was not found.");
    var source = File.ReadAllText(sourcePath);
    Assert(source.Contains("\"ip\": [\"tcp:45832\"]", StringComparison.Ordinal), "runtime policy does not grant the isolated SSH port");
    Assert(source.Contains("\"tag:taildesk-managed:45832\"", StringComparison.Ordinal), "runtime policy does not test managed-device SSH denial");
    Assert(source.Contains("\"tag:taildesk-controller:45832\"", StringComparison.Ordinal), "runtime policy does not test controller SSH denial");
}
static void TestUpdateJournalPersistence()
{
    var directory = Path.Combine(Path.GetTempPath(), "opticon-update-journal-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var journal = new UpdateJournal
        {
            OperationId = Guid.NewGuid(),
            Phase = UpdatePhase.Ready,
            MaintenanceBootstrap = true,
            SshWasListening = true,
            GuardianClaimedAt = DateTimeOffset.UtcNow,
            CurrentVersion = "1.0.0",
            TargetVersion = "1.1.0",
            StartedAt = DateTimeOffset.UtcNow,
            RollbackDirectory = Path.Combine(directory, "agent.rollback"),
            Message = "verified"
        };
        var persistence = ReadSource("src", "Taildesk.Shared", "UpdateJournalPersistence.cs");
        var coordination = ReadSource("src", "Taildesk.Shared", "UpdateJournalCoordination.cs");
        Assert(persistence.Contains("RequireUpdatePath", StringComparison.Ordinal)
               && persistence.Contains("MachineStorageSecurity.RequireRestrictedDirectory", StringComparison.Ordinal)
               && persistence.Contains("MachineStorageSecurity.WriteRestrictedFileAtomicAsync", StringComparison.Ordinal)
               && persistence.Contains("journal.UpdatedAt = DateTimeOffset.UtcNow", StringComparison.Ordinal)
               && coordination.Contains("WriteRestrictedFileCreateNewAsync", StringComparison.Ordinal)
               && coordination.Contains("MachineStorageSecurity.RequireRestrictedFile(path)", StringComparison.Ordinal),
            "update persistence is not bound to the protected machine root, atomic writer, and sealed coordination lock");

        journal.UpdatedAt = DateTimeOffset.UtcNow;
        var content = JsonSerializer.SerializeToUtf8Bytes(journal, JsonDefaults.Options);
        var loaded = JsonSerializer.Deserialize<UpdateJournal>(content, JsonDefaults.Options);
        Assert(loaded?.OperationId == journal.OperationId && loaded.Phase == UpdatePhase.Ready, "atomic update journal did not round-trip");
        Assert(loaded!.MaintenanceBootstrap, "maintenance-only Guardian state was not durable");
        Assert(loaded.ToStatus().MaintenanceBootstrap,
            "maintenance-only Guardian state was not exposed through authenticated status");
        Assert(loaded.SshWasListening && loaded.GuardianClaimedAt == journal.GuardianClaimedAt,
            "Guardian pickup and SSH lifeline requirements were not durable");
        Assert(loaded.UpdatedAt >= journal.StartedAt, "journal persistence did not stamp its durable update time");
        Assert(!loaded.ToStatus().RollbackAvailable,
            "a planned rollback path was reported as a physical rollback copy");
        Directory.CreateDirectory(loaded.RollbackDirectory);
        Assert(loaded.ToStatus().RollbackAvailable,
            "a physical rollback copy was not exposed through authenticated status");
        Directory.Delete(loaded.RollbackDirectory);
        Assert(!loaded.ToStatus().RollbackAvailable,
            "a consumed rollback copy remained exposed through authenticated status");

        var lockPath = Path.Combine(directory, "transaction.lock");
        using var lease = UpdateJournalCoordination.AcquireAsync(
            TimeSpan.FromSeconds(1), path: lockPath).GetAwaiter().GetResult();
        AssertThrows<TimeoutException>(() =>
            UpdateJournalCoordination.AcquireAsync(
                TimeSpan.FromMilliseconds(150), path: lockPath).GetAwaiter().GetResult());
    }
    finally
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }
}
static void TestUploadPolicy()
{
    var config = new AgentConfig();
    Assert(config.MaxUploadBytes >= 20L * 1024 * 1024 * 1024, "uploads are capped below 20 GiB");
    Assert(config.MaxUploadBytes == 256L * 1024 * 1024 * 1024, "default maximum upload is not the reviewed 256 GiB limit");
    Assert(config.MaxConcurrentUploads is >= 1 and <= 2, "concurrent upload bound is unsafe");
    Assert(config.MinimumFreeSpaceBytes >= 5L * 1024 * 1024 * 1024, "free-space reserve is too small");
    Assert(config.MaxUploadDurationMinutes <= 24 * 60, "upload lifetime is unbounded");
    var transfers = ReadSource("src", "Taildesk.Admin", "TransferManager.cs");
    var browser = ReadSource("src", "Taildesk.Admin", "FileManagerWindow.xaml.cs");
    var agentClient = ReadSource("src", "Taildesk.Admin", "AgentClient.cs");
    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    var transferView = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    Assert(transfers.Contains("public void Resume(TransferRow row)", StringComparison.Ordinal)
           && transfers.Contains("row.Cancellation?.Cancel()", StringComparison.Ordinal)
           && transfers.Contains("row.Id", StringComparison.Ordinal),
        "the application transfer manager must own cancellation and resumable transfer identity");
    Assert(browser.Contains("StartDownload", StringComparison.Ordinal)
           && browser.Contains("StartUpload", StringComparison.Ordinal)
           && !browser.Contains("_transfers.DownloadAsync", StringComparison.Ordinal)
           && !browser.Contains("_transfers.UploadAsync", StringComparison.Ordinal),
        "the file browser must start application-owned transfers instead of awaiting window-owned transfers");
    Assert(agentClient.Contains("GuardedLocalTransferTarget.Create", StringComparison.Ordinal)
           && agentClient.Contains("unexpected partial response to a full-file download", StringComparison.Ordinal)
           && !agentClient.Contains("RangeHeaderValue(offset", StringComparison.Ordinal)
           && agentClient.Contains("files/upload-status", StringComparison.Ordinal)
           && agentClient.Contains("HttpStatusCode.NotFound", StringComparison.Ordinal)
           && agentProgram.Contains("GetUploadStatus", StringComparison.Ordinal)
           && agentProgram.Contains("UploadLegacyAsync", StringComparison.Ordinal),
        "downloads must restart without unverifiable prefixes while uploads retain authenticated receiver offsets");
    Assert(transferView.Contains("Header=\"Resume\"", StringComparison.Ordinal)
           && transferView.Contains("PreviewMouseRightButtonDown", StringComparison.Ordinal),
        "the Transfers page must expose row-targeted right-click resume");
}

static void TestResumableUpload()
{
    var directory = Path.Combine(Path.GetTempPath(), "opticon-resume-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var expected = "resumable-transfer-payload"u8.ToArray();
        var partial = Path.Combine(directory, ".taildesk-upload-test.partial");
        using (var prefix = new MemoryStream(expected[..9]))
        {
            try
            {
                ResumableTransferFile.AppendToLengthAsync(
                        partial, prefix, 0, expected.Length, 1024 * 1024, CancellationToken.None)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("The deliberately incomplete upload unexpectedly completed.");
            }
            catch (IOException exception)
            {
                Assert(exception.Message.Contains("ended before", StringComparison.Ordinal),
                    "the incomplete upload failed for an unexpected reason");
            }
        }

        var offset = ResumableTransferFile.GetValidatedLength(partial, expected.Length);
        Assert(offset == 9,
            "the Agent did not retain the exact resumable offset");
        using (var remainder = new MemoryStream(expected[9..]))
        {
            ResumableTransferFile.AppendToLengthAsync(
                    partial, remainder, offset, expected.Length, 1024 * 1024, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        Assert(File.ReadAllBytes(partial).SequenceEqual(expected),
            "the resumed upload did not reproduce the original bytes");
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}
static void TestPathGuard()
{
    var temporary = Path.Combine(Path.GetTempPath(), "taildesk-selftest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var child = Path.Combine(temporary, "child");
        Directory.CreateDirectory(child);
        var guard = new PathGuard(new Dictionary<string, string> { ["test"] = temporary });
        Assert(guard.Resolve("test", "child") == Path.GetFullPath(child), "valid child did not resolve");
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", ".."));
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", "file.txt:stream", mustExist: false));
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", "C:\\Windows", mustExist: false));
        AssertThrows<UnauthorizedAccessException>(() => guard.Resolve("test", "\\\\server\\share", mustExist: false));

        if (OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(temporary, "outside.txt");
            var linked = Path.Combine(child, "hard-linked.txt");
            File.WriteAllText(outside, "outside-root");
            if (!SelfTestNative.CreateHardLink(linked, outside, IntPtr.Zero))
                throw new InvalidOperationException(
                    "The hard-link security fixture could not be created. Windows error "
                    + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ".");
            AssertThrows<UnauthorizedAccessException>(() =>
            {
                using var _ = guard.Acquire("test", "child/hard-linked.txt", readFile: true, delete: true);
            });
            Assert(File.ReadAllText(outside) == "outside-root",
                "the guarded hard-link rejection changed the out-of-root file");
        }
    }
    finally
    {
        Directory.Delete(temporary, true);
    }
}

static void TestLocalVolumeRoots()
{
    if (!OperatingSystem.IsWindows()) return;
    var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
    var guard = new PathGuard(new Dictionary<string, string> { ["Documents"] = userRoot });
    Assert(guard.GetRoots().Count == 1
           && guard.GetRoots()[0].PathHint.Equals(Path.GetFullPath(userRoot), StringComparison.OrdinalIgnoreCase),
        "the explicitly configured user root was not retained");
    AssertThrows<InvalidOperationException>(() =>
        new PathGuard(new Dictionary<string, string>(), includeLocalVolumes: true));
    AssertThrows<UnauthorizedAccessException>(() =>
        PathGuard.ValidateRemoteFileRoot(Path.GetPathRoot(Environment.SystemDirectory)!));
    AssertThrows<UnauthorizedAccessException>(() =>
        PathGuard.ValidateRemoteFileRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Taildesk")));
}

static void TestAgentEndpointPolicy()
{
    foreach (var path in new[]
             {
                 "/api/v1/security/rotate", "/API/V1/SECURITY/ROTATE",
                 "/api/v1/ssh/access", "/Api/V1/Update/Guardian",
                 "/API/V1/ACTIONS/EXIT-NODE"
             })
        Assert(AgentEndpointPolicy.RequiresPrimaryCommandCenter(path), $"primary capability missed {path}");
    Assert(!AgentEndpointPolicy.RequiresPrimaryCommandCenter("/api/v1/security-adjacent"),
        "a near-prefix was treated as a primary-only segment");
    Assert(AgentEndpointPolicy.IsInternalUpdateHealth("/INTERNAL/UPDATE-HEALTH"),
        "internal health classification is case-sensitive");
    Assert(AgentEndpointPolicy.IsSignedMediaDownload("/API/V1/MEDIA"),
        "signed media classification is case-sensitive");
}

static void TestFileBrowserContract()
{
    var xamlPath = FindSourceFile("src", "Taildesk.Admin", "FileManagerWindow.xaml");
    var document = XDocument.Load(xamlPath);
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    var names = document.Descendants()
        .Select(element => element.Attribute(x + "Name")?.Value)
        .Where(name => name is not null)
        .ToHashSet(StringComparer.Ordinal);
    Assert(names.Contains("PathText"), "the remote directory address bar is missing");
    Assert(names.Contains("ShowThumbnailsCheck"), "the list/thumbnail toggle is missing");
    Assert(names.Contains("FileGrid") && names.Contains("ThumbnailList"), "both file browser views must be present");

    var fileGrid = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "FileGrid");
    var thumbnailList = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "ThumbnailList");
    Assert(fileGrid.Attribute("SelectionMode")?.Value == "Extended"
           && thumbnailList.Attribute("SelectionMode")?.Value == "Extended",
        "list and thumbnail views must both support Ctrl/Shift multi-selection");

    var browser = ReadSource("src", "Taildesk.Admin", "FileManagerWindow.xaml.cs");
    Assert(browser.Contains("SelectedItems", StringComparison.Ordinal)
           && browser.Contains("PlanFolderDownloadsAsync", StringComparison.Ordinal)
           && browser.Contains("_transfers.StartDownload", StringComparison.Ordinal)
           && browser.Contains("GuardedLocalTransferPath.EnsureDirectory", StringComparison.Ordinal)
           && browser.Contains("Path.GetRelativePath(destinationRoot, download.LocalPath)", StringComparison.Ordinal)
           && !browser.Contains("Directory.CreateDirectory(localDirectory)", StringComparison.Ordinal)
           && browser.IndexOf("foreach (var download in batch.Downloads)", StringComparison.Ordinal)
              > browser.IndexOf("PlanFolderDownloadsAsync", StringComparison.Ordinal),
        "multi-download must queue root-relative files and defer directory creation to the guarded transfer path");

    var transfers = ReadSource("src", "Taildesk.Admin", "TransferManager.cs");
    Assert(transfers.Contains("MaximumConcurrentTransfers", StringComparison.Ordinal)
           && transfers.Contains("_transferSlots.WaitAsync", StringComparison.Ordinal),
        "large multi-download batches must use bounded transfer concurrency");

    var agentConfig = ReadSource("src", "Taildesk.Shared", "AgentConfiguration.cs");
    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    var legacyExposureMigration = agentProgram.IndexOf("if (config.ExposeAllLocalVolumes)", StringComparison.Ordinal);
    var legacyExposureSave = agentProgram.IndexOf("await configStore.SaveAsync(config)", StringComparison.Ordinal);
    var configuredAgentCheck = agentProgram.IndexOf(
        "if (string.IsNullOrWhiteSpace(config.AgentTokenHash))", StringComparison.Ordinal);
    Assert(agentConfig.Contains("ExposeAllLocalVolumes", StringComparison.Ordinal)
           && agentProgram.Contains("config.ExposeAllLocalVolumes = false", StringComparison.Ordinal)
           && agentProgram.Contains("new PathGuard(config.SharedRoots)", StringComparison.Ordinal)
           && !agentProgram.Contains("new PathGuard(config.SharedRoots, config.ExposeAllLocalVolumes)", StringComparison.Ordinal)
           && legacyExposureMigration >= 0
           && legacyExposureSave > legacyExposureMigration
           && configuredAgentCheck > legacyExposureSave
           && !agentProgram.Contains("config.SharedRoots.Count == 0", StringComparison.Ordinal),
        "the Agent must durably retire legacy all-volume access while allowing a no-folder recovery configuration");
}

static void TestDeviceRenameContract()
{
    var xaml = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    Assert(xaml.Contains("Header=\"Rename device\"", StringComparison.Ordinal)
           && xaml.Contains("PreviewMouseRightButtonDown=\"DeviceGrid_PreviewMouseRightButtonDown\"", StringComparison.Ordinal)
           && xaml.Contains("Click=\"RenameDevice_Click\"", StringComparison.Ordinal),
        "the device grid must select a right-clicked row and expose Rename device");

    var window = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml.cs");
    Assert(window.Contains("_viewModel.RenameDeviceAsync(device, prompt.Value)", StringComparison.Ordinal),
        "the Rename device menu action must submit the selected device and entered name");

    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    var renameStart = viewModel.IndexOf("public async Task RenameDeviceAsync", StringComparison.Ordinal);
    var renameEnd = viewModel.IndexOf("public async Task ChangeRoleAsync", renameStart, StringComparison.Ordinal);
    Assert(renameStart >= 0 && renameEnd > renameStart, "the persisted device rename operation is missing");
    var rename = viewModel[renameStart..renameEnd];
    Assert(rename.Contains("registered.Name = normalized", StringComparison.Ordinal)
           && rename.Contains("await _state.SaveAsync(cancellationToken)", StringComparison.Ordinal)
           && rename.Contains("ReplaceDevices(Config.Devices)", StringComparison.Ordinal),
        "device rename must update, persist, and refresh the primary registry");
}

static void TestExitNodeApprovalRoutes()
{
    Assert(HeadscaleRoutes.ExitNode.Count == 2, "exit-node approval must contain exactly two default routes");
    Assert(HeadscaleRoutes.ExitNode.Contains("0.0.0.0/0", StringComparer.Ordinal), "IPv4 default route is missing");
    Assert(HeadscaleRoutes.ExitNode.Contains("::/0", StringComparer.Ordinal), "IPv6 default route is missing");
    Assert(!HeadscaleRoutes.ExitNode.Any(string.IsNullOrWhiteSpace), "exit-node approval contains an empty route");
}

static void TestDirectHttpTransport()
{
    using var handler = DirectHttp.CreateHandler();
    Assert(!handler.UseProxy, "private HTTP transport inherited the system proxy");
    Assert(!handler.AllowAutoRedirect, "private HTTP transport follows redirects");
}

static void TestEnrollmentReplayPolicy()
{
    var secret = SecurityHelpers.CreateToken();
    var device = new DeviceRecord
    {
        TailnetDeviceId = "node-42",
        TailscaleIp = "100.90.0.42",
        HostName = "WORKSHOP",
        DnsName = "workshop.example.ts.net",
        OperatingSystem = "Windows",
        AgentVersion = "1.2.3"
    };
    var invite = new InviteRecord
    {
        Id = Guid.NewGuid(),
        InviteSecretHash = SecurityHelpers.HashToken(secret),
        RedeemedAt = DateTimeOffset.UtcNow,
        EnrolledDeviceId = device.Id
    };
    var request = new EnrollmentRequest
    {
        InviteId = invite.Id,
        InviteSecret = secret,
        TailnetDeviceId = device.TailnetDeviceId,
        TailscaleIp = device.TailscaleIp,
        HostName = device.HostName,
        DnsName = device.DnsName,
        OperatingSystem = device.OperatingSystem,
        AgentVersion = device.AgentVersion
    };

    Assert(EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, device, request), "the exact committed retry was rejected");
    request.TailscaleIp = "100.90.0.43";
    Assert(!EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, device, request), "a different Tailscale address was accepted");
    request.TailscaleIp = device.TailscaleIp;
    request.InviteSecret = SecurityHelpers.CreateToken();
    Assert(!EnrollmentReplayPolicy.IsExactAcceptedReplay(invite, device, request), "a different invitation secret was accepted");
}

static void TestCredentialRotationState()
{
    var oldToken = SecurityHelpers.CreateToken();
    var newToken = SecurityHelpers.CreateToken();
    var password = SecurityHelpers.CreateHumanPassword();
    var operationId = Guid.NewGuid();
    var started = DateTimeOffset.UtcNow;
    var config = new AgentConfig { AgentTokenHash = SecurityHelpers.HashToken(oldToken) };

    CredentialRotationState.Begin(config, operationId, newToken, password, started);
    Assert(CredentialRotationState.IsExactAppliedRotation(config, operationId, newToken, password), "the applied operation was not replayable");
    Assert(CredentialRotationState.CanAuthenticate(config, newToken, false, started), "the new token was not active");
    Assert(CredentialRotationState.CanAuthenticate(config, oldToken, true, started), "the prior token could not retry the exact rotation");
    Assert(!CredentialRotationState.CanAuthenticate(config, oldToken, false, started), "the prior token retained general API access");
    Assert(!CredentialRotationState.CanAuthenticate(
            config, oldToken, true, started.Add(CredentialRotationState.PreviousTokenGracePeriod).AddSeconds(1)),
        "the prior token survived its bounded replay window");
    Assert(!CredentialRotationState.IsExactAppliedRotation(config, operationId, newToken, password + "x"), "a changed retry payload was accepted");

    var durable = JsonSerializer.Deserialize<AgentConfig>(
        JsonSerializer.Serialize(config, JsonDefaults.Options), JsonDefaults.Options)
        ?? throw new InvalidDataException("credential rotation state did not deserialize");
    Assert(CredentialRotationState.IsExactAppliedRotation(durable, operationId, newToken, password), "durable pending rotation was not recoverable");
    CredentialRotationState.Commit(durable, operationId);
    CredentialRotationState.Commit(durable, operationId);
    Assert(!CredentialRotationState.CanAuthenticate(durable, oldToken, true, started), "the prior token survived commit");
    Assert(CredentialRotationState.CanAuthenticate(durable, newToken, false, started), "commit retired the new token");
}

static void TestDurableCollectionMutation()
{
    var values = new List<string>();
    using var gate = new SemaphoreSlim(1, 1);
    AssertThrows<InvalidOperationException>(() => DurableCollectionMutation.AddAsync(
        values, "ghost", gate, _ => throw new InvalidOperationException("simulated persistence failure")).GetAwaiter().GetResult());
    Assert(values.Count == 0, "a failed persistence operation left a ghost record in memory");
    DurableCollectionMutation.AddAsync(values, "durable", gate, _ => Task.CompletedTask).GetAwaiter().GetResult();
    Assert(values.SequenceEqual(["durable"]), "a successful durable collection mutation was lost");
}

static void TestPathLease()
{
    if (!OperatingSystem.IsWindows()) return;
    var temporary = Path.Combine(Path.GetTempPath(), "taildesk-path-lease-" + Guid.NewGuid().ToString("N"));
    var child = Path.Combine(temporary, "child");
    var moved = Path.Combine(temporary, "moved");
    Directory.CreateDirectory(child);
    File.WriteAllText(Path.Combine(child, "proof.txt"), "guarded");
    try
    {
        var guard = new PathGuard(new Dictionary<string, string> { ["test"] = temporary });
        using (var lease = guard.Acquire("test", "child"))
        {
            Assert(lease.IsDirectory, "directory lease did not identify its target");
            Directory.Move(child, moved);
            try { Directory.CreateDirectory(child); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            using var created = lease.CreateFile("leased.txt");
            using (var writer = new StreamWriter(created.OpenWriteStream(), leaveOpen: false))
                writer.Write("original-directory");
            created.RenameTo(lease, "promoted.txt");
            Assert(File.Exists(Path.Combine(moved, "promoted.txt")), "relative create or rename escaped to the replacement pathname");
            Assert(!File.Exists(Path.Combine(child, "promoted.txt")), "relative create or rename used the replacement pathname");
            Assert(lease.Enumerate().Any(entry => entry.Name == "promoted.txt" && entry.Size > 0),
                "handle-based enumeration did not observe the promoted file");

            using (var partial = lease.OpenOrCreateFile(".taildesk-upload-test.partial"))
            {
                using var output = partial.OpenWriteStream();
                output.Write("first"u8);
            }
            using (var resumed = lease.OpenOrCreateFile(".taildesk-upload-test.partial"))
            {
                Assert(resumed.Length == 5, "guarded resumable file lost its retained byte offset");
                using (var output = resumed.OpenWriteStream())
                {
                    output.Position = resumed.Length;
                    output.Write("-second"u8);
                }
                resumed.RenameTo(lease, "resumed.txt");
            }
            Assert(File.ReadAllText(Path.Combine(moved, "resumed.txt")) == "first-second",
                "guarded resumable append or promotion changed the payload");
        }
        if (Directory.Exists(child)) Directory.Delete(child, true);
        Directory.Move(moved, child);

        var readParentMoved = false;
        using (var stream = guard.Acquire("test", "child\\proof.txt", readFile: true).OpenReadStream())
        {
            try { Directory.Move(child, moved); readParentMoved = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (readParentMoved)
            {
                try
                {
                    Directory.CreateDirectory(child);
                    File.WriteAllText(Path.Combine(child, "proof.txt"), "replacement");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert(reader.ReadToEnd() == "guarded", "guarded read returned the wrong file");
        }
        if (readParentMoved)
        {
            if (Directory.Exists(child)) Directory.Delete(child, true);
            Directory.Move(moved, child);
        }

        using (var deleteLease = guard.Acquire("test", "child\\proof.txt", delete: true))
            deleteLease.Delete();
        Assert(!File.Exists(Path.Combine(child, "proof.txt")), "handle-based deletion did not remove the verified file");
    }
    finally
    {
        try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
    }
}

static void TestDpapi()
{
    if (!OperatingSystem.IsWindows()) return;
    var secret = SecurityHelpers.CreateToken();
    var user = SecretProtector.Protect(secret, SecretScope.CurrentUser);
    var machine = SecretProtector.Protect(secret, SecretScope.LocalMachine);
    Assert(SecretProtector.Unprotect(user, SecretScope.CurrentUser) == secret, "current-user DPAPI failed");
    Assert(SecretProtector.Unprotect(machine, SecretScope.LocalMachine) == secret, "machine DPAPI failed");
}

static void TestWpfStyleTemplateTargets()
{
    string? xamlPath = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Taildesk.Admin", "App.xaml");
            if (File.Exists(candidate))
            {
                xamlPath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        if (xamlPath is not null) break;
    }
    if (xamlPath is null) throw new InvalidOperationException("Taildesk.Admin App.xaml was not found.");
    var document = XDocument.Load(xamlPath);
    foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "Style"))
    {
        var styleTarget = NormalizeTargetType(style.Attribute("TargetType")?.Value ?? string.Empty);
        if (styleTarget.Length == 0) continue;
        foreach (var template in style.Descendants().Where(element => element.Name.LocalName == "ControlTemplate"))
        {
            var templateTarget = NormalizeTargetType(template.Attribute("TargetType")?.Value ?? string.Empty);
            if (templateTarget.Length == 0) continue;
            Assert(templateTarget == styleTarget, $"{styleTarget} style contains a {templateTarget} control template");
        }
    }
}

static void TestWpfContrastContract()
{
    var adminPath = FindSourceFile("src", "Taildesk.Admin", "App.xaml");
    var opticonRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(adminPath)!, "..", ".."));
    var setupPath = FindSourceFile("src", "Taildesk.Setup", "App.xaml");
    var admin = XDocument.Load(adminPath);
    var setup = XDocument.Load(setupPath);
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

    static string TargetType(XElement style) =>
        NormalizeTargetType(style.Attribute("TargetType")?.Value ?? string.Empty);
    static bool HasDirectSetter(XElement style, string property) =>
        style.Elements().Any(element => element.Name.LocalName == "Setter"
                                        && element.Attribute("Property")?.Value == property);
    static XElement RequireImplicitStyle(XDocument document, XNamespace xaml, string targetType)
    {
        var style = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Style"
            && TargetType(element) == targetType
            && element.Attribute(xaml + "Key") is null);
        return style ?? throw new InvalidOperationException($"{targetType} has no implicit application style");
    }
    static void RequireColorPair(XElement style, string label)
    {
        Assert(HasDirectSetter(style, "Background") && HasDirectSetter(style, "Foreground"),
            $"{label} must set both Background and Foreground");
    }
    static void RequireTriggerColorPair(XElement style, string property, string value, string label)
    {
        var trigger = style.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Trigger"
            && element.Attribute("Property")?.Value == property
            && element.Attribute("Value")?.Value == value);
        Assert(trigger is not null
               && HasDirectSetter(trigger, "Background")
               && HasDirectSetter(trigger, "Foreground"),
            $"{label} must replace both Background and Foreground");
    }

    var adminText = File.ReadAllText(adminPath);
    var setupText = File.ReadAllText(setupPath);
    Assert(!adminText.Contains("SystemColors", StringComparison.Ordinal)
           && !setupText.Contains("SystemColors", StringComparison.Ordinal),
        "fixed dark WPF canvases must not consume independently changing Windows theme colors");

    foreach (var targetType in new[]
             {
                 "Button", "TextBox", "PasswordBox", "ComboBox", "CheckBox", "ListBox",
                 "ListBoxItem", "ContextMenu", "MenuItem", "DataGrid", "DataGridColumnHeader",
             })
    {
        var style = RequireImplicitStyle(admin, x, targetType);
        RequireColorPair(style, targetType);
    }
    foreach (var targetType in new[] { "DataGridCell", "DataGridRow" })
    {
        Assert(HasDirectSetter(RequireImplicitStyle(admin, x, targetType), "Foreground"),
            $"{targetType} must set Foreground; its background is supplied by DataGrid row/alternation states");
    }

    foreach (var templatedType in new[] { "Button", "ComboBox", "CheckBox" })
    {
        var style = RequireImplicitStyle(admin, x, templatedType);
        Assert(style.Descendants().Any(element =>
                element.Name.LocalName == "ControlTemplate" && TargetType(element) == templatedType),
            $"{templatedType} must own its template instead of inheriting Windows theme text colors");
    }

    var primaryButton = admin.Descendants().FirstOrDefault(element =>
        element.Name.LocalName == "Style" && element.Attribute(x + "Key")?.Value == "PrimaryButton")
        ?? throw new InvalidOperationException("PrimaryButton style was not found");
    RequireTriggerColorPair(primaryButton, "IsMouseOver", "True", "PrimaryButton hover");
    RequireTriggerColorPair(primaryButton, "IsPressed", "True", "PrimaryButton pressed");
    RequireTriggerColorPair(primaryButton, "IsEnabled", "False", "PrimaryButton disabled");

    var pairedStates = new[]
    {
        (Type: "ListBoxItem", Property: "IsMouseOver", Value: "True"),
        (Type: "ListBoxItem", Property: "IsSelected", Value: "True"),
        (Type: "MenuItem", Property: "IsHighlighted", Value: "True"),
        (Type: "DataGridColumnHeader", Property: "IsMouseOver", Value: "True"),
        (Type: "DataGridColumnHeader", Property: "IsPressed", Value: "True"),
        (Type: "DataGridCell", Property: "IsSelected", Value: "True"),
        (Type: "DataGridRow", Property: "IsSelected", Value: "True")
    };
    foreach (var state in pairedStates)
        RequireTriggerColorPair(RequireImplicitStyle(admin, x, state.Type), state.Property, state.Value,
            $"{state.Type} {state.Property}");

    var setupButton = RequireImplicitStyle(setup, x, "Button");
    RequireColorPair(setupButton, "Setup Button");
    Assert(setupButton.Descendants().Any(element =>
            element.Name.LocalName == "ContentPresenter"
            && element.Attributes().Any(attribute => attribute.Name.LocalName is "Foreground" or "TextElement.Foreground")),
        "Setup button content must bind the explicit foreground into its template");
    RequireTriggerColorPair(setupButton, "IsMouseOver", "True", "Setup button hover");
    RequireTriggerColorPair(setupButton, "IsPressed", "True", "Setup button pressed");

    var audit = Taildesk.SelfTest.WpfContrastAudit.Verify(opticonRoot);
    Console.WriteLine($"      audited {audit.TextSurfaceCount} text surfaces and "
                      + $"{audit.ControlStateCount} control states across {audit.ViewCount} WPF views");
}

static string FindSourceFile(params string[] parts)
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
    }
    throw new InvalidOperationException($"Source file was not found: {Path.Combine(parts)}");
}

static void TestOpenSshRecoveryDesign()
{
    DirectoryInfo? root = new(AppContext.BaseDirectory);
    while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Taildesk.Agent")))
        root = root.Parent;
    if (root is null) throw new InvalidOperationException("Opticon source root was not found.");

    string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    var launcher = ReadSource("src", "Taildesk.Admin", "SshSessionLauncher.cs");
    Assert(launcher.Contains("System32", StringComparison.Ordinal)
           && launcher.Contains("OpenSSH", StringComparison.Ordinal),
        "SSH launcher must resolve the Windows System32 OpenSSH client");
    Assert(!launcher.Contains("FindOnPath", StringComparison.Ordinal),
        "SSH launcher must not execute a PATH-resolved client");
    Assert(launcher.Contains("WorkingDirectory = Path.GetDirectoryName(privateKeyPath)", StringComparison.Ordinal),
        "interactive SSH must not inherit and lock Opticon's installed command-center directory");
    Assert(launcher.Contains("new LoopbackSshRelay(grant.Host, DedicatedPort)", StringComparison.Ordinal)
           && launcher.Contains("new TcpListener(IPAddress.Loopback, 0)", StringComparison.Ordinal)
           && launcher.Contains("target.ConnectAsync(_targetHost, _targetPort", StringComparison.Ordinal)
           && launcher.Contains("connectionHost ?? grant.Host", StringComparison.Ordinal),
        "SSH must traverse a per-lease loopback relay so endpoint VPN policy cannot block the hardened child client from the Tailscale peer");
    var updateCoordinator = ReadSource("src", "Taildesk.Admin", "RemoteDeviceUpdateCoordinator.cs");
    Assert(updateCoordinator.Contains("GetUpdateStatusAsync(device, agentToken", StringComparison.Ordinal)
           && updateCoordinator.Contains("Update failed safely:", StringComparison.Ordinal),
        "guarded updates must surface the remote journal while preparation is running and after a safe failure");
    var mainWindow = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml.cs");
    Assert(mainWindow.Contains("if (release.RequiresCleanReinstall)", StringComparison.Ordinal)
           && mainWindow.Contains("No remote candidate was staged or activated", StringComparison.Ordinal)
           && mainWindow.Contains("Opticon 1.1.38 cannot receive a remote source stage", StringComparison.Ordinal)
           && mainWindow.Contains("Guarded source-built Opticon update", StringComparison.Ordinal)
           && !mainWindow.Contains("requiresAttendedMaintenance", StringComparison.Ordinal)
           && !mainWindow.Contains("await RunMaintenanceBootstrapAsync(", StringComparison.Ordinal),
        "legacy devices must fail closed to clean uninstall/re-enrollment while supported devices use only the source-built update path");
    var adminApp = ReadSource("src", "Taildesk.Admin", "App.xaml.cs");
    var commandCenterInstallerSource = ReadSource("src", "Taildesk.CommandCenterInstaller", "Program.cs");
    var incrementalRebuild = File.ReadAllText(Path.Combine(root.FullName, "..", "Taildesk", "rebuild-if-source-changed.ps1"));
    var sourceLauncher = File.ReadAllText(Path.Combine(root.FullName, "..", "Taildesk", "start.bat"));
    var startupHelper = File.ReadAllText(Path.Combine(root.FullName, "..", "Taildesk", "launch-opticon.ps1"));
    Assert(adminApp.Contains("Taildesk.Admin.ShutdownForUpdate", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Request-InstalledOpticonShutdown", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Taildesk.Admin.ShutdownForUpdate", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Get-InstalledOpticonProcesses", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Taildesk.OpticonCli", StringComparison.Ordinal)
           && incrementalRebuild.Contains("TotalSeconds -ge 2", StringComparison.Ordinal)
           && startupHelper.Contains("Local\\Taildesk.Opticon.Startup", StringComparison.Ordinal)
           && startupHelper.Contains("WaitOne(0)", StringComparison.Ordinal),
        "source-triggered controller rebuilds must serialize startup, gracefully close UI and CLI processes, and require a quiet window before swapping the installed payload");
    Assert(commandCenterInstallerSource.Contains("RequestInstalledControllerShutdownAsync", StringComparison.Ordinal)
           && commandCenterInstallerSource.Contains("Taildesk.Admin.ShutdownForUpdate", StringComparison.Ordinal)
           && commandCenterInstallerSource.Contains("Taildesk.OpticonCli", StringComparison.Ordinal)
           && commandCenterInstallerSource.Contains("TimeSpan.FromSeconds(2)", StringComparison.Ordinal),
        "the signed command-center installer must independently require a quiet UI and CLI window before its protected swap");
    Assert(incrementalRebuild.Contains("Get-PowerShell7Path", StringComparison.Ordinal)
           && incrementalRebuild.Contains("PowerShell\\7\\pwsh.exe", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Opticon\\Tools\\PowerShell-7.6.4\\pwsh.exe", StringComparison.Ordinal)
           && incrementalRebuild.Contains("$PSVersionTable.PSVersion.Major", StringComparison.Ordinal)
           && incrementalRebuild.Contains("-BuildProfile OwnerManaged", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Opticon-CommandCenter-OWNER-MANAGED-win-x64.zip", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Assert-OwnerManagedInstaller", StringComparison.Ordinal)
           && incrementalRebuild.Contains("Install-Opticon.exe", StringComparison.Ordinal)
           && incrementalRebuild.Contains("--controller-only-repair", StringComparison.Ordinal)
           && !incrementalRebuild.Contains("Install-Opticon.ps1", StringComparison.Ordinal)
           && !incrementalRebuild.Contains("ExecutionPolicy', 'Bypass", StringComparison.Ordinal)
           && sourceLauncher.Contains("ExecutionPolicy RemoteSigned", StringComparison.Ordinal)
           && !sourceLauncher.Contains("ExecutionPolicy Bypass", StringComparison.Ordinal),
        "source-triggered controller rebuilds must use PowerShell 7 and the signed OwnerManaged package without a loose elevated script");
    var agentUpdateDownload = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    Assert(agentUpdateDownload.Contains("UseProxy = false", StringComparison.Ordinal)
           && agentUpdateDownload.Contains("Last error:", StringComparison.Ordinal),
        "Agent artifact downloads must bypass ambient proxies and preserve bounded network diagnostics");
    var agentDownloadComplete = agentUpdateDownload.IndexOf("offset == expectedSize", StringComparison.Ordinal);
    var agentDownloadRange = agentUpdateDownload.IndexOf("new RangeHeaderValue(offset", StringComparison.Ordinal);
    var agentDownloadFlush = agentUpdateDownload.IndexOf("await output.FlushAsync", StringComparison.Ordinal);
    var agentDownloadMove = agentUpdateDownload.IndexOf("File.Move(partial, destination", agentDownloadFlush, StringComparison.Ordinal);
    var agentDownloadScopeEnd = agentUpdateDownload.LastIndexOf('}', agentDownloadMove);
    Assert(agentDownloadComplete >= 0 && agentDownloadComplete < agentDownloadRange
           && agentUpdateDownload.Contains("RequestedRangeNotSatisfiable", StringComparison.Ordinal)
           && agentDownloadFlush >= 0 && agentDownloadScopeEnd > agentDownloadFlush
           && agentDownloadMove > agentDownloadScopeEnd,
        "Agent resume must promote a complete partial without an EOF range and dispose its stream before the atomic move");
    var administratorProofIndex = launcher.IndexOf("await VerifyRemoteAdministratorAsync", StringComparison.Ordinal);
    var requestedCommandIndex = launcher.IndexOf("var remoteCommand = options.PowerShellEncodedCommand", StringComparison.Ordinal);
    Assert(administratorProofIndex >= 0 && requestedCommandIndex > administratorProofIndex
           && launcher.Contains("attestation.AdministrativeCapability", StringComparison.Ordinal)
           && launcher.Contains("attestation.IntegrityRid is < 0x3000 or >= 0x4000", StringComparison.Ordinal),
        "every SSH shell or command must pass the signed full-administrator attestation before launch");
    Assert(launcher.Contains("AddressFamily.InterNetwork", StringComparison.Ordinal),
        "SSH launcher must reject IPv6 before comparing target addresses");
    var validationIndex = launcher.IndexOf("ValidateCommandOptions(options)", StringComparison.Ordinal);
    var staleCleanupIndex = launcher.IndexOf("CleanupStaleSessionDirectories()", StringComparison.Ordinal);
    Assert(validationIndex >= 0 && staleCleanupIndex > validationIndex
           && launcher.Contains("options.RemoteCommand.Length > MaximumRemoteCommandCharacters", StringComparison.Ordinal)
           && launcher.Contains("MaximumEncodedPowerShellCharacters = 5600", StringComparison.Ordinal)
           && launcher.Contains("options.RemoteCommand.Contains('\\0')", StringComparison.Ordinal),
        "raw and encoded SSH commands must be bounded and reject NUL before key creation or remote provisioning");

    var agentClient = ReadSource("src", "Taildesk.Admin", "AgentClient.cs");
    Assert(agentClient.Contains("UseProxy = false", StringComparison.Ordinal)
           && agentClient.Contains("AllowAutoRedirect = false", StringComparison.Ordinal),
        "authenticated Agent requests must bypass proxies and refuse redirects");
    var downloadFlush = agentClient.IndexOf("await output.FlushAsync", StringComparison.Ordinal);
    var downloadStreamScopeEnd = agentClient.IndexOf("if (expectedLength.HasValue", StringComparison.Ordinal);
    var downloadPromote = agentClient.IndexOf("target.Promote(overwrite)", StringComparison.Ordinal);
    var guardedTransfer = ReadSource("src", "Taildesk.Admin", "GuardedLocalTransferFile.cs");
    var guardedCreate = guardedTransfer.IndexOf("directory.CreateFile(temporaryName)", StringComparison.Ordinal);
    var guardedRename = guardedTransfer.IndexOf("_temporary.RenameTo(_directory, _fileName)", StringComparison.Ordinal);
    Assert(downloadFlush >= 0
           && downloadStreamScopeEnd > downloadFlush
           && downloadPromote > downloadStreamScopeEnd
           && guardedCreate >= 0
           && guardedRename > guardedCreate,
        "downloads must flush and dispose their guarded temporary handle before handle-relative promotion");

    var cli = ReadSource("src", "Taildesk.Cli", "Program.cs");
    Assert(cli.Contains("Volatile.Read(ref interactiveSshAttached)", StringComparison.Ordinal)
           && cli.Contains("_setInteractiveSshAttached(true)", StringComparison.Ordinal)
           && cli.Contains("_setInteractiveSshAttached(false)", StringComparison.Ordinal),
        "the CLI must cancel preflight but deliver Ctrl+C to an attached interactive ssh.exe");

    var systemHealth = ReadSource("src", "Taildesk.Admin", "SystemHealthChecker.cs");
    var nordPowerShell = ReadSource("scripts", "Configure-NordTailscaleSplit.ps1");
    var nordPython = ReadSource("scripts", "Configure-NordTailscaleSplit.py");
    Assert(systemHealth.Contains("\"Admin\", \"Cli\", \"opticon.exe\"", StringComparison.Ordinal)
           && systemHealth.Contains("\"System32\", \"OpenSSH\", \"ssh.exe\"", StringComparison.Ordinal)
           && nordPowerShell.Contains(@"Admin\Cli\opticon.exe", StringComparison.Ordinal)
           && nordPowerShell.Contains(@"System32\OpenSSH\ssh.exe", StringComparison.Ordinal)
           && nordPython.Contains(@"Admin\Cli\opticon.exe", StringComparison.Ordinal)
           && nordPython.Contains("\"System32\", \"OpenSSH\", \"ssh.exe\"", StringComparison.Ordinal),
        "NordVPN split tunneling and drift checks must include the Opticon CLI and exact Windows OpenSSH client");
    Assert(systemHealth.Contains("DirectHttp.CreateClient", StringComparison.Ordinal)
           && systemHealth.Contains("ResponseHeadersRead", StringComparison.Ordinal)
           && systemHealth.Contains("RequireSystemExecutable", StringComparison.Ordinal)
           && systemHealth.Contains("clearEnvironment: true", StringComparison.Ordinal)
           && systemHealth.Contains("VerifyVendorExecutableAsync", StringComparison.Ordinal)
           && systemHealth.Contains("ProductSigning.VerifyAuthenticodeAsync(snapshot.RouteHelperPath", StringComparison.Ordinal)
           && systemHealth.Contains("IsExactRouteTask", StringComparison.Ordinal)
           && !systemHealth.Contains("Set-TaildeskFlyBypassRoute.ps1", StringComparison.Ordinal)
           && !systemHealth.Contains("RouteTaskProtected", StringComparison.Ordinal)
           && !systemHealth.Contains("WScript.Shell", StringComparison.Ordinal)
           && !systemHealth.Contains("new HttpClient", StringComparison.Ordinal),
        "health diagnostics must use direct bounded HTTP, fixed sanitized tools, exact signed RouteKeeper state, and no elevated-profile legacy helpers");

    var manager = ReadSource("src", "Taildesk.Agent", "SshAccessManager.cs");
    foreach (var unsupported in new[]
             {
                 "\"PidFile ", "\"KbdInteractiveAuthentication ", "\"StrictModes ",
                 "\"X11Forwarding ", "\"PermitTunnel ", "\"PermitUserEnvironment ", "\"PermitUserRC "
             })
        Assert(!manager.Contains(unsupported, StringComparison.Ordinal), $"sshd_config contains unsupported {unsupported}");
    Assert(manager.Contains("AccountName.ToLowerInvariant()", StringComparison.Ordinal),
        "Windows AllowUsers account must be lowercase");
    Assert(manager.Contains("AuthorizedKeysFile \\\"", StringComparison.Ordinal)
           && !manager.Contains("Match Group", StringComparison.OrdinalIgnoreCase)
           && !manager.Contains("administrators_authorized_keys", StringComparison.OrdinalIgnoreCase),
        "isolated sshd must use its global absolute authorized_keys file without the stock Administrators Match override");
    Assert(manager.Contains("*S-1-5-18:F", StringComparison.Ordinal)
           && manager.Contains("*S-1-5-32-544:F", StringComparison.Ordinal),
        "administrator authorized_keys ACL must allow only SYSTEM and built-in Administrators");
    Assert(manager.Contains("RestrictDaemonReadablePathAsync", StringComparison.Ordinal)
           && manager.Contains("/remove:g", StringComparison.Ordinal),
        "Agent SSH preflight must remove legacy named daemon ACEs from host-key inputs");
    Assert(manager.Contains("RequireSystemOpenSshExecutable", StringComparison.Ordinal)
           && !manager.Contains("FindOnPath", StringComparison.Ordinal),
        "SYSTEM SSH binaries must use exact System32 paths");
    Assert(manager.Contains("_schtasksPath", StringComparison.Ordinal)
           && manager.Contains("_netshPath", StringComparison.Ordinal)
           && manager.Contains("_icaclsPath", StringComparison.Ordinal)
           && manager.Contains("NetLocalGroupDelMembers", StringComparison.Ordinal)
           && manager.Contains("ErrorMemberNotInAlias", StringComparison.Ordinal),
        "SYSTEM helpers must use exact paths and the idle SSH account must leave Administrators");
    Assert(manager.Contains("ReadSupervisorFailureAsync", StringComparison.Ordinal)
           && manager.Contains("File.Delete(_failurePath)", StringComparison.Ordinal)
           && manager.Contains("could not start:", StringComparison.Ordinal),
        "Agent SSH provisioning must clear stale diagnostics and surface a new supervisor failure immediately");
    var agentApiProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    Assert(agentApiProgram.Contains("catch (System.ComponentModel.Win32Exception", StringComparison.Ordinal)
           && agentApiProgram.Contains("catch (AggregateException", StringComparison.Ordinal)
           && agentApiProgram.Contains("Unexpected Agent failure", StringComparison.Ordinal),
        "the Agent API must serialize bounded detail for every SSH/Windows failure class");

    var setup = ReadSource("src", "Taildesk.Setup", "InstallerServices.cs");
    Assert(setup.Contains("OpenSSH.Server~~~~0.0.1.0", StringComparison.Ordinal),
        "Setup must preinstall OpenSSH Server while normal control is healthy");
    Assert(setup.Contains("OpenSSH.Client~~~~0.0.1.0", StringComparison.Ordinal),
        "controller-capable Setup must preinstall OpenSSH Client");
    Assert(setup.Contains("internal static async Task EnsureOpenSshServerCapabilityAsync", StringComparison.Ordinal)
           && setup.Contains("internal static async Task EnsureOpenSshClientCapabilityAsync", StringComparison.Ordinal),
        "maintenance mode must be able to invoke both idempotent OpenSSH preflights");

    var adminXaml = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml");
    var remoteButton = adminXaml.IndexOf("Content=\"Remote into\"", StringComparison.Ordinal);
    var sshButton = adminXaml.IndexOf("Content=\"Open SSH\"", StringComparison.Ordinal);
    var browseButton = adminXaml.IndexOf("Content=\"Browse files\"", StringComparison.Ordinal);
    Assert(remoteButton >= 0 && remoteButton < sshButton && sshButton < browseButton,
        "Open SSH must be immediately next to Remote into on the Devices page");

    var adminWindow = ReadSource("src", "Taildesk.Admin", "MainWindow.xaml.cs");
    Assert(adminWindow.Contains("OpenSsh_Click", StringComparison.Ordinal)
           && adminWindow.Contains("_viewModel.LaunchSshAsync(device)", StringComparison.Ordinal),
        "the Devices-page Open SSH button must invoke the selected-device launcher");

    Assert(setup.Contains("RemoteAdministrationProtocol.GuardianWatchdogTaskName", StringComparison.Ordinal)
           && setup.Contains("RemoteAdministrationProtocol.GuardianWatchdogArgument", StringComparison.Ordinal)
           && setup.Contains("RemoteAdministrationProtocol.MinimumWatchdogGuardianVersion", StringComparison.Ordinal)
           && setup.Contains("\"MINUTE\", \"/MO\", \"1\"", StringComparison.Ordinal)
           && setup.Contains("RequireInstalledGuardianWatchdogCompatibilityAsync", StringComparison.Ordinal),
        "fresh Setup must prove Guardian compatibility and install the minute watchdog before enrollment completes");
    var setupGuardianPreflight = setup.IndexOf("await InstallGuardianAsync(guardianPayload", StringComparison.Ordinal);
    Assert(setupGuardianPreflight >= 0
           && setupGuardianPreflight < setup.IndexOf("await EnsureOpenSshServerCapabilityAsync", StringComparison.Ordinal),
        "fresh Setup must prove Guardian compatibility before changing recovery or network state");
    Assert(setup.Contains("SupportsGuardianWatchdog(installedVersion)", StringComparison.Ordinal)
           && !setup.Contains("if (installedVersion < sourceVersion)", StringComparison.Ordinal),
        "fresh Setup must verify the installed Guardian against the watchdog contract after attended maintenance");
    var stableGuardianMaintenance = ReadSource("src", "Taildesk.Shared", "StableGuardianMaintenance.cs");
    Assert(stableGuardianMaintenance.Contains("UpdateJournalCoordination.AcquireAsync", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("ProductSigning.VerifyAuthenticodeAsync", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("File.Replace(staged, installed, backup", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("File.Replace(backup, installedExecutable, failed", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("GuardianWatchdogArgument", StringComparison.Ordinal)
           && stableGuardianMaintenance.IndexOf("GuardianWatchdogArgument", StringComparison.Ordinal)
              < stableGuardianMaintenance.IndexOf("DeleteWithRetryAsync(backup", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("RequireRecognizedInstalledFiles", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("Guid.TryParseExact", StringComparison.Ordinal)
           && stableGuardianMaintenance.Contains("FilesMatchAsync", StringComparison.Ordinal),
        "attended Setup must atomically reconcile and roll back a signed stable Guardian while cleaning only recognized transaction residue");
    var setupWatchdogSettings = setup[setup.IndexOf("var watchdogSettings", StringComparison.Ordinal)..setup.IndexOf("var guardianTaskSettings", StringComparison.Ordinal)];
    Assert(!setupWatchdogSettings.Contains("StartWhenAvailable", StringComparison.Ordinal),
        "the recurring watchdog must not queue missed StartWhenAvailable runs");

    var maintenance = ReadSource("src", "Taildesk.Setup", "MaintenanceBootstrapCoordinator.cs");
    Assert(maintenance.Contains("target == current", StringComparison.Ordinal)
           && maintenance.Contains("Maintenance requires a newer Agent or Guardian", StringComparison.Ordinal),
        "attended maintenance must permit a same-release Agent transaction only to repair an older Guardian");
    Assert(maintenance.Contains("Environment.ProcessPath", StringComparison.Ordinal)
           && maintenance.Contains("VerifyAuthenticodeAsync(setupExecutable", StringComparison.Ordinal)
           && maintenance.Contains("setupVersion.Equals", StringComparison.Ordinal),
        "maintenance must pin its running Setup and bind it to the signed release version");
    Assert(maintenance.Contains("MaintenanceExpectedTarget", StringComparison.Ordinal)
           && maintenance.Contains("[\"status\", \"--json\"]", StringComparison.Ordinal)
           && maintenance.Contains("Environment.SpecialFolder.ProgramFiles", StringComparison.Ordinal)
           && maintenance.Contains("expected.TailnetDeviceId", StringComparison.Ordinal)
           && maintenance.Contains("expected.TailscaleIp", StringComparison.Ordinal),
        "maintenance must bind to the copied Tailnet node and exact Tailscale IPv4 before mutation");
    Assert(maintenance.Contains("MaintenanceBootstrap = true", StringComparison.Ordinal)
           && maintenance.Contains("UpdateJournalCoordination.AcquireAsync", StringComparison.Ordinal),
        "legacy preflight bypass must be local-only and journal replacement must be coordinated");
    Assert(maintenance.Contains("EnsureRecoveryLifelines(config.BindAddress)", StringComparison.Ordinal)
           && maintenance.Contains("EnsureOpenSshServerCapabilityAsync", StringComparison.Ordinal),
        "maintenance must establish recovery lifelines before activation");
    Assert(maintenance.Contains("LoadOrCreateSidecarAsync", StringComparison.Ordinal)
           && maintenance.Contains("RequireInstalledGuardianCompatibilityAsync", StringComparison.Ordinal)
           && maintenance.Contains("Directory.EnumerateFiles(installedRoot", StringComparison.Ordinal)
           && maintenance.Contains("declaration.Sha256", StringComparison.Ordinal)
           && !maintenance.Contains("configStore.SaveAsync", StringComparison.Ordinal)
           && !maintenance.Contains("SaveAsync(config", StringComparison.Ordinal),
        "maintenance must use the protected update-health sidecar without rewriting agent.json");
    Assert(maintenance.Contains("Guid.TryParseExact(operationText, \"N\"", StringComparison.Ordinal)
           && maintenance.Contains("var operationId = _expectedTarget.OperationId", StringComparison.Ordinal),
        "Setup must strictly parse and journal the exact command-center operation ID");
    Assert(maintenance.Contains("Replacement Agent protected health sample", StringComparison.Ordinal)
           && maintenance.Contains("ObserveCandidateAndWaitForExternalCommitAsync", StringComparison.Ordinal)
           && !maintenance.Contains("UpdateJournalPersistence.RequestCommitAsync", StringComparison.Ordinal),
        "Setup must keep three protected local samples but have no maintenance commit authority");
    Assert(maintenance.Contains("RemoteAdministrationProtocol.GuardianWatchdogTaskName", StringComparison.Ordinal)
           && maintenance.Contains("RemoteAdministrationProtocol.GuardianWatchdogArgument", StringComparison.Ordinal)
           && maintenance.Contains("\"MINUTE\", \"/MO\", \"1\"", StringComparison.Ordinal),
        "legacy maintenance must install the minute Guardian watchdog before writing ActivationScheduled");
    var maintenanceWatchdogSettings = maintenance[maintenance.IndexOf("var watchdogSettings", StringComparison.Ordinal)..maintenance.IndexOf("var settingsCommand", StringComparison.Ordinal)];
    Assert(!maintenanceWatchdogSettings.Contains("StartWhenAvailable", StringComparison.Ordinal),
        "the maintenance watchdog must not queue missed StartWhenAvailable runs");

    var bundleBuilder = ReadSource("fly-headscale", "scripts", "Build-OpticonBundles.ps1");
    var releaseVersion = System.Text.RegularExpressions.Regex.Match(
        ReadSource("Directory.Build.props"), @"<Version>([^<]+)</Version>").Groups[1].Value;
    Assert(bundleBuilder.Contains($"[string]$Version = \"{releaseVersion}\"", StringComparison.Ordinal),
        "the checked-in hosted release default must match the product version");
    Assert(bundleBuilder.Contains("$setupPath", StringComparison.Ordinal)
           && bundleBuilder.Contains("Get-Item -LiteralPath $setupPath", StringComparison.Ordinal),
        "the signed inner release manifest must include the root Setup executable");
    Assert(bundleBuilder.Contains("[string]$MinimumGuardianVersion = \"1.1.2\"", StringComparison.Ordinal),
        "the hosted release must permit a watchdog-capable Guardian to install the Agent that performs signed Guardian reconciliation");
    var guardianUpdateManager = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    Assert(guardianUpdateManager.Contains("VerifyAndExtractGuardianAsync", StringComparison.Ordinal)
           && guardianUpdateManager.Contains("StableGuardianMaintenance.ReconcileSignedReleaseAsync", StringComparison.Ordinal)
           && guardianUpdateManager.Contains("GuardianWatchdogArgument", StringComparison.Ordinal)
           && guardianUpdateManager.Contains("Close the active Opticon SSH lease", StringComparison.Ordinal)
           && ReadSource("src", "Taildesk.Agent", "Program.cs").Contains("/api/v1/update/source/guardian", StringComparison.Ordinal)
           && updateCoordinator.Contains("ReconcileSourceGuardianAsync", StringComparison.Ordinal)
           && updateCoordinator.Contains("result.OperationId != sourceRequest.OperationId", StringComparison.Ordinal)
           && updateCoordinator.Contains("post-source-maintenance Agent sample", StringComparison.Ordinal),
        "source updates must reconcile the locally built Guardian only through the exact committed source operation and externally attest it without UAC");
    Assert(adminWindow.Contains("Copied PowerShell/UAC maintenance bootstraps are retired", StringComparison.Ordinal)
           && adminWindow.Contains("No command was copied or started", StringComparison.Ordinal)
           && !adminWindow.Contains("Expand-Archive", StringComparison.Ordinal)
           && !adminWindow.Contains("$env:TEMP", StringComparison.Ordinal)
           && !adminWindow.Contains("-Verb RunAs", StringComparison.Ordinal)
           && !adminWindow.Contains("InvitationSigning.PinnedCertificate", StringComparison.Ordinal),
        "the replace-after-verify maintenance bootstrap is still reachable through copied PowerShell or ordinary TEMP");
    var productSigning = ReadSource("src", "Taildesk.Shared", "ProductSigning.cs");
    var authenticode = ReadSource("src", "Taildesk.Shared", "BoundWindowsProductSignatureVerifier.cs");
    Assert(productSigning.Contains("BoundWindowsProductSignatureVerifier.VerifyPinnedAsync", StringComparison.Ordinal)
           && authenticode.Contains("GetSecondarySignatureCount", StringComparison.Ordinal)
           && authenticode.Contains("WTHelperGetProvCertFromChain", StringComparison.Ordinal)
           && authenticode.Contains("CryptVerifyTimeStampSignature", StringComparison.Ordinal)
           && authenticode.Contains("does not cover the primary Authenticode signature value", StringComparison.Ordinal),
        "runtime Authenticode checks are not bound to the exact Windows-validated signer and RFC 3161 token");
    Assert(adminWindow.Contains("if (release.RequiresMaintenanceBootstrap)", StringComparison.Ordinal)
           && adminWindow.Contains("Source-only remote updates do not use the retired maintenance bootstrap.", StringComparison.Ordinal)
           && !adminWindow.Contains("await RunMaintenanceBootstrapAsync(", StringComparison.Ordinal),
        "source-only update selection must fail closed instead of launching the retired maintenance flow");
    Assert(adminWindow.Contains("clipboardBusy = unchecked((int)0x800401D0)", StringComparison.Ordinal)
           && adminWindow.Contains("attempt <= 20", StringComparison.Ordinal)
           && adminWindow.Contains("Clipboard.SetDataObject(value, copy: true)", StringComparison.Ordinal),
        "security-sensitive invitation and credential clipboard handoffs must tolerate transient ownership");

    var remoteUpdates = ReadSource("src", "Taildesk.Admin", "RemoteDeviceUpdateCoordinator.cs");
    Assert(remoteUpdates.Contains("ObserveMaintenanceBootstrapAsync", StringComparison.Ordinal)
           && remoteUpdates.Contains("DateTimeOffset.UtcNow.AddMinutes(30)", StringComparison.Ordinal)
           && remoteUpdates.Contains("update?.OperationId != operationId", StringComparison.Ordinal)
           && remoteUpdates.Contains("!update.MaintenanceBootstrap", StringComparison.Ordinal)
           && remoteUpdates.Contains("CommitUpdateAsync(device, agentToken, operationId", StringComparison.Ordinal),
        "the command center must bound discovery and commit only the exact maintenance operation");
    Assert(remoteUpdates.Contains("status.Architecture, release.Architecture", StringComparison.Ordinal)
           && remoteUpdates.Contains("status.UpdateProtocolVersion == RemoteAdministrationProtocol.UpdateVersion", StringComparison.Ordinal)
           && remoteUpdates.Contains("status.TailscaleIp, device.TailscaleIp", StringComparison.Ordinal)
           && remoteUpdates.Contains("status.TailnetDeviceId, device.TailnetDeviceId", StringComparison.Ordinal)
           && remoteUpdates.Contains("Maintenance external health sample", StringComparison.Ordinal)
           && remoteUpdates.Contains("RemoteAdministrationProtocol.SshPort", StringComparison.Ordinal),
        "maintenance commit must require three exact authenticated external identity and recovery samples");

    var updateHealthStore = ReadSource("src", "Taildesk.Shared", "UpdateHealthTokenStore.cs");
    Assert(updateHealthStore.Contains("SecretScope.LocalMachine", StringComparison.Ordinal)
           && updateHealthStore.Contains("MachineStorageSecurity.WriteRestrictedFileCreateNewAsync", StringComparison.Ordinal)
           && updateHealthStore.Contains("MachineStorageSecurity.ReadRestrictedFile", StringComparison.Ordinal)
           && updateHealthStore.Contains("RequireUpdateSidecarPath", StringComparison.Ordinal)
           && updateHealthStore.IndexOf("configuredProtectedToken", StringComparison.Ordinal)
              < updateHealthStore.IndexOf("LoadSidecar", StringComparison.Ordinal),
        "update health resolution must prefer config and create a DPAPI LocalMachine write-once sidecar");

    var guardian = ReadSource("src", "Taildesk.UpdateGuardian", "GuardianRunner.cs");
    Assert(guardian.Contains("CommittedBootHealthWindow = TimeSpan.FromMinutes(6.5)", StringComparison.Ordinal)
           && guardian.Contains("RollbackHealthWindow = TimeSpan.FromMinutes(6.5)", StringComparison.Ordinal)
           && guardian.Contains("new LifelineSnapshot(journal.SshWasListening)", StringComparison.Ordinal),
        "committed boot verification must outlast the Agent's five-minute Tailscale bind wait");
    Assert(guardian.Contains("durable.OperationId != journal.OperationId", StringComparison.Ordinal),
        "a stale Guardian can overwrite a newer durable transaction");
    Assert(guardian.Contains("WaitForLegacyRollbackAsync", StringComparison.Ordinal)
           && guardian.Contains("requiredHealthySamples = 3", StringComparison.Ordinal),
        "maintenance rollback must accept a signed running legacy Agent without its unavailable health endpoint");
    Assert(guardian.Contains("UpdatePhase.Downloading or UpdatePhase.Verifying", StringComparison.Ordinal),
        "interrupted staging must fail durably without blocking later updates");
    Assert(guardian.Contains("watchdogOnly ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(20)", StringComparison.Ordinal)
           && guardian.Contains("journal.Phase is UpdatePhase.None", StringComparison.Ordinal)
           && guardian.Contains("or UpdatePhase.Committed", StringComparison.Ordinal)
           && guardian.Contains("or UpdatePhase.RolledBack", StringComparison.Ordinal)
           && guardian.Contains("or UpdatePhase.Failed", StringComparison.Ordinal),
        "the minute watchdog must use bounded contention and no-op for terminal/non-actionable phases");
    var guardianProgram = ReadSource("src", "Taildesk.UpdateGuardian", "Program.cs");
    Assert(guardianProgram.Contains("RemoteAdministrationProtocol.GuardianWatchdogArgument", StringComparison.Ordinal)
           && guardianProgram.Contains("watchdogOnly ? TimeSpan.Zero : TimeSpan.FromMinutes(2)", StringComparison.Ordinal)
           && guardianProgram.Contains(".RunAsync(watchdogOnly, cancellation.Token)", StringComparison.Ordinal)
           && guardianProgram.Contains(".GetAwaiter()", StringComparison.Ordinal)
           && guardianProgram.Contains(".GetResult()", StringComparison.Ordinal)
           && !guardianProgram.Contains("return await new GuardianRunner", StringComparison.Ordinal),
        "full ONSTART mode must wait through a quick watchdog so boot health cannot be suppressed");

    var updateManager = ReadSource("src", "Taildesk.Agent", "UpdateManager.cs");
    Assert(updateManager.Contains("WaitForGuardianPickupAsync", StringComparison.Ordinal)
           && updateManager.Contains("durable.GuardianClaimedAt is not null", StringComparison.Ordinal),
        "Task Scheduler success must not be mistaken for Guardian transaction pickup");
    Assert(updateManager.Contains("RunGuardianTaskForCommitAsync", StringComparison.Ordinal)
           && updateManager.Contains("durable.OperationId != operationId", StringComparison.Ordinal)
           && updateManager.Contains("UpdatePhase.Committed or UpdatePhase.RolledBack or UpdatePhase.Failed", StringComparison.Ordinal)
           && !updateManager.Contains("allowAlreadyRunning", StringComparison.Ordinal),
        "commit wakeup must require terminal evidence from the exact durable operation");
    var packageVerifier = ReadSource("src", "Taildesk.Shared", "UpdatePackageVerifier.cs");
    Assert(packageVerifier.Contains("Both archive", StringComparison.Ordinal)
           && packageVerifier.IndexOf("await target.FlushAsync", StringComparison.Ordinal)
              < packageVerifier.IndexOf("VerifyAuthenticodeAsync(output", StringComparison.Ordinal),
        "Guardian extraction must close its exclusive output handle before Authenticode reopens the staged executable");
    Assert(manager.Contains("SessionTerminationGeneration++", StringComparison.Ordinal)
           && manager.Contains("terminateAuthenticatedSessions: true", StringComparison.Ordinal),
        "revocation and expiry must durably request termination of already-authenticated SSH shells");

    var agentProgram = ReadSource("src", "Taildesk.Agent", "Program.cs");
    Assert(agentProgram.Split("remote?.AddressFamily != AddressFamily.InterNetwork", StringSplitOptions.None).Length == 3,
        "internal-health and sensitive API authorization must reject mapped/native IPv6 identities");
    Assert(agentProgram.Contains("var updateHealthToken", StringComparison.Ordinal)
           && agentProgram.Contains("FixedTimeEquals(healthHeader, updateHealthToken)", StringComparison.Ordinal),
        "Agent internal health must use the config-first sidecar-capable credential resolver");
    Assert(agentProgram.Contains("RemoteAdministrationProtocol.IsTailscaleIpv4(coordinator.Host)", StringComparison.Ordinal),
        "the configured coordinator authorization identity is not canonical Tailscale IPv4");
    Assert(agentProgram.Contains("SystemRoot = grant.SystemRoot", StringComparison.Ordinal),
        "the authenticated SSH grant must propagate the exact target SystemRoot for automation");
    var guardianHealth = ReadSource("src", "Taildesk.UpdateGuardian", "InternalHealthClient.cs");
    Assert(guardianHealth.Contains("UpdateHealthTokenStore.LoadFromAgentConfigFile()", StringComparison.Ordinal),
        "Guardian internal health must use the same config-first sidecar fallback");
    var maintenanceBootstrap = ReadSource("src", "Taildesk.Setup", "MaintenanceBootstrapCoordinator.cs");
    Assert(maintenanceBootstrap.Contains("AddMinutes(2.5)", StringComparison.Ordinal)
           && maintenanceBootstrap.Contains("UpdateGuardianStartupDiagnostics.Read()", StringComparison.Ordinal)
           && guardianProgram.Contains("UpdateGuardianStartupDiagnostics.TryWrite", StringComparison.Ordinal),
        "Setup pickup must outlive the Guardian mutex wait and surface protected pre-claim startup failures");

    var adminToken = ReadSource("src", "Taildesk.UpdateGuardian", "SshAdminToken.cs");
    Assert(adminToken.Contains("TokenElevationTypeLimited", StringComparison.Ordinal)
           && adminToken.Contains("SecurityMandatoryHighRid", StringComparison.Ordinal)
           && adminToken.Contains("BuiltinAdministratorsSid", StringComparison.Ordinal)
           && adminToken.Contains("ScManagerCreateService", StringComparison.Ordinal)
           && adminToken.Contains("LocalSystemSid", StringComparison.Ordinal),
        "the SSH administrator proof must reject filtered/SYSTEM tokens and prove high-integrity SCM access");
    Assert(adminToken.Contains("Marshal.SizeOf<TokenLinkedToken>()", StringComparison.Ordinal)
           && adminToken.Contains("GetLinkedTokenInformation", StringComparison.Ordinal)
           && !adminToken.Contains("ReadTokenInformation(token, TokenLinkedTokenClass", StringComparison.Ordinal),
        "the fixed TOKEN_LINKED_TOKEN query must not depend on a zero-buffer sizing probe that returns ERROR_BAD_LENGTH on supported Windows builds");
    Assert(adminToken.Contains("GetInt32TokenInformation", StringComparison.Ordinal)
           && adminToken.Contains("const uint expectedLength = sizeof(int)", StringComparison.Ordinal)
           && !adminToken.Contains("var buffer = ReadTokenInformation(token, informationClass, description)", StringComparison.Ordinal),
        "fixed-size token fields must not depend on zero-buffer sizing probes that preserve stale Windows last-error values");
    Assert(adminToken.Contains("TokenAccessLevels.Query | TokenAccessLevels.Duplicate", StringComparison.Ordinal),
        "the in-session SSH proof must retain TOKEN_DUPLICATE while constructing its independent WindowsIdentity");
    var daemonUser = ReadSource("src", "Taildesk.UpdateGuardian", "SshDaemonUserContext.cs");
    Assert(daemonUser.Contains("LogonFullAdministrator", StringComparison.Ordinal)
           && daemonUser.Contains("CreateProcessAsUserW", StringComparison.Ordinal)
           && daemonUser.Contains("ScopedProcessPrivilege.Enable(\"SeBackupPrivilege\")", StringComparison.Ordinal)
           && daemonUser.Contains("ScopedProcessPrivilege.Enable(\"SeRestorePrivilege\")", StringComparison.Ordinal)
           && daemonUser.Contains("ProfileUnloadAttempts", StringComparison.Ordinal),
        "Guardian must create sshd with the full dedicated token and safely load/unload its profile");

    var supervisor = ReadSource("src", "Taildesk.UpdateGuardian", "SshSupervisor.cs");
    Assert(supervisor.Contains("WriteFailureAsync(exception)", StringComparison.Ordinal)
           && supervisor.Contains("supervisor.failure", StringComparison.Ordinal)
           && supervisor.Contains("File.Delete(_failurePath)", StringComparison.Ordinal)
           && supervisor.Contains("WithDaemonLog", StringComparison.Ordinal),
        "the independent SSH supervisor must publish protected failures and clear them after readiness");
    Assert(supervisor.Contains("await supervisor.WriteFailureAsync(exception)", StringComparison.Ordinal)
           && supervisor.IndexOf("await supervisor.WriteFailureAsync(exception)", StringComparison.Ordinal)
              < supervisor.IndexOf("await supervisor.FailClosedAsync()", StringComparison.Ordinal),
        "early SSH supervisor initialization failures must be published before fail-closed cleanup");
    Assert(supervisor.Contains("JobObjectLimitKillOnJobClose", StringComparison.Ordinal)
           && supervisor.Contains("CreateSuspended", StringComparison.Ordinal),
        "stable guardian must own sshd and shells in a kill-on-close job");
    Assert(supervisor.Contains("supervisor.lock", StringComparison.Ordinal)
           && supervisor.Contains("state.lock", StringComparison.Ordinal),
        "supervisor instance and scoped state locks must remain separate");
    Assert(supervisor.Contains("sessionTerminationRequired", StringComparison.Ordinal)
           && supervisor.Contains("state.SessionTerminationGeneration != _observedTerminationGeneration", StringComparison.Ordinal)
           && supervisor.Contains("_observedActiveSessionIds.Except(activeSessionIds).Any()", StringComparison.Ordinal)
           && supervisor.Contains("authorizationSetShrank", StringComparison.Ordinal)
           && supervisor.Contains("await StopDaemonAsync()", StringComparison.Ordinal),
        "the stable supervisor must restart its kill-on-close job whenever a lease is revoked or expires, even while the agent is offline");
    Assert(supervisor.Contains("UtcDateTime:yyyyMMddHHmmss}Z", StringComparison.Ordinal),
        "native authorized-key expiry must use an unambiguous UTC Z timestamp");
    Assert(supervisor.Contains("TerminateJobObject", StringComparison.Ordinal)
           && supervisor.Contains("QueryInformationJobObject", StringComparison.Ordinal)
           && supervisor.Contains("RotateDaemonLogAsync", StringComparison.Ordinal)
           && supervisor.Contains("MaximumArchivedLogBytes", StringComparison.Ordinal)
           && supervisor.Contains("LogLevel INFO", StringComparison.Ordinal),
        "Guardian teardown and SSH logging must remain bounded and fail-closed");
    var runtimeAclStart = supervisor.IndexOf("private async Task GrantDaemonRuntimeAccessAsync", StringComparison.Ordinal);
    var runtimeAclEnd = supervisor.IndexOf("private async Task RotateDaemonLogAsync", runtimeAclStart, StringComparison.Ordinal);
    Assert(runtimeAclStart >= 0 && runtimeAclEnd > runtimeAclStart
           && !supervisor[runtimeAclStart..runtimeAclEnd].Contains("_hostKeyPath", StringComparison.Ordinal)
           && supervisor.Contains("RestrictDaemonReadableAsync", StringComparison.Ordinal)
           && supervisor.Contains("/remove:g", StringComparison.Ordinal),
        "Guardian must let the elevated daemon read host keys through Administrators without a rejected named-user ACE");

    var app = ReadSource("src", "Taildesk.Admin", "App.xaml.cs");
    var viewModel = ReadSource("src", "Taildesk.Admin", "MainViewModel.cs");
    Assert(app.Contains("ShutdownSshSessionsAsync", StringComparison.Ordinal)
           && viewModel.Contains("session.TerminateAsync", StringComparison.Ordinal)
           && viewModel.Contains("RemoteRevocationError", StringComparison.Ordinal)
           && viewModel.Contains("LocalCleanupError", StringComparison.Ordinal),
        "Command Center shutdown must terminate SSH and report remote/local cleanup independently");

    var buildScript = ReadSource("build.ps1");
    var buildProps = ReadSource("Directory.Build.props");
    var targetReleaseCheck = ReadSource("scripts", "Ensure-OpticonTargetRelease.ps1");
    var repositoryRoot = root.Parent
        ?? throw new InvalidOperationException("Repository root was not found above Opticon.");
    var buildWorkflow = File.ReadAllText(Path.Combine(
        repositoryRoot.FullName, ".github", "workflows", "opticon-security.yml"));
    var hostedBuild = ReadSource("fly-headscale", "scripts", "Build-OpticonBundles.ps1");
    var installer = ReadSource("installer", "Install-CommandCenter.ps1");
    var commandCenterInstaller = ReadSource("src", "Taildesk.CommandCenterInstaller", "Program.cs");
    var solution = ReadSource("Taildesk.sln");
    Assert(buildScript.Contains("The Opticon solution build failed", StringComparison.Ordinal)
           && buildScript.Contains("The Opticon self-tests failed", StringComparison.Ordinal)
           && buildScript.Contains("$solutionArtifacts = Join-Path $workspace 'solution-artifacts'", StringComparison.Ordinal)
           && buildScript.Contains("--artifacts-path', $solutionArtifacts", StringComparison.Ordinal)
           && buildScript.Contains("bin\\Taildesk.SelfTest\\release\\Taildesk.SelfTest.dll", StringComparison.Ordinal)
           && buildScript.Contains("must contain only the signed opticon.exe", StringComparison.Ordinal)
           && buildScript.Contains("IncludeSourceRevisionInInformationalVersion=false", StringComparison.Ordinal)
           && hostedBuild.Contains("The clean $component publish must contain only", StringComparison.Ordinal)
           && hostedBuild.Contains("IncludeSourceRevisionInInformationalVersion=false", StringComparison.Ordinal),
        "release packaging must fail on native build/test errors and ship a single signed CLI app");
    Assert(buildScript.Contains("$SdkPolicy = '10.*.*'", StringComparison.Ordinal)
           && buildScript.Contains("rollForward = 'latestMinor'", StringComparison.Ordinal)
           && buildProps.Contains("net10.0-windows10.0.19041.0", StringComparison.Ordinal)
           && buildProps.Contains("<OpticonRuntimeVersion>10.0.10</OpticonRuntimeVersion>", StringComparison.Ordinal)
           && buildProps.Contains("<TargetLatestRuntimePatch>true</TargetLatestRuntimePatch>", StringComparison.Ordinal)
           && buildScript.Contains("OpticonSigningProfile", StringComparison.Ordinal)
           && buildScript.Contains("SourceReleaseSigningCertificateThumbprint", StringComparison.Ordinal)
           && buildScript.Contains("CodeSigningCertificateThumbprint", StringComparison.Ordinal)
           && buildScript.Contains("/tr", StringComparison.Ordinal)
           && buildScript.Contains("/td", StringComparison.Ordinal)
           && buildScript.Contains("TimeStamperCertificate", StringComparison.Ordinal)
           && buildScript.Contains("safe.directory=$($script:trustedGitRoot.Replace('\\', '/'))", StringComparison.Ordinal)
           && buildScript.Contains("The production build resolved an unexpected Git root", StringComparison.Ordinal)
           && buildScript.Contains("$signature.Status -ne 'Valid'", StringComparison.Ordinal)
           && buildScript.Contains("RSASignaturePadding]::Pss", StringComparison.Ordinal)
           && buildScript.Contains("DEV-UNTRUSTED", StringComparison.Ordinal)
           && !buildScript.Contains("Taildesk.InviteLauncher", StringComparison.Ordinal)
           && !buildScript.Contains("Install-Opticon.ps1", StringComparison.Ordinal)
           && solution.Contains("Taildesk.CommandCenterInstaller", StringComparison.Ordinal)
           && solution.Contains("Taildesk.RouteKeeper", StringComparison.Ordinal)
           && !solution.Contains("Taildesk.InviteLauncher", StringComparison.Ordinal),
        "command-center packaging must use the stable .NET 10 SDK policy, an exact output-runtime pin, separated timestamped signers, and the signed wrapper only");
    Assert(commandCenterInstaller.Contains("SourceReleaseSigning.Verify", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("AllowedPayloadPaths", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("ProductSigning.VerifyAuthenticodeAsync", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("CreateProtectedStagingDirectory", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("-ExecutionPolicy", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("RemoteSigned", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("RedirectStandardError = true", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("BoundedInstallerDiagnostic", StringComparison.Ordinal)
           && commandCenterInstaller.Contains("ALLUSERSPROFILE", StringComparison.Ordinal)
           && !commandCenterInstaller.Contains("Bypass", StringComparison.Ordinal),
        "the only command-center entry point must bind manifest verification to protected staging and fixed PowerShell policy");
    Assert(installer.Contains("Assert-LegacyCanonicalOpticonDirectory", StringComparison.Ordinal)
           && installer.Contains("$script:InvitationSigningThumbprint", StringComparison.Ordinal)
           && installer.Contains("The legacy Opticon executable is not signed by the exact retired Opticon signer", StringComparison.Ordinal)
           && installer.Contains("-AllowLegacyCanonical", StringComparison.Ordinal),
        "only exact legacy signer payloads at the canonical controller path may migrate to the OwnerManaged signer");
    Assert(installer.Contains("Invoke-ExactSchtasks", StringComparison.Ordinal)
           && installer.Contains("$start.Arguments", StringComparison.Ordinal)
           && installer.Contains("sanitized environment", StringComparison.Ordinal)
           && !installer.Contains("$start.ArgumentList.Add", StringComparison.Ordinal),
        "the protected installer must run task and vendor commands through Windows PowerShell 5.1-compatible process APIs");
    Assert(installer.Contains("if ($ControllerOnlyRepair)", StringComparison.Ordinal)
           && installer.Contains("without changing persistent tasks", StringComparison.Ordinal)
           && installer.Contains("must not query, replace, or run machine tasks", StringComparison.Ordinal),
        "a source-triggered command-center repair must not touch persistent scheduled tasks");
    Assert(buildScript.Contains("SkipTargetReleaseDeployment", StringComparison.Ordinal)
           && buildScript.Contains("Ensure-OpticonTargetRelease.ps1", StringComparison.Ordinal)
           && buildWorkflow.Contains("contents: read", StringComparison.Ordinal)
           && buildWorkflow.Contains("dotnet-version: '10.x'", StringComparison.Ordinal)
           && buildWorkflow.Contains("go test -race", StringComparison.Ordinal)
           && buildWorkflow.Contains("actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("Test-CompleteRelease", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("Publish-OpticonSourceRelease.ps1", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("schemaVersion -ne 2", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("OpticonSource", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("opticon-source-$ReleaseVersion.zip", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("status --porcelain", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("refs/remotes/origin/main", StringComparison.Ordinal)
           && targetReleaseCheck.Contains("DeploymentRequired", StringComparison.Ordinal),
        "operator builds must deploy missing target releases only from clean synchronized main while CI opts out explicitly");
    const string packageBuildLock = ".opticon-package-build.lock";
    const string acquirePackageBuildLock = "$packageBuildLock = Enter-OpticonPackageBuildLock";
    const string firstStandaloneBuildInvocation = "Invoke-DotNet -Arguments @('--version')";
    const string firstHostedBuildMutation = "foreach ($path in @($buildRoot, $stageRoot))";
    Assert(buildScript.Contains(packageBuildLock, StringComparison.Ordinal)
           && hostedBuild.Contains(packageBuildLock, StringComparison.Ordinal)
           && buildScript.Contains("[IO.FileShare]::None", StringComparison.Ordinal)
            && hostedBuild.Contains("[IO.FileShare]::None", StringComparison.Ordinal)
            && buildScript.IndexOf(acquirePackageBuildLock, StringComparison.Ordinal) < buildScript.IndexOf(firstStandaloneBuildInvocation, StringComparison.Ordinal)
            && hostedBuild.IndexOf(acquirePackageBuildLock, StringComparison.Ordinal) < hostedBuild.IndexOf(firstHostedBuildMutation, StringComparison.Ordinal)
           && buildScript.LastIndexOf("$packageBuildLock.Dispose()", StringComparison.Ordinal) > buildScript.LastIndexOf("New-ReproducibleZip", StringComparison.Ordinal)
           && hostedBuild.LastIndexOf("$packageBuildLock.Dispose()", StringComparison.Ordinal) > hostedBuild.LastIndexOf("Compress-Archive", StringComparison.Ordinal),
        "standalone and hosted packaging must share one exclusive lock across every build and publication mutation");
    var cliPathIntegration = ReadSource("src", "Taildesk.Admin", "CliPathIntegration.cs");
    var cliProgram = ReadSource("src", "Taildesk.Cli", "Program.cs");
    const string ownershipMarker = ".opticon-controller-owned";
    const string readyMarker = ".opticon-controller-ready";
    const string installLock = ".controller-install.lock";

    Assert(installer.Contains(ownershipMarker, StringComparison.Ordinal)
           && setup.Contains(ownershipMarker, StringComparison.Ordinal)
           && cliPathIntegration.Contains(ownershipMarker, StringComparison.Ordinal)
           && cliProgram.Contains(ownershipMarker, StringComparison.Ordinal),
        "installers, UI, and CLI must share the exact controller ownership marker");
    Assert(installer.Contains(readyMarker, StringComparison.Ordinal)
           && setup.Contains(readyMarker, StringComparison.Ordinal)
           && cliPathIntegration.Contains(readyMarker, StringComparison.Ordinal)
           && cliProgram.Contains(readyMarker, StringComparison.Ordinal)
           && installer.Contains("ControllerReadyMarkerValue)|$version", StringComparison.Ordinal)
           && setup.Contains("ControllerReadyMarkerValue}|{version}", StringComparison.Ordinal)
           && cliPathIntegration.Contains("Assembly.GetExecutingAssembly().GetName().Version", StringComparison.Ordinal)
           && cliProgram.Contains("Assembly.GetExecutingAssembly().GetName().Version", StringComparison.Ordinal),
        "the durable commit marker must bind the on-disk UI/CLI version to the executing UI or CLI generation");

    Assert(installer.Contains(installLock, StringComparison.Ordinal)
           && setup.Contains(installLock, StringComparison.Ordinal)
           && installer.Contains("[IO.FileShare]::None", StringComparison.Ordinal)
           && setup.Contains("FileShare.None", StringComparison.Ordinal)
           && cliPathIntegration.Contains("FileShare.Read", StringComparison.Ordinal)
           && cliProgram.Contains("FileShare.Read", StringComparison.Ordinal),
        "both installers must take the exclusive persistent lock while UI and CLI hold compatible lifetime reader leases");
    Assert(installer.IndexOf("$installLock = Enter-ControllerInstallLock", StringComparison.Ordinal)
               < installer.LastIndexOf("Ensure-OpenSshClientCapability", StringComparison.Ordinal)
           && installer.LastIndexOf("$installLock.Dispose()", StringComparison.Ordinal)
               > installer.IndexOf("Install-OpticonPayloadTransaction", StringComparison.Ordinal)
           && setup.IndexOf("AcquireControllerInstallLockAsync", StringComparison.Ordinal)
               < setup.IndexOf("await RecoverControllerDirectoryTransactionAsync(destination", StringComparison.Ordinal)
           && setup.IndexOf("AcquireControllerInstallLockAsync", StringComparison.Ordinal)
               < setup.IndexOf("await InstallControllerDirectoryTransactionalAsync", StringComparison.Ordinal),
        "exclusive installation locking must cover recovery, swap, protected handoff, and post-commit configuration");

    Assert(installer.Contains("Assert-InstallDestinationPreflight", StringComparison.Ordinal)
           && installer.Contains("restricted to the canonical directory", StringComparison.Ordinal)
           && installer.Contains("Assert-OwnedOpticonDirectory", StringComparison.Ordinal)
           && setup.Contains("RequireOwnedControllerDirectoryAsync", StringComparison.Ordinal)
           && setup.Contains("legacyExecutables", StringComparison.Ordinal)
           && setup.Contains("contains a reparse point", StringComparison.Ordinal),
        "destructive controller swaps must be canonical, ownership guarded, reparse safe, and verify every legacy executable");
    Assert(installer.Contains("Restore-InterruptedOpticonInstall", StringComparison.Ordinal)
           && installer.Contains("Assert-CommittedOrLegacyOpticonDirectory -Directory $backup", StringComparison.Ordinal)
           && installer.Contains("Move-Item -LiteralPath $backup -Destination $destination", StringComparison.Ordinal)
           && setup.Contains("RequireCommittedOrLegacyControllerDirectoryAsync(backup", StringComparison.Ordinal)
           && setup.Contains("HasExactControllerReadyMarker(destination)", StringComparison.Ordinal)
           && setup.Contains("Directory.Move(backup, destination)", StringComparison.Ordinal),
        "recovery must validate/restore .previous and never discard it for an uncommitted live candidate");
    Assert(installer.IndexOf("& $ConfigureActivatedPayload", StringComparison.Ordinal)
               < installer.IndexOf("Write-ControllerReadyMarker -Directory $destination", StringComparison.Ordinal)
           && setup.IndexOf("await configureActivatedPayload()", StringComparison.Ordinal)
               < setup.IndexOf("WriteControllerReadyMarker(destination)", StringComparison.Ordinal)
            && installer.Contains("Restore-RouteTaskSnapshot", StringComparison.Ordinal)
            && installer.Contains("Restore-UiTaskSnapshot", StringComparison.Ordinal)
            && installer.Contains("Register-ExactRouteKeeperTask", StringComparison.Ordinal)
            && installer.Contains("Register-ExactUiTask", StringComparison.Ordinal)
            && installer.Contains("InteractiveToken", StringComparison.Ordinal)
            && installer.Contains("LeastPrivilege", StringComparison.Ordinal)
            && !installer.Contains("& $tailscale login", StringComparison.Ordinal)
            && !installer.Contains("Install-TaildeskFlyRouteTask.ps1", StringComparison.Ordinal)
            && !installer.Contains("New-Object -ComObject WScript.Shell", StringComparison.Ordinal)
            && setup.IndexOf("await MachineStorageSecurity.WriteUserBootstrapAsync", StringComparison.Ordinal)
                < setup.IndexOf("WriteControllerReadyMarker(destination)", StringComparison.Ordinal)
            && setup.Contains("MachineStorageSecurity.DeleteUserBootstrap", StringComparison.Ordinal)
            && setup.Contains("BuildRouteKeeperTaskXml", StringComparison.Ordinal)
            && setup.Contains("BuildControllerUiTaskXml", StringComparison.Ordinal)
            && setup.Contains("StartControllerTasksIfInstalledAsync", StringComparison.Ordinal),
        "ready must be written after protected handoff, and handoff failure must delete it while payload rollback restores the prior directory");

    Assert(installer.Contains("@($destination, $backup)", StringComparison.Ordinal)
           && setup.Contains("RequireInstalledControllerProcessesClosed(destination, backup)", StringComparison.Ordinal)
           && cliPathIntegration.IndexOf("await AcquireControllerLifetimeLeaseAsync", StringComparison.Ordinal)
               < cliPathIntegration.IndexOf("if (runningRetainedInstall)", StringComparison.Ordinal)
           && cliProgram.IndexOf("var lease = new FileStream", StringComparison.Ordinal)
               < cliProgram.IndexOf("if (!await HasExactControllerMarkersAsync", StringComparison.Ordinal),
        "live and .previous UI/CLI generations must be checked under the shared/exclusive lease before use or deletion");

    Assert(cliPathIntegration.Contains("recordedDirectory.Equals(defaultInstalledDirectory", StringComparison.Ordinal)
           && cliPathIntegration.Contains("previous = null; // Never remove an unverified", StringComparison.Ordinal)
           && setup.Contains("full.Equals(canonical + \".previous\"", StringComparison.Ordinal)
           && setup.Contains("await VerifyControllerDirectoryAsync(directory", StringComparison.Ordinal)
           && installer.Contains("Test-TrustedRecordedOpticonCliPath", StringComparison.Ordinal)
           && installer.Contains("CanonicalControllerInstallDirectory", StringComparison.Ordinal)
           && cliPathIntegration.Contains("if (uiVersion != cliVersion)", StringComparison.Ordinal)
           && setup.Contains("if (uiVersion != cliVersion)", StringComparison.Ordinal)
           && installer.Contains("Assert-MatchingOpticonUiCliVersion", StringComparison.Ordinal),
        "PATH repair must use only the canonical recorded install and exact matching UI/CLI versions");
}

static string NormalizeTargetType(string value) => value.Replace("{x:Type ", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal).Trim();

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"expected {typeof(TException).Name}");
}

internal static class SelfTestNative
{
    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}
