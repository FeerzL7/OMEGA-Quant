"""Chronological splits with purging: no training label may overlap the period it is evaluated on."""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import pandas as pd


@dataclass(frozen=True)
class Period:
    start: pd.Timestamp
    end: pd.Timestamp  # exclusive

    @staticmethod
    def parse(text: str) -> "Period":
        """'2025-01-01:2025-10-01' (dates) or '2025-01-01T00:00:00Z..2025-10-01T00:00:00Z' (ISO timestamps). UTC."""
        if ".." in text:
            start, end = text.split("..", 1)
        elif text.count(":") == 1:
            start, end = text.split(":")
        else:
            raise ValueError("Use 'YYYY-MM-DD:YYYY-MM-DD' or 'ISO..ISO'.")
        period = Period(_utc(start), _utc(end))
        if period.end <= period.start:
            raise ValueError(f"Period {text!r} ends before it starts.")
        return period

    def mask(self, frame: pd.DataFrame) -> pd.Series:
        decision = frame["decision_open_time_utc"]
        return (decision >= self.start) & (decision < self.end)

    def __str__(self) -> str:
        return f"{self.start.isoformat()}..{self.end.isoformat()}"


def _utc(text: str) -> pd.Timestamp:
    stamp = pd.Timestamp(text)
    return stamp.tz_localize("UTC") if stamp.tzinfo is None else stamp.tz_convert("UTC")


def purge(train: pd.DataFrame, test_start: pd.Timestamp) -> pd.DataFrame:
    """Drops training samples whose label is still open when the test period starts (their outcome depends on
    prices inside the test period: leakage). A label whose exit candle opens before test_start is fully known
    before it."""
    return train[train["label_end_open_time_utc"] < test_start]


@dataclass(frozen=True)
class Fold:
    number: int
    train_index: np.ndarray
    test_index: np.ndarray
    purged: int
    test_start: pd.Timestamp
    test_end: pd.Timestamp


def walk_forward(frame: pd.DataFrame, folds: int) -> list[Fold]:
    """Expanding-window walk-forward over `frame` (already restricted to one period, sorted by time):
    the rows are cut into folds + 1 contiguous blocks; fold k trains on blocks 0..k (purged) and tests on block k+1."""
    if folds < 2:
        raise ValueError("At least 2 folds.")
    if len(frame) < (folds + 1) * 20:
        raise ValueError(f"Too few samples ({len(frame)}) for {folds} folds.")

    blocks = np.array_split(np.arange(len(frame)), folds + 1)
    result = []
    for k in range(folds):
        test = blocks[k + 1]
        test_start = frame["decision_open_time_utc"].iloc[test[0]]
        train_all = frame.iloc[np.concatenate(blocks[: k + 1])]
        train = purge(train_all, test_start)
        result.append(Fold(
            number=k + 1,
            train_index=frame.index.get_indexer(train.index),
            test_index=test,
            purged=len(train_all) - len(train),
            test_start=test_start,
            test_end=frame["decision_open_time_utc"].iloc[test[-1]],
        ))
    return result
