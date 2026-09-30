"""Loading and verifying datasets exported by the C# system (format omega-dataset-v1)."""
from __future__ import annotations

import hashlib
import io
import json
from dataclasses import dataclass
from pathlib import Path

import pandas as pd

FORMAT = "omega-dataset-v1"

# Model inputs: the scale-free features of features-v1. Price-level features (sma_20, ema_20, ema_50, atr_14,
# macd_*, volume) are non-stationary (docs/FEATURES.md) and are not used as model inputs. A normalized feature
# set would be a new feature-set version, computed in C#, not here.
MODEL_FEATURES = [
    "log_return_1", "log_return_3", "log_return_12", "dist_ema_20",
    "rsi_14", "volatility_20", "volume_zscore_20", "adx_14",
]

TIME_COLUMNS = ["decision_open_time_utc", "available_at_utc", "label_end_open_time_utc"]
LABELS = {"TP_FIRST", "SL_FIRST", "TIMEOUT"}


class DatasetIntegrityError(Exception):
    """The dataset does not match its manifest or violates the format."""


@dataclass(frozen=True)
class Dataset:
    directory: Path
    manifest: dict
    frame: pd.DataFrame  # sorted by decision time; includes `target` = 1 if TP_FIRST else 0

    @property
    def dataset_id(self) -> str:
        return self.manifest["datasetId"]


def load(directory: str | Path) -> Dataset:
    """Loads a dataset directory (dataset.csv + manifest.json) and verifies it. Raises DatasetIntegrityError."""
    directory = Path(directory)
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    raw = (directory / "dataset.csv").read_bytes()

    if manifest.get("format") != FORMAT:
        raise DatasetIntegrityError(f"Unsupported format {manifest.get('format')!r}; expected {FORMAT}.")

    digest = hashlib.sha256(raw).hexdigest()
    if digest != manifest["datasetId"]:
        raise DatasetIntegrityError(f"dataset.csv hash {digest} does not match manifest datasetId {manifest['datasetId']}.")

    frame = pd.read_csv(io.BytesIO(raw))
    if list(frame.columns) != manifest["columns"]:
        raise DatasetIntegrityError("Columns differ from the manifest.")

    missing = [f for f in MODEL_FEATURES if f not in manifest["featureColumns"]]
    if missing:
        raise DatasetIntegrityError(f"Model features missing from the dataset: {missing}.")

    if frame.isna().any().any():
        raise DatasetIntegrityError("The dataset contains empty values.")

    if not set(frame["label"]).issubset(LABELS):
        raise DatasetIntegrityError(f"Unknown labels: {set(frame['label']) - LABELS}.")

    for column in TIME_COLUMNS:
        frame[column] = pd.to_datetime(frame[column], utc=True, format="ISO8601")

    if not frame["decision_open_time_utc"].is_monotonic_increasing or frame["decision_open_time_utc"].duplicated().any():
        raise DatasetIntegrityError("Decision times must be strictly increasing.")

    if (frame["label_end_open_time_utc"] <= frame["decision_open_time_utc"]).any():
        raise DatasetIntegrityError("A label ends at or before its decision candle.")

    if len(frame) != manifest["samples"]:
        raise DatasetIntegrityError("Row count differs from the manifest.")

    frame["target"] = (frame["label"] == "TP_FIRST").astype(int)
    return Dataset(directory, manifest, frame)
