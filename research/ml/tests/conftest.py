"""Synthetic datasets in the exact omega-dataset-v1 format written by the C# DatasetBuilder."""
import hashlib
import json
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

FEATURES = ["log_return_1", "log_return_3", "log_return_12", "sma_20", "ema_20", "ema_50", "dist_ema_20", "rsi_14",
            "atr_14", "macd_line", "macd_signal", "macd_histogram", "volatility_20", "volume", "volume_zscore_20", "adx_14"]
COLUMNS = (["decision_open_time_utc", "available_at_utc"] + FEATURES +
           ["baseline_long", "label", "label_end_open_time_utc", "entry_price", "stop_loss", "take_profit", "exit_price", "gross_return", "holding_candles"])
START = pd.Timestamp("2025-01-01T00:00:00Z")


def write_dataset(directory: Path, n: int = 3000, signal: bool = True, seed: int = 7) -> Path:
    rng = np.random.default_rng(seed)
    t = START + pd.to_timedelta(np.arange(n) * 5, unit="min")
    rsi = rng.normal(50, 12, n)
    logit = -0.6 + (1.6 * (rsi - 50) / 12 if signal else 0.0)
    target = rng.random(n) < 1 / (1 + np.exp(-logit))
    holding = rng.integers(1, 49, n)
    label = np.where(target, "TP_FIRST", np.where(rng.random(n) < 0.7, "SL_FIRST", "TIMEOUT"))

    frame = pd.DataFrame({name: rng.normal(0, 1, n) for name in FEATURES})
    frame["rsi_14"] = rsi
    frame.insert(0, "decision_open_time_utc", t.strftime("%Y-%m-%dT%H:%M:%S.000Z"))
    frame.insert(1, "available_at_utc", (t + pd.Timedelta(minutes=5) - pd.Timedelta(milliseconds=1)).strftime("%Y-%m-%dT%H:%M:%S.%fZ").str[:-4] + "Z")
    frame["baseline_long"] = (frame["dist_ema_20"] > 0.5).astype(int)
    frame["label"] = label
    frame["label_end_open_time_utc"] = (t + pd.to_timedelta(holding * 5, unit="min")).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    # Barrier outcomes in ATR units around an entry of 100 with ATR 1 (written into atr_14 below).
    frame["atr_14"] = 1.0
    frame["entry_price"] = 100.0
    frame["stop_loss"] = 98.0
    frame["take_profit"] = 103.0
    timeout_r = rng.uniform(-1.5, 2.5, n)
    frame["exit_price"] = 100.0 + np.where(label == "TP_FIRST", 3.0, np.where(label == "SL_FIRST", -2.0, timeout_r))
    frame["gross_return"] = frame["exit_price"] / frame["entry_price"] - 1
    frame["holding_candles"] = holding
    frame = frame[COLUMNS]

    content = frame.to_csv(index=False, lineterminator="\n").encode("utf-8")
    dataset_id = hashlib.sha256(content).hexdigest()
    target_dir = directory / dataset_id
    target_dir.mkdir(parents=True)
    (target_dir / "dataset.csv").write_bytes(content)
    manifest = {
        "datasetId": dataset_id, "format": "omega-dataset-v1", "createdAtUtc": "2026-09-30T00:00:00Z",
        "symbol": "BTCUSDT", "interval": "5m", "fromUtc": START.isoformat(), "toUtc": (START + pd.Timedelta(minutes=5 * n)).isoformat(),
        "featureSetVersion": "features-v1", "featureSetHash": "test", "featureColumns": FEATURES,
        "label": {"stopAtr": 2, "targetAtr": 3, "horizonCandles": 48}, "labelDefinition": "test",
        "baselineStrategy": "baseline-ema-trend v1", "candles": {}, "samples": n,
        "outcomes": {k: int((label == k).sum()) for k in ("TP_FIRST", "SL_FIRST", "TIMEOUT")},
        "excluded": {}, "columns": COLUMNS,
    }
    (target_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return target_dir


def period_bounds(n: int, development_share: float = 0.75) -> tuple[str, str]:
    """Development / holdout period strings covering a synthetic dataset of n rows."""
    split = START + pd.Timedelta(minutes=5 * int(n * development_share))
    end = START + pd.Timedelta(minutes=5 * n)
    iso = lambda ts: ts.strftime("%Y-%m-%dT%H:%M:%SZ")
    return f"{iso(START)}..{iso(split)}", f"{iso(split)}..{iso(end)}"


@pytest.fixture
def signal_dataset(tmp_path):
    return write_dataset(tmp_path / "datasets", signal=True)


@pytest.fixture
def noise_dataset(tmp_path):
    return write_dataset(tmp_path / "datasets", signal=False, seed=11)
