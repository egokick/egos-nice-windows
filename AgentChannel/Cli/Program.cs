using System.Text;
using System.Text.Json;

namespace AgentChannel.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
        {
            PrintHelp();
            return 0;
        }

        if (Has(args, "post") || Has(args, "--post"))
        {
            var text = GetValue(args, "--text") ?? GetTrailingText(args, "post") ?? GetTrailingText(args, "--post");
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("Missing message text. Use --text \"message\".");
                return 2;
            }

            MessageStore.Append(AgentMessage.Create(
                from: GetValue(args, "--from") ?? Environment.UserName,
                to: GetValue(args, "--to") ?? "all",
                channel: GetValue(args, "--channel") ?? "general",
                fromSessionId: GetValue(args, "--session-id"),
                toSessionId: GetValue(args, "--to-session"),
                text: text));
            return 0;
        }

        if (Has(args, "read") || Has(args, "--read"))
        {
            var count = Math.Clamp(GetIntValue(args, "--count", 20), 1, 500);
            var channel = GetValue(args, "--channel");
            var format = GetValue(args, "--format") ?? "text";
            var messages = MessageStore.LoadRecent(count, channel);

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(messages, MessageStore.PrettyJsonOptions));
                return 0;
            }

            foreach (var message in messages)
            {
                Console.WriteLine(FormatMessage(message));
            }

            return 0;
        }

        if (Has(args, "route") || Has(args, "--route"))
        {
            var agent = GetValue(args, "--agent");
            var windowTitle = GetValue(args, "--window-title");
            if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(windowTitle))
            {
                Console.Error.WriteLine("Missing route fields. Use route --agent <name> --window-title <title-fragment>.");
                return 2;
            }

            var sessionId = GetValue(args, "--session-id");
            RouteStore.Upsert(agent, windowTitle, sessionId);
            Console.WriteLine(string.IsNullOrWhiteSpace(sessionId)
                ? $"Route saved: {agent} -> window title containing \"{windowTitle}\""
                : $"Route saved: {agent} ({sessionId}) -> window title containing \"{windowTitle}\"");
            return 0;
        }

        if (Has(args, "unroute") || Has(args, "--unroute"))
        {
            var agent = GetValue(args, "--agent");
            if (string.IsNullOrWhiteSpace(agent))
            {
                Console.Error.WriteLine("Missing agent. Use unroute --agent <name>.");
                return 2;
            }

            RouteStore.Remove(agent);
            Console.WriteLine($"Route removed: {agent}");
            return 0;
        }

        if (Has(args, "routes") || Has(args, "--routes"))
        {
            foreach (var route in RouteStore.Load())
            {
                var session = string.IsNullOrWhiteSpace(route.SessionId) ? string.Empty : $" ({route.SessionId})";
                Console.WriteLine($"{route.Agent}{session} -> {route.WindowTitle}");
            }

            return 0;
        }

        Console.Error.WriteLine("Unknown command.");
        PrintHelp();
        return 2;
    }

    private static bool Has(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string? GetTrailingText(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index == args.Length - 1)
        {
            return null;
        }

        return string.Join(" ", args.Skip(index + 1));
    }

    private static int GetIntValue(string[] args, string name, int fallback)
    {
        var value = GetValue(args, name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string FormatMessage(AgentMessage message)
    {
        var localTime = message.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var session = string.IsNullOrWhiteSpace(message.FromSessionId) ? string.Empty : $" ({message.FromSessionId})";
        var toSession = string.IsNullOrWhiteSpace(message.ToSessionId) ? string.Empty : $" ({message.ToSessionId})";
        return $"[{localTime}] #{message.Channel} {message.From}{session} -> {message.To}{toSession}: {message.Text}";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AgentChannel.Cli");
        Console.WriteLine();
        Console.WriteLine("Post a message:");
        Console.WriteLine("  AgentChannel.Cli.exe post --from codex --session-id <id> --to all --text \"hello\"");
        Console.WriteLine();
        Console.WriteLine("Read messages:");
        Console.WriteLine("  AgentChannel.Cli.exe read");
        Console.WriteLine("  AgentChannel.Cli.exe read --count 50");
        Console.WriteLine("  AgentChannel.Cli.exe read --channel general --format json");
        Console.WriteLine();
        Console.WriteLine("Route directed messages into an AI's Codex session:");
        Console.WriteLine("  AgentChannel.Cli.exe route --agent reviewer --session-id <id> --window-title \"Reviewer Codex\"");
        Console.WriteLine("  AgentChannel.Cli.exe routes");
        Console.WriteLine("  AgentChannel.Cli.exe unroute --agent reviewer");
    }
}

internal sealed record AgentMessage(
    string Id,
    DateTime TimestampUtc,
    string From,
    string To,
    string Channel,
    string Text,
    string? FromSessionId = null,
    string? ToSessionId = null)
{
    public static AgentMessage Create(string? from, string? to, string? channel, string? fromSessionId, string? toSessionId, string text)
    {
        return new AgentMessage(
            Id: Guid.NewGuid().ToString("N")[..12],
            TimestampUtc: DateTime.UtcNow,
            From: Normalize(from, Environment.UserName),
            To: Normalize(to, "all"),
            Channel: Normalize(channel, "general"),
            Text: text.Trim(),
            FromSessionId: NormalizeOptional(fromSessionId),
            ToSessionId: NormalizeOptional(toSessionId));
    }

    private static string Normalize(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= 96 ? normalized : normalized[..96];
    }
}

internal static class MessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentChannel");

    private static readonly string MessagesPath = Path.Combine(StoreDirectory, "messages.jsonl");

    public static void Append(AgentMessage message)
    {
        EnsureStoreExists();
        using var mutex = new Mutex(false, "AgentChannel.MessageStore");
        mutex.WaitOne();
        try
        {
            File.AppendAllText(MessagesPath, JsonSerializer.Serialize(message, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static IReadOnlyList<AgentMessage> LoadRecent(int count, string? channel)
    {
        EnsureStoreExists();
        string[] lines;
        using var mutex = new Mutex(false, "AgentChannel.MessageStore");
        mutex.WaitOne();
        try
        {
            lines = File.ReadAllLines(MessagesPath, Encoding.UTF8);
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        var messages = new List<AgentMessage>();
        foreach (var line in lines.Reverse())
        {
            try
            {
                var message = JsonSerializer.Deserialize<AgentMessage>(line);
                if (message is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(channel)
                    && !string.Equals(message.Channel, channel.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                messages.Add(message);
                if (messages.Count >= count)
                {
                    break;
                }
            }
            catch
            {
            }
        }

        messages.Reverse();
        return messages;
    }

    private static void EnsureStoreExists()
    {
        Directory.CreateDirectory(StoreDirectory);
        if (!File.Exists(MessagesPath))
        {
            File.WriteAllText(MessagesPath, string.Empty, Encoding.UTF8);
        }
    }
}

internal sealed class AgentRoute
{
    public string Agent { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public string WindowTitle { get; set; } = string.Empty;
}

internal static class RouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentChannel");

    private static readonly string RoutesPath = Path.Combine(StoreDirectory, "routes.json");

    public static IReadOnlyList<AgentRoute> Load()
    {
        EnsureRoutesFileExists();
        try
        {
            return JsonSerializer.Deserialize<List<AgentRoute>>(File.ReadAllText(RoutesPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Upsert(string agent, string windowTitle, string? sessionId)
    {
        var routes = Load()
            .Where(route => !string.Equals(route.Agent, agent, StringComparison.OrdinalIgnoreCase))
            .ToList();
        routes.Add(new AgentRoute
        {
            Agent = agent.Trim(),
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
            WindowTitle = windowTitle.Trim()
        });
        Save(routes.OrderBy(route => route.Agent, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static void Remove(string agent)
    {
        Save(Load()
            .Where(route => !string.Equals(route.Agent, agent, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    private static void Save(IReadOnlyList<AgentRoute> routes)
    {
        Directory.CreateDirectory(StoreDirectory);
        File.WriteAllText(RoutesPath, JsonSerializer.Serialize(routes, JsonOptions), Encoding.UTF8);
    }

    private static void EnsureRoutesFileExists()
    {
        Directory.CreateDirectory(StoreDirectory);
        if (!File.Exists(RoutesPath))
        {
            Save([]);
        }
    }
}
