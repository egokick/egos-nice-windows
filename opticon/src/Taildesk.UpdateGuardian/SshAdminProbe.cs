using System.Text.Json;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

/// <summary>
/// Signed, fixed-purpose remote proof that an OpenSSH child retained the full
/// dedicated administrator token. It performs no requested action and accepts
/// only a bounded caller challenge.
/// </summary>
internal static class SshAdminProbe
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count >= 1
        && args[0].Equals(RemoteAdministrationProtocol.SshAdminProbeArgument, StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !IsValidChallenge(args[1]))
        {
            Console.Error.WriteLine("The SSH administrator probe requires one bounded challenge.");
            return 2;
        }

        try
        {
            var attestation = SshAdminToken.InspectCurrent(args[1]);
            Console.Out.WriteLine(JsonSerializer.Serialize(attestation, JsonDefaults.Options));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SSH administrator attestation failed: " + exception.Message);
            return 1;
        }
    }

    private static bool IsValidChallenge(string value) =>
        value.Length is >= 32 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
