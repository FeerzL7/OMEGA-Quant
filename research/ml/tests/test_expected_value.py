import json

import pandas as pd
import pytest

from omega_ml import expected_value as ev, models
from omega_ml.experiment import run
from omega_ml.splits import Period
from conftest import period_bounds


def test_expected_return_matches_the_documented_formula():
    costs = ev.Costs(fee_rate=0.001, spread_bps=2, slippage_bps=3)
    r = ev.expected_return(p=0.4, atr=200, price=50_000, win_mean=2.9, loss_mean=-1.6, costs=costs)

    a = 200 / 50_000
    market = 0.001 + 0.0001 + 0.0003
    assert r["gain"] == pytest.approx(2.9 * a)
    assert r["costs"] == pytest.approx(market + 0.4 * 0.001 + 0.6 * market)
    assert r["expected_return"] == pytest.approx(0.4 * 2.9 * a + 0.6 * -1.6 * a - r["costs"])


def test_higher_probability_or_lower_costs_never_lower_the_expected_value():
    base = dict(atr=150, price=60_000, win_mean=2.95, loss_mean=-1.4)
    values = [ev.expected_return(p, costs=ev.Costs(), **base)["expected_return"] for p in (0.2, 0.4, 0.6)]
    cheap = ev.expected_return(0.4, costs=ev.Costs(fee_rate=0.0005), **base)["expected_return"]

    assert values == sorted(values)
    assert cheap > values[1]


def test_outcome_profile_measures_wins_and_losses_in_atr_units():
    samples = pd.DataFrame({
        "label": ["TP_FIRST", "SL_FIRST", "TIMEOUT", "SL_FIRST"],
        "entry_price": [100.0, 100.0, 100.0, 200.0],
        "exit_price": [106.0, 96.0, 101.0, 192.0],
        "atr_14": [2.0, 2.0, 2.0, 4.0],
    })

    profile = ev.outcome_profile(samples, "dev", "dataset", {"stopAtr": 2})

    assert profile["winMean"] == pytest.approx(3.0)
    assert profile["lossMean"] == pytest.approx((-2.0 + 0.5 - 2.0) / 3)
    assert profile["stopLossShare"] == pytest.approx(2 / 3)
    assert profile["timeoutMean"] == pytest.approx(0.5)


def test_experiment_writes_the_profile_from_development_samples_next_to_registered_models(signal_dataset, tmp_path):
    development, holdout = period_bounds(3000)
    record = run(signal_dataset, Period.parse(development), Period.parse(holdout), False,
                 ["logistic_regression"], 4, 1, tmp_path / "experiments", tmp_path / "models")

    profile = json.loads((tmp_path / "models" / record["models"]["logistic_regression"]["model_id"] / "outcome_profile.json").read_text())
    assert profile["format"] == ev.PROFILE_FORMAT
    assert profile["winMean"] == pytest.approx(3.0)
    assert -2.0 < profile["lossMean"] < 0.5
    assert profile["period"] == str(Period.parse(development))
