# ADR-005: Backtesting

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-09-30
* Fase: 5

## Contexto

La Fase 5 pide reproducir velas históricas con una interfaz de estrategia, ejecución simulada, comisiones,
spread, slippage, SL/TP, tamaño de posición, curva de equity y drawdown, sin ML. La constitución exige un
backtest realista, sin look-ahead y sin precios futuros para simular fills (§20), y registrar cada experimento (§22).

## Decisiones

1. **Simulador por eventos sobre velas cerradas** con un orden fijo por vela (ver
   [BACKTESTING.md](../BACKTESTING.md)): lo decidido al cierre de *i* se ejecuta a la **apertura de *i+1***.
2. **Supuestos conservadores de ejecución:** SL antes que TP si ambos caen en la misma vela; gap a través del
   stop → apertura; sin crédito por gaps a favor del take profit; costos de mercado en entradas, stops y
   salidas.
3. **Spot solo largo.** `SHORT` cierra un largo o se rechaza; nunca abre cortos.
4. **Interfaz de estrategia en `Omega.Strategy`** (`IStrategy.Evaluate(StrategyContext) → Signal`). El
   contexto solo contiene la vela que cerró, sus features y la posición: no hay forma de mirar al futuro.
5. **Modelo de señal en `Omega.Core`** (`Signal`): `LONG` requiere stop loss (el tamaño lo necesita), `NO_TRADE`
   requiere una `NoTradeReason` (§8).
6. **Tamaño de posición** por fracción fija con tope de capital (§18), detrás de `IPositionSizer`; lo
   reemplazará el Risk Engine (Fase 10).
7. **Costos por defecto:** comisión 0.10 % por lado (Binance Spot VIP 0), spread 1 pb, slippage 2 pb; todos
   configurables y registrados en el resultado.
8. **Reproducibilidad:** el resultado incluye estrategia, configuración, hash de features y huella SHA-256 del
   dataset. El motor es determinista.
9. **Carga histórica opcional** (`MarketData:Backfill:HistoryStart`): el Worker importa por REST las velas
   cerradas desde esa fecha hasta la más antigua guardada, en bloques de 30 días, de forma idempotente.

## Alternativas consideradas

**Usar `IExecutionProvider` y el ciclo de vida de órdenes ya en el backtest (§15-16).** Sería lo ideal para que
backtest, paper y live compartan código, pero el ciclo de vida de órdenes (CREATED → FILLED, parciales,
reconciliación) pertenece a la ejecución real y es alcance de las Fases 12-14. Implementarlo ahora sería diseñar
contra supuestos. El backtester concentra el modelo de fills en un solo lugar (`BacktestEngine`) para que en la
Fase 12 se extraiga detrás de `IExecutionProvider` (`BacktestExecutionProvider`) sin cambiar las estrategias,
que ya no saben en qué modo corren.

**Frameworks de backtesting externos (Python: backtrader, vectorbt; .NET: Lean).** Maduros, pero duplicarían
la lógica de features y ejecución en otro stack o impondrían su propio modelo de datos. Un backtester propio
comparte exactamente los mismos features que el sistema en vivo.

**Backtest vectorizado.** Mucho más rápido, pero hace muy fácil el look-ahead accidental y modela mal SL/TP
intravela. El simulador por eventos tarda ~0.7 s por mes de velas de 5 m, suficiente.

**Llenar al cierre de la vela de la señal.** Común en backtests simples; es look-ahead, porque el cierre ya
ocurrió cuando se decide. Descartado.

## Consecuencias

* Los resultados son **pesimistas por diseño** en los casos ambiguos. Una estrategia que solo es rentable bajo
  supuestos optimistas no pasará.
* El modelo de costos es simple (constantes). Antes de paper trading conviene medir spread y slippage reales.
* Sin punto de entrada de usuario todavía: la primera estrategia real (baseline, Fase 6) traerá la forma de
  ejecutar y guardar backtests.
* La verificación se apoya en escenarios calculados a mano y en pruebas de mutación (incluido look-ahead
  deliberado), que encontraron un test débil que ya fue reforzado.
