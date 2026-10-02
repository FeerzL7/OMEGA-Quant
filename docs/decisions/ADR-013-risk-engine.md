# ADR-013: Risk Engine

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-10-01
* Fase: 10

## Contexto

La constitución exige un Risk Engine independiente del modelo de predicción, capaz de rechazar operaciones, con
riesgo por operación, pérdida diaria, drawdown, posiciones, exposición, rachas, spread, slippage, integridad de
datos, disponibilidad de modelo y de ejecución, y un kill switch (§18, roadmap Fase 10). Hasta la Fase 9 el tamaño de
la posición lo decidía el backtester (`IPositionSizer`) y no existía ningún otro control.

## Decisiones

1. `Omega.Risk` con `RiskLimits` (política), `RiskManager` (estado y controles) y `KillSwitch`. Solo referencia
   `Omega.Core`; un test de arquitectura lo verifica.
2. El backtester pide **aprobación** al cerrar la vela de decisión y **tamaño** al ejecutar; ya no dimensiona por su
   cuenta (`IPositionSizer` y `RiskPerTrade`/`MaxPositionFraction` salen de `BacktestConfig`). Las salidas nunca pasan
   por el riesgo.
3. Controles en orden fijo, kill switch primero; cada rechazo con código `RISK_...` y detalle; mapeo a `NO_TRADE`.
4. Límites diarios sobre el equity **marcado a mercado** (incluye posiciones abiertas) y por día UTC.
5. Kill switch automático por drawdown máximo, manual con motivo, **sin rearme automático**; bloquea entradas, no
   cierra posiciones.
6. Política en la sección `Risk` de la configuración, validada al arrancar; cambios por ejecución en backtests;
   la política usada y un resumen (`RiskSummary`) se guardan con cada resultado.
7. Restricciones del instrumento (`InstrumentFilters`) en Core, aplicadas por el dimensionamiento; sus valores
   deben venir del exchange.

## Alternativas consideradas

**Riesgo dentro de la estrategia.** Más simple, pero viola §18: el componente que genera la señal no puede ser el
mismo que la limita.

**Pérdida diaria sobre resultado realizado.** Ignora pérdidas abiertas grandes; con posiciones abiertas el equity a
mercado es la medida honesta. Contrapartida: un bloqueo puede activarse por una caída intradía que luego se recupera
(el detalle lo explica).

**Kill switch que cierra todo a mercado.** Ofrece certeza de salida pero ejecuta ventas en el peor momento y sin
control de slippage; con stops siempre presentes se prefiere bloquear entradas y dejar que cada posición salga por
su stop. Un "cerrar todo" explícito puede añadirse en la fase de ejecución.

**Rearme automático del kill switch (por ejemplo, al día siguiente).** Convierte una alarma grave en un retraso; la
constitución pide que la ejecución accidental sea difícil.

**Límites sin valores por defecto (obligar a configurarlos).** Más explícito, pero haría que cada backtest de
investigación necesitara configuración; se eligen defectos conservadores, visibles y registrados.

## Consecuencias

* Los backtests ahora pueden mostrar rechazos de riesgo y una activación del kill switch; los resultados de fases
  anteriores no cambian salvo cuando un límite se alcanza (con los defectos, los backtests de prueba coinciden).
* La Fase 12 conectará el mismo `RiskManager` al pipeline en vivo, con estado persistido, spread y slippage reales,
  integridad del estado de mercado y comandos de la API para activar o rearmar el kill switch.
* La disponibilidad del modelo ya se traduce en `NO_TRADE(MODEL_UNAVAILABLE)` en la estrategia (Fase 9); el riesgo
  añade la disponibilidad de ejecución.
