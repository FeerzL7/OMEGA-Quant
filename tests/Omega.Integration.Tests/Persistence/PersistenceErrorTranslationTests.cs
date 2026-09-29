using Npgsql;
using Omega.Core.MarketData;
using Omega.Core.Persistence;
using Omega.Infrastructure.Persistence;

namespace Omega.Integration.Tests.Persistence;

/// <summary>Driver errors reach callers as <see cref="PersistenceException"/>. Needs no PostgreSQL server.</summary>
public class PersistenceErrorTranslationTests
{
    [Fact]
    public async Task Unreachable_database_is_reported_as_a_transient_persistence_failure()
    {
        await using var unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=omega;Username=omega;Password=unused;Timeout=3");
        var store = new PostgresCandleStore(unreachable);

        var error = await Assert.ThrowsAsync<PersistenceException>(
            () => store.GetLatestAsync("BTCUSDT", CandleInterval.FiveMinutes, CancellationToken.None));

        Assert.True(error.IsTransient);
        Assert.IsType<NpgsqlException>(error.InnerException);
    }
}
