using System.Text;
using Taildesk.Shared;

namespace Taildesk.Setup;

internal static class SetupDiagnostics
{
    private const int MaximumDetailLength = 16 * 1024;

    internal static string LogPath => Path.Combine(
        AppPaths.BootstrapHandoffDirectory,
        "setup.log");

    internal static async Task WriteAsync(
        string eventName,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = HostedBootstrapper.CreateOrRequireProtectedHandoffRoot();
            var entry = new StringBuilder()
                .Append('[').Append(DateTimeOffset.UtcNow.ToString("O")).Append("] ")
                .Append(eventName).AppendLine();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                entry.Append(Trim(detail)).AppendLine();
            }

            await File.AppendAllTextAsync(LogPath, entry.ToString(), Encoding.UTF8, cancellationToken);
        }
        catch
        {
            // Diagnostics must never prevent setup from continuing or reporting its real failure.
        }
    }

    private static string Trim(string detail) => detail.Length <= MaximumDetailLength
        ? detail
        : detail[..MaximumDetailLength] + Environment.NewLine + "[diagnostic output truncated]";
}
