import json

import pytest

from omega_ml import dataset as ds


def test_loads_and_derives_the_binary_target(signal_dataset):
    data = ds.load(signal_dataset)

    assert len(data.frame) == data.manifest["samples"]
    assert set(data.frame["target"]) == {0, 1}
    assert (data.frame["target"] == (data.frame["label"] == "TP_FIRST")).all()
    assert str(data.frame["decision_open_time_utc"].dt.tz) == "UTC"


def test_a_modified_file_is_rejected(signal_dataset):
    csv = signal_dataset / "dataset.csv"
    csv.write_bytes(csv.read_bytes().replace(b"TP_FIRST", b"SL_FIRST", 1))

    with pytest.raises(ds.DatasetIntegrityError, match="hash"):
        ds.load(signal_dataset)


def test_unknown_format_is_rejected(signal_dataset):
    path = signal_dataset / "manifest.json"
    manifest = json.loads(path.read_text())
    manifest["format"] = "something-else"
    path.write_text(json.dumps(manifest))

    with pytest.raises(ds.DatasetIntegrityError, match="format"):
        ds.load(signal_dataset)


def test_only_scale_free_features_are_model_inputs():
    assert "sma_20" not in ds.MODEL_FEATURES and "atr_14" not in ds.MODEL_FEATURES and "volume" not in ds.MODEL_FEATURES
    assert len(ds.MODEL_FEATURES) == 8
