# Paper trading (Fase 12)

Decisiones en [ADR-015](decisions/ADR-015-paper-trading.md). Código: `src/Omega.Execution` (órdenes, modelo de llenado,
proveedor de paper), `src/Omega.Application/Paper` (motor), `src/Omega.Worker/PaperTradingWorker.cs`.

El paper trading ejecuta **el pipeline completo** sobre cada vela cerrada nueva, sin dinero real:

```text
vela cerrada → features → estrategia (modelo → calibración → EV) → Risk Engine → decisión
            → ejecución simulada (órdenes con ciclo de vida) → diario
```

**Es un backtest hacia adelante con reglas verificadas:** usa el mismo modelo de llenado que el backtester
(`CandleFillModel`) y el mismo orden de eventos por vela. Un test exige que, con las mismas velas, paper y backtester
produzcan **las mismas operaciones hasta el último dígito**. No prueba cómo se llenan órdenes reales: eso es la Fase 14
(Testnet).

## Ejecutar

```bash
# Worker en modo Paper (ingesta + paper trading)
Trading__Mode=Paper dotnet run --project src/Omega.Worker

# API para consultar y operar el kill switch
dotnet run --project src/Omega.Api
```

`Trading:Mode` decide qué corre: `Backtest` (por defecto) solo ingiere datos; `Paper` añade el paper trading;
`Testnet` y `Live` **se niegan a arrancar** (código de salida 2) porque su ejecución no existe todavía.

## Configuración (sección `Paper` del Worker)

| Clave | Defecto | Significado |
|-------|---------|-------------|
| `Session` | `paper-1` | Nombre de la sesión (minúsculas y guiones) |
| `Strategy` | `baseline-ema-trend` | Estrategia candidata del catálogo, o `model-ev` |
| `ModelId` | — | Modelo registrado para `model-ev` (con `Research:ModelsDirectory`) |
| `MinExpectedReturn` | 0 | Umbral de EV para `model-ev` |
| `InitialCapital` | 10 000 | Capital simulado |
| `FeeRate`, `SpreadBps`, `SlippageBps` | 0.001, 1, 2 | Costos (los mismos para el EV y para los llenados) |
| `PollInterval` | 5 s | Cada cuánto busca velas nuevas y comandos |
| `FreshnessGrace` | 1 min | Una vela procesada más tarde que esto es vieja: puede cerrar posiciones, nunca abrirlas |

La política de riesgo viene de la sección `Risk` ([RISK_MODEL.md](RISK_MODEL.md)).

**Una sesión nunca cambia de configuración.** Si estrategia, modelo, costos, capital, riesgo o barreras difieren de los
con que se creó, el Worker se niega a continuar (código de salida 3): el diario de una sesión nunca mezcla
configuraciones. Para probar otra configuración, usa otro `Session`.

## Qué pasa en cada vela (mismo orden que el backtester)

1. **Apertura:** si hubo un hueco de datos, la entrada pendiente expira. Se ejecuta la salida decidida en la vela
   anterior, y luego la entrada (dimensionada por el Risk Engine al precio de llenado) con su protección: un stop-market y
   un take profit límite, donde uno cancela al otro.
2. **Durante la vela:** stop o take profit (el stop primero si ambos están dentro de la vela; gaps al precio de apertura
   o del objetivo).
3. **Cierre:** timeout de la barrera, equity marcado a mercado (alimenta pérdida diaria, drawdown y kill switch).
4. **Cierre:** la estrategia decide para la vela siguiente; una entrada pasa por el Risk Engine.

Todo lo que cambia en una vela (órdenes y sus eventos, operaciones, entrada del diario, estado y cursor) se guarda en
**una transacción**. Si el Worker se detiene, al volver retoma desde la última vela guardada; las velas que llegaron
mientras estaba caído se procesan en orden, pero, al ser viejas, solo pueden cerrar posiciones.

## API

| Método | Ruta | Contenido |
|--------|------|-----------|
| GET | `/api/paper/sessions` | Sesiones con caja, equity, drawdown, posición, racha y kill switch |
| GET | `/api/paper/sessions/{name}` | Una sesión |
| GET | `/api/paper/sessions/{name}/trades` | Operaciones cerradas |
| GET | `/api/paper/sessions/{name}/decisions` | Diario: señal, motivo, métricas (probabilidad cruda y calibrada, EV), acción, rechazo de riesgo, equity, notas |
| GET | `/api/paper/sessions/{name}/orders` | Órdenes con su historial completo de estados |
| GET | `/api/paper/sessions/{name}/commands` | Comandos enviados y su resultado |
| POST | `/api/paper/sessions/{name}/kill-switch` | `{"action": "trip" \| "reset", "reason": "...", "requestedBy": "..."}` |

El kill switch **no lo cambia la API**: el comando se encola en la base de datos y lo aplica el Worker en su siguiente
ciclo (la respuesta es 202 Accepted). Motivo y autor son obligatorios y quedan registrados.

> **Seguridad:** la API todavía no tiene autenticación. Quien llegue a ella puede rearmar el kill switch. Exponla solo en
> `localhost` hasta la Fase 15.

## Antes de la Fase 14

El paper trading debe superar la puerta de estabilidad con datos reales antes de enviar órdenes a cualquier entorno de
Binance: [PAPER_STABILITY.md](PAPER_STABILITY.md).

## Limitaciones conocidas

* Los llenados se simulan con velas cerradas y los costos configurados; no hay libro de órdenes ni latencia real.
* El spread y el slippage que ve el Risk Engine son los configurados, no observados.
* Una posición a la vez (Spot, `MaxOpenPositions = 1` en la práctica de esta fase).
* No hay actualizaciones en tiempo real hacia la UI (se decidirá con el panel, Fase 13, ADR-007).
