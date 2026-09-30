"""Model factory. Hyperparameters are fixed, conservative defaults chosen before looking at results; they are not
tuned (tuning on the evaluation data is a classic source of overfitting). Changing them is a new experiment."""
from __future__ import annotations

from lightgbm import LGBMClassifier
from sklearn.ensemble import RandomForestClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

# Roadmap order: start simple, add complexity only with evidence.
MODEL_ORDER = ["logistic_regression", "random_forest", "lightgbm"]


def make(name: str, seed: int):
    if name == "logistic_regression":
        # The scaler is inside the pipeline, so it is fitted on each training fold only (no leakage of test statistics).
        return Pipeline([("scale", StandardScaler()), ("model", LogisticRegression(C=1.0, max_iter=2000))])
    if name == "random_forest":
        return RandomForestClassifier(
            n_estimators=300, max_depth=6, min_samples_leaf=50, max_features="sqrt", random_state=seed, n_jobs=1)
    if name == "lightgbm":
        return LGBMClassifier(
            n_estimators=300, learning_rate=0.03, num_leaves=15, min_child_samples=100, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.8, reg_lambda=1.0, random_state=seed, n_jobs=1, deterministic=True, force_row_wise=True, verbose=-1)
    raise ValueError(f"Unknown model {name!r}; known: {MODEL_ORDER}.")


def hyperparameters(estimator) -> dict:
    """Plain-valued parameters, for the experiment record."""
    params = estimator.get_params(deep=True)
    return {k: v for k, v in sorted(params.items()) if isinstance(v, (int, float, str, bool, type(None)))}
