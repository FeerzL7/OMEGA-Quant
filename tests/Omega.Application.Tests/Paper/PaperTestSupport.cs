using Omega.Execution;
using Omega.Execution.Paper;

namespace Omega.Application.Tests.Paper;

/// <summary>Clock the test moves by hand, as candles "arrive".</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory <see cref="IPaperTradingStore"/> with the same semantics as the PostgreSQL one.</summary>
internal sealed class InMemoryPaperTradingStore : IPaperTradingStore
{
    private readonly Dictionary<string, PaperSessionRecord> _sessions = [];
    private readonly Dictionary<Guid, Order> _orders = [];
    private readonly List<PaperCommand> _commands = [];

    public List<PaperTrade> Trades { get; } = [];

    public List<PaperDecision> Decisions { get; } = [];

    public int Steps { get; private set; }

    public Task<PaperSessionRecord?> GetSessionAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.GetValueOrDefault(name));

    public Task<IReadOnlyList<PaperSessionRecord>> ListSessionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaperSessionRecord>>([.. _sessions.Values]);

    public Task CreateSessionAsync(PaperSessionRecord session, CancellationToken cancellationToken)
    {
        _sessions.Add(session.Name, session);
        return Task.CompletedTask;
    }

    public Task SaveStepAsync(PaperStep step, CancellationToken cancellationToken)
    {
        var session = _sessions.Values.Single(s => s.Id == step.SessionId);
        _sessions[session.Name] = session with { StateJson = step.StateJson, LastCandleOpenTimeUtc = step.CandleOpenTimeUtc };
        foreach (var order in step.Orders)
        {
            _orders[order.Id] = order;
        }

        Trades.AddRange(step.Trades);
        Decisions.Add(step.Decision);
        Steps++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Order>> GetWorkingOrdersAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>([.. _orders.Values.Where(o => o.IsWorking)]);

    public Task<long> EnqueueCommandAsync(Guid sessionId, PaperCommandType type, string reason, string requestedBy, DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        var id = _commands.Count + 1L;
        _commands.Add(new PaperCommand(id, sessionId, type, reason, requestedBy, requestedAtUtc, null, null));
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<PaperCommand>> GetPendingCommandsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaperCommand>>([.. _commands.Where(c => c.SessionId == sessionId && c.AppliedAtUtc is null)]);

    public Task CompleteCommandAsync(long commandId, Guid sessionId, string result, string stateJson, DateTimeOffset appliedAtUtc, CancellationToken cancellationToken)
    {
        var index = _commands.FindIndex(c => c.Id == commandId);
        _commands[index] = _commands[index] with { AppliedAtUtc = appliedAtUtc, Result = result };
        var session = _sessions.Values.Single(s => s.Id == sessionId);
        _sessions[session.Name] = session with { StateJson = stateJson };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaperCommand>> GetCommandsAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaperCommand>>([.. _commands.Where(c => c.SessionId == sessionId).Reverse().Take(limit)]);

    public Task<IReadOnlyList<Order>> GetOrdersAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>([.. _orders.Values.OrderByDescending(o => o.CreatedAtUtc).Take(limit)]);

    public Task<IReadOnlyList<OrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderEvent>>(_orders.TryGetValue(orderId, out var order) ? order.Events : []);

    public Task<IReadOnlyList<PaperTrade>> GetTradesAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaperTrade>>([.. Trades.AsEnumerable().Reverse().Take(limit)]);

    public Task<IReadOnlyList<PaperDecision>> GetDecisionsAsync(Guid sessionId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PaperDecision>>([.. Decisions.AsEnumerable().Reverse().Take(limit)]);

    public IReadOnlyList<Order> AllOrders => [.. _orders.Values];

    /// <summary>Overwrites a stored session (to simulate how a database returns it).</summary>
    public void Replace(PaperSessionRecord session) => _sessions[session.Name] = session;
}
