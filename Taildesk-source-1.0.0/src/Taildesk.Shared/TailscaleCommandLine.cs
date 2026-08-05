namespace Taildesk.Shared;

public static class TailscaleCommandLine
{
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
