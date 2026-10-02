# Machine learning (Fase 7)

Decisiones en [ADR-004](decisions/ADR-004-ml-python-onnx.md). Protocolo general en
[EVALUATION_PROTOCOL.md](EVALUATION_PROTOCOL.md).

## Flujo

```text
PostgreSQL (velas) ──► POST /api/datasets (C#: features + etiquetas) ──► research/datasets/<id>/{dataset.csv, manifest.json}
                                                                               │
                         python -m omega_ml.experiment (verifica hash, walk-forward con purging, modelos)
                                                                               │
          research/experiments/<id>/experiment.json + oos_predictions.csv      research/models/<modelo>/{model.onnx, manifest.json}
```

## 1. Exportar el dataset

Con la API corriendo (`dotnet run --project src/Omega.Api`) e historia importada:

```bash
curl -X POST http://localhost:5080/api/datasets -H "Content-Type: application/json" -d '{
  "symbol": "BTCUSDT", "interval": "5m", "fromUtc": "2025-01-01T00:00:00Z", "toUtc": "2026-01-01T00:00:00Z" }'
```

Escribe en `research/datasets/<datasetId>/` (configurable con `Research:DatasetsDirectory`). La API carga
también el calentamiento de features (antes del periodo) y las 48 velas posteriores que necesitan las etiquetas.

### Formato `omega-dataset-v1`

Una fila por vela de decisión cerrada con features completos y etiqueta determinable:

| Columna | Contenido |
|---------|-----------|
| `decision_open_time_utc`, `available_at_utc` | Vela *t* y cierre de *t* (desde cuándo se conocen los features) |
| 16 columnas de `features-v1` | Valores calculados por `FeatureEngine` con velas hasta *t* |
| `baseline_long` | 1 si la estrategia base daría LONG en *t* (sin posición) |
| `label` | `TP_FIRST`, `SL_FIRST` o `TIMEOUT` |
| `label_end_open_time_utc` | Vela en la que se conoce el resultado (para el purging) |
| `entry_price`, `stop_loss`, `take_profit`, `exit_price`, `gross_return`, `holding_candles` | Detalle de la etiqueta (precios brutos) |

`manifest.json` registra: id (SHA-256 del CSV), periodo, versión y hash de features, especificación y
definición de la etiqueta, huella de las velas, conteos por resultado y exclusiones.

## 2. Preparar el entorno de Python

```bash
cd research/ml
python -m venv .venv && source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
python -m pytest                                        # 34 tests
```

## 3. Ejecutar un experimento

Fija los periodos **antes** de ejecutar (protocolo §1) y no los cambies después de ver resultados:

```bash
python -m omega_ml.experiment --dataset ../datasets/<datasetId> \
    --development 2025-01-01:2025-10-01 --holdout 2025-10-01:2026-01-01
```

Al final, **una sola vez**, con la estrategia/modelo congelados:

```bash
python -m omega_ml.experiment ... --evaluate-holdout
```

## 4. Cómo leer el resultado

* **Referencia:** predecir la tasa base de TP_FIRST del entrenamiento. `log_loss_skill` y `brier_skill` > 0
  significan que el modelo mejora sobre no saber nada; valores cercanos a 0 significan que no aporta.
* **AUC:** 0.5 es azar. En mercados reales, valores de 0.52–0.56 ya pueden ser relevantes **si** sobreviven fuera
  de muestra y a los costos (Fase 9); valores muy altos deben hacer sospechar leakage.
* **Contra la estrategia base:** `precision@baseline-coverage` compara la tasa de TP_FIRST de las señales del
  modelo con la de la regla base, con el mismo número de señales.
* **Folds:** un modelo que solo funciona en uno o dos folds no es estable.
* Las probabilidades **no están calibradas** todavía (Fase 8): no se interpretan como probabilidades reales.

## 5. Calibrar (Fase 8)

Decisiones en [ADR-011](decisions/ADR-011-probability-calibration.md).

```bash
python -m omega_ml.calibration --experiment ../experiments/<experimentId>
# al final, una sola vez (requiere que el experimento haya evaluado el holdout):
python -m omega_ml.calibration --experiment ../experiments/<experimentId> --evaluate-holdout
```

* Compara `none`, `platt` e `isotonic` con un walk-forward anidado sobre las predicciones fuera de muestra (cada
  fold se calibra con los anteriores, con purging) y elige el menor log loss; en empate, el más simple.
* Reporta, para crudo y calibrado: log loss, Brier, ECE, MCE, tabla de fiabilidad (10 bins con IC de Wilson 95 %)
  y la descomposición de Brier. Registro en `research/experiments/<id>/calibration-<fecha>.json`.
* Guarda el calibrador elegido como `calibration.json` junto al `model.onnx` de cada modelo registrado. C# lo carga
  con `ProbabilityCalibrator`.
* Si `none` gana, calibrar no ayuda fuera de muestra con esos datos: es un resultado válido, no un error.

## 6. Valor esperado y backtest del modelo (Fase 9)

Decisiones en [ADR-012](decisions/ADR-012-expected-value.md).

El experimento escribe `outcome_profile.json` (resultado medio de TP_FIRST y del resto, en múltiplos de ATR, del
periodo de desarrollo) junto a cada modelo registrado; la calibración añade `calibration.json`. Con los cuatro
archivos el modelo está listo (`GET /api/models` → `readyForExpectedValue`).

```bash
curl -X POST http://localhost:5080/api/backtests -H "Content-Type: application/json" -d '{
  "strategy": "model-ev", "modelId": "logistic_regression-<hash>",
  "symbol": "BTCUSDT", "interval": "5m",
  "fromUtc": "2025-10-01T00:00:00Z", "toUtc": "2026-01-01T00:00:00Z", "periodLabel": "holdout" }'
```

* La estrategia entra solo si `EV > minExpectedReturn` (0 por defecto) con la probabilidad **calibrada** y los
  costos del backtest; cada operación guarda P cruda, P calibrada y el desglose del EV.
* Compárala con `baseline-ema-trend` y `buy-and-hold` en **el mismo periodo y con los mismos costos**, y repite con
  costos mayores. Si el periodo se solapa con el de entrenamiento, la API lo advierte: ese resultado no es evidencia.
* `research/models` se configura con `Research:ModelsDirectory`.

## 7. Modelos registrados

`research/models/<tipo>-<hash>/` contiene `model.onnx`, `manifest.json`, `outcome_profile.json` y, tras la Fase 8,
`calibration.json`
(entrada `features` en `float64` con el
orden exacto de los 8 features, salida P(TP_FIRST), dataset, periodo, hiperparámetros, semilla y verificación
de paridad). Un modelo cuya exportación ONNX no reproduce exactamente al original no se registra.

## 8. Qué se versiona

* `research/experiments/<id>/experiment.json` y `calibration-*.json`: sí (registros, pequeños).
* `oos_predictions.csv`, datasets y modelos: no (reproducibles desde el código, el hash del dataset y la semilla).
