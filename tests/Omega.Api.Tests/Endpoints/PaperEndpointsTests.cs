using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Omega.Api.Endpoints;
using Omega.Core.Persistence;
using Omega.Execution;
using Omega.Execution.Paper;

namespace Omega.Api.Tests.Endpoints;

public class PaperEndpointsTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Kill_switch_requests_need_an_action_a_reason_and_an_author()
    {
        var result = await PaperEndpoints.KillSwitchAsync("paper-1", new KillSwitchRequest("explode", " ", null), new Store(), TimeProvider.System, CancellationToken.None);

        Assert.Equal(["action", "reason", "requestedBy"], Assert.IsType<ValidationProblem>(result.Result).ProblemDetails.Errors.Keys);
    }

    [Fact]
    public async Task A_valid_kill_switch_request_is_queued_for_the_worker()
    {
        var store = new Store();

        var result = await PaperEndpoints.KillSwitchAsync("paper-1", new KillSwitchRequest("trip", " exchange incident ", "fer"), store, TimeProvider.System, CancellationToken.None);

        var accepted = Assert.IsType<Accepted<KillSwitchAccepted>>(result.Result).Value!;
        var command = Assert.Single(store.Commands);
        Assert.Equal((PaperCommandType.TripKillSwitch, "exchange incident", "fer"), (command.Type, command.Reason, command.RequestedBy));
        Assert.Equal(command.Id, accepted.CommandId);
    }

    [Fact]
    public async Task Unknown_sessions_are_404()
    {
        var store = new Store();

        Assert.IsType<NotFound>((await PaperEndpoints.GetAsync("nope", store, CancellationToken.None)).Result);
        Assert.IsType<NotFound>((await PaperEndpoints.TradesAsync("nope", store, CancellationToken.None)).Result);
        Assert.IsType<NotFound>((await PaperEndpoints.KillSwitchAsync("nope", new KillSwitchRequest("reset", "r", "fer"), store, TimeProvider.System, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task An_unavailable_store_is_503_on_every_endpoint()
    {
        var down = new Store { Down = true };

        Assert.Equal(503, Status((await PaperEndpoints.ListAsync(down, CancellationToken.None)).Result));
        Assert.Equal(503, Status((await PaperEndpoints.GetAsync("paper-1", down, CancellationToken.None)).Result));
        Assert.Equal(503, Status((await PaperEndpoints.TradesAsync("paper-1", down, CancellationToken.None)).Result));
        Assert.Equal(503, Status((await PaperEndpoints.DecisionsAsync("paper-1", down, CancellationToken.None)).Result));
        Assert.Equal(503, Status((await PaperEndpoints.OrdersAsync("paper-1", down, CancellationToken.None)).Result));
        Assert.Equal(503, Status((await PaperEndpoints.CommandsAsync("paper-1", down, CancellationToken.None)).Result));
        Assert.Equal(503, Status((await PaperEndpoints.KillSwitchAsync("paper-1", new KillSwitchRequest("trip", "r", "fer"), down, TimeProvider.System, CancellationToken.None)).Result));
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private sealed class Store : IPaperTradingStore
    {
        private readonly PaperSessionRecord _session = new(Guid.NewGuid(), "paper-1", "BTCUSDT", "5m", "{}", "{}", Now, Now, null);

        public bool Down { get; init; }

        public List<PaperCommand> Commands { get; } = [];

        public Task<PaperSessionRecord?> GetSessionAsync(string name, CancellationToken c) => Run(() => name == _session.Name ? _session : null);

        public Task<IReadOnlyList<PaperSessionRecord>> ListSessionsAsync(CancellationToken c) => Run<IReadOnlyList<PaperSessionRecord>>(() => []);

        public Task CreateSessionAsync(PaperSessionRecord session, CancellationToken c) => throw new NotSupportedException();

        public Task SaveStepAsync(PaperStep step, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<Order>> GetWorkingOrdersAsync(Guid id, CancellationToken c) => Run<IReadOnlyList<Order>>(() => []);

        public Task<long> EnqueueCommandAsync(Guid id, PaperCommandType type, string reason, string by, DateTimeOffset at, CancellationToken c) => Run(() =>
        {
            Commands.Add(new PaperCommand(Commands.Count + 1, id, type, reason, by, at, null, null));
            return (long)Commands.Count;
        });

        public Task<IReadOnlyList<PaperCommand>> GetPendingCommandsAsync(Guid id, CancellationToken c) => Run<IReadOnlyList<PaperCommand>>(() => Commands);

        public Task CompleteCommandAsync(long id, Guid s, string r, string st, DateTimeOffset at, CancellationToken c) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaperCommand>> GetCommandsAsync(Guid id, int l, CancellationToken c) => Run<IReadOnlyList<PaperCommand>>(() => Commands);

        public Task<IReadOnlyList<Order>> GetOrdersAsync(Guid id, int l, CancellationToken c) => Run<IReadOnlyList<Order>>(() => []);

        public Task<IReadOnlyList<OrderEvent>> GetOrderEventsAsync(Guid id, CancellationToken c) => Run<IReadOnlyList<OrderEvent>>(() => []);

        public Task<IReadOnlyList<PaperTrade>> GetTradesAsync(Guid id, int l, CancellationToken c) => Run<IReadOnlyList<PaperTrade>>(() => []);

        public Task<IReadOnlyList<PaperDecision>> GetDecisionsAsync(Guid id, int l, CancellationToken c) => Run<IReadOnlyList<PaperDecision>>(() => []);

        private Task<T> Run<T>(Func<T> value) => Down ? Task.FromException<T>(new PersistenceException("down", isTransient: true)) : Task.FromResult(value());
    }
}
