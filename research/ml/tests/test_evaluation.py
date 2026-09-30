import numpy as np
import pytest

from omega_ml import evaluation


def test_probabilistic_metrics():
    y = np.array([1, 0, 1, 0])
    p = np.array([0.9, 0.2, 0.6, 0.4])

    m = evaluation.probabilistic(y, p)

    assert m["base_rate"] == 0.5
    assert m["brier"] == pytest.approx(np.mean((p - y) ** 2))
    assert m["roc_auc"] == 1.0
    assert m["log_loss"] == pytest.approx(-np.mean(y * np.log(p) + (1 - y) * np.log(1 - p)))


def test_skill_is_relative_to_the_no_skill_reference():
    s = evaluation.skill({"log_loss": 0.6, "brier": 0.2}, {"log_loss": 0.65, "brier": 0.25})

    assert s["log_loss_skill"] == pytest.approx(1 - 0.6 / 0.65)
    assert s["brier_skill"] == pytest.approx(0.2)


def test_baseline_comparison_uses_the_same_number_of_signals():
    y = np.array([1, 1, 0, 0, 1, 0])
    p = np.array([0.9, 0.8, 0.7, 0.1, 0.2, 0.3])
    baseline = np.array([0, 0, 1, 1, 1, 0], dtype=bool)   # 3 signals, 1 hit

    c = evaluation.against_baseline(y, p, baseline)

    assert c["signals"] == 3
    assert c["baseline_precision"] == pytest.approx(1 / 3)
    assert c["model_precision_same_coverage"] == pytest.approx(2 / 3)   # top 3 by p: indices 0, 1, 2
    assert evaluation.against_baseline(y, p, np.zeros(6, dtype=bool)) is None
