import pandas as pd
import pytest

from omega_ml import dataset as ds
from omega_ml.splits import Period, purge, walk_forward


def test_periods_parse_dates_and_iso_timestamps():
    by_date = Period.parse("2025-01-01:2025-02-01")
    by_iso = Period.parse("2025-01-01T00:00:00Z..2025-02-01T00:00:00Z")

    assert by_date == by_iso
    assert str(by_date.start.tz) == "UTC"
    with pytest.raises(ValueError):
        Period.parse("2025-02-01:2025-01-01")


def test_walk_forward_is_chronological_contiguous_and_purged(signal_dataset):
    frame = ds.load(signal_dataset).frame
    folds = walk_forward(frame, 5)

    assert len(folds) == 5
    previous_end = None
    for fold in folds:
        train, test = frame.iloc[fold.train_index], frame.iloc[fold.test_index]
        assert train["decision_open_time_utc"].max() < test["decision_open_time_utc"].min()
        # Purged: every training label is fully known before the test period starts.
        assert (train["label_end_open_time_utc"] < fold.test_start).all()
        assert (fold.test_index == range(fold.test_index[0], fold.test_index[-1] + 1)).all()
        if previous_end is not None:
            assert test["decision_open_time_utc"].min() > previous_end
        previous_end = test["decision_open_time_utc"].max()

    assert sum(f.purged for f in folds) > 0   # labels lasting up to 48 candles do overlap the boundaries


def test_purge_removes_only_labels_open_at_the_test_start():
    t = pd.Timestamp("2025-01-01T01:00:00Z")
    train = pd.DataFrame({"label_end_open_time_utc": [t - pd.Timedelta(minutes=5), t, t + pd.Timedelta(minutes=5)]})

    assert list(purge(train, t).index) == [0]
