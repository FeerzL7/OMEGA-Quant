# Modelo de riesgo (Fase 10)

Decisiones en [ADR-013](decisions/ADR-013-risk-engine.md). Código: `src/Omega.Risk` (`RiskManager`, `RiskLimits`,
`KillSwitch`).

## Principios

* **Independiente del modelo** (CLAUDE.md §18): `Omega.Risk` solo referencia `Omega.Core` (un test de arquitectura
  lo impide de otro modo). Juzga una señal y el estado de la cuenta; nunca ve probabilidades, features ni modelos.
* **Puede rechazar cualquier entrada**, aunque su valor esperado sea positivo.
* **Decide el tamaño de toda posición.** La estrategia propone dirección, stop y objetivo; el riesgo decide cuánto.
* **Nunca bloquea una salida.** Reducir riesgo siempre está permitido.
* Cada rechazo nombra su control, con un código estable (`RISK_...`) y un detalle legible.

## Política (`Risk` en la configuración)

| Límite | Defecto | Efecto |
|--------|---------|--------|
| `RiskPerTrade` | 0.01 | Fracción del equity que se pierde si salta el stop (antes de costos) |
| `MaxPositionFraction` | 1.0 | Valor máximo de una posición / equity (Spot: sin apalancamiento) |
| `MaxOpenPositions` | 1 | Posiciones simultáneas |
| `MaxExposureFraction` | 1.0 | Valor total de posiciones (incluida la nueva) / equity |
| `MaxDailyLoss` | 0.03 | Pérdida desde el equity de inicio del día UTC que bloquea entradas hasta mañana |
| `MaxDrawdown` | 0.20 | Caída desde el máximo de equity que **activa el kill switch** |
| `MaxConsecutiveLosses` | 6 | Racha de pérdidas que bloquea entradas hasta mañana (`null` lo desactiva) |
| `MaxSpreadBps` | 10 | Spread máximo aceptable para entrar |
| `MaxSlippageBps` | 25 | Slippage esperado máximo aceptable para entrar |

Los valores por defecto son puntos de partida convencionales y conservadores, **no optimizados**: son decisión del
propietario. Una política inválida impide que la API arranque. `GET /api/risk/limits` muestra la vigente; cada
backtest puede cambiar límites solo para esa ejecución (`"risk": {...}` en `POST /api/backtests`), y el resultado
registra la política usada.

## Controles de entrada (en este orden; se informa el primero que falla)

1. **Kill switch** activo. Es la causa más grave y la única persistente, por eso va primero.
2. **Pérdida diaria**: el equity (marcado a mercado, incluidas posiciones abiertas) tocó
   `inicio del día × (1 − MaxDailyLoss)`. El detalle indica cuándo y en qué valor, porque el equity puede haberse
   recuperado después.
3. **Racha de pérdidas** (operaciones cerradas con resultado neto negativo; una ganadora la reinicia).
4. **Posiciones abiertas** y 5. **exposición**.
6. **Spread** y 7. **slippage** esperados.
8. **Datos de mercado fiables** (en backtest: la vela de decisión es contigua a la anterior; en vivo: estado de
   mercado y frescura, Fase 12).
9. **Ejecución disponible**.

Correspondencia con los motivos de `NO_TRADE` (§8): spread → `EXCESSIVE_SPREAD`, slippage → `EXCESSIVE_SLIPPAGE`,
datos → `INVALID_MARKET_DATA`, ejecución → `EXECUTION_UNAVAILABLE`; el resto → `RISK_LIMIT_REACHED`.

## Tamaño de la posición

```text
cantidad = mín( equity × RiskPerTrade / (entrada − stop),
                equity × MaxPositionFraction / (entrada × (1 + comisión)),
                (equity × MaxExposureFraction − exposición abierta) / (entrada × (1 + comisión)),
                caja / (entrada × (1 + comisión)) )
```

Luego se redondea hacia abajo al paso de cantidad y se rechaza si queda en cero o por debajo de la cantidad o el
valor mínimos del instrumento (`InstrumentFilters`, que deben venir de `exchangeInfo`; nada se supone). Un stop que
no es un precio positivo por debajo de la entrada se rechaza.

En backtest el tamaño se calcula con el precio de llenado simulado; en vivo se calculará con el precio de
referencia justo antes de enviar la orden (la diferencia es el slippage, ya cobrado en el llenado).

## Kill switch

* Se activa **solo** al alcanzar `MaxDrawdown`, o manualmente con un motivo.
* **Nunca se rearma solo**: un reset exige indicar quién lo hace (auditoría).
* Bloquea entradas nuevas; las posiciones abiertas conservan su stop. Cerrar a mercado durante un desplome podría
  empeorar la pérdida; si se quiere cerrar todo, será una acción explícita (fase de ejecución).
* En backtest, una vez activado permanece así el resto de la simulación; el resultado registra cuándo y por qué.
* En paper trading (Fase 12) su estado se persiste con la sesión, sobrevive a reinicios y se opera con
  `POST /api/paper/sessions/{name}/kill-switch` (motivo y autor obligatorios; ver [PAPER_TRADING.md](PAPER_TRADING.md)).

## Días

Los límites diarios usan el día UTC. El día nuevo empieza con el último equity marcado del día anterior; ese
momento expira los bloqueos por pérdida diaria y por racha (el kill switch no expira).
