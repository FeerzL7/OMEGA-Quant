"""Out-of-sample metrics and comparisons. Phase 7 measures predictive power only; calibration is Phase 8 and
economic value (expected value, backtests) is Phase 9."""
from __future__ import annotations

import numpy as np
from sklearn.metrics import brier_score_loss, log_loss, roc_auc_score


def probabilistic(y: np.ndarray, p: np.ndarray) -> dict:
    y = np.asarray(y, dtype=int)
    p = np.clip(np.asarray(p, dtype=float), 1e-12, 1 - 1e-12)
    two_classes = len(np.unique(y)) == 2
    return {
        "samples": int(len(y)),
        "base_rate": float(y.mean()),
        "log_loss": float(log_loss(y, p, labels=[0, 1])),
        "brier": float(brier_score_loss(y, p)),
        "roc_auc": float(roc_auc_score(y, p)) if two_classes else None,
    }


def skill(model: dict, reference: dict) -> dict:
    """Relative improvement over the no-skill reference (> 0 is better). A reference predicts, for every test sample,
    the base rate observed in its own training data: what you would know without a model."""
    return {
        "log_loss_skill": 1 - model["log_loss"] / reference["log_loss"],
        "brier_skill": 1 - model["brier"] / reference["brier"],
    }


def against_baseline(y: np.ndarray, p: np.ndarray, baseline_long: np.ndarray) -> dict | None:
    """Precision (share of TP_FIRST) of the baseline rule's signals versus the model's most confident samples at the
    same coverage (same number of signals). Equal coverage makes the comparison fair."""
    y = np.asarray(y, dtype=int)
    baseline_long = np.asarray(baseline_long, dtype=bool)
    k = int(baseline_long.sum())
    if k == 0:
        return None

    order = np.argsort(-np.asarray(p, dtype=float), kind="mergesort")  # stable: deterministic ties
    top = order[:k]
    return {
        "signals": k,
        "coverage": k / len(y),
        "base_rate": float(y.mean()),
        "baseline_precision": float(y[baseline_long].mean()),
        "model_precision_same_coverage": float(y[top].mean()),
    }
