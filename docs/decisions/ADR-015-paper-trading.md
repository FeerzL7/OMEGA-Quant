# ADR-015: Paper trading

* Estado: Aceptado (diseño presentado y confirmado por el propietario antes de implementarlo)
* Fecha: 2026-10-01
* Fase: 12

## Contexto

La Fase 12 ejecuta por primera vez el pipeline completo en tiempo real: datos → features → modelo → calibración → régimen
→ EV → riesgo → decisión → ejecución simulada → diario. Cambia el modelo de ejecución y agrega estado persistente
(§2.2), así que el diseño se presentó antes de implementarlo.

## Decisiones

1. **Modelo de llenado compartido** (`CandleFillModel` en `Omega.Execution`), usado por el backtester y por el paper. Un
   test exige paridad exacta (mismas operaciones, al último dígito decimal) sobre las mismas velas.
2. **Llenados basados en velas cerradas:** decisión al cierre de *t*, ejecución a la apertura de *t+1*, procesada cuando
   *t+1* cierra; costos configurados. Llenados reales: Fase 14.
3. **Ciclo de vida de órdenes** (§15) con transiciones validadas: CREATED → SUBMITTED → ACKNOWLEDGED →
   (PARTIALLY_)FILLED | REJECTED | CANCELED | EXPIRED. Un SUBMITTED sin acuse no puede cancelarse ni llenarse: es
   incierto y debe reconciliarse. Cada evento se persiste con una secuencia que continúa tras reinicios.
4. **`IExecutionProvider`** (§16): `PlaceMarketOrderAsync`, `CancelOrderAsync` y `SynchronizeAsync` (el simulador dispara
   protecciones desde la vela; un proveedor real reconciliará con el exchange). `PaperExecutionProvider` primero.
5. **Motor en Application y Worker con cursor persistido:** cada vela cerrada se procesa una vez, en orden, en **una
   transacción**; el Worker sondea la base de datos (desacoplado de la ingesta y seguro ante reinicios).
6. **Estado del riesgo persistido** (`RiskState`); un kill switch activo sobrevive a reinicios.
7. **Kill switch por comando:** la API encola `TRIP_KILL_SWITCH` / `RESET_KILL_SWITCH` con motivo y autor; el Worker los
   aplica y guarda el resultado junto con el estado, de forma atómica.
8. **Seguridad del modo:** `Testnet` y `Live` hacen que el Worker se niegue a arrancar; una sesión con otra configuración
   también (código de salida 3). La configuración se compara por significado, no por texto.
9. **Diario estructurado** (§27): cada vela con su señal, motivo, métricas numéricas de la estrategia (`Signal.Metrics`:
   probabilidad cruda y calibrada, EV), acción, rechazo de riesgo, equity y notas de ejecución.
10. **Consultas por REST**; tiempo real hacia la UI se decide en la Fase 13 (ADR-007).

## Alternativas consideradas

**Llenados con libro de órdenes en vivo (bookTicker).** Más realista, pero requiere otro stream y rompe la paridad con el
backtester, que es justamente lo que da sentido al paper como verificación de las reglas. Se estudia en Testnet.

**Comunicación en memoria entre ingesta y paper (evento/canal).** Más inmediata, pero acopla procesos y pierde velas en un
reinicio; el sondeo con cursor persistido es simple y exactamente-una-vez.

**Kill switch modificado directamente por la API.** Habría dos escritores del mismo estado (API y Worker); con comandos, el
Worker es el único dueño de la sesión y cada cambio queda auditado.

**Permitir cambiar la configuración de una sesión existente.** Mezclaría resultados de configuraciones distintas en un
mismo diario; cada configuración es un experimento (§22).

## Consecuencias

* El backtester se refactorizó para usar el modelo compartido sin cambiar ningún resultado (la suite completa lo
  verifica).
* `ExitReason` vive ahora en `Omega.Execution`.
* Nuevas dependencias: `Backtesting → Execution`, `Infrastructure → Execution` (implementa `IPaperTradingStore`),
  `Application → Risk, Execution`.
* La API no tiene autenticación: no debe exponerse fuera de `localhost` (Fase 15).
