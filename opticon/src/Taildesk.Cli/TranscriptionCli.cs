using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Taildesk.Admin;
using Taildesk.Shared;

namespace Taildesk.Cli;

internal sealed class TranscriptionCli
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.Out.WriteLine("Run 'opticon help' for transcription commands and options.");
            return 0;
        }
        return args[0].ToLowerInvariant() switch
        {
            "devices" => await DevicesAsync(args[1..], cancellationToken),
            "sync" or "download" => await SyncAsync(args[1..], cancellationToken),
            _ => throw CliException.Usage($"Unknown transcriptions command '{Clean(args[0])}'. Run 'opticon help' for usage.")
        };
    }

    private static async Task<int> DevicesAsync(string[] args, CancellationToken cancellationToken)
    {
        var json = ParseJsonOnly(args, "transcriptions devices");
        var localAgent = await TranscriptionTransferService.LoadLocalAgentAsync(cancellationToken);
        var canAccessRemote = localAgent?.Role == DeviceRole.ControllerAndManaged;
        var state = new AdminState();
        try { await state.InitializeAsync(cancellationToken); }
        catch when (!canAccessRemote) { }
        var local = FindLocal(state.Config.Devices, localAgent);
        var devices = new List<object>
        {
            new
            {
                id = local?.Id ?? localAgent?.DeviceId ?? Guid.Empty,
                name = local is null ? Environment.MachineName : DisplayName(local),
                hostName = Environment.MachineName,
                role = localAgent?.Role ?? DeviceRole.ManagedOnly,
                state = local?.State ?? DeviceConnectionState.Unknown,
                isLocal = true
            }
        };
        if (canAccessRemote && state.Config.SetupComplete)
        {
            devices.AddRange(state.Config.Devices.Where(item => local is null || item.Id != local.Id)
                .OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => (object)new
                {
                    id = item.Id, name = DisplayName(item), hostName = item.HostName,
                    role = item.Role, state = item.State, isLocal = false
                }));
        }
        if (json)
            WriteJson(new { schemaVersion = 1, ok = true, command = "transcriptions devices",
                currentRole = localAgent?.Role ?? DeviceRole.ManagedOnly, canAccessRemote, devices });
        else
        {
            Console.Out.WriteLine($"Current role: {localAgent?.Role.ToString() ?? "ManagedOnly"}");
            Console.Out.WriteLine("ID\tNAME\tHOST\tSTATE\tLOCAL");
            foreach (dynamic device in devices) Console.Out.WriteLine($"{device.id}\t{Clean(device.name)}\t{Clean(device.hostName)}\t{device.state}\t{device.isLocal}");
        }
        return 0;
    }

    private static async Task<int> SyncAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseSync(args);
        var localAgent = await TranscriptionTransferService.LoadLocalAgentAsync(cancellationToken);
        TranscriptionTransferService.RequireControllerAndManaged(localAgent);
        var state = new AdminState();
        await state.InitializeAsync(cancellationToken);
        if (!state.Config.SetupComplete) throw new CliException("not_configured", "Complete Opticon command-center setup first.", 1);
        var device = SelectDevice(state.Config.Devices, options.Device);
        if (FindLocal(state.Config.Devices, localAgent)?.Id == device.Id)
            throw new CliException("local_device", "The local Continuous-transcriber folder should be read directly, not downloaded through Opticon.", 1);
        var service = new TranscriptionTransferService(new AgentClient());
        var result = await service.SyncAsync(device, options.Destination, options.Start, options.End,
            options.MetadataOnly, options.Move, cancellationToken);
        if (options.Json) WriteJson(new { schemaVersion = 1, ok = true, command = "transcriptions sync", result });
        else Console.Out.WriteLine($"Downloaded {result.TranscriptFiles:N0} transcript file(s), {result.AudioFiles:N0} audio file(s), and {result.ManifestFiles:N0} manifest(s) from {Clean(result.DeviceName)}.");
        return 0;
    }

    private static SyncOptions ParseSync(string[] args)
    {
        string? device = null, destination = null, start = null, end = null;
        var json = false; var metadataOnly = false; var move = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--device": device = Value(args, ref index, "--device"); break;
                case "--destination": destination = Value(args, ref index, "--destination"); break;
                case "--start": start = Value(args, ref index, "--start"); break;
                case "--end": end = Value(args, ref index, "--end"); break;
                case "--metadata-only" when !metadataOnly: metadataOnly = true; break;
                case "--move" when !move: move = true; break;
                case "--json" when !json: json = true; break;
                default: throw CliException.Usage($"Unknown or repeated option '{Clean(args[index])}' for transcriptions sync.");
            }
        }
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(destination)
            || !DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedStart)
            || !DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedEnd))
            throw CliException.Usage("Usage: opticon transcriptions sync --device <device> --destination <folder> --start <ISO-8601> --end <ISO-8601> [--metadata-only] [--move] [--json]");
        return new SyncOptions(device, Path.GetFullPath(destination), parsedStart, parsedEnd, metadataOnly, move, json);
    }

    private static string Value(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])) throw CliException.Usage($"{option} requires a value.");
        return args[index];
    }

    private static DeviceRecord SelectDevice(IEnumerable<DeviceRecord> devices, string selector)
    {
        var matches = devices.Where(item => (Guid.TryParse(selector, out var id) && item.Id == id)
            || item.TailnetDeviceId.Equals(selector, StringComparison.Ordinal)
            || item.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || item.HostName.Equals(selector, StringComparison.OrdinalIgnoreCase)).DistinctBy(item => item.Id).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new CliException("device_not_found", $"No device exactly matches '{Clean(selector)}'.", 1),
            _ => throw new CliException("ambiguous_device", "The device selector is ambiguous; use its immutable device ID.", 1)
        };
    }

    private static DeviceRecord? FindLocal(IEnumerable<DeviceRecord> devices, AgentConfig? agent) => devices.FirstOrDefault(item =>
        item.Id == agent?.DeviceId || item.HostName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || item.Name.Equals(agent?.DeviceName ?? Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    private static string DisplayName(DeviceRecord device) => string.IsNullOrWhiteSpace(device.Name) ? device.HostName : device.Name;
    private static bool ParseJsonOnly(string[] args, string command) => args.Length switch
    { 0 => false, 1 when args[0] == "--json" => true, _ => throw CliException.Usage($"Usage: opticon {command} [--json]") };
    private static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
    private static string Clean(string? value) => Program.SanitizeDiagnostic(value);
    private sealed record SyncOptions(string Device, string Destination, DateTimeOffset Start, DateTimeOffset End, bool MetadataOnly, bool Move, bool Json);
}