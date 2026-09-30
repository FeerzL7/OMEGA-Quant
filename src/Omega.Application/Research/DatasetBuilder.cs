using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Results;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy;
using Omega.Strategy.Baseline;

namespace Omega.Application.Research;

/// <summary>What to export.</summary>
/// <param name="Symbol">Symbol.</param>
/// <param name="Interval">Interval.</param>
/// <param name="FromUtc">First decision candle (inclusive, aligned).</param>
/// <param name="ToUtc">End of the decision candles (exclusive, aligned). Labels may use candles after it.</param>
public sealed record DatasetRequest(string Symbol, CandleInterval Interval, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>Describes a dataset file so that any experiment can verify and cite exactly what it used (§22).</summary>
public sealed record DatasetManifest(
    string DatasetId,
    string Format,
    DateTimeOffset CreatedAtUtc,
    string Symbol,
    string Interval,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string FeatureSetVersion,
    string FeatureSetHash,
    IReadOnlyList<string> FeatureColumns,
    TripleBarrierSpec Label,
    string LabelDefinition,
    string BaselineStrategy,
    DatasetFingerprint Candles,
    int Samples,
    IReadOnlyDictionary<string, int> Outcomes,
    IReadOnlyDictionary<string, int> Excluded,
    IReadOnlyList<string> Columns);

public sealed record Dataset(DatasetManifest Manifest, string Csv);

public static class DatasetErrors
{
    public const string InvalidPeriod = "DATASET_INVALID_PERIOD";
    public const string NoSamples = "DATASET_NO_SAMPLES";
}

/// <summary>
/// Builds the supervised dataset for research (Phase 7): one row per closed decision candle with complete features
/// and a determinable triple-barrier label. Features and labels are computed here, once, by the same code the
/// system uses live and in backtests; Python only consumes the file.
/// </summary>
public sealed class DatasetBuilder(ICandleStore candles, FeatureEngine featureEngine, TimeProvider timeProvider)
{
    public const string Format = "omega-dataset-v1";

    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(3 * 366);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string LabelDefinition =
        "Long decided at the close of candle t, entered at the open of t+1. Stop = close_t - StopAtr*atr_14_t, " +
        "target = close_t + TargetAtr*atr_14_t. First barrier reached within HorizonCandles candles (stop first if both " +
        "inside one candle; a gap through the stop exits at the open; through the target, at the target); otherwise " +
        "TIMEOUT at the close of the last candle. Gross prices, no costs. Same rules as the backtester.";

    /// <exception cref="Omega.Core.Persistence.PersistenceException">The store is unavailable.</exception>
    public async Task<Result<Dataset>> BuildAsync(DatasetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var length = request.Interval.ToTimeSpan();
        if (request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > MaxPeriod
            || request.FromUtc.UtcTicks % length.Ticks != 0 || request.ToUtc.UtcTicks % length.Ticks != 0)
        {
            return Result.Failure<Dataset>(new Error(DatasetErrors.InvalidPeriod,
                "The period must be aligned to the interval, end after it starts, and last at most 3 years."));
        }

        var spec = TripleBarrierSpec.Default;
        var from = request.FromUtc - (length * (featureEngine.FeatureSet.MaxLookback - 1));
        var to = request.ToUtc + (length * (spec.HorizonCandles + 1));
        var history = await candles.GetRangeAsync(request.Symbol, request.Interval, from, to, cancellationToken).ConfigureAwait(false);

        var series = featureEngine.ComputeSeries(history);
        if (series.IsFailure)
        {
            return Result.Failure<Dataset>(series.Error!);
        }

        var names = featureEngine.FeatureSet.Definitions.Select(d => d.Name).ToList();
        var baseline = new EmaTrendBaseline();
        var outcomes = Enum.GetValues<BarrierOutcome>().ToDictionary(o => o.ToString(), _ => 0);
        var excluded = new Dictionary<string, int> { ["featuresIncomplete"] = 0, ["labelUndeterminable"] = 0 };
        var csv = new StringBuilder();
        var samples = 0;

        var columns = new List<string> { "decision_open_time_utc", "available_at_utc" };
        columns.AddRange(names);
        columns.AddRange(["baseline_long", "label", "label_end_open_time_utc", "entry_price", "stop_loss", "take_profit", "exit_price", "gross_return", "holding_candles"]);
        csv.Append(string.Join(',', columns)).Append('\n');

        for (var t = 0; t < history.Count; t++)
        {
            var candle = history[t];
            if (candle.OpenTimeUtc < request.FromUtc || candle.OpenTimeUtc >= request.ToUtc)
            {
                continue;
            }

            var vector = series.Value[t];
            if (!vector.IsComplete)
            {
                excluded["featuresIncomplete"]++;
                continue;
            }

            var label = TripleBarrierLabeler.Label(history, t, (decimal)vector.Values["atr_14"]!.Value, spec);
            if (label is null)
            {
                excluded["labelUndeterminable"]++;
                continue;
            }

            var baselineLong = baseline.Evaluate(new StrategyContext(candle, vector, null)).Direction == SignalDirection.Long;

            csv.Append(Time(candle.OpenTimeUtc)).Append(',').Append(Time(vector.AvailableAtUtc));
            foreach (var name in names)
            {
                csv.Append(',').Append(vector.Values[name]!.Value.ToString("R", CultureInfo.InvariantCulture));
            }

            csv.Append(',').Append(baselineLong ? '1' : '0')
               .Append(',').Append(Code(label.Outcome))
               .Append(',').Append(Time(label.ExitCandleOpenTimeUtc))
               .Append(',').Append(Number(label.EntryPrice))
               .Append(',').Append(Number(label.StopLossPrice))
               .Append(',').Append(Number(label.TakeProfitPrice))
               .Append(',').Append(Number(label.ExitPrice))
               .Append(',').Append(Number(label.GrossReturn))
               .Append(',').Append(label.HoldingCandles.ToString(CultureInfo.InvariantCulture))
               .Append('\n');

            outcomes[label.Outcome.ToString()]++;
            samples++;
        }

        if (samples == 0)
        {
            return Result.Failure<Dataset>(new Error(DatasetErrors.NoSamples,
                "No labelled samples in the period: import more history (features need 250 candles of warm-up and labels 48 candles ahead)."));
        }

        var content = csv.ToString();
        var manifest = new DatasetManifest(
            DatasetId: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            Format: Format,
            CreatedAtUtc: timeProvider.GetUtcNow(),
            Symbol: request.Symbol,
            Interval: request.Interval.ToCode(),
            FromUtc: request.FromUtc,
            ToUtc: request.ToUtc,
            FeatureSetVersion: featureEngine.FeatureSet.Version,
            FeatureSetHash: featureEngine.FeatureSet.Hash,
            FeatureColumns: names,
            Label: spec,
            LabelDefinition: LabelDefinition,
            BaselineStrategy: $"{baseline.Identity.Name} v{baseline.Identity.Version}",
            Candles: DatasetFingerprint.From(history),
            Samples: samples,
            Outcomes: outcomes,
            Excluded: excluded,
            Columns: columns);

        return Result.Success(new Dataset(manifest, content));
    }

    /// <summary>Writes <c>dataset.csv</c> and <c>manifest.json</c> into <paramref name="root"/>/&lt;datasetId&gt;. Idempotent.</summary>
    public static string Write(Dataset dataset, string root)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var directory = Path.Combine(root, dataset.Manifest.DatasetId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dataset.csv"), dataset.Csv, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(dataset.Manifest, Json), new UTF8Encoding(false));
        return directory;
    }

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Code(BarrierOutcome outcome) => outcome switch
    {
        BarrierOutcome.TakeProfitFirst => "TP_FIRST",
        BarrierOutcome.StopLossFirst => "SL_FIRST",
        _ => "TIMEOUT",
    };
}
