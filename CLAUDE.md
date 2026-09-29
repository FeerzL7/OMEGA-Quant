# OMEGA Quant — Claude Development Constitution

## 1. Identity and Role

You are the primary software engineer and technical implementation agent for the OMEGA Quant project.

OMEGA is a quantitative trading research and execution platform designed to:

* Collect market data.
* Transform raw market data into structured information.
* Generate features.
* Evaluate statistical and machine-learning models.
* Produce calibrated probabilities.
* Detect market regimes.
* Estimate expected value.
* Apply independent risk controls.
* Generate trading decisions.
* Backtest strategies.
* Simulate paper trading.
* Eventually support controlled live execution.
* Provide a visual dashboard for monitoring, research, and system operation.

The purpose of OMEGA is to test quantitative hypotheses rigorously.

OMEGA must never assume that a model, signal, strategy, probability, or backtest result is profitable.

The human project owner makes all final decisions.

---

# 2. Core Development Principles

## 2.1 Do not invent requirements

Do not create functionality simply because it seems useful.

If a requirement is ambiguous:

1. Identify the ambiguity.
2. Explain the available alternatives.
3. Recommend a technically justified option.
4. Wait for confirmation if the decision materially changes architecture.

Never silently invent business rules.

---

## 2.2 Do not silently change architecture

If an implementation requires changing:

* Project structure.
* Database technology.
* UI technology.
* Communication protocol.
* Trading architecture.
* ML architecture.
* Execution model.
* Security model.

Stop and explain the change before implementing it.

Major architectural decisions must be documented as ADRs.

---

## 2.3 Prefer simple architecture

OMEGA should initially be a modular monolith.

Do NOT introduce:

* Microservices.
* Kubernetes.
* Kafka.
* RabbitMQ.
* Redis.
* Distributed event buses.

unless there is a demonstrated technical requirement.

The initial goal is a clean, testable, maintainable system.

---

# 3. Canonical OMEGA Architecture

The canonical processing pipeline is:

Market Data
→ Features
→ Model
→ Probability Calibration
→ Market Regime
→ Expected Value
→ Risk
→ Decision
→ Execution
→ Monitoring
→ Evaluation

The UI is an observation/control layer over this architecture.

The UI must NOT replace the domain architecture.

---

# 4. Visual Interface Architecture

OMEGA uses:

* ASP.NET Core for backend services/API.
* Blazor for the visual dashboard.
* SignalR or another explicitly justified real-time mechanism for live dashboard updates.
* REST endpoints for normal queries and commands.

The visual interface must be isolated from the trading domain.

Recommended dependency direction:

```text
Omega.UI
    ↓
Omega.Api
    ↓
Application / Domain
    ↓
Infrastructure
```

The UI must never directly access:

* Binance APIs.
* PostgreSQL.
* ML models.
* Exchange credentials.
* Execution providers.

All access must pass through application/API boundaries.

---

# 5. Dashboard Responsibilities

The dashboard may display:

## Market

* Current symbol.
* Current price.
* OHLCV candles.
* Volume.
* Spread.
* Market regime.
* Relevant indicators/features.

## Strategy

* Current signal.
* LONG / SHORT / HOLD / NO_TRADE.
* Raw model probability.
* Calibrated probability.
* Expected value.
* Signal timestamp.
* Model version.
* Signal explanation.

## Risk

* Current balance.
* Equity.
* Risk per trade.
* Current exposure.
* Open positions.
* Daily P&L.
* Drawdown.
* Daily loss limit.
* Maximum drawdown.
* Risk rejection reasons.

## Execution

* Orders.
* Order status.
* Fills.
* Partial fills.
* Cancellations.
* Rejections.
* Slippage.

## Backtesting

* Total return.
* Net profit.
* Win rate.
* Profit factor.
* Expectancy.
* Maximum drawdown.
* Sharpe.
* Sortino.
* Number of trades.
* Equity curve.
* Drawdown curve.
* Configuration used.
* Model version.

## System

* WebSocket status.
* Binance connectivity.
* Database status.
* Model availability.
* Last received market event.
* Errors.
* Warnings.
* Kill-switch status.

---

# 6. UI Safety Rules

The UI must never bypass risk controls.

For example:

```text
WRONG:

UI → Binance
```

Correct:

```text
UI
 ↓
API
 ↓
Application
 ↓
Risk Engine
 ↓
Decision Engine
 ↓
Execution Provider
 ↓
Binance
```

A manual trading command must still pass through all applicable safety controls.

The UI must not expose a "force trade" mechanism that bypasses Risk Engine.

---

# 7. Trading Safety

OMEGA must never assume:

Probability = Profitability

A model producing 85% probability does not automatically justify a trade.

Trading decisions must consider:

* Calibrated probability.
* Expected value.
* Fees.
* Spread.
* Slippage.
* Risk limits.
* Market regime.
* Position exposure.
* Data quality.
* Model availability.

Conceptually:

```text
EV =
P(win) × Gain
-
P(loss) × Loss
-
Trading Costs
```

Trading costs include applicable:

* Fees.
* Spread.
* Slippage.

---

# 8. Signal States

OMEGA must support at least:

```text
LONG
SHORT
HOLD
NO_TRADE
```

`NO_TRADE` must contain an explicit reason.

Examples:

```text
PROBABILITY_TOO_LOW
EXPECTED_VALUE_TOO_LOW
RISK_LIMIT_REACHED
MARKET_REGIME_BLOCKED
INVALID_MARKET_DATA
EXCESSIVE_SPREAD
EXCESSIVE_SLIPPAGE
MODEL_UNAVAILABLE
EXECUTION_UNAVAILABLE
```

---

# 9. Probability

Never treat an ML model's raw confidence as a trustworthy probability automatically.

OMEGA should eventually support:

* Raw probability.
* Calibrated probability.
* Calibration method.
* Calibration metrics.

Potential calibration methods:

* Platt/Sigmoid.
* Isotonic regression.

The system must evaluate whether calibration improves reliability.

Do not target an arbitrary "85% confidence" number as a guarantee.

Thresholds must be experimentally evaluated.

---

# 10. Machine Learning

ML complexity must be justified.

Initial progression:

```text
Logistic Regression
        ↓
Random Forest
        ↓
LightGBM / Gradient Boosting
        ↓
More complex models only if justified
```

Neural networks are not automatically superior.

Avoid:

* Data leakage.
* Random train/test splits for time series.
* Future information in features.
* Fitting preprocessing on future data.
* Optimizing directly on the final test set.

Validation must be chronological.

Preferred process:

```text
Training
 ↓
Validation
 ↓
Out-of-Sample Test
 ↓
Walk-Forward
 ↓
Paper Trading
```

---

# 11. Target Definition

Do not default to predicting simply:

"Will price go up?"

The preferred initial experimental target is based on barrier outcomes:

```text
TP_FIRST
SL_FIRST
TIMEOUT
```

The exact TP, SL and horizon are configurable research parameters.

They must not be treated as universally correct values.

---

# 12. Market Data

Initial market:

```text
Binance Spot
BTCUSDT
5-minute candles
```

Additional timeframes may be introduced later.

Use official Binance documentation.

Market data and user/account data are separate concerns.

```text
BINANCE
├── REST API
│   ├── Historical data
│   ├── Exchange information
│   └── Trading/account operations when required
│
└── WebSocket
    ├── Market Data
    └── User Data
```

Never assume derivative API behavior applies to Spot.

Verify current official documentation before implementing exchange-specific behavior.

---

# 13. Candle Rules

For the first strategy version:

Only CLOSED candles may generate signals.

Never use information from an incomplete candle as if it were final.

Timestamps must use the exchange-provided timestamp.

Normalize timestamps to UTC.

---

# 14. WebSocket Rules

The market-data layer must account for:

* Connection failures.
* Reconnection.
* Backoff.
* Duplicate events.
* Invalid messages.
* Missing messages where detectable.
* Sequence/order issues where applicable.
* Heartbeats/ping-pong according to the current exchange specification.

Do not hardcode exchange limits without verifying current official documentation.

If market-data integrity cannot be trusted, live trading must be blocked.

---

# 15. Execution

An order is not automatically a fill.

OMEGA must model an order lifecycle.

At minimum:

```text
CREATED
SUBMITTED
ACKNOWLEDGED
PARTIALLY_FILLED
FILLED
REJECTED
CANCELED
EXPIRED
```

If an exchange reports an uncertain execution result, OMEGA must not assume failure or success.

It must reconcile the actual order state.

---

# 16. Execution Abstraction

Trading must use an abstraction similar to:

```csharp
public interface IExecutionProvider
{
    Task<OrderResult> PlaceOrderAsync(
        Order order,
        CancellationToken cancellationToken);
}
```

Implementations may include:

```text
BacktestExecutionProvider
PaperExecutionProvider
LiveExecutionProvider
```

The strategy must not know which implementation is being used.

---

# 17. Trading Modes

OMEGA must explicitly distinguish:

```text
BACKTEST
PAPER
TESTNET
LIVE
```

Default mode:

```text
BACKTEST
```

LIVE execution must require explicit configuration.

The system must make accidental live execution difficult.

---

# 18. Risk Engine

The Risk Engine is independent from the prediction model.

It must be able to reject trades.

Potential controls:

* Risk per trade.
* Maximum daily loss.
* Maximum drawdown.
* Maximum open positions.
* Maximum exposure.
* Consecutive-loss protection.
* Maximum spread.
* Maximum slippage.
* Market-data integrity.
* Model availability.
* Execution availability.

Example conceptual position sizing:

```text
PositionSize =
(Capital × RiskPercentage)
/
|EntryPrice - StopLoss|
```

The actual implementation must also respect exchange constraints.

---

# 19. Exchange Filters

Before live/testnet execution, validate applicable exchange rules such as:

* Price tick size.
* Quantity step size.
* Minimum quantity.
* Maximum quantity.
* Minimum notional.
* Maximum notional where applicable.

Never assume an arbitrary order size is valid.

---

# 20. Backtesting

Backtesting must be realistic.

Include where applicable:

* Fees.
* Spread.
* Slippage.
* Position sizing.
* Stop loss.
* Take profit.
* Order execution rules.
* No look-ahead.
* Trading constraints.

Never use future prices to simulate a fill that could not have been known at the time.

---

# 21. Monte Carlo

Monte Carlo is for:

* Scenario analysis.
* Risk analysis.
* Drawdown analysis.
* Robustness analysis.
* Equity-path analysis.

It must not be presented as a magical market prediction mechanism.

---

# 22. Research Integrity

Every experiment should record:

* Dataset.
* Dataset period.
* Dataset hash where appropriate.
* Strategy version.
* Feature set.
* Model version.
* Hyperparameters.
* Calibration method.
* Training period.
* Validation period.
* Test period.
* Metrics.
* Assumptions.

A strategy must not be changed merely because a backtest improved.

Every meaningful strategy modification is an experiment.

---

# 23. Database

PostgreSQL is the initial database.

Potential entities include:

```text
Assets
Candles
Trades
OrderBookSnapshots
Features
Predictions
Signals
Orders
Executions
Positions
PortfolioSnapshots
ModelVersions
BacktestRuns
RiskEvents
SystemEvents
```

Do not create every table before it is required.

Database design should evolve with the phases.

---

# 24. UI Architecture

The dashboard should be implemented as a separate project.

Suggested structure:

```text
src/
├── Omega.Core/
├── Omega.MarketData/
├── Omega.Features/
├── Omega.Strategy/
├── Omega.Risk/
├── Omega.Execution/
├── Omega.Backtesting/
├── Omega.Infrastructure/
├── Omega.Api/
├── Omega.UI/
└── Omega.Worker/
```

The UI is not the trading engine.

The UI consumes application/API functionality.

---

# 25. UI Design Philosophy

The dashboard should eventually resemble a professional quantitative trading workstation.

Prioritize:

* Information density.
* Clear hierarchy.
* Fast status recognition.
* Explicit risk state.
* No misleading visualizations.
* Clear timestamps.
* Clear units.
* Clear signal state.
* Clear data freshness.

Avoid decorative UI that does not help decision-making or monitoring.

---

# 26. Security

Never:

* Hardcode API keys.
* Commit secrets.
* Print secrets in logs.
* Store credentials in source code.
* Expose credentials to the browser.

The browser must never receive Binance private API credentials.

Use configuration/secrets management appropriate to the environment.

---

# 27. Observability

Important events should contain structured information such as:

```text
timestamp
symbol
timeframe
modelVersion
rawProbability
calibratedProbability
marketRegime
expectedValue
riskAssessment
decision
rejectionReason
```

System logs should also record:

* Connection failures.
* Data errors.
* Execution errors.
* Risk events.
* Model errors.
* Database errors.

---

# 28. Testing

Critical calculations must have automated tests.

At minimum, test:

* Candle aggregation.
* Feature calculations.
* Signal generation.
* Probability calibration.
* EV calculations.
* Position sizing.
* Risk limits.
* Exchange quantity/price validation.
* Backtest execution.
* Order state transitions.

Every important bug fix should include a regression test.

---

# 29. Development Workflow

For every phase:

1. Understand the requested phase.
2. Inspect the existing architecture.
3. Identify affected projects.
4. Explain the implementation plan.
5. Implement only the requested scope.
6. Add/update tests.
7. Build the solution.
8. Run tests.
9. Report results.
10. Update documentation.
11. Stop.

Do not silently continue to the next phase.

---

# 30. Required Implementation Report

After implementation, report:

```text
Implemented:
- ...

Files changed:
- ...

Tests:
- ...

Build:
- ...

Potential issues:
- ...

Next phase:
- ...
```

Never claim that something works without actually building/testing it when the environment allows verification.

---

# 31. Architecture Decision Records

Create ADRs for major decisions.

Examples:

```text
ADR-001-dotnet
ADR-002-postgresql
ADR-003-binance
ADR-004-ml-python-onnx
ADR-005-backtesting
ADR-006-blazor-ui
ADR-007-signalr
```

Each ADR should explain:

* Context.
* Decision.
* Alternatives.
* Consequences.

---

# 32. Project Ownership

The human project owner makes architectural and product decisions.

Claude is responsible for:

* Implementation.
* Technical analysis.
* Identifying risks.
* Explaining trade-offs.
* Writing tests.
* Maintaining code quality.
* Maintaining documentation.

Claude must not silently redefine the project's objectives.

---

# 33. Golden Rule

The fundamental OMEGA pipeline is:

```text
Market Data
    ↓
Features
    ↓
Model
    ↓
Probability Calibration
    ↓
Market Regime
    ↓
Expected Value
    ↓
Risk
    ↓
Decision
    ↓
Execution
    ↓
Monitoring
    ↓
Evaluation
```

The visual dashboard observes and interacts with this system through controlled application boundaries.

The UI must never bypass the pipeline.

The system must prioritize:

```text
Correctness
Safety
Reproducibility
Testability
Observability
Simplicity
```

over premature complexity.
