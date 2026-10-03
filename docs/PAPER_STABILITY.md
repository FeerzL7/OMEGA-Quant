# Puerta de entrada a la Fase 14: estabilidad del paper trading

El roadmap exige que la Fase 14 (ejecución en un entorno de Binance sin dinero real) empiece **solo después de que el paper
trading haya demostrado estabilidad técnica**. Este documento convierte esa condición en criterios verificables. El
propietario puede ajustar los umbrales; lo que no debe hacerse es empezar la Fase 14 sin haberlos evaluado.

"Estabilidad técnica" significa que el sistema **funciona sin intervención y sin perder ni inventar información**. No mide
si la estrategia gana dinero: eso lo responde el protocolo de evaluación ([EVALUATION_PROTOCOL.md](EVALUATION_PROTOCOL.md)).

## Preparación

1. PostgreSQL en marcha; `Database:ConnectionString` en user-secrets del Worker y de la API.
2. Historia importada (calentamiento de features):
   `dotnet run --project src/Omega.Worker -- --MarketData:Backfill:HistoryStart=2025-01-01T00:00:00Z`
3. Worker en modo Paper con una **sesión nueva** dedicada a esta prueba:
   `Trading__Mode=Paper Paper__Session=paper-real-1 dotnet run --project src/Omega.Worker`
   (estrategia base, o `model-ev` solo con un modelo validado fuera de muestra).
4. API y panel para vigilarlo ([DASHBOARD.md](DASHBOARD.md)).
5. Costos y política de riesgo **iguales** en el Worker (`Paper`, `Risk`) y en la API (`Risk`), para que el criterio 6 sea
   comparable.

En las consultas, sustituye `paper-real-1` por el nombre de tu sesión.

## Criterios

### 1. Duración: al menos 14 días continuos (≈ 4 000 velas)

```sql
SELECT min(d.candle_open_time) AS primera, max(d.candle_open_time) AS ultima, count(*) AS decisiones
FROM paper_decisions d JOIN paper_sessions s ON s.id = d.session_id
WHERE s.name = 'paper-real-1';
```

**Aprueba si** `ultima − primera ≥ 14 días`.

### 2. Diario completo: una decisión por cada vela cerrada

```sql
WITH s AS (SELECT id FROM paper_sessions WHERE name = 'paper-real-1'),
     r AS (SELECT min(candle_open_time) AS a, max(candle_open_time) AS b FROM paper_decisions WHERE session_id = (SELECT id FROM s))
SELECT
  (SELECT count(*) FROM candles c, r
    WHERE c.symbol = 'BTCUSDT' AND c.interval_code = '5m' AND c.open_time BETWEEN r.a AND r.b)        AS velas,
  (SELECT count(*) FROM paper_decisions d, r
    WHERE d.session_id = (SELECT id FROM s) AND d.candle_open_time BETWEEN r.a AND r.b)               AS decisiones,
  (SELECT count(*) FROM candles c CROSS JOIN r
     LEFT JOIN paper_decisions d ON d.session_id = (SELECT id FROM s) AND d.candle_open_time = c.open_time
    WHERE c.symbol = 'BTCUSDT' AND c.interval_code = '5m' AND c.open_time BETWEEN r.a AND r.b
      AND d.id IS NULL)                                                                               AS velas_sin_decision;
```

**Aprueba si** `velas = decisiones` y `velas_sin_decision = 0`. (Una vela no puede tener dos decisiones: la base de datos lo
impide con una restricción única.)

### 3. Datos íntegros

Velas presentes frente a esperadas en el periodo de la sesión:

```sql
WITH r AS (SELECT min(candle_open_time) AS a, max(candle_open_time) AS b
           FROM paper_decisions d JOIN paper_sessions s ON s.id = d.session_id WHERE s.name = 'paper-real-1')
SELECT (extract(epoch FROM (r.b - r.a)) / 300)::bigint + 1 AS esperadas,
       (SELECT count(*) FROM candles c WHERE c.symbol = 'BTCUSDT' AND c.interval_code = '5m' AND c.open_time BETWEEN r.a AND r.b) AS presentes
FROM r;
```

Eventos del sistema en el periodo (reconexiones, huecos, errores):

```sql
SELECT severity, source, event_type, count(*)
FROM system_events
WHERE occurred_at >= '<primera>' AND occurred_at <= '<ultima>'
GROUP BY severity, source, event_type
ORDER BY CASE severity WHEN 'Critical' THEN 0 WHEN 'Error' THEN 1 WHEN 'Warning' THEN 2 ELSE 3 END, count(*) DESC;
```

**Aprueba si** `presentes = esperadas` (los huecos se rellenaron), las reconexiones del WebSocket se recuperaron solas y no
hay eventos `Error` o `Critical` sin una explicación conocida (por ejemplo, un corte de red tuyo). El panel debe mostrar
"Datos de mercado: Operativo" la mayor parte del tiempo.

### 4. Recuperación probada a propósito

Haz cada prueba una vez durante la sesión y anota cuándo:

| Prueba | Qué debe pasar |
|--------|----------------|
| Reiniciar el Worker (Ctrl+C y arrancar de nuevo) | El log dice "resumed"; el criterio 2 sigue aprobado (sin velas saltadas ni duplicadas) |
| Detener PostgreSQL ~1 minuto | El Worker registra errores de almacén y reintenta; al volver la base, procesa las velas pendientes en orden |
| Activar y rearmar el kill switch desde el panel (`/paper/paper-real-1`) | Las entradas quedan bloqueadas mientras está activo; ambos comandos aparecen aplicados |

```sql
SELECT c.id, c.command, c.reason, c.requested_by, c.requested_at, c.applied_at, c.result
FROM paper_commands c JOIN paper_sessions s ON s.id = c.session_id
WHERE s.name = 'paper-real-1' ORDER BY c.id;
```

**Aprueba si** las tres pruebas se comportaron como indica la tabla.

### 5. Sin errores no controlados

En los logs del Worker y de la API del periodo: **ninguna** línea `fail:` o `crit:` sin explicación. (Las advertencias
`warn:` por reconexiones o por la API momentáneamente caída son esperables.)

### 6. Paridad con el backtester sobre datos reales

La prueba más fuerte: con las mismas velas reales, la misma estrategia, los mismos costos y la misma política de riesgo, el
backtester debe producir **las mismas operaciones** que la sesión de paper.

1. Operaciones de la sesión:
   `GET /api/paper/sessions/paper-real-1/trades?limit=1000`
2. Backtest del mismo periodo (desde la primera vela de decisión hasta la vela siguiente a la última):

   ```bash
   curl -X POST http://localhost:5080/api/backtests -H "Content-Type: application/json" -d '{
     "strategy": "baseline-ema-trend", "symbol": "BTCUSDT", "interval": "5m",
     "fromUtc": "<primera>", "toUtc": "<ultima + 5 min>", "periodLabel": "paper-parity" }'
   ```

   (Para `model-ev`, añade `"modelId"` y `"minExpectedReturn"` con los mismos valores que la sesión.)
3. Compara las operaciones cerradas: entrada, precio, cantidad, salida, motivo y PnL.

**Aprueba si** coinciden. Las únicas diferencias aceptables son las explicadas por el diario: velas procesadas tarde (nota
`STALE`, por ejemplo tras un reinicio o un corte), que en paper no abren posiciones y en el backtest sí, y su efecto en
cadena sobre el dimensionamiento de las operaciones siguientes. Cualquier otra diferencia es un defecto que hay que
entender antes de la Fase 14.

```sql
SELECT count(*) AS velas_procesadas_tarde
FROM paper_decisions d JOIN paper_sessions s ON s.id = d.session_id
WHERE s.name = 'paper-real-1' AND d.notes::text LIKE '%STALE%';
```

## Registro del resultado

Cuando la puerta se supere (o falle), anótalo en [DECISION_LOG.md](DECISION_LOG.md) con la fecha, la sesión, el periodo y
el resultado de cada criterio. Ese registro es la condición para retomar la Fase 14 ([ADR-017](decisions/ADR-017-exchange-execution-demo-testnet.md)).
