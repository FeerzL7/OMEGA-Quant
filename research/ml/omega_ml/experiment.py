"""Runs a Phase 7 experiment:

    python -m omega_ml.experiment --dataset ../datasets/<id> --development 2025-01-01:2025-10-01 \\
        --holdout 2025-10-01:2026-01-01 [--evaluate-holdout]

1. Loads and verifies the dataset (hash against its manifest).
2. Walk-forward validation inside the development period (expanding window, purged).
3. Metrics per model against the no-skill reference and against the baseline rule at equal coverage.
4. Final models trained on the whole development period (purged against the holdout), exported to ONNX.
5. Only with --evaluate-holdout: one evaluation on the holdout, counted against earlier ones.
6. Writes research/experiments/<id>/experiment.json and oos_predictions.csv (input for calibration, Phase 8).
"""
from __future__ import annotations

import argparse
import json
import platform
import sys
from datetime import datetime, timezone
from importlib import metadata as importlib_metadata
from pathlib import Path

import numpy as np
import pandas as pd

from . import dataset as ds
from . import evaluation, models, registry
from .splits import Period, purge, walk_forward

REPO_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_SEED = 20260930


def run(dataset_dir: Path, development: Period, holdout: Period, evaluate_holdout: bool, model_names: list[str],
        folds: int, seed: int, experiments_dir: Path, models_dir: Path) -> dict:
    data = ds.load(dataset_dir)
    frame = data.frame
    _check_periods(data.manifest, development, holdout)

    dev = frame[development.mask(frame)].reset_index(drop=True)
    x_dev, y_dev = dev[ds.MODEL_FEATURES].to_numpy(), dev["target"].to_numpy()
    wf = walk_forward(dev, folds)

    created = datetime.now(timezone.utc)
    experiment_id = f"{created:%Y%m%dT%H%M%SZ}-{data.dataset_id[:8]}"
    oos = pd.DataFrame({
        "decision_open_time_utc": dev["decision_open_time_utc"].iloc[np.concatenate([f.test_index for f in wf])].to_numpy(),
        "label_end_open_time_utc": dev["label_end_open_time_utc"].iloc[np.concatenate([f.test_index for f in wf])].to_numpy(),
        "target": np.concatenate([y_dev[f.test_index] for f in wf]),
        "baseline_long": np.concatenate([dev["baseline_long"].to_numpy()[f.test_index] for f in wf]),
        "fold": np.concatenate([np.full(len(f.test_index), f.number) for f in wf]),
    })

    # No-skill reference: each test fold predicts the base rate of its own training data.
    reference_p = np.concatenate([np.full(len(f.test_index), y_dev[f.train_index].mean()) for f in wf])
    reference = evaluation.probabilistic(oos["target"], reference_p)

    results = {}
    final_train = purge(dev, holdout.start)
    for name in model_names:
        fold_metrics, predictions = [], []
        for fold in wf:
            model = models.make(name, seed)
            model.fit(x_dev[fold.train_index], y_dev[fold.train_index])
            p = model.predict_proba(x_dev[fold.test_index])[:, 1]
            predictions.append(p)
            fold_metrics.append({"fold": fold.number, **evaluation.probabilistic(y_dev[fold.test_index], p)})

        p_oos = np.concatenate(predictions)
        oos[f"p_{name}"] = p_oos
        overall = evaluation.probabilistic(oos["target"], p_oos)

        final = models.make(name, seed)
        final.fit(final_train[ds.MODEL_FEATURES].to_numpy(), final_train["target"].to_numpy())
        export = _export(final, name, x_dev, models_dir,
            {
                "experimentId": experiment_id,
                "datasetId": data.dataset_id,
                "featureSetVersion": data.manifest["featureSetVersion"],
                "featureSetHash": data.manifest["featureSetHash"],
                "label": data.manifest["label"],
                "trainingPeriod": str(development),
                "trainingSamples": int(len(final_train)),
                "hyperparameters": models.hyperparameters(final),
                "seed": seed,
            })

        results[name] = {
            "hyperparameters": models.hyperparameters(final),
            "folds": fold_metrics,
            "out_of_sample": overall,
            "skill_vs_reference": evaluation.skill(overall, reference),
            "vs_baseline": evaluation.against_baseline(oos["target"], p_oos, oos["baseline_long"]),
            "export": export,
            "model_id": export.get("modelId"),
            "_final": final,
        }

    holdout_record = {"period": str(holdout), "evaluated": False}
    holdout_predictions = None
    if evaluate_holdout:
        holdout_record, holdout_predictions = _evaluate_holdout(frame, holdout, results, data.dataset_id, experiments_dir)

    record = {
        "experimentId": experiment_id,
        "createdAtUtc": created.isoformat(),
        "dataset": {k: data.manifest[k] for k in ("datasetId", "symbol", "interval", "fromUtc", "toUtc", "featureSetVersion", "featureSetHash", "label", "samples", "outcomes")},
        "modelFeatures": ds.MODEL_FEATURES,
        "target": "1 if TP_FIRST else 0 (SL_FIRST or TIMEOUT)",
        "development": {
            "period": str(development),
            "samples": int(len(dev)),
            "folds": [{"fold": f.number, "train": int(len(f.train_index)), "purged": f.purged, "test": int(len(f.test_index)),
                       "test_start": f.test_start.isoformat(), "test_end": f.test_end.isoformat()} for f in wf],
            "reference": reference,
            "baseline_rule": evaluation.against_baseline(oos["target"], oos["target"], oos["baseline_long"]),
        },
        "models": {name: {k: v for k, v in r.items() if not k.startswith("_")} for name, r in results.items()},
        "holdout": holdout_record,
        "seed": seed,
        "environment": _environment(),
    }

    directory = experiments_dir / experiment_id
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "experiment.json").write_text(json.dumps(record, indent=2, default=_json_default), encoding="utf-8")
    oos.to_csv(directory / "oos_predictions.csv", index=False, date_format="%Y-%m-%dT%H:%M:%S.%fZ")
    if holdout_predictions is not None:
        holdout_predictions.to_csv(directory / "holdout_predictions.csv", index=False, date_format="%Y-%m-%dT%H:%M:%S.%fZ")
    record["directory"] = str(directory)
    return record


def _export(final, name: str, x_check: np.ndarray, models_dir: Path, metadata: dict) -> dict:
    """Registers the model for deployment only if its ONNX export reproduces it; otherwise records why not."""
    try:
        manifest = registry.register(final, name, ds.MODEL_FEATURES, x_check, models_dir, metadata)
        return {"status": "registered", "modelId": manifest["modelId"], "inputType": manifest["input"]["type"],
                "parityMaxDifference": manifest["parity"]["maxAbsoluteDifference"]}
    except registry.ExportError as error:
        return {"status": "not_registered", "reason": str(error)}


def _evaluate_holdout(frame: pd.DataFrame, holdout: Period, results: dict, dataset_id: str, experiments_dir: Path) -> tuple[dict, pd.DataFrame]:
    previous = count_holdout_evaluations(experiments_dir, dataset_id, str(holdout))
    test = frame[holdout.mask(frame)]
    x, y = test[ds.MODEL_FEATURES].to_numpy(), test["target"].to_numpy()
    predictions = pd.DataFrame({
        "decision_open_time_utc": test["decision_open_time_utc"].to_numpy(),
        "label_end_open_time_utc": test["label_end_open_time_utc"].to_numpy(),
        "target": y,
        "baseline_long": test["baseline_long"].to_numpy(),
    })
    per_model = {}
    for name, r in results.items():
        p = r["_final"].predict_proba(x)[:, 1]
        predictions[f"p_{name}"] = p
        per_model[name] = {
            "metrics": evaluation.probabilistic(y, p),
            "vs_baseline": evaluation.against_baseline(y, p, test["baseline_long"].to_numpy()),
        }
    return {
        "period": str(holdout),
        "evaluated": True,
        "previous_evaluations": previous,
        "warning": (f"This holdout had already been evaluated {previous} time(s) on this dataset; its value as "
                    "out-of-sample evidence is reduced.") if previous else None,
        "samples": int(len(test)),
        "base_rate": float(y.mean()) if len(y) else None,
        "models": per_model,
    }, predictions


def count_holdout_evaluations(experiments_dir: Path, dataset_id: str, period: str) -> int:
    count = 0
    for path in experiments_dir.glob("*/experiment.json"):
        record = json.loads(path.read_text(encoding="utf-8"))
        holdout = record.get("holdout", {})
        if record.get("dataset", {}).get("datasetId") == dataset_id and holdout.get("evaluated") and holdout.get("period") == period:
            count += 1
    return count


def _check_periods(manifest: dict, development: Period, holdout: Period) -> None:
    start, end = pd.Timestamp(manifest["fromUtc"]).tz_convert("UTC"), pd.Timestamp(manifest["toUtc"]).tz_convert("UTC")
    if development.end > holdout.start:
        raise ValueError("The development period must end before the holdout starts.")
    if development.start < start or holdout.end > end:
        raise ValueError(f"Periods must lie inside the dataset range {start.isoformat()}..{end.isoformat()}.")


def _environment() -> dict:
    packages = ["numpy", "pandas", "scikit-learn", "lightgbm", "skl2onnx", "onnxmltools", "onnxruntime"]
    return {"python": platform.python_version(), **{p: importlib_metadata.version(p) for p in packages}}


def _json_default(value):
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, (np.floating,)):
        return float(value)
    if isinstance(value, pd.Timestamp):
        return value.isoformat()
    return str(value)


def _print_summary(record: dict) -> None:
    dev = record["development"]
    print(f"Experiment {record['experimentId']}  dataset {record['dataset']['datasetId'][:12]}…  development {dev['period']}")
    print(f"  development samples {dev['samples']}, walk-forward folds {len(dev['folds'])}, base rate TP_FIRST {dev['reference']['base_rate']:.3f}")
    print(f"  reference (base rate)  log_loss {dev['reference']['log_loss']:.4f}  brier {dev['reference']['brier']:.4f}")
    rule = dev["baseline_rule"]
    if rule:
        print(f"  baseline rule          signals {rule['signals']} ({rule['coverage']:.1%})  precision {rule['baseline_precision']:.3f}")
    for name, r in record["models"].items():
        o, s, b = r["out_of_sample"], r["skill_vs_reference"], r["vs_baseline"]
        line = (f"  {name:<22} log_loss {o['log_loss']:.4f} ({s['log_loss_skill']:+.2%})  brier {o['brier']:.4f} ({s['brier_skill']:+.2%})"
                f"  auc {o['roc_auc']:.3f}")
        if b:
            line += f"  precision@baseline-coverage {b['model_precision_same_coverage']:.3f}"
        export = r["export"]
        print(line + (f"  -> {r['model_id']}" if export["status"] == "registered" else "  -> NOT REGISTERED (ONNX parity)"))
    holdout = record["holdout"]
    if holdout["evaluated"]:
        print(f"  HOLDOUT {holdout['period']}: samples {holdout['samples']}, base rate {holdout['base_rate']}")
        for name, h in holdout["models"].items():
            print(f"    {name:<20} log_loss {h['metrics']['log_loss']:.4f}  auc {h['metrics']['roc_auc']}")
        if holdout["warning"]:
            print(f"  WARNING: {holdout['warning']}")
    else:
        print("  holdout not evaluated (use --evaluate-holdout once, at the end).")
    print(f"  record: {record['directory']}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="OMEGA Phase 7 experiment.")
    parser.add_argument("--dataset", required=True, type=Path, help="Dataset directory (dataset.csv + manifest.json).")
    parser.add_argument("--development", required=True, help="Development period, e.g. 2025-01-01:2025-10-01.")
    parser.add_argument("--holdout", required=True, help="Holdout period, fixed in advance, e.g. 2025-10-01:2026-01-01.")
    parser.add_argument("--evaluate-holdout", action="store_true", help="Evaluate the holdout (do this once, at the end).")
    parser.add_argument("--models", default=",".join(models.MODEL_ORDER))
    parser.add_argument("--folds", type=int, default=5)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--experiments-dir", type=Path, default=REPO_ROOT / "research" / "experiments")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "research" / "models")
    args = parser.parse_args(argv)

    record = run(args.dataset, Period.parse(args.development), Period.parse(args.holdout), args.evaluate_holdout,
                 [m.strip() for m in args.models.split(",") if m.strip()], args.folds, args.seed, args.experiments_dir, args.models_dir)
    _print_summary(record)
    return 0


if __name__ == "__main__":
    sys.exit(main())
