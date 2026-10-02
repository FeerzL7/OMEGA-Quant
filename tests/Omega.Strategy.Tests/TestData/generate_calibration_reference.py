"""Reference cases for the C# ProbabilityCalibrator, produced by the Python calibration module itself.

Usage (with the research environment active): python3 generate_calibration_reference.py
Writes platt.json, isotonic.json, none.json and calibration_cases.csv (raw probability and expected outputs).
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "research" / "ml"))
from omega_ml import calibration  # noqa: E402

rng = np.random.default_rng(20260930)
true = rng.uniform(0.02, 0.98, 4000)
y = (rng.random(4000) < true).astype(int)
raw = 1 / (1 + np.exp(-2.2 * np.log(true / (1 - true))))   # overconfident model

calibrators = {m: calibration.fit(m, raw, y) for m in calibration.METHODS}
iso = calibrators["isotonic"]
assert all(b > a for a, b in zip(iso.isotonic_x, iso.isotonic_x[1:])), "isotonic x must be strictly increasing"

for method, cal in calibrators.items():
    Path(f"{method}.json").write_text(json.dumps(cal.to_dict(), indent=2))

# Edge values, the isotonic points themselves, values between them, and a dense grid.
cases = np.unique(np.concatenate([[0.0, 1e-12, 1e-7, 1e-6, 0.5, 1 - 1e-6, 1 - 1e-12, 1.0],
                                  iso.isotonic_x, np.array(iso.isotonic_x[:-1]) + np.diff(iso.isotonic_x) / 3,
                                  np.linspace(0, 1, 1001)]))
with open("calibration_cases.csv", "w") as f:
    f.write("raw,none,platt,isotonic\n")
    for p in cases:
        f.write(",".join(repr(float(v)) for v in (p, *(calibrators[m].apply(np.array([p]))[0] for m in calibration.METHODS))) + "\n")
print(f"{len(cases)} cases; isotonic points: {len(iso.isotonic_x)}; platt a={calibrators['platt'].platt_a:.4f} b={calibrators['platt'].platt_b:.4f}")
