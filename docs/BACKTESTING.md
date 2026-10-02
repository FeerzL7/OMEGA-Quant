# Backtesting

Reglas del simulador (`Omega.Backtesting.BacktestEngine`) y definiciones de las métricas. Decisiones y
alternativas en [ADR-005](decisions/ADR-005-backtesting.md). Un backtest **no** demuestra rentabilidad: mide el
comportamiento de una hipótesis bajo supuestos explícitos (CLAUDE.md §1, §20).

## Orden de eventos por vela

Para cada vela *i*:

1. **Apertura de *i*:** se ejecuta lo decidido al cierre de *i−1*: primero una salida por señal y después una
   entrada. Las órdenes a mercado se llenan a la apertura con medio spread más el slippage en contra.
2. **Durante *i*:** stop loss / take profit de la posición abierta, a partir de open, high y low:
   * si la apertura ya está en o por debajo del stop (gap), la salida es a la apertura;
   * si la apertura ya está en o por encima del take profit, la salida es al take profit (sin acreditar el gap);
   * si el rango toca **ambos** niveles, se asume que **el stop ocurrió primero** (conservador);
   * el stop es una orden stop a mercado: se llena al precio del stop con medio spread más slippage en contra;
   * el take profit es una orden límite: se llena exactamente a su precio.
3. **Cierre de *i*:** salida por límite de tiempo (`MaxHoldingCandles`) o por fin de datos, al cierre, a mercado.
   El equity se marca al cierre: `efectivo + cantidad × close`.
4. **Cierre de *i*:** la estrategia ve la vela *i* y sus features (disponibles solo ahora) y decide para *i+1*.
   En la última vela no se evalúa nada, porque no habría vela siguiente.

La estrategia solo recibe la vela que acaba de cerrar, su vector de features y la posición abierta: no puede
ver velas posteriores. Los tests verifican que alterar el futuro no cambia decisiones ni operaciones pasadas.

## Señales en Spot

| Señal | Sin posición | Con posición larga |
|-------|--------------|--------------------|
| `LONG` (con stop y take profit opcional) | Entra a la apertura siguiente | Mantiene |
| `SHORT` | Rechazada (`SHORT_NOT_SUPPORTED_ON_SPOT`) | Sale a la apertura siguiente |
| `HOLD` | Nada | Mantiene |
| `NO_TRADE` (con razón) | Nada; se cuenta por razón | Mantiene |

Mientras los features se calientan, la decisión es `NO_TRADE(FEATURES_UNAVAILABLE)` sin consultar a la estrategia.

## Rechazos

| Código | Cuándo |
|--------|--------|
| `SHORT_NOT_SUPPORTED_ON_SPOT` | SHORT sin posición abierta. |
| `STOP_NOT_BELOW_ENTRY` | La apertura de entrada quedó en o por debajo del stop (gap). |
| `TAKE_PROFIT_NOT_ABOVE_ENTRY` | El precio de entrada ya superó el take profit. |
| `POSITION_TOO_SMALL` | Cantidad 0 tras redondear, o valor menor que `MinNotional`. |
| `SIGNAL_EXPIRED_BY_DATA_GAP` | La vela siguiente a la señal no es contigua (hueco): la señal está vencida. |

## Costos (configurables, `BacktestConfig`)

| Parámetro | Por defecto | Nota |
|-----------|-------------|------|
| `FeeRate` | 0.001 (0.10 % por lado) | Binance Spot, nivel regular (VIP 0), 2026. Sin descuento BNB (conservador). Verifica tu nivel. |
| `SpreadBps` | 1 pb | Spread completo; las órdenes a mercado pagan la mitad. |
| `SlippageBps` | 2 pb | Entradas, stops y salidas a mercado. No se aplica al take profit (límite). |

Estos valores son supuestos de investigación, no verdades. Los resultados deben leerse junto con ellos, y una
estrategia debe sobrevivir a costos más altos que los supuestos.

## Tamaño de posición

`FixedFractionalSizer` (CLAUDE.md §18): `cantidad = equity × RiskPerTrade / (entrada − stop)`, limitada a
`equity × MaxPositionFraction / (entrada × (1 + FeeRate))` (sin apalancamiento) y redondeada hacia abajo a
`QuantityStep`. Por defecto `RiskPerTrade = 1 %` y `MaxPositionFraction = 100 %`. El riesgo por operación es
antes de costos. Lo reemplazará el Risk Engine en la Fase 10.

## Métricas

Sobre la curva de equity (una observación por cierre de vela) y las operaciones cerradas:

| Métrica | Definición |
|---------|-----------|
| Total return | `equity final / capital inicial − 1` |
| Net profit | `equity final − capital inicial` |
| Win rate | operaciones con PnL neto > 0 / operaciones (`null` sin operaciones) |
| Profit factor | suma de PnL positivos / \|suma de PnL negativos\| (`null` sin pérdidas) |
| Expectancy | PnL neto medio por operación, en USDT |
| Max drawdown | mínimo de `equity / máximo previo − 1` sobre los cierres (no intravela) |
| Sharpe | `media(r) / desvEst(r) × √(velas por año)`, con `r` = retornos simples del equity por vela; tasa libre de riesgo 0; 5 m → 105 120 velas/año |
| Sortino | `media(r) / √(media(min(r,0)²)) × √(velas por año)` |
| Exposure | velas con posición abierta tras la apertura / velas totales |
| Total fees | comisiones pagadas en entradas y salidas |

El PnL de cada operación es neto: `cantidad × salida − comisión de salida − (cantidad × entrada + comisión de entrada)`,
con precios de ejecución que ya incluyen spread y slippage.

## Riesgo (Fase 10)

Cada entrada que pide la estrategia pasa por el Risk Engine ([RISK_MODEL.md](RISK_MODEL.md)): aprobación al cerrar
la vela de decisión (kill switch, pérdida diaria, racha, posiciones, exposición, spread, slippage, datos) y tamaño al
ejecutar. El backtester ya no dimensiona por su cuenta: `RiskPerTrade` y `MaxPositionFraction` son límites de
riesgo, no parámetros de simulación. Los rechazos aparecen en `rejections` (códigos `RISK_...`) y en
`riskSummary`, junto con la activación del kill switch si ocurrió; `risk` registra la política usada.

## Periodo de trading y calentamiento

El servicio de backtests carga, antes del periodo pedido, solo las velas que los features necesitan para
calentarse (lookback máximo − 1 = 249 velas en `features-v1`). Esas velas no operan ni cuentan en las métricas;
la decisión al cierre de la última vela de calentamiento se ejecuta en la apertura de la primera vela del periodo.

## Estadísticas por operación

`TradeStatistics` responde si el retorno neto medio por operación (sobre el capital comprometido) se distingue de
cero: media, desviación estándar muestral, estadístico t (`media / (desv / √n)`) e intervalo de confianza de
95 % por bootstrap (10 000 remuestreos, semilla fija, reproducible). Suponen operaciones independientes, lo que
rara vez es cierto: son un primer filtro, no una prueba. Ver [EVALUATION_PROTOCOL.md](EVALUATION_PROTOCOL.md).

## Reproducibilidad

Cada `BacktestResult` incluye estrategia (nombre, versión, parámetros), versión y hash del conjunto de
features, configuración completa y huella del dataset (símbolo, intervalo, rango, número de velas y SHA-256
de las velas con números normalizados). Dos ejecuciones sobre el mismo dataset producen resultados idénticos.

Cada ejecución hecha por `POST /api/backtests` se guarda en `backtest_runs` con el resultado completo (la curva
de equity se guarda con un punto diario; las métricas se calculan antes, con la curva completa).

## Limitaciones conocidas

* Una sola posición a la vez, solo largos (Spot).
* Sin libro de órdenes: el spread y el slippage son constantes, no dependen del tamaño ni de la volatilidad.
* Comisión en moneda cotizada (USDT); Binance puede cobrarla en el activo recibido o en BNB.
* El drawdown se mide en cierres; el intravela puede ser mayor.
* Un hueco de datos con una posición abierta: la posición sigue y se evalúa en la siguiente vela disponible
  (con gap a la apertura si corresponde); se emite una advertencia.
