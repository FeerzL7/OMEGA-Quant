"""Model versioning: every trained model is exported to ONNX (the format the C# system will run) with a manifest.
The ONNX output is checked against the original model before it is registered."""
from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import onnxruntime as ort
from lightgbm import LGBMClassifier
from onnxmltools import convert_lightgbm
from skl2onnx import convert_sklearn
from skl2onnx.common.data_types import DoubleTensorType as SklDouble
from onnxmltools.convert.common.data_types import FloatTensorType as LgbFloat

INPUT_NAME = "features"
PARITY_TOLERANCE = 1e-6

# scikit-learn models are exported with float64 inputs (the C# features are doubles): exact parity.
# The LightGBM converter only accepts float32 inputs; its trees then compare float32-rounded thresholds, which can
# send a sample down another branch. Parity is checked on every development sample and a model that does not
# reproduce itself is not registered: what is evaluated must be what is deployed.
ML_OPSET = {"": 17, "ai.onnx.ml": 3}


class ExportError(Exception):
    """The ONNX model does not reproduce the original model."""


def to_onnx(estimator, feature_count: int, graph_name: str = "omega_model") -> tuple[bytes, str]:
    """Returns (serialized model, input element type)."""
    if isinstance(estimator, LGBMClassifier):
        onnx_model = convert_lightgbm(estimator, initial_types=[(INPUT_NAME, LgbFloat([None, feature_count]))], zipmap=False)
        input_type = "float32"
    else:
        onnx_model = convert_sklearn(
            estimator, initial_types=[(INPUT_NAME, SklDouble([None, feature_count]))], options={"zipmap": False}, target_opset=ML_OPSET)
        input_type = "float64"
    # The converters name the graph with a random UUID; a fixed name makes the bytes (and the model id) reproducible.
    onnx_model.graph.name = graph_name
    return onnx_model.SerializeToString(), input_type


def onnx_probabilities(onnx_bytes: bytes, x: np.ndarray, input_type: str = "float64") -> np.ndarray:
    """P(class 1) from the ONNX model."""
    options = ort.SessionOptions()
    options.log_severity_level = 3  # errors only (LightGBM's label output declares a static shape: harmless warning)
    session = ort.InferenceSession(onnx_bytes, options, providers=["CPUExecutionProvider"])
    feed = x.astype(np.float64 if input_type == "float64" else np.float32)
    outputs = session.run(None, {INPUT_NAME: feed})
    probabilities = next(o for o in outputs if isinstance(o, np.ndarray) and o.ndim == 2 and o.shape[1] == 2)
    return probabilities[:, 1]


def register(estimator, model_type: str, feature_names: list[str], x_check: np.ndarray, models_dir: Path, metadata: dict) -> dict:
    """Exports, verifies parity on every row of `x_check`, and writes models_dir/<model_id>/{model.onnx, manifest.json}.
    Raises ExportError (and writes nothing) when the ONNX model does not reproduce the original."""
    onnx_bytes, input_type = to_onnx(estimator, len(feature_names), graph_name=model_type)
    native = estimator.predict_proba(x_check)[:, 1]
    exported = onnx_probabilities(onnx_bytes, x_check, input_type)
    max_difference = float(np.max(np.abs(native - exported)))
    if max_difference > PARITY_TOLERANCE:
        raise ExportError(f"{model_type}: ONNX differs from the original model by {max_difference} (> {PARITY_TOLERANCE}).")

    model_id = f"{model_type}-{hashlib.sha256(onnx_bytes).hexdigest()[:12]}"
    directory = models_dir / model_id
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "model.onnx").write_bytes(onnx_bytes)

    manifest = {
        "modelId": model_id,
        "modelType": model_type,
        "createdAtUtc": datetime.now(timezone.utc).isoformat(),
        "input": {"name": INPUT_NAME, "type": input_type, "features": feature_names},
        "output": "P(TP_FIRST): probability that the take profit is reached before the stop loss and the timeout. Not calibrated (Phase 8).",
        "onnxSha256": hashlib.sha256(onnx_bytes).hexdigest(),
        "parity": {"samples": int(len(x_check)), "maxAbsoluteDifference": max_difference, "tolerance": PARITY_TOLERANCE},
        **metadata,
    }
    (directory / "manifest.json").write_text(json.dumps(manifest, indent=2, default=str), encoding="utf-8")
    return manifest
