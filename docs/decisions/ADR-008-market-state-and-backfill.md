# ADR-008: Estado de mercado, frescura de datos y relleno de huecos

* Estado: Aceptado (decisiones delegadas por el propietario; revisables)
* Fecha: 2026-09-29
* Fase: 3

## Contexto

La Fase 3 pide validación de velas, agregación donde se requiera, estado de mercado, detección de vela
cerrada y frescura de datos. La constitución exige que, si la integridad de los datos de mercado no es
confiable, el trading en vivo se bloquee (§14), y que las fases de investigación usen datos sin huecos
silenciosos (§22). Tras la Fase 2 los huecos se detectaban pero no se rellenaban.

## Decisiones

1. **Estado de mercado como evaluación pura** (`MarketStateEvaluator` en `Omega.Core`) sobre hechos
   persistidos: última vela cerrada, huecos y reloj. Resultado: `MarketState` con frescura (`NO_DATA`,
   `FRESH`, `STALE`), antigüedad de los datos, huecos recientes y `IsReliable`.
2. **Frescura:** los datos son frescos mientras la siguiente vela no esté vencida:
   `ahora <= cierre de la última vela + 1 intervalo + FreshnessGracePeriod` (60 s por defecto).
3. **Integridad:** un hueco que se solapa con la ventana `IntegrityWindow` (24 h por defecto) hace los datos
   no confiables. `IsReliable = frescos y sin huecos en la ventana`. Las fases de estrategia y riesgo deberán
   usar `IsReliable = false` como bloqueo.
4. **Relleno de huecos por REST** (`GET /api/v3/klines` en `https://data-api.binance.vision`, solo datos
   públicos): al detectar un hueco durante la ingesta y al arrancar (huecos de los últimos 7 días).
   Best effort: un fallo se registra y **nunca** detiene la ingesta en vivo; el hueco sigue siendo detectable.
5. **Detección de vela cerrada en REST:** la respuesta no tiene indicador de cierre y **incluye la vela en
   formación**. Una vela REST solo se acepta si su cierre quedó al menos `ClockSkewTolerance` (2 s) en el pasado.
6. **Validación temporal:** si el stream reporta cerrada una vela cuyo cierre, según el reloj local, aún no
   llega (más allá de la tolerancia), el reloj local está atrasado: se registra `CLOCK_SKEW_DETECTED`. La vela
   se acepta igual (el timestamp del exchange manda, §13), pero la frescura y la latencia medidas quedan sesgadas.
7. **Agregación de velas: no se implementa.** Binance entrega velas de 5 m ya cerradas y ninguna fase requiere
   otro intervalo todavía. Se implementará cuando una fase lo pida (por ejemplo, features multi-timeframe).
8. **Exposición:** `GET /api/market/{symbol}/{interval}/state` en `Omega.Api` (la UI la consumirá en la Fase 13)
   y un monitor en el Worker que registra `MARKET_DATA_STALE` / `MARKET_DATA_FRESH` solo en las transiciones.

## Diseño

```text
Worker
 ├─ MarketDataIngestionService ──► ICandleStore
 │     ├─ al arrancar: CandleGapFiller.FillRecentGapsAsync (7 días)
 │     ├─ DataGapDetectedEvent ──► CandleGapFiller.FillAsync ──► IHistoricalCandleSource (BinanceRestKlineSource)
 │     └─ ClockSkewDetectedEvent ──► CLOCK_SKEW_DETECTED
 └─ MarketDataFreshnessMonitor (cada 30 s) ──► MarketStateService ──► STALE / FRESH en transiciones

Api
 └─ GET /api/market/{symbol}/{interval}/state ──► MarketStateService ──► MarketStateEvaluator
```

* `ICandleStore.FindGapsAsync`: consulta con `lag()` sobre la clave primaria. Incluye la vela previa al inicio
  del rango y la siguiente a su fin, para reportar completos los huecos que cruzan los bordes. Lo que falta
  después de la última vela no es un hueco (es frescura).
* `BinanceRestKlineSource`: páginas de 1000 velas (máximo del endpoint), mismas validaciones que el stream
  (alineación, `T = t + intervalo − 1 ms`, invariantes de `Candle`), linaje `binance-spot-rest`.
* Errores REST clasificados en `HistoricalDataException(IsTransient, RetryAfter)`: red, timeout y 5xx →
  transitorio; 429 (límite excedido) y 418 (IP bloqueada por ignorar 429) → transitorio con el `Retry-After`
  del servidor; otros 4xx y formato inesperado → no transitorio.
* `CandleGapFiller`: hasta 3 intentos por hueco con backoff, esperando **al menos** el `Retry-After`. Si el
  exchange no tiene velas para parte del periodo (por ejemplo, mantenimiento), el resultado es parcial y se
  registra como advertencia con cuántas siguen faltando, nunca se oculta.
* Composición de PostgreSQL y validación de opciones centralizadas en `Omega.Infrastructure`
  (`AddOmegaPersistence`, `AddValidatedOptions`, `EnsureDatabaseReadyAsync`), usadas por API y Worker.

## Alternativas consideradas

**Rellenar desde el stream reconectando con un punto de inicio.** Los streams de Binance no permiten
reproducir el pasado; REST es la única fuente oficial de velas históricas.

**Rellenar en un proceso o job separado.** Añade un componente operativo más. Integrado en la ingesta, el
hueco se cierra en segundos tras detectarse; un job periódico se puede añadir si los fallos se vuelven
frecuentes (los huecos no rellenados quedan en la base y son detectables).

**Frescura basada en el último mensaje recibido (incluidas velas en formación).** Mediría la salud de la
conexión, no la de los datos utilizables. Solo las velas cerradas pueden generar señales, así que la frescura
se mide sobre ellas.

**Rechazar velas con desfase de reloj.** Descartaría datos válidos del exchange por un problema local.
Se registra y se acepta; el operador debe corregir la sincronización (NTP).

## Consecuencias

* El Worker y la API necesitan PostgreSQL; la API también verifica el esquema al arrancar.
* Con el relleno activo, los huecos por reconexiones o reinicios se cierran solos; los que el exchange no puede
  cubrir quedan registrados como parciales.
* `IsReliable` es la señal que Strategy y Risk deberán respetar. Un umbral de gracia demasiado estrecho
  generaría falsos "stale"; uno demasiado amplio retrasaría el bloqueo. 60 s es un punto de partida y es
  configurable; debe revisarse con datos reales de latencia (los logs registran la latencia de cada vela).
* El relleno al arrancar mira 7 días. Huecos más antiguos quedan registrados pero no se rellenan
  automáticamente; una carga histórica completa (para investigación) sería una decisión aparte.
