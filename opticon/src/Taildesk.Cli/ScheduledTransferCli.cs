using System.Globalization;
using System.Text.Json;
using Taildesk.Admin;
using Taildesk.Shared;

namespace Taildesk.Cli;

internal sealed class ScheduledTransferCli
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.Out.WriteLine("Run 'opticon help' for scheduled-transfer commands and options.");
            return 0;
        }
        return args[0].ToLowerInvariant() switch
        {
            "list" => await ListAsync(args[1..], cancellationToken),
            "add" or "create" => await SaveAsync(args[1..], existingId: null, cancellationToken),
            "edit" or "update" => await EditAsync(args[1..], cancellationToken),
            "run" => await RunNowAsync(args[1..], cancellationToken),
            "enable" => await EnableAsync(args[1..], true, cancellationToken),
            "disable" => await EnableAsync(args[1..], false, cancellationToken),
            "remove" or "delete" => await RemoveAsync(args[1..], cancellationToken),
            "history" => await HistoryAsync(args[1..], cancellationToken),
            "retry" => await RetryAsync(args[1..], cancellationToken),
            _ => throw CliException.Usage($"Unknown schedule command '{Clean(args[0])}'. Run 'opticon help' for usage.")
        };
    }

    private static async Task<int> ListAsync(string[] args, CancellationToken cancellationToken)
    {
        var json = ParseJsonOnly(args, "schedule list");
        var document = await new ScheduledTransferStore().LoadAsync(cancellationToken);
        if (json) WriteJson(new { schemaVersion = 1, ok = true, command = "schedule list", schedules = document.Schedules });
        else if (document.Schedules.Count == 0) Console.Out.WriteLine("No scheduled transfers are configured.");
        else
        {
            Console.Out.WriteLine("ID\tNAME\tENABLED\tDIRECTION\tMODE\tSCHEDULE\tNEXT RUN");
            foreach (var item in document.Schedules.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                Console.Out.WriteLine(string.Join('\t', item.Id, Clean(item.Name), item.Enabled, item.Direction, item.Mode,
                    Clean(CronSchedule.Describe(item.CronExpression)), item.NextRunAt?.ToLocalTime().ToString("g") ?? "-"));
        }
        return 0;
    }

    private static async Task<int> EditAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !Guid.TryParse(args[0], out var id))
            throw CliException.Usage("Usage: opticon schedule edit <schedule-id> [options]");
        return await SaveAsync(args[1..], id, cancellationToken);
    }

    private static async Task<int> SaveAsync(string[] args, Guid? existingId, CancellationToken cancellationToken)
    {
        var parsed = OptionSet.Parse(args, ValueOptions, FlagOptions);
        var state = await LoadStateAsync(cancellationToken);
        var store = new ScheduledTransferStore();
        ScheduledTransferDefinition definition;
        if (existingId.HasValue)
        {
            var document = await store.LoadAsync(cancellationToken);
            definition = document.Schedules.FirstOrDefault(item => item.Id == existingId)?.Copy()
                         ?? throw new CliException("schedule_not_found", "The scheduled transfer was not found.", 1);
        }
        else definition = new ScheduledTransferDefinition();

        if (parsed.TryValue("--name", out var name)) definition.Name = name;
        if (parsed.TryValue("--device", out var selector)) definition.DeviceId = SelectDevice(state.Config.Devices, selector).Id;
        if (parsed.TryValue("--direction", out var direction)) definition.Direction = ParseEnum<ScheduledTransferDirection>(direction, "direction");
        if (parsed.TryValue("--local-folder", out var local)) definition.LocalFolder = local;
        if (parsed.TryValue("--remote-root", out var root)) definition.RemoteRoot = root;
        if (parsed.TryValue("--remote-folder", out var folder)) definition.RemoteFolder = folder;
        if (parsed.Has("--extension")) { definition.Filter = ScheduledTransferFilter.Extension; definition.FilterPattern = parsed.Value("--extension"); }
        if (parsed.Has("--regex")) { definition.Filter = ScheduledTransferFilter.Regex; definition.FilterPattern = parsed.Value("--regex"); }
        if (parsed.Has("--all-files")) { definition.Filter = ScheduledTransferFilter.All; definition.FilterPattern = string.Empty; }
        if (parsed.Has("--recursive")) definition.Recursive = true;
        if (parsed.Has("--no-recursive")) definition.Recursive = false;
        if (parsed.Has("--move")) definition.Mode = ScheduledTransferMode.Move;
        if (parsed.Has("--copy")) definition.Mode = ScheduledTransferMode.Copy;
        if (parsed.Has("--overwrite")) definition.Overwrite = true;
        if (parsed.Has("--no-overwrite")) definition.Overwrite = false;
        if (parsed.Has("--disabled")) definition.Enabled = false;
        if (parsed.Has("--enabled")) definition.Enabled = true;
        if (parsed.TryValue("--timezone", out var zone)) definition.TimeZoneId = zone;
        if (parsed.TryValue("--cron", out var cron)) definition.CronExpression = cron;
        if (parsed.TryValue("--every", out var every)) definition.CronExpression = FriendlyCron(every, parsed);

        if (!existingId.HasValue)
        {
            var missing = new List<string>();
            if (!parsed.Has("--name")) missing.Add("--name");
            if (!parsed.Has("--device")) missing.Add("--device");
            if (!parsed.Has("--direction")) missing.Add("--direction");
            if (!parsed.Has("--local-folder")) missing.Add("--local-folder");
            if (!parsed.Has("--remote-root")) missing.Add("--remote-root");
            if (!parsed.Has("--cron") && !parsed.Has("--every")) missing.Add("--cron or --every");
            if (missing.Count > 0) throw CliException.Usage("Missing required option(s): " + string.Join(", ", missing));
        }
        RequireExclusive(parsed, "--cron", "--every");
        RequireExclusive(parsed, "--extension", "--regex", "--all-files");
        RequireExclusive(parsed, "--move", "--copy");
        RequireExclusive(parsed, "--recursive", "--no-recursive");
        RequireExclusive(parsed, "--overwrite", "--no-overwrite");
        RequireExclusive(parsed, "--enabled", "--disabled");
        if ((parsed.Has("--at") || parsed.Has("--day")) && !parsed.Has("--every"))
            throw CliException.Usage("--at and --day are available only with --every.");
        if (parsed.TryValue("--every", out every))
        {
            every = every.ToLowerInvariant();
            if (parsed.Has("--at") && every is not ("day" or "week"))
                throw CliException.Usage("--at is available only with --every day or --every week.");
            if (parsed.Has("--day") && every != "week")
                throw CliException.Usage("--day is available only with --every week.");
        }
        ScheduledTransferRules.Validate(definition);
        var saved = await store.UpsertAsync(definition, cancellationToken);
        if (parsed.Json) WriteJson(new { schemaVersion = 1, ok = true, command = existingId.HasValue ? "schedule edit" : "schedule add", schedule = saved });
        else Console.Out.WriteLine($"{(existingId.HasValue ? "Updated" : "Created")} scheduled transfer {Clean(saved.Name)} ({saved.Id}).");
        return 0;
    }

    private static async Task<int> RunNowAsync(string[] args, CancellationToken cancellationToken)
    {
        var (id, json) = ParseIdAndJson(args, "schedule run");
        var state = await LoadStateAsync(cancellationToken);
        var agents = new AgentClient();
        var store = new ScheduledTransferStore();
        var run = await store.ClaimManualAsync(id, cancellationToken);
        var progress = json ? null : new InlineProgress<string>(value => Console.Error.WriteLine(Clean(value)));
        var result = await new ScheduledTransferEngine(state, agents, store).RunClaimedAsync(run, progress, cancellationToken);
        WriteRun(result, json, "schedule run");
        return result.State == ScheduledTransferRunState.Succeeded ? 0 : 1;
    }

    private static async Task<int> RetryAsync(string[] args, CancellationToken cancellationToken)
    {
        var (id, json) = ParseIdAndJson(args, "schedule retry");
        var state = await LoadStateAsync(cancellationToken);
        var agents = new AgentClient();
        var store = new ScheduledTransferStore();
        var run = await store.ClaimRetryAsync(id, cancellationToken);
        var progress = json ? null : new InlineProgress<string>(value => Console.Error.WriteLine(Clean(value)));
        var result = await new ScheduledTransferEngine(state, agents, store).RunClaimedAsync(run, progress, cancellationToken);
        WriteRun(result, json, "schedule retry");
        return result.State == ScheduledTransferRunState.Succeeded ? 0 : 1;
    }

    private static async Task<int> EnableAsync(string[] args, bool enabled, CancellationToken cancellationToken)
    {
        var (id, json) = ParseIdAndJson(args, enabled ? "schedule enable" : "schedule disable");
        var saved = await new ScheduledTransferStore().SetEnabledAsync(id, enabled, cancellationToken);
        if (json) WriteJson(new { schemaVersion = 1, ok = true, command = enabled ? "schedule enable" : "schedule disable", schedule = saved });
        else Console.Out.WriteLine($"{Clean(saved.Name)} is now {(enabled ? "enabled" : "paused")}.");
        return 0;
    }

    private static async Task<int> RemoveAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length is < 2 or > 3 || !Guid.TryParse(args[0], out var id) || !args.Contains("--yes", StringComparer.Ordinal)
            || args.Skip(1).Any(item => item is not "--yes" and not "--json"))
            throw CliException.Usage("Usage: opticon schedule remove <schedule-id> --yes [--json]");
        var json = args.Contains("--json", StringComparer.Ordinal);
        if (!await new ScheduledTransferStore().DeleteAsync(id, cancellationToken))
            throw new CliException("schedule_not_found", "The scheduled transfer was not found.", 1);
        if (json) WriteJson(new { schemaVersion = 1, ok = true, command = "schedule remove", scheduleId = id });
        else Console.Out.WriteLine($"Deleted scheduled transfer {id}. Its history was kept.");
        return 0;
    }

    private static async Task<int> HistoryAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = OptionSet.Parse(args, new HashSet<string>(StringComparer.Ordinal) { "--schedule", "--limit" },
            new HashSet<string>(StringComparer.Ordinal));
        Guid? scheduleId = null;
        if (parsed.TryValue("--schedule", out var schedule))
        {
            if (!Guid.TryParse(schedule, out var parsedId)) throw CliException.Usage("--schedule requires a schedule ID.");
            scheduleId = parsedId;
        }
        var limit = 50;
        if (parsed.TryValue("--limit", out var limitText)
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 500))
            throw CliException.Usage("--limit must be from 1 through 500.");
        var document = await new ScheduledTransferStore().LoadAsync(cancellationToken);
        var history = document.History.Where(item => !scheduleId.HasValue || item.ScheduleId == scheduleId).Take(limit).ToArray();
        if (parsed.Json) WriteJson(new { schemaVersion = 1, ok = true, command = "schedule history", runs = history });
        else if (history.Length == 0) Console.Out.WriteLine("No scheduled-transfer runs match.");
        else
        {
            Console.Out.WriteLine("RUN ID\tSTARTED\tNAME\tTRIGGER\tRESULT\tFILES\tDETAIL");
            foreach (var run in history) Console.Out.WriteLine(string.Join('\t', run.Id, run.StartedAt.ToLocalTime().ToString("g"),
                Clean(run.ScheduleName), run.Trigger, run.State, $"{run.FilesTransferred}/{run.FilesDiscovered}", Clean(run.Message)));
        }
        return 0;
    }

    private static string FriendlyCron(string value, OptionSet options)
    {
        value = value.ToLowerInvariant();
        if (value == "minute") return "* * * * *";
        if (value == "hour") return "0 * * * *";
        var at = options.TryValue("--at", out var atText) ? atText : "09:00";
        if (!TimeSpan.TryParseExact(at, @"hh\:mm", CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1))
            throw CliException.Usage("--at must be a 24-hour time in HH:mm format.");
        if (value == "day") return $"{time.Minutes} {time.Hours} * * *";
        if (value == "week")
        {
            var day = options.TryValue("--day", out var dayText) ? dayText : "monday";
            var number = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.DayNames,
                item => item.Equals(day, StringComparison.OrdinalIgnoreCase));
            if (number < 0 && (!int.TryParse(day, out number) || number is < 0 or > 7))
                throw CliException.Usage("--day must be Sunday through Saturday or 0 through 7.");
            return $"{time.Minutes} {time.Hours} * * {number % 7}";
        }
        throw CliException.Usage("--every must be minute, hour, day, or week.");
    }

    private static async Task<AdminState> LoadStateAsync(CancellationToken cancellationToken)
    {
        var state = new AdminState();
        await state.InitializeAsync(cancellationToken);
        if (!state.Config.SetupComplete) throw new CliException("not_configured", "Complete Opticon command-center setup first.", 1);
        return state;
    }

    private static DeviceRecord SelectDevice(IEnumerable<DeviceRecord> devices, string selector)
    {
        var matches = devices.Where(item => (Guid.TryParse(selector, out var id) && item.Id == id)
            || item.TailscaleIp.Equals(selector, StringComparison.Ordinal)
            || item.TailnetDeviceId.Equals(selector, StringComparison.Ordinal)
            || item.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || item.HostName.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || item.DnsName.Equals(selector, StringComparison.OrdinalIgnoreCase)).DistinctBy(item => item.Id).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new CliException("device_not_found", $"No device exactly matches '{Clean(selector)}'.", 1),
            _ => throw new CliException("ambiguous_device", "The device selector is ambiguous; use its ID.", 1)
        };
    }

    private static T ParseEnum<T>(string text, string option) where T : struct, Enum =>
        Enum.TryParse<T>(text, true, out var value) ? value : throw CliException.Usage($"--{option} has an unsupported value '{Clean(text)}'.");

    private static (Guid Id, bool Json) ParseIdAndJson(string[] args, string command)
    {
        if (args.Length is < 1 or > 2 || !Guid.TryParse(args[0], out var id) || (args.Length == 2 && args[1] != "--json"))
            throw CliException.Usage($"Usage: opticon {command} <id> [--json]");
        return (id, args.Length == 2);
    }

    private static bool ParseJsonOnly(string[] args, string command)
    {
        if (args.Length == 0) return false;
        if (args.Length == 1 && args[0] == "--json") return true;
        throw CliException.Usage($"Usage: opticon {command} [--json]");
    }

    private static void RequireExclusive(OptionSet set, params string[] names)
    {
        if (names.Count(set.Has) > 1) throw CliException.Usage("Use only one of " + string.Join(", ", names) + ".");
    }

    private static void WriteRun(ScheduledTransferRun run, bool json, string command)
    {
        if (json) WriteJson(new { schemaVersion = 1, ok = run.State == ScheduledTransferRunState.Succeeded, command, run });
        else Console.Out.WriteLine($"{Clean(run.ScheduleName)}: {run.State}. {Clean(run.Message)} Run ID: {run.Id}");
    }

    private static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
    private static string Clean(string? value) => Program.SanitizeDiagnostic(value);

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    { "--name", "--device", "--direction", "--local-folder", "--remote-root", "--remote-folder", "--extension",
      "--regex", "--cron", "--every", "--at", "--day", "--timezone" };
    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    { "--recursive", "--no-recursive", "--move", "--copy", "--overwrite", "--no-overwrite", "--disabled",
      "--enabled", "--all-files" };

    private sealed class OptionSet
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        public bool Json { get; private set; }
        public bool Has(string name) => _values.ContainsKey(name);
        public string Value(string name) => _values[name] ?? string.Empty;
        public bool TryValue(string name, out string value)
        {
            if (_values.TryGetValue(name, out var found) && found is not null) { value = found; return true; }
            value = string.Empty; return false;
        }
        public static OptionSet Parse(string[] args, IReadOnlySet<string> values, IReadOnlySet<string> flags)
        {
            var result = new OptionSet();
            for (var index = 0; index < args.Length; index++)
            {
                var name = args[index];
                if (name == "--json")
                {
                    if (result.Json) throw CliException.Usage("--json may be specified only once.");
                    result.Json = true; continue;
                }
                if (!values.Contains(name) && !flags.Contains(name)) throw CliException.Usage($"Unknown option '{Clean(name)}'.");
                if (result._values.ContainsKey(name)) throw CliException.Usage($"{name} may be specified only once.");
                if (values.Contains(name))
                {
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])) throw CliException.Usage($"{name} requires a value.");
                    result._values[name] = args[index];
                }
                else result._values[name] = null;
            }
            return result;
        }
    }
}
