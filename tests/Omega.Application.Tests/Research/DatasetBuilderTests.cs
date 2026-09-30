using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Omega.Application.Research;
using Omega.Application.Tests.TestSupport;
using Omega.Backtesting;
using Omega.Core.MarketData;
using Omega.Core.Trading;
using Omega.Features;
using Omega.Strategy;
using Omega.Strategy.Baseline;

namespace Omega.Application.Tests.Research;

public class DatasetBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FeatureEngine Engine = new(FeatureSets.V1());
    private readonly InMemoryCandleStore _store = new();
    private readonly List<Candle> _candles = [];

    public DatasetBuilderTests()
    {
        for (var i = 0; i < 700; i++)
        {
            var open = i == 0 ? 100m : _candles[^1].Close;
            var close = Math.Round(100m + (decimal)(6 * Math.Sin(i / 35.0) + 0.8 * Math.Sin(i * 1.9)), 2);
            var time = At(i);
            var candle = Candle.Create("BTCUSDT", CandleInterval.FiveMinutes, time, time.AddMinutes(5).AddMilliseconds(-1),
                open, Math.Max(open, close) + 0.35m, Math.Min(open, close) - 0.35m, close, 5m + (i % 7), 500m, 5).Value;
            _candles.Add(candle);
            _store.Seed(candle);
        }
    }

    [Fact]
    public async Task One_row_per_labelled_decision_candle_in_the_period()
    {
        var dataset = (await Build(At(300), At(600))).Value;
        var rows = Rows(dataset);

        Assert.Equal(dataset.Manifest.Samples, rows.Count);
        Assert.Equal(dataset.Manifest.Samples, dataset.Manifest.Outcomes.Values.Sum());
        Assert.True(rows.Count >= 250);
        Assert.All(rows, row => Assert.InRange(DateTimeOffset.Parse(row["decision_open_time_utc"], CultureInfo.InvariantCulture), At(300), At(599)));
        Assert.Equal(dataset.Manifest.Columns, dataset.Csv.Split('\n')[0].Split(','));
    }

    [Fact]
    public async Task Features_in_a_row_use_only_candles_up_to_its_decision_candle()
    {
        var dataset = (await Build(At(300), At(600))).Value;
        var row = Rows(dataset)[100];
        var t = _candles.FindIndex(c => c.OpenTimeUtc == DateTimeOffset.Parse(row["decision_open_time_utc"], CultureInfo.InvariantCulture));

        var pastOnly = Engine.Compute(_candles[..(t + 1)]).Value;

        foreach (var name in FeatureSets.V1().Definitions.Select(d => d.Name))
        {
            Assert.Equal(pastOnly.Values[name]!.Value.ToString("R", CultureInfo.InvariantCulture), row[name]);
        }

        Assert.Equal(pastOnly.AvailableAtUtc, DateTimeOffset.Parse(row["available_at_utc"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Labels_and_baseline_column_come_from_the_labeler_and_the_baseline_strategy()
    {
        var dataset = (await Build(At(300), At(600))).Value;
        var series = Engine.ComputeSeries(_candles).Value;
        var baseline = new EmaTrendBaseline();

        foreach (var row in Rows(dataset).Take(120))
        {
            var t = _candles.FindIndex(c => c.OpenTimeUtc == DateTimeOffset.Parse(row["decision_open_time_utc"], CultureInfo.InvariantCulture));
            var label = TripleBarrierLabeler.Label(_candles, t, (decimal)series[t].Values["atr_14"]!.Value, TripleBarrierSpec.Default)!;
            var expected = label.Outcome switch
            {
                BarrierOutcome.TakeProfitFirst => "TP_FIRST",
                BarrierOutcome.StopLossFirst => "SL_FIRST",
                _ => "TIMEOUT",
            };
            var baselineLong = baseline.Evaluate(new StrategyContext(_candles[t], series[t], null)).Direction == SignalDirection.Long;

            Assert.Equal(expected, row["label"]);
            Assert.Equal(label.ExitCandleOpenTimeUtc, DateTimeOffset.Parse(row["label_end_open_time_utc"], CultureInfo.InvariantCulture));
            Assert.Equal(baselineLong ? "1" : "0", row["baseline_long"]);
        }
    }

    [Fact]
    public async Task Dataset_id_is_the_hash_of_its_content_and_builds_are_reproducible()
    {
        var first = (await Build(At(300), At(600))).Value;
        var second = (await Build(At(300), At(600))).Value;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first.Csv))), first.Manifest.DatasetId);
        Assert.Equal(first.Manifest.DatasetId, second.Manifest.DatasetId);
        Assert.Equal(FeatureSets.V1().Hash, first.Manifest.FeatureSetHash);
        Assert.Equal(TripleBarrierSpec.Default, first.Manifest.Label);
    }

    [Fact]
    public async Task Decisions_whose_label_would_need_missing_future_candles_are_excluded()
    {
        var dataset = (await Build(At(600), At(700))).Value;   // the last 48 decisions cannot be labelled

        Assert.True(dataset.Manifest.Excluded["labelUndeterminable"] >= 48);
        Assert.True(Rows(dataset).Count <= 52);
    }

    [Fact]
    public async Task Invalid_periods_and_periods_without_samples_are_rejected()
    {
        Assert.Equal(DatasetErrors.InvalidPeriod, (await Build(At(10).AddMinutes(1), At(20))).Error!.Code);
        Assert.Equal(DatasetErrors.NoSamples, (await Build(At(0), At(200))).Error!.Code);   // still warming up
    }

    [Fact]
    public async Task Files_are_written_under_the_dataset_id()
    {
        var dataset = (await Build(At(300), At(400))).Value;
        var root = Directory.CreateTempSubdirectory("omega-dataset-").FullName;

        var directory = DatasetBuilder.Write(dataset, root);

        Assert.Equal(Path.Combine(root, dataset.Manifest.DatasetId), directory);
        Assert.Equal(dataset.Csv, File.ReadAllText(Path.Combine(directory, "dataset.csv")));
        Assert.Contains(dataset.Manifest.DatasetId, File.ReadAllText(Path.Combine(directory, "manifest.json")), StringComparison.Ordinal);
        Directory.Delete(root, recursive: true);
    }

    private Task<Omega.Core.Results.Result<Dataset>> Build(DateTimeOffset from, DateTimeOffset to) =>
        new DatasetBuilder(_store, Engine, TimeProvider.System).BuildAsync(new DatasetRequest("BTCUSDT", CandleInterval.FiveMinutes, from, to), CancellationToken.None);

    private static DateTimeOffset At(int index) => Start.AddMinutes(5 * index);

    private static List<Dictionary<string, string>> Rows(Dataset dataset)
    {
        var lines = dataset.Csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        return [.. lines.Skip(1).Select(line => header.Zip(line.Split(',')).ToDictionary(p => p.First, p => p.Second))];
    }
}
