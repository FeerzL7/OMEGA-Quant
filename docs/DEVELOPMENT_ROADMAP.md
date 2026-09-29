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
| 3    | Market State and Candle Engine      | Siguiente     |
| 4    | Feature Engine                      | Pendiente     |
| 5    | Backtester                          | Pendiente     |
| 6    | Baseline Strategy                   | Pendiente     |
| 7    | Machine Learning                    | Pendiente     |
| 8    | Probability Calibration             | Pendiente     |
| 9    | Expected Value                      | Pendiente     |
| 10   | Risk Engine                         | Pendiente     |
| 11   | Monte Carlo                         | Pendiente     |
| 12   | Paper Trading                       | Pendiente     |
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
