using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class ChromiumDevToolsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private int _messageId;

    private ChromiumDevToolsClient()
    {
    }

    public static async Task<ChromiumDevToolsClient> ConnectAsync(string webSocketUrl, CancellationToken cancellationToken)
    {
        var client = new ChromiumDevToolsClient();
        await client._socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken);
        return client;
    }

    public async Task<JsonElement> SendCommandAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var messageId = Interlocked.Increment(ref _messageId);
        var payload = JsonSerializer.Serialize(new
        {
            id = messageId,
            method,
            @params = parameters ?? new { }
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        while (true)
        {
            var message = await ReceiveMessageAsync(cancellationToken);
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt32() != messageId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                throw new InvalidOperationException($"CDP command '{method}' failed: {errorElement}");
            }

            return root.Clone();
        }
    }

    private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var memory = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The browser debugging connection closed unexpectedly.");
            }

            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch
            {
                // Best-effort close.
            }
        }

        _socket.Dispose();
    }
}
