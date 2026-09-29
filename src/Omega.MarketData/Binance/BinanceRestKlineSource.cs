using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Omega.Core.MarketData;
using Omega.MarketData.Configuration;

namespace Omega.MarketData.Binance;

/// <summary>
/// Historical closed candles from the Binance Spot REST endpoint <c>GET /api/v3/klines</c>
/// (weight 2, at most 1000 klines per request, times in UTC).
/// </summary>
/// <remarks>
/// <para>
/// Closed-candle detection: the REST payload has no "closed" flag and includes the
/// candle still being formed. A kline counts as closed only when its close time is
/// at least <see cref="MarketDataOptions.ClockSkewTolerance"/> in the past.
/// </para>
/// <para>
/// Rate limits: HTTP 429 (limit exceeded) and 418 (IP banned after ignoring 429s) are
/// reported as transient with the server's Retry-After, so callers back off instead of
/// retrying immediately.
/// </para>
/// </remarks>
public sealed partial class BinanceRestKlineSource : IHistoricalCandleSource
{
    public const string Name = "binance-spot-rest";

    /// <summary>Maximum klines per request allowed by the endpoint.</summary>
    public const int PageSize = 1000;

    private readonly HttpClient _httpClient;
    private readonly MarketDataOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BinanceRestKlineSource> _logger;

    /// <param name="httpClient">Client whose BaseAddress is <see cref="MarketDataOptions.RestBaseUrl"/>.</param>
    /// <param name="options">Market-data options.</param>
    /// <param name="timeProvider">Clock used for closed-candle detection.</param>
    /// <param name="logger">Logger.</param>
    public BinanceRestKlineSource(
        HttpClient httpClient, MarketDataOptions options, TimeProvider timeProvider, ILogger<BinanceRestKlineSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException("The HttpClient must have a BaseAddress.", nameof(httpClient));
        }

        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string SourceName => Name;

    public async Task<IReadOnlyList<Candle>> GetClosedCandlesAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset fromOpenTimeUtc,
        DateTimeOffset toOpenTimeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (toOpenTimeUtc <= fromOpenTimeUtc)
        {
            throw new ArgumentException("The end of the range must be after its start.", nameof(toOpenTimeUtc));
        }

        var intervalCode = BinanceIntervals.ToCode(interval);
        var intervalMs = (long)interval.ToTimeSpan().TotalMilliseconds;
        var endMs = toOpenTimeUtc.ToUnixTimeMilliseconds();
        var startMs = fromOpenTimeUtc.ToUnixTimeMilliseconds();
        var candles = new SortedDictionary<DateTimeOffset, Candle>();

        while (startMs < endMs)
        {
            var rows = await FetchPageAsync(symbol, intervalCode, startMs, endMs - 1, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                break;
            }

            // Evaluated per page: the clock moves while paging.
            var closedBeforeMs = (_timeProvider.GetUtcNow() - _options.ClockSkewTolerance).ToUnixTimeMilliseconds();

            foreach (var row in rows)
            {
                if (row.OpenTimeMs < startMs || row.OpenTimeMs >= endMs)
                {
                    continue;
                }

                var kline = row.ToKline(symbol, intervalCode, isClosed: row.CloseTimeMs < closedBeforeMs);
                if (!kline.IsClosed)
                {
                    LogNotYetClosed(_logger, symbol, row.OpenTimeMs);
                    continue;
                }

                var normalized = BinanceKlineNormalizer.ToClosedCandle(kline, symbol, interval);
                if (normalized.IsFailure)
                {
                    LogRejected(_logger, symbol, row.OpenTimeMs, normalized.Error!.Code, normalized.Error.Message);
                    continue;
                }

                candles[normalized.Value.OpenTimeUtc] = normalized.Value;
            }

            var nextStartMs = rows[^1].OpenTimeMs + intervalMs;
            if (rows.Count < PageSize || nextStartMs <= startMs)
            {
                break;
            }

            startMs = nextStartMs;
        }

        return [.. candles.Values];
    }

    private async Task<IReadOnlyList<BinanceRestKlineRow>> FetchPageAsync(
        string symbol, string intervalCode, long startMs, long endMs, CancellationToken cancellationToken)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"api/v3/klines?symbol={symbol}&interval={intervalCode}&startTime={startMs}&endTime={endMs}&limit={PageSize}");

        using var timeout = new CancellationTokenSource(_options.RestRequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await _httpClient.GetAsync(path, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw ToException(response, body);
            }

            return BinanceRestKlineParser.Parse(body);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HistoricalDataException(
                string.Create(CultureInfo.InvariantCulture, $"Request timed out after {_options.RestRequestTimeout.TotalSeconds:0.#} s."),
                isTransient: true,
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HistoricalDataException($"Request failed: {ex.Message}", isTransient: true, innerException: ex);
        }
        catch (FormatException ex)
        {
            throw new HistoricalDataException($"Unexpected response format: {ex.Message}", isTransient: false, innerException: ex);
        }
    }

    private HistoricalDataException ToException(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        var excerpt = body.Length > 200 ? body[..200] : body;

        if (response.StatusCode is HttpStatusCode.TooManyRequests || status == 418)
        {
            var retryAfter = response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - _timeProvider.GetUtcNow(),
                _ => (TimeSpan?)null,
            };

            LogRateLimited(_logger, status, retryAfter?.TotalSeconds);
            return new HistoricalDataException($"Rate limited by the exchange (HTTP {status}).", isTransient: true, retryAfter);
        }

        var transient = status >= 500;
        return new HistoricalDataException($"HTTP {status}: {excerpt}", transient);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "REST kline {Symbol} {OpenTimeMs} not closed yet; skipped.")]
    private static partial void LogNotYetClosed(ILogger logger, string symbol, long openTimeMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "REST kline {Symbol} {OpenTimeMs} rejected ({ErrorCode}): {ErrorMessage}")]
    private static partial void LogRejected(ILogger logger, string symbol, long openTimeMs, string errorCode, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exchange rate limit hit (HTTP {Status}); Retry-After {RetryAfterSeconds} s.")]
    private static partial void LogRateLimited(ILogger logger, int status, double? retryAfterSeconds);
}
