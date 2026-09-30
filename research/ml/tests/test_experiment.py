import json

import numpy as np
import pandas as pd
import pytest
from sklearn.pipeline import Pipeline

from omega_ml import models
from omega_ml.experiment import run
from omega_ml.registry import onnx_probabilities
from omega_ml.splits import Period
from conftest import period_bounds, write_dataset


def _run(dataset_dir, tmp_path, evaluate_holdout=False, names=None, folds=4):
    development, holdout = period_bounds(3000)
    return run(dataset_dir, Period.parse(development), Period.parse(holdout), evaluate_holdout,
               names or models.MODEL_ORDER, folds, 20260930, tmp_path / "experiments", tmp_path / "models")


def test_a_planted_signal_is_found_out_of_sample(signal_dataset, tmp_path):
    record = _run(signal_dataset, tmp_path)

    for name in models.MODEL_ORDER:
        result = record["models"][name]
        assert result["out_of_sample"]["roc_auc"] > 0.65, name
        assert result["skill_vs_reference"]["log_loss_skill"] > 0.03, name


def test_pure_noise_shows_no_skill(noise_dataset, tmp_path):
    record = _run(noise_dataset, tmp_path)

    for name in models.MODEL_ORDER:
        result = record["models"][name]
        assert 0.44 < result["out_of_sample"]["roc_auc"] < 0.56, name
        assert result["skill_vs_reference"]["log_loss_skill"] < 0.01, name


def test_models_are_exported_to_onnx_with_parity_and_a_manifest(signal_dataset, tmp_path):
    record = _run(signal_dataset, tmp_path)

    for name in ["logistic_regression", "random_forest"]:
        assert record["models"][name]["export"]["status"] == "registered"
        model_dir = tmp_path / "models" / record["models"][name]["model_id"]
        manifest = json.loads((model_dir / "manifest.json").read_text())
        assert manifest["parity"]["maxAbsoluteDifference"] <= 1e-6
        assert manifest["input"]["type"] == "float64"
        assert manifest["datasetId"] == record["dataset"]["datasetId"]
        assert manifest["input"]["features"][0] == "log_return_1"
        p = onnx_probabilities((model_dir / "model.onnx").read_bytes(), np.zeros((3, 8)), "float64")
        assert p.shape == (3,) and ((p > 0) & (p < 1)).all()


def test_holdout_is_only_evaluated_on_request_and_repeated_looks_are_counted(signal_dataset, tmp_path):
    first = _run(signal_dataset, tmp_path, names=["logistic_regression"])
    second = _run(signal_dataset, tmp_path, evaluate_holdout=True, names=["logistic_regression"])
    third = _run(signal_dataset, tmp_path, evaluate_holdout=True, names=["logistic_regression"])

    assert first["holdout"]["evaluated"] is False
    assert second["holdout"]["previous_evaluations"] == 0 and second["holdout"]["warning"] is None
    assert third["holdout"]["previous_evaluations"] == 1 and "already been evaluated 1" in third["holdout"]["warning"]


def test_out_of_sample_predictions_are_written_for_calibration(signal_dataset, tmp_path):
    record = _run(signal_dataset, tmp_path, names=["logistic_regression"])

    oos = pd.read_csv(f"{record['directory']}/oos_predictions.csv")
    assert len(oos) == sum(f["test"] for f in record["development"]["folds"])
    assert oos["decision_open_time_utc"].is_monotonic_increasing
    assert oos["p_logistic_regression"].between(0, 1).all()


def test_runs_are_reproducible(signal_dataset, tmp_path):
    a = _run(signal_dataset, tmp_path / "a")
    b = _run(signal_dataset, tmp_path / "b")

    for name in models.MODEL_ORDER:
        assert a["models"][name]["out_of_sample"] == b["models"][name]["out_of_sample"]
        assert a["models"][name]["model_id"] == b["models"][name]["model_id"]


def test_onnx_export_is_byte_for_byte_reproducible():
    rng = np.random.default_rng(1)
    x, y = rng.normal(size=(400, 8)), rng.integers(0, 2, 400)
    from omega_ml.registry import to_onnx
    for name in models.MODEL_ORDER:
        first, second = models.make(name, 3).fit(x, y), models.make(name, 3).fit(x, y)
        assert to_onnx(first, 8, name)[0] == to_onnx(second, 8, name)[0], name


def test_a_model_whose_onnx_does_not_reproduce_it_is_evaluated_but_not_registered(signal_dataset, tmp_path, monkeypatch):
    from omega_ml import registry
    monkeypatch.setattr(registry, "PARITY_TOLERANCE", -1.0)   # no export can pass

    record = _run(signal_dataset, tmp_path, names=["logistic_regression"])

    result = record["models"]["logistic_regression"]
    assert result["export"]["status"] == "not_registered"
    assert result["model_id"] is None
    assert result["out_of_sample"]["roc_auc"] > 0.65   # still evaluated
    assert not (tmp_path / "models").exists() or not any((tmp_path / "models").iterdir())


def test_periods_must_be_ordered_and_inside_the_dataset(signal_dataset, tmp_path):
    with pytest.raises(ValueError, match="before the holdout"):
        run(signal_dataset, Period.parse("2025-01-05:2025-01-10"), Period.parse("2025-01-02:2025-01-04"), False,
            ["logistic_regression"], 4, 1, tmp_path / "e", tmp_path / "m")
    with pytest.raises(ValueError, match="inside the dataset"):
        run(signal_dataset, Period.parse("2024-12-01:2025-01-05"), Period.parse("2025-01-05:2025-01-10"), False,
            ["logistic_regression"], 4, 1, tmp_path / "e", tmp_path / "m")


def test_logistic_regression_scales_inside_the_pipeline():
    # The scaler is refitted on each training fold; statistics of test data never reach it.
    assert isinstance(models.make("logistic_regression", 1), Pipeline)
