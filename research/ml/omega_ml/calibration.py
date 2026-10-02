"""Probability calibration (Phase 8), on out-of-sample predictions only.

    python -m omega_ml.calibration --experiment ../experiments/<id> [--evaluate-holdout]

For every model of a Phase 7 experiment:
1. Nested walk-forward: for each fold j >= 2, calibrators are fitted on the out-of-sample predictions of folds < j
   (purged: labels still open when fold j starts are dropped) and evaluated on fold j. This measures whether
   calibration helps *out of sample*; calibrating and measuring on the same data always looks better.
2. Methods compared: none (raw, only clipped), Platt (sigmoid on the logit) and isotonic. The method with the lowest
   walk-forward log loss is selected; near-ties go to the simpler method. "none" can win: calibration is not assumed
   to help (CLAUDE.md §9).
3. The selected method is refitted on all out-of-sample predictions and written as calibration.json next to the
   registered ONNX model (omega-calibration-v1), so the C# system can apply it (Phase 9).
4. Reliability analysis for raw and calibrated probabilities. With --evaluate-holdout (needs holdout predictions from
   the experiment), the final calibrator is also judged on the holdout; repeated looks are counted.
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression

from . import evaluation

FORMAT = "omega-calibration-v1"
METHODS = ["none", "platt", "isotonic"]          # simplest first: near-ties go to the earlier one
EPSILON = 1e-3                                   # calibrated output is kept in [EPSILON, 1 - EPSILON]
LOGIT_EPSILON = 1e-6                             # raw probabilities are clipped before the logit
TIE_TOLERANCE = 1e-4                             # log-loss difference treated as a tie
BINS = 10
REPO_ROOT = Path(__file__).resolve().parents[3]


def _logit(p: np.ndarray) -> np.ndarray:
    p = np.clip(np.asarray(p, dtype=float), LOGIT_EPSILON, 1 - LOGIT_EPSILON)
    return np.log(p / (1 - p))


@dataclass(frozen=True)
class Calibrator:
    method: str
    platt_a: float = 1.0
    platt_b: float = 0.0
    isotonic_x: tuple[float, ...] = ()
    isotonic_y: tuple[float, ...] = ()

    def apply(self, p: np.ndarray) -> np.ndarray:
        p = np.asarray(p, dtype=float)
        if self.method == "none":
            out = p
        elif self.method == "platt":
            out = 1 / (1 + np.exp(-(self.platt_a * _logit(p) + self.platt_b)))
        elif self.method == "isotonic":
            out = np.interp(p, self.isotonic_x, self.isotonic_y)   # linear between thresholds, flat outside
        else:
            raise ValueError(self.method)
        return np.clip(out, EPSILON, 1 - EPSILON)

    def to_dict(self) -> dict:
        d = {"format": FORMAT, "method": self.method, "epsilon": EPSILON, "logitEpsilon": LOGIT_EPSILON}
        if self.method == "platt":
            d["platt"] = {"a": self.platt_a, "b": self.platt_b,
                          "formula": "sigmoid(a * logit(clip(p, logitEpsilon, 1 - logitEpsilon)) + b)"}
        if self.method == "isotonic":
            d["isotonic"] = {"x": list(self.isotonic_x), "y": list(self.isotonic_y),
                             "formula": "linear interpolation between (x, y) points; constant outside [x0, xN]"}
        d["output"] = "clip(calibrated, epsilon, 1 - epsilon)"
        return d


def fit(method: str, p: np.ndarray, y: np.ndarray) -> Calibrator:
    p, y = np.asarray(p, dtype=float), np.asarray(y, dtype=int)
    if method == "none":
        return Calibrator("none")
    if method == "platt":
        model = LogisticRegression(C=np.inf, max_iter=1000).fit(_logit(p).reshape(-1, 1), y)
        return Calibrator("platt", platt_a=float(model.coef_[0, 0]), platt_b=float(model.intercept_[0]))
    if method == "isotonic":
        model = IsotonicRegression(out_of_bounds="clip", y_min=0.0, y_max=1.0, increasing=True).fit(p, y)
        return Calibrator("isotonic", isotonic_x=tuple(map(float, model.X_thresholds_)), isotonic_y=tuple(map(float, model.y_thresholds_)))
    raise ValueError(f"Unknown method {method!r}; known: {METHODS}.")


def reliability(y: np.ndarray, p: np.ndarray, bins: int = BINS) -> dict:
    """Equal-frequency bins: predicted mean vs observed frequency (95 % Wilson interval), ECE, MCE and the
    Murphy decomposition of the Brier score (computed on the bin means; it is approximate when forecasts vary
    inside a bin, so `brier` is reported separately)."""
    y, p = np.asarray(y, dtype=int), np.asarray(p, dtype=float)
    order = np.argsort(p, kind="mergesort")
    groups = [g for g in np.array_split(order, bins) if len(g)]
    n, base = len(y), float(y.mean())
    rows, ece, mce, rel, res = [], 0.0, 0.0, 0.0, 0.0
    for i, g in enumerate(groups):
        predicted, observed, count = float(p[g].mean()), float(y[g].mean()), len(g)
        low, high = _wilson(observed, count)
        gap = abs(observed - predicted)
        ece += count / n * gap
        mce = max(mce, gap)
        rel += count / n * (predicted - observed) ** 2
        res += count / n * (observed - base) ** 2
        rows.append({"bin": i + 1, "count": count, "mean_predicted": predicted, "observed_rate": observed,
                     "observed_ci95": [low, high], "predicted_in_ci95": low <= predicted <= high})
    return {"bins": rows, "ece": ece, "mce": mce,
            "brier_decomposition": {"reliability": rel, "resolution": res, "uncertainty": base * (1 - base)}}


def _wilson(rate: float, n: int, z: float = 1.96) -> tuple[float, float]:
    denominator = 1 + z * z / n
    centre = (rate + z * z / (2 * n)) / denominator
    half = z * np.sqrt(rate * (1 - rate) / n + z * z / (4 * n * n)) / denominator
    return float(centre - half), float(centre + half)


def walk_forward(oos: pd.DataFrame, column: str) -> dict:
    """Out-of-sample evaluation of each method: fold j is calibrated with folds < j (purged)."""
    folds = sorted(oos["fold"].unique())
    if len(folds) < 2:
        raise ValueError("Calibration needs out-of-sample predictions from at least 2 folds.")

    predictions = {m: [] for m in METHODS}
    targets, purged, per_fold = [], 0, []
    for j in folds[1:]:
        test = oos[oos["fold"] == j]
        start = test["decision_open_time_utc"].min()
        history = oos[oos["fold"] < j]
        train = history[history["label_end_open_time_utc"] < start]
        purged += len(history) - len(train)
        targets.append(test["target"].to_numpy())
        fold_metrics = {"fold": int(j), "train": int(len(train)), "test": int(len(test))}
        for method in METHODS:
            calibrator = fit(method, train[column].to_numpy(), train["target"].to_numpy())
            p = calibrator.apply(test[column].to_numpy())
            predictions[method].append(p)
            fold_metrics[method] = evaluation.probabilistic(test["target"].to_numpy(), p)["log_loss"]
        per_fold.append(fold_metrics)

    y = np.concatenate(targets)
    raw = oos[oos["fold"] != folds[0]][column].to_numpy()
    methods = {}
    for method in METHODS:
        p = np.concatenate(predictions[method])
        methods[method] = {**evaluation.probabilistic(y, p), "ece": reliability(y, p)["ece"]}

    best = min(METHODS, key=lambda m: methods[m]["log_loss"])
    selected = next(m for m in METHODS if methods[m]["log_loss"] - methods[best]["log_loss"] <= TIE_TOLERANCE)
    return {
        "evaluated_folds": [int(f) for f in folds[1:]],
        "samples": int(len(y)),
        "purged": int(purged),
        "raw_reliability": reliability(y, raw),
        "methods": methods,
        "per_fold_log_loss": per_fold,
        "selected": selected,
        "selected_reliability": reliability(y, np.concatenate(predictions[selected])),
        "improves_on_raw": selected != "none",
    }


def run(experiment_dir: Path, models_dir: Path, evaluate_holdout: bool, model_names: list[str] | None = None) -> dict:
    experiment = json.loads((experiment_dir / "experiment.json").read_text(encoding="utf-8"))
    oos = _read_predictions(experiment_dir / "oos_predictions.csv")
    names = model_names or list(experiment["models"])
    created = datetime.now(timezone.utc)
    record = {"calibratedAtUtc": created.isoformat(), "experimentId": experiment["experimentId"], "format": FORMAT, "models": {}}

    holdout_path = experiment_dir / "holdout_predictions.csv"
    if evaluate_holdout and not holdout_path.exists():
        raise ValueError("The experiment has no holdout predictions; run it with --evaluate-holdout first.")

    for name in names:
        column = f"p_{name}"
        analysis = walk_forward(oos, column)
        final = fit(analysis["selected"], oos[column].to_numpy(), oos["target"].to_numpy())
        model = experiment["models"][name]
        entry = {"walk_forward": analysis, "final": final.to_dict(), "fitted_on_samples": int(len(oos))}

        if model["export"]["status"] == "registered":
            package = {**final.to_dict(), "modelId": model["model_id"], "experimentId": experiment["experimentId"],
                       "fittedOn": "out-of-sample walk-forward predictions of the development period",
                       "fittedOnSamples": int(len(oos)), "createdAtUtc": created.isoformat()}
            (models_dir / model["model_id"] / "calibration.json").write_text(json.dumps(package, indent=2), encoding="utf-8")
            entry["packaged_with"] = model["model_id"]
        else:
            entry["packaged_with"] = None   # the model itself is not registered (Phase 7 parity check)

        if evaluate_holdout:
            entry["holdout"] = _holdout(experiment_dir, holdout_path, name, final)
        record["models"][name] = entry

    path = experiment_dir / f"calibration-{created:%Y%m%dT%H%M%SZ}.json"
    path.write_text(json.dumps(record, indent=2), encoding="utf-8")
    record["path"] = str(path)
    return record


def _holdout(experiment_dir: Path, path: Path, name: str, calibrator: Calibrator) -> dict:
    previous = sum(1 for p in experiment_dir.glob("calibration-*.json")
                   if json.loads(p.read_text(encoding="utf-8"))["models"].get(name, {}).get("holdout"))
    holdout = _read_predictions(path)
    y, raw = holdout["target"].to_numpy(), holdout[f"p_{name}"].to_numpy()
    calibrated = calibrator.apply(raw)
    return {
        "previous_evaluations": previous,
        "warning": f"Calibration of {name} had already been judged on this holdout {previous} time(s)." if previous else None,
        "raw": {**evaluation.probabilistic(y, raw), **reliability(y, raw)},
        "calibrated": {**evaluation.probabilistic(y, calibrated), **reliability(y, calibrated)},
    }


def _read_predictions(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    for column in ("decision_open_time_utc", "label_end_open_time_utc"):
        frame[column] = pd.to_datetime(frame[column], utc=True, format="ISO8601")
    return frame


def _print_summary(record: dict) -> None:
    print(f"Calibration of experiment {record['experimentId']}")
    for name, entry in record["models"].items():
        wf = entry["walk_forward"]
        print(f"  {name}: walk-forward folds {wf['evaluated_folds']}, samples {wf['samples']}, purged {wf['purged']}")
        for method, m in wf["methods"].items():
            mark = "  <- selected" if method == wf["selected"] else ""
            print(f"    {method:<9} log_loss {m['log_loss']:.4f}  brier {m['brier']:.4f}  ece {m['ece']:.4f}{mark}")
        packaged = entry["packaged_with"] or "not packaged (model not registered)"
        print(f"    calibrator: {entry['final']['method']} -> {packaged}")
        if "holdout" in entry:
            h = entry["holdout"]
            print(f"    HOLDOUT raw ece {h['raw']['ece']:.4f} log_loss {h['raw']['log_loss']:.4f} | "
                  f"calibrated ece {h['calibrated']['ece']:.4f} log_loss {h['calibrated']['log_loss']:.4f}")
            if h["warning"]:
                print(f"    WARNING: {h['warning']}")
    print(f"  record: {record['path']}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="OMEGA Phase 8 calibration.")
    parser.add_argument("--experiment", required=True, type=Path, help="Experiment directory from Phase 7.")
    parser.add_argument("--models", default=None, help="Comma-separated model names (default: all in the experiment).")
    parser.add_argument("--evaluate-holdout", action="store_true", help="Also judge the final calibrator on the holdout (once).")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "research" / "models")
    args = parser.parse_args(argv)
    names = [m.strip() for m in args.models.split(",")] if args.models else None
    _print_summary(run(args.experiment, args.models_dir, args.evaluate_holdout, names))
    return 0


if __name__ == "__main__":
    sys.exit(main())
