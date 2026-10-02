"""Expected value of a triple-barrier long (Phase 9). Reference implementation: the C# ExpectedValueCalculator
must give the same numbers (a parity test checks it).

Outcomes are measured in ATR14 multiples at the decision candle, r = (exit - entry) / atr_14, so a profile
estimated in one market regime transfers across price levels and volatility.

    a        = atr / price
    gain     = win_mean * a            (mean r of TP_FIRST outcomes, around +3)
    loss     = loss_mean * a           (mean r of SL_FIRST and TIMEOUT outcomes, negative)
    costs    = (fee + half_spread + slippage)                      entry, market order
             + p * fee                                             exit at the target, limit order
             + (1 - p) * (fee + half_spread + slippage)            exit at the stop or timeout, market order
    EV       = p * gain + (1 - p) * loss - costs                   (return on the position value)
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd

PROFILE_FORMAT = "omega-outcome-profile-v1"


@dataclass(frozen=True)
class Costs:
    fee_rate: float = 0.001
    spread_bps: float = 1.0
    slippage_bps: float = 2.0

    @property
    def market(self) -> float:
        """Cost of one market order as a fraction of notional: commission, half the spread, slippage."""
        return self.fee_rate + self.spread_bps / 2 / 10_000 + self.slippage_bps / 10_000


def expected_return(p: float, atr: float, price: float, win_mean: float, loss_mean: float, costs: Costs) -> dict:
    a = atr / price
    gain, loss = win_mean * a, loss_mean * a
    total_costs = costs.market + p * costs.fee_rate + (1 - p) * costs.market
    return {"probability": p, "gain": gain, "loss": loss, "costs": total_costs,
            "expected_return": p * gain + (1 - p) * loss - total_costs}


def outcome_profile(samples: pd.DataFrame, period: str, dataset_id: str, label: dict) -> dict:
    """Mean outcome in ATR multiples for wins (TP_FIRST) and for everything else, from labelled samples."""
    r = (samples["exit_price"] - samples["entry_price"]) / samples["atr_14"]
    win = samples["label"] == "TP_FIRST"
    if win.sum() == 0 or (~win).sum() == 0:
        raise ValueError("The outcome profile needs both winning and non-winning samples.")
    return {
        "format": PROFILE_FORMAT,
        "unit": "ATR14 multiples at the decision candle: (exit_price - entry_price) / atr_14",
        "winMean": float(r[win].mean()),
        "winCount": int(win.sum()),
        "lossMean": float(r[~win].mean()),
        "lossCount": int((~win).sum()),
        "stopLossShare": float((samples.loc[~win, "label"] == "SL_FIRST").mean()),
        "timeoutMean": float(r[samples["label"] == "TIMEOUT"].mean()) if (samples["label"] == "TIMEOUT").any() else None,
        "label": label,
        "period": period,
        "datasetId": dataset_id,
        "note": "Estimated on the development period only (the samples the model was trained on); never on the holdout.",
    }


def write_profile(profile: dict, model_dir: Path) -> None:
    (model_dir / "outcome_profile.json").write_text(json.dumps(profile, indent=2), encoding="utf-8")
