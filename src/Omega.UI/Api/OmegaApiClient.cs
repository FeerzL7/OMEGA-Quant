using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omega.UI.Api;

public enum ApiStatus
{
    Ok = 1,
    NotFound = 2,
    Rejected = 3,
    Unavailable = 4,
}

/// <summary>Outcome of an API call. Components never receive exceptions: an unavailable API is a state to display.</summary>
public sealed record ApiResult<T>(ApiStatus Status, T? Value, string? Problem, DateTimeOffset ReceivedAtUtc)
{
    public bool IsOk => Status == ApiStatus.Ok && Value is not null;
}

/// <summary>
/// The dashboard's only way into the system: HTTP calls to Omega.Api (docs/ARCHITECTURE.md §6). It never talks to
/// Binance, the database, models or execution providers.
/// </summary>
public sealed partial class OmegaApiClient(HttpClient http, TimeProvider time, Microsoft.Extensions.Logging.ILogger<OmegaApiClient>? logger = null)
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OmegaApiClient>.Instance;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<ApiResult<SystemStatusDto>> StatusAsync(CancellationToken ct) => GetAsync<SystemStatusDto>("api/system/status", ct);

    public Task<ApiResult<SystemHealthDto>> HealthAsync(string symbol, string interval, CancellationToken ct) =>
        GetAsync<SystemHealthDto>($"api/system/health?symbol={symbol}&interval={interval}", ct);

    public Task<ApiResult<IReadOnlyList<SystemEventDto>>> EventsAsync(int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<SystemEventDto>>($"api/system/events?limit={limit}", ct);

    public Task<ApiResult<MarketStateDto>> MarketStateAsync(string symbol, string interval, CancellationToken ct) =>
        GetAsync<MarketStateDto>($"api/market/{symbol}/{interval}/state", ct);

    public Task<ApiResult<IReadOnlyList<CandleDto>>> CandlesAsync(string symbol, string interval, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<CandleDto>>($"api/market/{symbol}/{interval}/candles?limit={limit}", ct);

    public Task<ApiResult<RiskLimitsDto>> RiskLimitsAsync(CancellationToken ct) => GetAsync<RiskLimitsDto>("api/risk/limits", ct);

    public Task<ApiResult<IReadOnlyList<PaperSessionDto>>> PaperSessionsAsync(CancellationToken ct) =>
        GetAsync<IReadOnlyList<PaperSessionDto>>("api/paper/sessions", ct);

    public Task<ApiResult<PaperSessionDto>> PaperSessionAsync(string name, CancellationToken ct) =>
        GetAsync<PaperSessionDto>($"api/paper/sessions/{Uri.EscapeDataString(name)}", ct);

    public Task<ApiResult<IReadOnlyList<PaperDecisionDto>>> DecisionsAsync(string name, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<PaperDecisionDto>>($"api/paper/sessions/{Uri.EscapeDataString(name)}/decisions?limit={limit}", ct);

    public Task<ApiResult<IReadOnlyList<PaperTradeDto>>> TradesAsync(string name, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<PaperTradeDto>>($"api/paper/sessions/{Uri.EscapeDataString(name)}/trades?limit={limit}", ct);

    public Task<ApiResult<IReadOnlyList<PaperOrderViewDto>>> OrdersAsync(string name, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<PaperOrderViewDto>>($"api/paper/sessions/{Uri.EscapeDataString(name)}/orders?limit={limit}", ct);

    public Task<ApiResult<IReadOnlyList<PaperCommandDto>>> CommandsAsync(string name, int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<PaperCommandDto>>($"api/paper/sessions/{Uri.EscapeDataString(name)}/commands?limit={limit}", ct);

    public Task<ApiResult<IReadOnlyList<BacktestRunSummaryDto>>> BacktestsAsync(int limit, CancellationToken ct) =>
        GetAsync<IReadOnlyList<BacktestRunSummaryDto>>($"api/backtests?limit={limit}", ct);

    public Task<ApiResult<BacktestRunDto>> BacktestAsync(Guid id, CancellationToken ct) => GetAsync<BacktestRunDto>($"api/backtests/{id}", ct);

    public Task<ApiResult<MonteCarloOutcomeDto>> MonteCarloAsync(Guid id, string method, CancellationToken ct) =>
        SendAsync<MonteCarloOutcomeDto>(HttpMethod.Post, $"api/backtests/{id}/monte-carlo", new { method }, ct);

    /// <summary>Queues a kill-switch command; the paper worker applies it (the dashboard never changes trading state itself).</summary>
    public Task<ApiResult<KillSwitchAcceptedDto>> KillSwitchAsync(string session, string action, string reason, string requestedBy, CancellationToken ct) =>
        SendAsync<KillSwitchAcceptedDto>(HttpMethod.Post, $"api/paper/sessions/{Uri.EscapeDataString(session)}/kill-switch", new { action, reason, requestedBy }, ct);

    private Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var now = time.GetUtcNow();
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
                return new ApiResult<T>(ApiStatus.Ok, value, null, now);
            }

            var problem = await ProblemTitleAsync(response, ct).ConfigureAwait(false);
            var status = response.StatusCode switch
            {
                HttpStatusCode.NotFound => ApiStatus.NotFound,
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ApiStatus.Rejected,
                _ => ApiStatus.Unavailable,
            };
            if (status == ApiStatus.Unavailable)
            {
                LogUnavailable(_logger, path, $"HTTP {(int)response.StatusCode} {problem}");
            }

            return new ApiResult<T>(status, default, problem ?? $"HTTP {(int)response.StatusCode}", now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            LogUnavailable(_logger, path, ex.GetType().Name + ": " + ex.Message);
            return new ApiResult<T>(ApiStatus.Unavailable, default, ex is JsonException ? "Formato de respuesta inesperado." : "La API no respondió.", time.GetUtcNow());
        }
    }

    /// <summary>One concise line per failed call (the HttpClient's own request logging is kept at Warning).</summary>
    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Omega.Api unavailable for {Path}: {Reason}")]
    private static partial void LogUnavailable(Microsoft.Extensions.Logging.ILogger logger, string path, string reason);

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                return string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString())));
            }

            return root.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
