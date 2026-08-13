namespace Taildesk.Shared;

public static class TailscaleCommandLine
{
    public static string NormalizeHostName(string value, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var safe = new string((value ?? string.Empty).ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? fallback.ToLowerInvariant() : safe[..Math.Min(safe.Length, 63)];
    }

    public static string[] BuildEnrollmentArguments(string loginServer, string authKey, string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(authKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        return
        [
            "up",
            "--reset",
            "--force-reauth",
            $"--login-server={loginServer}",
            $"--auth-key={authKey}",
            $"--hostname={hostName}",
            "--unattended=true",
            "--accept-dns=false",
            "--accept-routes=false"
        ];
    }
}
