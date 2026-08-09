using System.Text.RegularExpressions;

namespace Taildesk.Shared;

public enum ScheduledTransferDirection { Upload, Download }
public enum ScheduledTransferMode { Copy, Move }
public enum ScheduledTransferFilter { All, Extension, Regex }
public enum ScheduledTransferRunState { Running, Succeeded, PartiallySucceeded, Failed }
public enum ScheduledTransferTrigger { Schedule, Manual, Retry }
public enum ScheduledTransferFileState { Succeeded, Failed, Skipped }

public sealed class ScheduledTransferDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Guid DeviceId { get; set; }
    public ScheduledTransferDirection Direction { get; set; }
    public string LocalFolder { get; set; } = string.Empty;
    public string RemoteRoot { get; set; } = string.Empty;
    public string RemoteFolder { get; set; } = string.Empty;
    public ScheduledTransferFilter Filter { get; set; }
    public string FilterPattern { get; set; } = string.Empty;
    public bool Recursive { get; set; }
    public ScheduledTransferMode Mode { get; set; }
    public bool Overwrite { get; set; }
    public string CronExpression { get; set; } = "0 * * * *";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public Guid? ActiveRunId { get; set; }

    public ScheduledTransferDefinition Copy() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, DeviceId = DeviceId, Direction = Direction,
        LocalFolder = LocalFolder, RemoteRoot = RemoteRoot, RemoteFolder = RemoteFolder,
        Filter = Filter, FilterPattern = FilterPattern, Recursive = Recursive, Mode = Mode,
        Overwrite = Overwrite, CronExpression = CronExpression, TimeZoneId = TimeZoneId,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, LastStartedAt = LastStartedAt,
        NextRunAt = NextRunAt, ActiveRunId = ActiveRunId
    };
}

public sealed class ScheduledTransferFileResult
{
    public string RelativePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public ScheduledTransferFileState State { get; set; }
    public bool TransferConfirmed { get; set; }
    public bool SourceDeleted { get; set; }
    public string SourceIdentity { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string DestinationSha256 { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    public ScheduledTransferFileResult Copy() => new()
    {
        RelativePath = RelativePath,
        DestinationPath = DestinationPath,
        Bytes = Bytes,
        State = State,
        TransferConfirmed = TransferConfirmed,
        SourceDeleted = SourceDeleted,
        SourceIdentity = SourceIdentity,
        SourceSha256 = SourceSha256,
        DestinationSha256 = DestinationSha256,
        Error = Error
    };
}

public sealed class ScheduledTransferRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public string ScheduleName { get; set; } = string.Empty;
    public ScheduledTransferTrigger Trigger { get; set; }
    public Guid? RetryOfRunId { get; set; }
    public bool RetryRequiresDiscovery { get; set; }
    public List<ScheduledTransferFileResult> RetryCandidates { get; set; } = [];
    public ScheduledTransferDefinition Definition { get; set; } = new();
    public int OwnerProcessId { get; set; }
    public DateTimeOffset? OwnerProcessStartedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public ScheduledTransferRunState State { get; set; } = ScheduledTransferRunState.Running;
    public int FilesDiscovered { get; set; }
    public int FilesTransferred { get; set; }
    public int FilesFailed { get; set; }
    public long BytesTransferred { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<ScheduledTransferFileResult> Files { get; set; } = [];
}

public sealed class ScheduledTransferDocument
{
    public int SchemaVersion { get; set; } = 2;
    public List<ScheduledTransferDefinition> Schedules { get; set; } = [];
    public List<ScheduledTransferRun> History { get; set; } = [];
}

public static class ScheduledTransferHistoryPolicy
{
    public const int MaximumRuns = 500;
    public const int MaximumFileResults = 50_000;

    public static void Trim(ScheduledTransferDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.History ??= [];
        document.Schedules ??= [];

        var pinned = document.History
            .Where(run => run.State == ScheduledTransferRunState.Running)
            .Select(run => run.Id)
            .Concat(document.Schedules.Where(schedule => schedule.ActiveRunId.HasValue)
                .Select(schedule => schedule.ActiveRunId!.Value))
            .ToHashSet();
        pinned.IntersectWith(document.History.Select(run => run.Id));
        foreach (var retrySource in document.History
                     .Where(run => pinned.Contains(run.Id) && run.RetryOfRunId.HasValue)
                     .Select(run => run.RetryOfRunId!.Value))
            pinned.Add(retrySource);

        var keep = new HashSet<Guid>(pinned);
        var retainedFiles = document.History
            .Where(run => pinned.Contains(run.Id))
            .Sum(run => (long)run.Files.Count);
        var retainedRuns = keep.Count;
        foreach (var run in document.History)
        {
            if (keep.Contains(run.Id)) continue;
            if (retainedRuns >= MaximumRuns) break;
            if (retainedFiles + run.Files.Count > MaximumFileResults) break;
            keep.Add(run.Id);
            retainedRuns++;
            retainedFiles += run.Files.Count;
        }

        document.History.RemoveAll(run => !keep.Contains(run.Id));
    }
}

public static class ScheduledTransferRules
{
    public static void Validate(ScheduledTransferDefinition value)
    {
        if (value.Id == Guid.Empty) throw new InvalidDataException("The schedule ID is invalid.");
        value.Name = value.Name.Trim();
        if (value.Name.Length is < 1 or > 120) throw new InvalidDataException("The schedule name must contain 1 to 120 characters.");
        if (value.DeviceId == Guid.Empty) throw new InvalidDataException("Choose a destination or source device.");
        if (string.IsNullOrWhiteSpace(value.LocalFolder)) throw new InvalidDataException("Choose a local folder.");
        if (value.Direction == ScheduledTransferDirection.Download && value.Mode == ScheduledTransferMode.Move)
            throw new InvalidDataException("Remote-to-local move is disabled because remote deletion cannot be proven against the downloaded file.");

        value.LocalFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.LocalFolder.Trim()));
        value.RemoteRoot = value.RemoteRoot.Trim();
        value.RemoteFolder = NormalizeRemotePath(value.RemoteFolder);
        if (string.IsNullOrWhiteSpace(value.RemoteRoot)) throw new InvalidDataException("Choose a remote root.");
        if (value.Filter == ScheduledTransferFilter.Extension)
        {
            value.FilterPattern = value.FilterPattern.Trim();
            if (!value.FilterPattern.StartsWith('.')) value.FilterPattern = "." + value.FilterPattern;
            if (value.FilterPattern.Length < 2 || value.FilterPattern.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || value.FilterPattern.Contains('*') || value.FilterPattern.Contains('?'))
                throw new InvalidDataException("Enter one file extension such as .pdf or .jpg.");
        }
        else if (value.Filter == ScheduledTransferFilter.Regex)
        {
            value.FilterPattern = value.FilterPattern.Trim();
            if (value.FilterPattern.Length is < 1 or > 1000) throw new InvalidDataException("Enter a regular expression up to 1,000 characters.");
            try { _ = new Regex(value.FilterPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException exception) { throw new InvalidDataException("The file regular expression is invalid: " + exception.Message, exception); }
        }
        else value.FilterPattern = string.Empty;
        value.TimeZoneId = value.TimeZoneId.Trim();
        _ = ResolveTimeZone(value.TimeZoneId);
        _ = CronSchedule.Parse(value.CronExpression);
    }

    public static bool Matches(ScheduledTransferDefinition definition, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return definition.Filter switch
        {
            ScheduledTransferFilter.All => true,
            ScheduledTransferFilter.Extension => Path.GetExtension(normalized).Equals(definition.FilterPattern, StringComparison.OrdinalIgnoreCase),
            ScheduledTransferFilter.Regex => Regex.IsMatch(normalized, definition.FilterPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
            _ => false
        };
    }

    public static DateTimeOffset NextRun(ScheduledTransferDefinition definition, DateTimeOffset after) =>
        CronSchedule.Parse(definition.CronExpression).GetNextOccurrence(after, ResolveTimeZone(definition.TimeZoneId));

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException exception) { throw new InvalidDataException($"The time zone '{id}' is not installed.", exception); }
        catch (InvalidTimeZoneException exception) { throw new InvalidDataException($"The time zone '{id}' is invalid.", exception); }
    }

    public static string NormalizeRemotePath(string path)
    {
        path = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new InvalidDataException("The remote folder cannot contain . or .. path segments.");
        return path;
    }
}

public sealed class CronSchedule
{
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronSchedule(bool[] minutes, bool[] hours, bool[] days, bool[] months, bool[] weekdays, bool anyDay, bool anyWeekday)
    {
        _minutes = minutes; _hours = hours; _days = days; _months = months; _weekdays = weekdays;
        _anyDay = anyDay; _anyWeekday = anyWeekday;
    }

    public static CronSchedule Parse(string expression)
    {
        var fields = (expression ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5) throw new InvalidDataException("Cron must contain five fields: minute hour day-of-month month day-of-week.");
        var weekdays = ParseField(fields[4], 0, 7, WeekdayNames, normalizeSevenToZero: true);
        return new CronSchedule(
            ParseField(fields[0], 0, 59), ParseField(fields[1], 0, 23),
            ParseField(fields[2], 1, 31), ParseField(fields[3], 1, 12, MonthNames),
            weekdays, fields[2] == "*", fields[4] == "*");
    }

    public bool IsMatch(DateTime local)
    {
        var dayMatch = _days[local.Day];
        var weekdayMatch = _weekdays[(int)local.DayOfWeek];
        var calendarMatch = _anyDay ? weekdayMatch : _anyWeekday ? dayMatch : dayMatch || weekdayMatch;
        return _minutes[local.Minute] && _hours[local.Hour] && _months[local.Month] && calendarMatch;
    }

    public DateTimeOffset GetNextOccurrence(DateTimeOffset after, TimeZoneInfo timeZone)
    {
        var utc = after.UtcDateTime;
        var candidate = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        var limit = candidate.AddYears(5);
        while (candidate <= limit)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(candidate, timeZone);
            if (IsMatch(local)) return new DateTimeOffset(candidate, TimeSpan.Zero);
            candidate = candidate.AddMinutes(1);
        }
        throw new InvalidDataException("The cron schedule has no occurrence within the next five years.");
    }

    public static string Describe(string expression)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5) return expression;
        if (expression == "* * * * *") return "Every minute";
        if (fields[0] == "0" && fields[1] == "*" && fields[2] == "*" && fields[3] == "*" && fields[4] == "*") return "Every hour";
        if (int.TryParse(fields[0], out var minute) && int.TryParse(fields[1], out var hour) && fields[2] == "*" && fields[3] == "*")
        {
            var time = new DateTime(2000, 1, 1, hour, minute, 0).ToString("h:mm tt");
            if (fields[4] == "*") return $"Every day at {time}";
            if (int.TryParse(fields[4], out var weekday) && weekday is >= 0 and <= 7)
                return $"Every {(DayOfWeek)(weekday % 7)} at {time}";
        }
        return $"Cron: {expression}";
    }

    private static bool[] ParseField(string text, int minimum, int maximum,
        IReadOnlyDictionary<string, int>? names = null, bool normalizeSevenToZero = false)
    {
        var values = new bool[maximum + 1];
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var stepParts = item.Split('/');
            if (stepParts.Length > 2 || (stepParts.Length == 2 && (!int.TryParse(stepParts[1], out var parsedStep) || parsedStep < 1)))
                throw new InvalidDataException($"Invalid cron field '{text}'.");
            var step = stepParts.Length == 2 ? int.Parse(stepParts[1]) : 1;
            var range = stepParts[0];
            int start, end;
            if (range == "*") { start = minimum; end = maximum; }
            else
            {
                var rangeParts = range.Split('-');
                start = ParseValue(rangeParts[0], minimum, maximum, names);
                end = rangeParts.Length == 1 ? start : rangeParts.Length == 2
                    ? ParseValue(rangeParts[1], minimum, maximum, names)
                    : throw new InvalidDataException($"Invalid cron range '{range}'.");
                if (start > end) throw new InvalidDataException($"Cron range '{range}' runs backwards.");
            }
            for (var value = start; value <= end; value += step) values[normalizeSevenToZero && value == 7 ? 0 : value] = true;
        }
        if (!values.Any(value => value)) throw new InvalidDataException($"Cron field '{text}' selects no values.");
        return values;
    }

    private static int ParseValue(string text, int minimum, int maximum, IReadOnlyDictionary<string, int>? names)
    {
        if (names is not null && names.TryGetValue(text[..Math.Min(3, text.Length)], out var named)) return named;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Cron value '{text}' must be from {minimum} through {maximum}.");
        return value;
    }

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    { ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
      ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12 };
    private static readonly Dictionary<string, int> WeekdayNames = new(StringComparer.OrdinalIgnoreCase)
    { ["sun"] = 0, ["mon"] = 1, ["tue"] = 2, ["wed"] = 3, ["thu"] = 4, ["fri"] = 5, ["sat"] = 6 };
}
