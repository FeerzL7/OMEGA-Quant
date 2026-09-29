using System.Net;
using System.Text;
using System.Web;

namespace Omega.MarketData.Tests.TestSupport;

/// <summary>HttpMessageHandler that answers every request with a function of the request.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static FakeHttpHandler Returning(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) =>
        new((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return respond(request, cancellationToken);
    }
}

/// <summary>Emulates GET /api/v3/klines over a set of available candle indices (5m steps).</summary>
internal static class FakeKlinesEndpoint
{
    public static FakeHttpHandler Serving(IReadOnlyCollection<int> availableIndices) => new((request, _) =>
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var start = long.Parse(query["startTime"]!, System.Globalization.CultureInfo.InvariantCulture);
        var end = long.Parse(query["endTime"]!, System.Globalization.CultureInfo.InvariantCulture);
        var limit = int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture);

        var rows = availableIndices
            .Select(index => KlineMessages.OpenTime(index).ToUnixTimeMilliseconds())
            .Where(open => open >= start && open <= end)
            .Order()
            .Take(limit)
            .Select(open => RestRow(open));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"[{string.Join(',', rows)}]", Encoding.UTF8, "application/json"),
        });
    });

    public static string RestRow(long openMs, string high = "101.0") =>
        $$$"""[{{{openMs}}},"100.0","{{{high}}}","99.5","100.5","12.5",{{{openMs + 299_999}}},"1250.0",42,"6.0","600.0","0"]""";
}

/// <summary>Clock frozen at a given instant; timers still use real time.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
