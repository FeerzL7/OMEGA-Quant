namespace Omega.MarketData.Transport;

/// <summary>
/// Minimal text-message WebSocket connection. Exists so the stream's
/// reconnection and integrity logic can be tested without a network.
/// </summary>
public interface IWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the next complete text message, or <c>null</c> when the server
    /// closed the connection. Throws on transport or protocol errors.
    /// </summary>
    Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken);
}

/// <summary>Creates a fresh, unconnected transport for every connection attempt.</summary>
public interface IWebSocketTransportFactory
{
    IWebSocketTransport Create();
}
