using System.Diagnostics;
using System.Windows.Forms;
using Taildesk.Shared;

namespace Taildesk.InviteLauncher;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var extractionDirectory = Path.Combine(Path.GetTempPath(), "Taildesk Invite", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extractionDirectory);
            var invitationPath = Environment.ProcessPath
                                 ?? throw new InvalidOperationException("Windows did not provide the invitation path.");
            await InvitationSigning.VerifyAuthenticodeAsync(invitationPath);
            await InviteContainer.ExtractAsync(invitationPath, extractionDirectory);

            var setup = Path.Combine(extractionDirectory, "Taildesk.Setup.exe");
            if (!File.Exists(setup)) throw new InvalidDataException("The Opticon setup program is missing from this invitation.");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                WorkingDirectory = extractionDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows could not start Opticon Setup.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Opticon could not start.\n\n" + exception.Message + "\n\nAsk the person who sent this invitation to create a new one.",
                "Opticon invitation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(extractionDirectory, true); } catch { }
        }
    }
}
