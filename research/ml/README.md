# omega_ml — investigación de la Fase 7

Pipeline de entrenamiento y evaluación sobre datasets exportados por el sistema C#. Guía completa:
[`docs/MACHINE_LEARNING.md`](../../docs/MACHINE_LEARNING.md).

```bash
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python -m pytest
python -m omega_ml.experiment --dataset ../datasets/<id> --development A:B --holdout B:C [--evaluate-holdout]
```

Reglas: no recalcular features ni etiquetas aquí; no evaluar el holdout más de una vez; no ajustar
hiperparámetros mirando resultados del holdout.
