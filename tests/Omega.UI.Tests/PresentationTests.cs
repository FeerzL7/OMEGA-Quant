using System.Net;
using System.Text;
using Omega.UI.Api;
using Omega.UI.Presentation;

namespace Omega.UI.Tests;

public class DisplayTests
{
    [Fact]
    public void Missing_values_are_dashes_never_zero()
    {
        Assert.Equal("—", Display.Price(null));
        Assert.Equal("—", Display.Percent((double?)null));
        Assert.Equal("—", Display.Percent(double.NaN));
        Assert.Equal("—", Display.Utc(null));
        Assert.Equal("—", Display.Age(null));
    }

    [Fact]
    public void Times_are_utc_and_labelled()
    {
        var local = new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.FromHours(-6));

        Assert.Equal("2026-10-02 14:30:00 UTC", Display.Utc(local));
    }

    [Fact]
    public void Percent_money_and_ages()
    {
        Assert.Equal("1.23 %", Display.Percent(0.0123));
        Assert.Equal("+0.120 %", Display.Percent(0.0012, 3, signed: true));
        Assert.Equal("-2.50 %", Display.Percent(-0.025m));
        Assert.Equal("+1,234.50 USDT", Display.SignedMoney(1234.5m));
        Assert.Equal("hace 12 s", Display.Age(TimeSpan.FromSeconds(12)));
        Assert.Equal("hace 3 min", Display.Age(TimeSpan.FromMinutes(3.2)));
        Assert.Equal("hace 2 h 5 min", Display.Age(new TimeSpan(2, 5, 0)));
        Assert.Equal("en el futuro (revisar reloj)", Display.Age(TimeSpan.FromSeconds(-5)));
        Assert.Equal("NO_TRADE", Display.Direction("NoTrade"));
    }

    [Theory]
    [InlineData(30, Freshness.Fresh)]      // 30 s after the close
    [InlineData(5 * 60 + 50, Freshness.Fresh)]
    [InlineData(10 * 60, Freshness.Late)]
    [InlineData(20 * 60, Freshness.Stale)]
    public void Candle_freshness(int secondsAfterClose, Freshness expected)
    {
        var close = new DateTimeOffset(2026, 10, 2, 12, 4, 59, TimeSpan.Zero);

        Assert.Equal(expected, DataAge.OfCandle(close, close.AddSeconds(secondsAfterClose), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
        Assert.Equal(Freshness.Unknown, DataAge.OfCandle(null, close, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void An_api_answer_older_than_three_refreshes_is_stale()
    {
        var received = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(Freshness.Fresh, DataAge.OfResponse(received, received.AddSeconds(14), TimeSpan.FromSeconds(5)));
        Assert.Equal(Freshness.Stale, DataAge.OfResponse(received, received.AddSeconds(16), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Decision_numbers_come_from_the_strategy_metrics()
    {
        var n = DecisionNumbers.From(new Dictionary<string, double> { ["raw_probability"] = 0.61, ["calibrated_probability"] = 0.58, ["expected_return"] = 0.0012 });
        var none = DecisionNumbers.From(new Dictionary<string, double>());

        Assert.Equal((0.61, 0.58, 0.0012), (n.RawProbability!.Value, n.CalibratedProbability!.Value, n.ExpectedReturn!.Value));
        Assert.Null(none.RawProbability);   // a baseline strategy reports no probability: shown as a dash
    }

    [Fact]
    public void Dashboard_options_are_validated()
    {
        Assert.Empty(new DashboardOptions().Validate());
        Assert.NotEmpty(new DashboardOptions { RefreshInterval = TimeSpan.FromMilliseconds(100) }.Validate());
        Assert.NotEmpty(new DashboardOptions { ApiTimeout = TimeSpan.FromSeconds(10) }.Validate());
        Assert.NotEmpty(new DashboardOptions { Interval = "1m" }.Validate());
    }
}

public class ChartTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Candles_map_prices_to_pixels_with_higher_prices_higher_up()
    {
        var model = Charts.Candles([(T0, 100m, 110m, 95m, 105m), (T0.AddMinutes(5), 105m, 106m, 90m, 92m)], 400, 200);

        var (rise, fall) = (model.Candles[0], model.Candles[1]);
        Assert.True(rise.Rising);
        Assert.False(fall.Rising);
        Assert.True(rise.WickTop < rise.BodyTop && rise.BodyTop < rise.BodyBottom && rise.BodyBottom < rise.WickBottom);
        Assert.True(fall.WickBottom > rise.WickBottom);   // 90 is below 95: drawn lower
        Assert.True(rise.X < fall.X);
        Assert.InRange(model.Min, 89m, 90m);                // data range plus 5 % padding, not truncated
        Assert.InRange(model.Max, 110m, 111m);
    }

    [Fact]
    public void Position_levels_extend_the_range_so_they_are_always_visible()
    {
        var model = Charts.Candles([(T0, 100m, 101m, 99m, 100m)], 400, 200, [(80m, "stop", "stop"), (130m, "objetivo", "target")]);

        Assert.True(model.Min < 80m && model.Max > 130m);
        Assert.All(model.Levels, l => Assert.InRange(l.Y, Charts.TopMargin, 200 - Charts.BottomMargin));
    }

    [Fact]
    public void A_flat_series_is_drawn_mid_height_and_an_empty_one_draws_nothing()
    {
        var flat = Charts.Line([(T0, 50.0), (T0.AddDays(1), 50.0)], 300, 100);
        var empty = Charts.Candles([], 300, 100);

        var y = double.Parse(flat.Points.Split(' ')[0].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(y, 40, 60);
        Assert.Empty(empty.Candles);
    }

    [Fact]
    public void Drawdown_charts_include_the_zero_line()
    {
        var model = Charts.Line([(T0, -0.02), (T0.AddDays(1), -0.05)], 300, 100, includeZero: true);

        Assert.NotNull(model.ZeroY);
        Assert.Contains(model.ValueTicks, t => t.Label == "0");
    }

    [Fact]
    public void Ticks_are_round_numbers_inside_the_range()
    {
        var ticks = Charts.Ticks(63_412m, 64_987m, 5);

        Assert.All(ticks, t => Assert.InRange(t, 63_412m, 64_987m));
        Assert.All(ticks, t => Assert.Equal(0m, t % 250m));
        Assert.True(ticks.Count >= 3);
    }
}

public class ApiClientTests
{
    [Fact]
    public async Task A_successful_answer_is_read()
    {
        var client = Client(HttpStatusCode.OK, """{"service":"Omega.Api","tradingMode":"PAPER","timestampUtc":"2026-10-02T12:00:00+00:00"}""");

        var result = await client.StatusAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("PAPER", result.Value!.TradingMode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ApiStatus.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ApiStatus.Rejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ApiStatus.Unavailable)]
    public async Task Error_statuses_become_states_never_exceptions(HttpStatusCode code, ApiStatus expected)
    {
        var result = await Client(code, """{"title":"Data store unavailable."}""").StatusAsync(CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.False(result.IsOk);
        Assert.Equal("Data store unavailable.", result.Problem);
    }

    [Fact]
    public async Task Validation_problems_are_explained()
    {
        var result = await Client(HttpStatusCode.BadRequest, """{"errors":{"reason":["Required: every kill-switch change is audited."]}}""")
            .KillSwitchAsync("paper-1", "trip", "", "fer", CancellationToken.None);

        Assert.Equal(ApiStatus.Rejected, result.Status);
        Assert.Contains("audited", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_that_does_not_answer_or_answers_garbage_is_unavailable()
    {
        var down = new OmegaApiClient(new HttpClient(new Handler(_ => throw new HttpRequestException("refused"))) { BaseAddress = new Uri("http://api/") }, TimeProvider.System);
        var timeout = new OmegaApiClient(new HttpClient(new Handler(_ => throw new TaskCanceledException("timeout"))) { BaseAddress = new Uri("http://api/") }, TimeProvider.System);
        var garbage = Client(HttpStatusCode.OK, "<html>not json</html>");

        Assert.Equal(ApiStatus.Unavailable, (await down.StatusAsync(CancellationToken.None)).Status);
        Assert.Equal(ApiStatus.Unavailable, (await timeout.StatusAsync(CancellationToken.None)).Status);
        Assert.Equal("Formato de respuesta inesperado.", (await garbage.StatusAsync(CancellationToken.None)).Problem);
        Assert.Equal("La API no respondió.", (await down.StatusAsync(CancellationToken.None)).Problem);   // the dashboard speaks Spanish
    }

    [Fact]
    public async Task Kill_switch_commands_are_sent_to_the_api_with_their_audit_fields()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = new OmegaApiClient(new HttpClient(new Handler(request =>
        {
            sent = request;
            body = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"commandId":3,"session":"paper-1","action":"trip","note":"Queued."}""", Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("http://api/") }, TimeProvider.System);

        var result = await client.KillSwitchAsync("paper-1", "trip", "incident", "fer", CancellationToken.None);

        Assert.Equal(3, result.Value!.CommandId);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/api/paper/sessions/paper-1/kill-switch", sent.RequestUri!.AbsolutePath);
        Assert.Equal("""{"action":"trip","reason":"incident","requestedBy":"fer"}""", body);
    }

    private static OmegaApiClient Client(HttpStatusCode code, string json) =>
        new(new HttpClient(new Handler(_ => new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") }))
        { BaseAddress = new Uri("http://api/") }, TimeProvider.System);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
