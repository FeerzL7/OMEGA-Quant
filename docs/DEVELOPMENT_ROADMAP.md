# OMEGA Quant — Roadmap de desarrollo

## Objetivo

El objetivo inicial no es crear un bot autónomo que gane dinero. Es construir un sistema capaz de responder:

> Bajo supuestos claramente definidos y condiciones históricas y fuera de muestra, ¿esta hipótesis de trading
> muestra un comportamiento estadísticamente significativo después de costos y restricciones de riesgo?

Solo después de una validación suficiente se consideran capacidades de ejecución.

## Estado de las fases

| Fase | Nombre                              | Estado        |
|------|-------------------------------------|---------------|
| 0    | Architecture and Repository         | **Completada** |
| 1    | Binance Market Data                 | **Completada** |
| 2    | Persistence                         | **Completada** |
| 3    | Market State and Candle Engine      | **Completada** |
| 4    | Feature Engine                      | **Completada** |
| 5    | Backtester                          | **Completada** |
| 6    | Baseline Strategy                   | **Completada** |
| 7    | Machine Learning                    | **Completada** |
| 8    | Probability Calibration             | **Completada** |
| 9    | Expected Value                      | **Completada** |
| 10   | Risk Engine                         | **Completada** |
| 11   | Monte Carlo                         | **Completada** |
| 12   | Paper Trading                       | Siguiente     |
| 13   | Monitoring Dashboard                | Pendiente     |
| 14   | Testnet                             | Pendiente     |
| 15   | Live Readiness                      | Pendiente     |
| 16   | Controlled Live                     | Pendiente     |

Cada fase termina cuando se cumple la definición de terminado (abajo) y se detiene ahí: no se avanza a la
siguiente sin instrucción del propietario.

### Notas de la Fase 0

* Resultado en detalle: ver el reporte de la fase y [`DECISION_LOG.md`](DECISION_LOG.md).
* P-001 (capa de aplicación) quedó resuelta en la Fase 1 (D-015).

### Notas de la Fase 1

* Diseño y decisiones: [ADR-003](decisions/ADR-003-binance.md).
* Limitaciones conocidas: sin persistencia ni relleno de huecos (Fases 2-3); sin detección de huecos previos al
  arranque; la conexión real se verifica manualmente, no en los tests.
* Para la Fase 2: la persistencia de velas es el punto natural para crear `Omega.Application` (D-015) y para
  reconciliar huecos contra el histórico. *(Hecho en la Fase 2.)*

### Notas de la Fase 2

* Diseño y decisiones: [ADR-002](decisions/ADR-002-postgresql.md).
* Los huecos se detectan también entre reinicios y quedan en `system_events`, pero **no se rellenan**.
  Propuesta para decidir al iniciar la Fase 3: incluir el relleno de huecos por REST (`GET /api/v3/klines`)
  como parte de "Candle validation" y "Data freshness", porque sin él el histórico tendrá agujeros.
* Tests de persistencia: requieren PostgreSQL (`OMEGA_TEST_POSTGRES`); sin él se reportan como omitidos.

### Notas de la Fase 3

* Diseño y decisiones: [ADR-008](decisions/ADR-008-market-state-and-backfill.md). Incluye el relleno de huecos
  por REST (decisión delegada, D-025).
* Agregación de velas no implementada: no la requiere ninguna fase todavía (D-028).
* `MarketState.IsReliable` es la señal de integridad que Strategy (Fases 6-9) y Risk (Fase 10) deberán respetar.
* Pendiente de revisar con datos reales: el periodo de gracia de frescura (60 s) y la latencia típica de las velas.

### Notas de la Fase 4

* Diseño: [ADR-009](decisions/ADR-009-feature-engine.md). Catálogo con las 6 fichas por feature: [FEATURES.md](FEATURES.md).
* `ComputeSeries` es la entrada natural del backtester (Fase 5): un vector por vela, sin look-ahead.
* Recomendación antes de la Fase 5: una **carga histórica** de velas (REST) para tener meses de datos sobre los
  que hacer backtests; hoy el relleno solo cubre huecos de los últimos 7 días. *(Hecho en la Fase 5.)*

### Notas de la Fase 5

* Reglas y métricas: [BACKTESTING.md](BACKTESTING.md). Decisiones: [ADR-005](decisions/ADR-005-backtesting.md).
* Sin estrategia real ni punto de entrada todavía: la Fase 6 (estrategia base) traerá la forma de ejecutar y
  guardar backtests, y la comparación contra buy & hold.
* Antes de interpretar resultados de la Fase 6 conviene importar varios meses de historia real
  (`MarketData:Backfill:HistoryStart`) y fijar el periodo de validación fuera de muestra **antes** de mirar
  resultados (§10, §22).

### Notas de la Fase 6

* Decisiones: [ADR-010](decisions/ADR-010-baseline-and-evaluation.md). Protocolo: [EVALUATION_PROTOCOL.md](EVALUATION_PROTOCOL.md).
* **Pendiente del propietario antes de interpretar nada:** importar historia real y fijar por escrito los
  periodos `development` y `holdout`.
* Para la Fase 7: el objetivo triple-barrier debe usar las mismas barreras que la base (2·ATR, 3·ATR, 48 velas)
  y los mismos periodos, y los modelos se evaluarán con `BacktestService` contra `baseline-ema-trend` y
  `buy-and-hold`. ADR-004 (ML con Python y ONNX) debe redactarse al inicio de esa fase. *(Hecho.)*

### Notas de la Fase 7

* Decisiones: [ADR-004](decisions/ADR-004-ml-python-onnx.md). Guía: [MACHINE_LEARNING.md](MACHINE_LEARNING.md).
* Para la Fase 8: la calibración debe ajustarse sobre `oos_predictions.csv` (predicciones fuera de muestra del
  walk-forward), nunca sobre predicciones del conjunto de entrenamiento.
* Pendiente: inferencia ONNX en C# (Fase 9); decidir qué hacer con LightGBM si resulta el mejor modelo
  (su exportación ONNX no es exacta).

### Notas de la Fase 8

* Decisiones: [ADR-011](decisions/ADR-011-probability-calibration.md). Uso: [MACHINE_LEARNING.md](MACHINE_LEARNING.md) §5.
* Para la Fase 9: cargar el paquete `model.onnx` + `calibration.json` en C# (inferencia ONNX), calcular el valor
  esperado con la probabilidad **calibrada** y los costos del backtester, y comparar en backtest contra la
  estrategia base y buy & hold con el protocolo de evaluación. *(Hecho.)*

### Notas de la Fase 9

* Decisiones: [ADR-012](decisions/ADR-012-expected-value.md). Uso: [MACHINE_LEARNING.md](MACHINE_LEARNING.md) §6.
* **Antes de interpretar resultados:** importar historia real, fijar periodos y ejecutar el ciclo completo
  (dataset → experimento → calibración → backtest del holdout una sola vez).
* Para la Fase 10: hoy el tamaño de posición lo fija el backtester (`RiskPerTrade`, `MaxPositionFraction`). El Risk
  Engine debe tomar esa responsabilidad (límites, exposición, pérdida diaria, drawdown, kill switch) y poder rechazar
  señales `model-ev` aunque su EV sea positivo. *(Hecho.)*

### Notas de la Fase 10

* Decisiones: [ADR-013](decisions/ADR-013-risk-engine.md). Modelo: [RISK_MODEL.md](RISK_MODEL.md).
* **Pendiente del propietario:** revisar los límites por defecto de la sección `Risk` (son conservadores, no
  optimizados) y fijar los propios antes de interpretar backtests.
* Para la Fase 12: conectar el mismo `RiskManager` al pipeline en vivo, con estado persistido (equity pico, inicio
  de día, racha, kill switch), spread y slippage observados, integridad del estado de mercado, y comandos de la API
  para activar y rearmar el kill switch (con auditoría).

### Notas de la Fase 11

* Decisiones: [ADR-014](decisions/ADR-014-monte-carlo.md). Guía: [MONTE_CARLO.md](MONTE_CARLO.md).
* Usar Monte Carlo como parte de la lectura de cada backtest relevante (¿fue afortunada la secuencia?, ¿resiste
  costos?, ¿cuánto drawdown planear?), nunca como pronóstico.
* La Fase 12 es la primera que ejecuta el pipeline completo en tiempo real: datos → features → modelo → calibración →
  EV → riesgo → decisión → ejecución simulada (paper) → diario. Implica decisiones de arquitectura (estado persistido
  del riesgo y de las posiciones, `IExecutionProvider`, ciclo de vida de órdenes, ADR-007 si el panel necesita tiempo
  real) que se plantearán al inicio de la fase.

---

## Definición de fases (texto original del propietario)

### Phase 0 — Architecture and Repository

Create: Solution. Projects. References. Tests. Git configuration. Documentation. Configuration foundation.

Do NOT implement trading. Do NOT implement Binance. Do NOT implement ML. Do NOT implement PostgreSQL.
Do NOT implement dashboard functionality.

The UI project may be created as an architectural placeholder if required.

### Phase 1 — Binance Market Data

Implement: Binance Spot market-data abstraction. WebSocket client. Market-data contracts. Connection lifecycle.
Basic event normalization. BTCUSDT 5m closed-candle stream. Reconnection strategy. Cancellation. Logging. Tests.

Do not implement: Trading orders. ML. Strategy. Risk engine. Live execution.

### Phase 2 — Persistence

Introduce PostgreSQL.

Persist: Candles. Market events where justified. System events.

Implement: Database configuration. Migrations. Repositories. Data integrity.

### Phase 3 — Market State and Candle Engine

Implement: Candle validation. Candle aggregation where required. Market state. Closed-candle detection.
Data freshness.

### Phase 4 — Feature Engine

Initial features may include: Returns. SMA/EMA. RSI. ATR. MACD. Volume. Volume z-score. Volatility.
Price distance from EMA. Trend strength.

Every feature must have:

```text
Name
Purpose
Formula
Lookback
Required data
Leakage risk
```

### Phase 5 — Backtester

Implement: Historical candle replay. Strategy interface. Simulated execution. Fees. Spread. Slippage. SL/TP.
Position sizing. Equity curve. Drawdown.

No ML required yet.

### Phase 6 — Baseline Strategy

Create a simple non-ML baseline. The purpose is to establish a benchmark.

```text
Baseline
vs
ML Model
```

If ML cannot outperform or provide useful information relative to the baseline under proper out-of-sample
testing, increasing model complexity is not justified.

### Phase 7 — Machine Learning

Research environment: Python.

Implement: Dataset generation. Triple-barrier labels. Training. Validation. Out-of-sample testing.
Model versioning.

Initial model: Logistic Regression. Then compare against: Random Forest, LightGBM.

Only after evidence should more complex models be considered.

### Phase 8 — Probability Calibration

Implement: Raw probabilities. Calibration. Brier score. Log loss. Reliability analysis.

Compare: Raw probability vs Calibrated probability.

### Phase 9 — Expected Value

```text
EV =
P(win) × Gain
-
P(loss) × Loss
-
Costs
```

The strategy must be evaluated using EV rather than simply `confidence > 85%`.

### Phase 10 — Risk Engine

Implement: Risk per trade. Position sizing. Maximum exposure. Daily loss. Drawdown. Open positions. Spread.
Slippage. Kill switch.

### Phase 11 — Monte Carlo

Implement scenario analysis for: Equity paths. Drawdown. Risk of ruin. Terminal capital. Robustness.

Monte Carlo must not replace the predictive model.

### Phase 12 — Paper Trading

Run the complete pipeline:

```text
Market Data → Features → Model → Calibration → Regime → EV → Risk → Decision → Paper Execution → Journal
```

The dashboard should now become significantly more useful.

### Phase 13 — Monitoring Dashboard

Implement the first complete visual dashboard. Include: Live price. Candles. Current signal. Probability. EV.
Market regime. Risk. Positions. Orders. System health. Logs.

### Phase 14 — Testnet

Only after paper trading demonstrates technical stability. Implement controlled testnet execution.

### Phase 15 — Live Readiness

Before live execution, verify: Risk controls. Data integrity. Order reconciliation. Exchange filters.
Secret management. Monitoring. Kill switch. Error handling. Recovery. Testnet behavior.

### Phase 16 — Controlled Live

Live execution should initially be: Small. Explicitly enabled. Closely monitored. Risk-limited.

No automatic increase of capital based solely on short-term performance.

---

## Definition of Done

A phase is complete only when:

```text
Code implemented
+
Tests implemented
+
Build succeeds
+
Tests pass
+
Documentation updated
+
Known limitations documented
```

## Important Rule

The existence of the UI does not change the trading architecture. The dashboard is a visualization and
controlled interaction layer.

```text
Never:   UI → Binance
Always:  UI → API → Application → Risk / Decision → Execution → Exchange
```
