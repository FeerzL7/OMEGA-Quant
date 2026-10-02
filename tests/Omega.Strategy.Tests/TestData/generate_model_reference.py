"""Reference model package and expected values for the C# ModelPackage / ExpectedValueCalculator tests.

Produced with the research pipeline's own code (registry, calibration, expected_value), so the C# side is
checked against exactly what Python would compute. Usage (research environment active):
    python3 generate_model_reference.py
Writes model_package/ and model_package_rf/ ({model.onnx, manifest.json, calibration.json, outcome_profile.json})
and model_cases.csv / model_cases_rf.csv.
"""
import json
import shutil
import sys
from pathlib import Path

import numpy as np
import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "research" / "ml"))
from omega_ml import calibration, dataset as ds, expected_value, models, registry  # noqa: E402

rng = np.random.default_rng(20261001)
n = 3000
x = np.column_stack([
    rng.normal(0, 0.002, n), rng.normal(0, 0.004, n), rng.normal(0, 0.01, n), rng.normal(0, 0.01, n),
    rng.uniform(20, 80, n), rng.uniform(0.0005, 0.004, n), rng.normal(0, 1, n), rng.uniform(10, 50, n)])
y = (rng.random(n) < 1 / (1 + np.exp(-(-0.8 + 0.06 * (x[:, 4] - 50))))).astype(int)



def build(model_type: str, package_name: str, cases_name: str) -> None:
    model = models.make(model_type, 7).fit(x, y)
    out = Path("models-tmp")
    shutil.rmtree(out, ignore_errors=True)
    label = {"stopAtr": 2.0, "targetAtr": 3.0, "horizonCandles": 48}
    manifest = registry.register(model, model_type, ds.MODEL_FEATURES, x, out, {
        "experimentId": "reference", "datasetId": "reference-dataset", "featureSetVersion": "features-v1",
        "featureSetHash": "reference", "label": label,
        "trainingPeriod": "2025-01-01T00:00:00+00:00..2025-06-01T00:00:00+00:00",
        "trainingSamples": n, "hyperparameters": {}, "seed": 7})

    raw_all = model.predict_proba(x)[:, 1]
    platt = calibration.fit("platt", raw_all, y)
    labels = np.where(y == 1, "TP_FIRST", np.where(rng.random(n) < 0.7, "SL_FIRST", "TIMEOUT"))
    atr = np.full(n, 150.0)
    exit_r = np.where(labels == "TP_FIRST", 2.9, np.where(labels == "SL_FIRST", -2.05, rng.uniform(-1.5, 2.5, n)))
    profile = expected_value.outcome_profile(
        pd.DataFrame({"label": labels, "entry_price": 60_000.0, "exit_price": 60_000.0 + exit_r * atr, "atr_14": atr}),
        label=label, period="2025-01-01..2025-06-01", dataset_id="reference-dataset")

    package = Path(package_name)
    shutil.rmtree(package, ignore_errors=True)
    shutil.move(str(out / manifest["modelId"]), package)
    shutil.rmtree(out)
    (package / "calibration.json").write_text(json.dumps({**platt.to_dict(), "modelId": manifest["modelId"]}, indent=2))
    expected_value.write_profile(profile, package)

    costs = expected_value.Costs(fee_rate=0.001, spread_bps=1.0, slippage_bps=2.0)
    rows = []
    for i in range(0, n, 15):
        raw = registry.onnx_probabilities((package / "model.onnx").read_bytes(), x[i:i + 1], "float64")[0]
        calibrated = platt.apply(np.array([raw]))[0]
        price, atr_value = 60_000.0 + 7.0 * i, 80.0 + (i % 50)
        ev = expected_value.expected_return(calibrated, atr_value, price, profile["winMean"], profile["lossMean"], costs)
        rows.append([*x[i], atr_value, price, raw, calibrated, ev["expected_return"]])
    columns = ds.MODEL_FEATURES + ["atr_14", "price", "p_raw", "p_calibrated", "expected_return"]
    with open(cases_name, "w") as f:
        f.write(",".join(columns) + "\n")
        for row in rows:
            f.write(",".join(repr(float(v)) for v in row) + "\n")
    print(f"{cases_name}: {len(rows)} cases; model {manifest['modelId']}; platt a={platt.platt_a:.4f}; profile win {profile['winMean']:.3f} loss {profile['lossMean']:.3f}")


# Logistic regression returns float64 probabilities; the random forest returns float32 ones (both with float64
# inputs): the C# side must read both.
build("logistic_regression", "model_package", "model_cases.csv")
build("random_forest", "model_package_rf", "model_cases_rf.csv")
