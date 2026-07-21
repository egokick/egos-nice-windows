using System.Text.Json;

namespace AdminPanel;

internal static class AdminPanelOrderStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LightDarkToggle");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "admin-panel.json");

    public static IReadOnlyList<AdminAppDefinition> LoadOrderedApps()
    {
        IEnumerable<string>? savedOrder = null;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var document = JsonSerializer.Deserialize<AdminPanelOrderDocument>(
                    File.ReadAllText(SettingsPath));
                savedOrder = document?.AppOrder;
            }
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or JsonException
                                             or NotSupportedException)
        {
            // A damaged layout preference must never prevent the tray app from opening.
        }

        return NormalizeOrder(savedOrder)
            .Select(AdminAppCatalog.GetById)
            .ToArray();
    }

    public static IReadOnlyList<string> NormalizeOrder(IEnumerable<string>? savedOrder)
    {
        var knownIds = AdminAppCatalog.Apps
            .Select(app => app.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(AdminAppCatalog.Apps.Count);

        if (savedOrder is not null)
        {
            foreach (var appId in savedOrder)
            {
                if (!string.IsNullOrWhiteSpace(appId)
                    && knownIds.Contains(appId)
                    && seen.Add(appId))
                {
                    normalized.Add(AdminAppCatalog.GetById(appId).Id);
                }
            }
        }

        foreach (var app in AdminAppCatalog.Apps)
        {
            if (seen.Add(app.Id))
            {
                normalized.Add(app.Id);
            }
        }

        return normalized;
    }

    public static bool TrySaveOrder(IEnumerable<string> appIds, out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(appIds);

        var temporaryPath = string.Empty;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            temporaryPath = Path.Combine(
                SettingsDirectory,
                $"admin-panel.{Guid.NewGuid():N}.tmp");

            var document = new AdminPanelOrderDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                AppOrder = NormalizeOrder(appIds).ToList()
            };

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or System.Security.SecurityException)
        {
            errorMessage = $"The app order could not be saved: {exception.Message}";
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private sealed class AdminPanelOrderDocument
    {
        public AdminPanelOrderDocument()
        {
        }

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<string> AppOrder { get; set; } = [];
    }
}
