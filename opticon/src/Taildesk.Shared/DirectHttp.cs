using System.Net;

namespace Taildesk.Shared;

public static class DirectHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public static HttpClient CreateClient(TimeSpan timeout) => new(CreateHandler())
    {
        Timeout = timeout
    };
}
