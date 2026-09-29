using Omega.MarketData.Transport;

namespace Omega.MarketData.Tests.TestSupport;

/// <summary>
/// In-memory transport that replays a script. When the script is exhausted it
/// stays silent (like a dead connection) until the receive is cancelled.
/// </summary>
internal sealed class ScriptedTransport : IWebSocketTransport
{
    private readonly Exception? _connectFailure;
    private readonly Queue<Func<string?>> _steps;

    private ScriptedTransport(Exception? connectFailure, IEnumerable<Func<string?>> steps)
    {
        _connectFailure = connectFailure;
        _steps = new Queue<Func<string?>>(steps);
    }

    public bool IsDisposed { get; private set; }

    public static ScriptedTransport Sending(params string[] messages) =>
        new(null, messages.Select(message => (Func<string?>)(() => message)));

    public static ScriptedTransport SendingThenClosing(params string[] messages) =>
        new(null, messages.Select(message => (Func<string?>)(() => message)).Append(() => null));

    public static ScriptedTransport SendingThenFailing(params string[] messages) =>
        new(null, messages.Select(message => (Func<string?>)(() => message)).Append(() => throw new IOException("Connection reset.")));

    public static ScriptedTransport FailingToConnect() => new(new IOException("Host unreachable."), []);

    public static ScriptedTransport Silent() => new(null, []);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _connectFailure is null ? Task.CompletedTask : Task.FromException(_connectFailure);
    }

    public async Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken)
    {
        if (_steps.TryDequeue(out var step))
        {
            return step();
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return null;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out scripted transports in order, then silent ones.</summary>
internal sealed class ScriptedTransportFactory(params ScriptedTransport[] transports) : IWebSocketTransportFactory
{
    private readonly Queue<ScriptedTransport> _transports = new(transports);
    private int _created;

    public int CreatedCount => Volatile.Read(ref _created);

    public IWebSocketTransport Create()
    {
        Interlocked.Increment(ref _created);

        lock (_transports)
        {
            return _transports.TryDequeue(out var transport) ? transport : ScriptedTransport.Silent();
        }
    }
}

/// <summary>Clock that jumps one hour every time it is read; timers still use real time.</summary>
internal sealed class HourlySteppingTimeProvider : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour), TimeSpan.Zero);
}
