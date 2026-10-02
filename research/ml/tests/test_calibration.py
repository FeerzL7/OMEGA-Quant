import json

import numpy as np
import pandas as pd
import pytest

from omega_ml import calibration, models
from omega_ml.experiment import run as run_experiment
from omega_ml.splits import Period
from conftest import period_bounds


def _sample(n, distortion, seed):
    """True probabilities, outcomes drawn from them, and raw predictions distorted on the logit scale
    (distortion > 1: overconfident, < 1: underconfident, 1: already calibrated)."""
    rng = np.random.default_rng(seed)
    true = rng.uniform(0.05, 0.95, n)
    y = (rng.random(n) < true).astype(int)
    raw = 1 / (1 + np.exp(-distortion * np.log(true / (1 - true))))
    return true, y, raw


def test_platt_repairs_overconfident_probabilities():
    _, y, raw = _sample(20_000, distortion=2.5, seed=1)
    cal = calibration.fit("platt", raw[:10_000], y[:10_000])

    before = calibration.reliability(y[10_000:], raw[10_000:])["ece"]
    after = calibration.reliability(y[10_000:], cal.apply(raw[10_000:]))["ece"]

    assert cal.platt_a == pytest.approx(1 / 2.5, abs=0.05)
    assert after < before / 4


def test_isotonic_is_monotone_and_stays_inside_the_clip_range():
    _, y, raw = _sample(5_000, distortion=0.5, seed=2)
    cal = calibration.fit("isotonic", raw, y)
    grid = np.linspace(0, 1, 501)

    out = cal.apply(grid)

    assert np.all(np.diff(out) >= 0)
    assert out.min() >= calibration.EPSILON and out.max() <= 1 - calibration.EPSILON


def test_reliability_table_ece_and_brier_decomposition():
    # Forecasts constant inside each bin: the Murphy decomposition is exact.
    p = np.repeat([0.1, 0.5, 0.9], 100)
    y = np.concatenate([np.r_[np.ones(20), np.zeros(80)], np.r_[np.ones(50), np.zeros(50)], np.r_[np.ones(90), np.zeros(10)]]).astype(int)

    r = calibration.reliability(y, p, bins=3)

    assert [b["observed_rate"] for b in r["bins"]] == pytest.approx([0.2, 0.5, 0.9])
    assert r["ece"] == pytest.approx((0.1 + 0 + 0) / 3)
    assert r["mce"] == pytest.approx(0.1)
    d = r["brier_decomposition"]
    assert d["reliability"] - d["resolution"] + d["uncertainty"] == pytest.approx(np.mean((p - y) ** 2))
    assert all(b["observed_ci95"][0] <= b["observed_rate"] <= b["observed_ci95"][1] for b in r["bins"])


def _oos(n_per_fold=2_000, folds=5, distortion=2.5, seed=3, label_minutes=60):
    _, y, raw = _sample(n_per_fold * folds, distortion, seed)
    t = pd.Timestamp("2025-01-01T00:00:00Z") + pd.to_timedelta(np.arange(len(y)) * 5, unit="min")
    return pd.DataFrame({
        "decision_open_time_utc": t, "label_end_open_time_utc": t + pd.Timedelta(minutes=label_minutes),
        "target": y, "fold": np.repeat(np.arange(1, folds + 1), n_per_fold), "p_model": raw})


def test_walk_forward_selects_a_calibrator_when_it_helps_out_of_sample():
    result = calibration.walk_forward(_oos(distortion=2.5), "p_model")

    assert result["selected"] in ("platt", "isotonic")
    assert result["improves_on_raw"]
    assert result["methods"][result["selected"]]["log_loss"] < result["methods"]["none"]["log_loss"]
    assert result["purged"] > 0   # labels lasting an hour overlap each fold boundary


def test_walk_forward_does_not_claim_an_improvement_on_calibrated_predictions():
    result = calibration.walk_forward(_oos(distortion=1.0, n_per_fold=6_000), "p_model")

    gain = result["methods"]["none"]["log_loss"] - min(m["log_loss"] for m in result["methods"].values())
    assert gain < 0.002


def test_a_fold_is_calibrated_only_with_earlier_folds():
    original = _oos()
    changed = original.copy()
    changed.loc[changed["fold"] == 5, "target"] = 1 - changed.loc[changed["fold"] == 5, "target"]

    a = calibration.walk_forward(original, "p_model")["per_fold_log_loss"]
    b = calibration.walk_forward(changed, "p_model")["per_fold_log_loss"]

    assert a[:3] == b[:3]          # folds 2-4 never saw fold 5
    assert a[3] != b[3]


def test_calibrator_export_round_trips():
    _, y, raw = _sample(3_000, distortion=2.0, seed=4)
    for method in calibration.METHODS:
        cal = calibration.fit(method, raw, y)
        d = cal.to_dict()
        assert d["format"] == "omega-calibration-v1" and d["method"] == method
        if method == "platt":
            again = calibration.Calibrator("platt", platt_a=d["platt"]["a"], platt_b=d["platt"]["b"])
        elif method == "isotonic":
            again = calibration.Calibrator("isotonic", isotonic_x=tuple(d["isotonic"]["x"]), isotonic_y=tuple(d["isotonic"]["y"]))
        else:
            again = calibration.Calibrator("none")
        assert np.array_equal(again.apply(raw), cal.apply(raw))


def _experiment(dataset, tmp_path, evaluate_holdout):
    development, holdout = period_bounds(3000)
    return run_experiment(dataset, Period.parse(development), Period.parse(holdout), evaluate_holdout,
                          models.MODEL_ORDER, 4, 20260930, tmp_path / "experiments", tmp_path / "models")


def test_calibration_is_packaged_with_registered_models_only(signal_dataset, tmp_path, monkeypatch):
    from omega_ml import registry
    original_register = registry.register

    def register_except_lightgbm(estimator, model_type, *args, **kwargs):
        if model_type == "lightgbm":
            raise registry.ExportError("simulated parity failure")
        return original_register(estimator, model_type, *args, **kwargs)

    monkeypatch.setattr(registry, "register", register_except_lightgbm)
    experiment = _experiment(signal_dataset, tmp_path, evaluate_holdout=False)

    record = calibration.run(tmp_path / "experiments" / experiment["experimentId"], tmp_path / "models", evaluate_holdout=False)

    for name in ("logistic_regression", "random_forest"):
        package = json.loads((tmp_path / "models" / experiment["models"][name]["model_id"] / "calibration.json").read_text())
        assert package["method"] == record["models"][name]["walk_forward"]["selected"]
        assert package["modelId"] == experiment["models"][name]["model_id"]
    assert record["models"]["lightgbm"]["packaged_with"] is None


def test_holdout_needs_holdout_predictions_and_repeated_looks_are_counted(signal_dataset, tmp_path):
    without = _experiment(signal_dataset, tmp_path / "a", evaluate_holdout=False)
    with pytest.raises(ValueError, match="holdout predictions"):
        calibration.run(tmp_path / "a" / "experiments" / without["experimentId"], tmp_path / "a" / "models", evaluate_holdout=True)

    with_holdout = _experiment(signal_dataset, tmp_path / "b", evaluate_holdout=True)
    directory = tmp_path / "b" / "experiments" / with_holdout["experimentId"]
    first = calibration.run(directory, tmp_path / "b" / "models", evaluate_holdout=True, model_names=["logistic_regression"])
    second = calibration.run(directory, tmp_path / "b" / "models", evaluate_holdout=True, model_names=["logistic_regression"])

    assert first["models"]["logistic_regression"]["holdout"]["previous_evaluations"] == 0
    assert second["models"]["logistic_regression"]["holdout"]["previous_evaluations"] == 1
    assert "raw" in first["models"]["logistic_regression"]["holdout"] and "calibrated" in first["models"]["logistic_regression"]["holdout"]


def test_selection_follows_the_documented_rule():
    for distortion in (0.6, 1.0, 2.5):
        result = calibration.walk_forward(_oos(distortion=distortion, seed=int(distortion * 10)), "p_model")
        losses = {m: result["methods"][m]["log_loss"] for m in calibration.METHODS}
        best = min(losses.values())
        expected = next(m for m in calibration.METHODS if losses[m] - best <= calibration.TIE_TOLERANCE)
        assert result["selected"] == expected
        assert result["improves_on_raw"] == (expected != "none")
