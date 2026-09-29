using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Omega.MarketData.Transport;

/// <summary>
/// <see cref="IWebSocketTransport"/> over <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// Heartbeats: Binance sends a ping frame every 20 seconds and expects a pong
/// echoing its payload within a minute. The .NET WebSocket implementation
/// answers pings automatically while a receive is pending, and OMEGA always
/// has one pending. Client keep-alive frames are disabled because unsolicited
/// pongs do not prevent disconnection and count towards the exchange's
/// incoming-message limit.
/// </remarks>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    /// <summary>Upper bound for one message; a kline event is well under 1 KB.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly ClientWebSocket _socket = new();

    public ClientWebSocketTransport()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.Zero;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public async Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken)
    {
        var message = new ArrayBufferWriter<byte>(4096);

        while (true)
        {
            var result = await _socket.ReceiveAsync(message.GetMemory(4096), cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Received a binary frame; only text messages are expected.");
            }

            message.Advance(result.Count);

            if (message.WrittenCount > MaxMessageBytes)
            {
                throw new InvalidDataException($"Message exceeds {MaxMessageBytes} bytes.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.WrittenSpan);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort: the socket is disposed right after.
            }
        }

        _socket.Dispose();
    }
}

public sealed class ClientWebSocketTransportFactory : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => new ClientWebSocketTransport();
}
