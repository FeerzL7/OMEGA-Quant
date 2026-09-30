# ADR-010: Estrategia base y protocolo de evaluación

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-09-30
* Fase: 6

## Contexto

La Fase 6 pide una estrategia simple sin ML que sirva de benchmark: si un modelo no la supera fuera de muestra,
más complejidad no está justificada. El roadmap exige además que la evaluación sea estadísticamente
significativa después de costos (Objetivo) y la constitución, que cada experimento quede registrado y que una
estrategia no se cambie solo porque un backtest mejoró (§22).

## Decisiones

1. **Estrategia base `baseline-ema-trend` v1**, tendencial: largo si EMA20 > EMA50 y close > EMA20; salida si
   EMA20 < EMA50. Parámetros convencionales, fijados antes de mirar datos y **no optimizados**.
2. **Mismas barreras que el objetivo de ML de la Fase 7** (stop 2·ATR14, take profit 3·ATR14, timeout 48 velas),
   para que la comparación modelo-vs-base mida la calidad de la entrada y no diferencias de salida.
3. **Benchmark `buy-and-hold`**, marcado como tal (sus estadísticas por operación no aplican).
4. **Inicio de trading explícito:** el calentamiento de features se carga antes del periodo y no opera ni cuenta
   en las métricas; la primera vela del periodo ya puede operar.
5. **Significancia:** estadístico t y IC 95 % bootstrap (10 000 remuestreos, semilla fija) del retorno neto
   medio por operación. Umbral orientativo de 30 operaciones para interpretarlos.
6. **Registro de experimentos:** cada ejecución se guarda en `backtest_runs` (migración 0002) con el resultado
   completo en JSON; la equity se guarda diaria (las métricas se calculan antes, con la curva completa).
7. **Disciplina del holdout:** la API cuenta evaluaciones previas del mismo periodo y estrategia (con cualquier
   etiqueta o costos) y advierte al evaluar un periodo `holdout` ya mirado.
8. **Punto de entrada:** `POST /api/backtests` (síncrono, periodos de hasta 3 años), `GET /api/backtests`,
   `GET /api/backtests/{id}`, `GET /api/strategies`.

## Alternativas consideradas

**Otra estrategia base (cruce de medias puro, ruptura de Donchian, reversión a la media con RSI).** Todas son
benchmarks válidos. Se elige tendencia con confirmación porque usa features ya definidos, tiene muy pocos
parámetros y encaja con las barreras del objetivo de ML. Añadir más referencias es barato (catálogo), pero cada
una más es otra oportunidad de elegir la que mejor sale (sesgo de selección): se añaden solo con motivo.

**Optimizar los parámetros de la base (grid search) en el periodo de desarrollo.** Daría un benchmark más fuerte
pero ajustado a los datos, y abriría la puerta a sobreajuste antes incluso de llegar al ML. Si se hace, será
como experimento explícito con walk-forward, no como benchmark.

**Ejecutar backtests desde un CLI o un job en segundo plano.** Un endpoint síncrono basta para periodos de hasta
3 años (~6 s) y es lo que consumirá el panel en la Fase 13. Si los periodos crecen, se pasa a un job con estado.

**Guardar la curva de equity completa.** ~105 000 puntos por año por ejecución; innecesario porque el motor es
determinista y la curva se puede regenerar.

**No contar evaluaciones del holdout.** El software no puede impedir que una persona mire el holdout, pero sí
hacer visible cada mirada. Es la forma más honesta de apoyar la disciplina del §22.

## Consecuencias

* Todo candidato futuro se evalúa con el mismo servicio, los mismos costos y los mismos periodos que la base.
* Los resultados de la base no se "mejoran": una versión nueva es otra estrategia y queda registrada como tal.
* La tabla `backtest_runs` crece con cada ejecución (append-only); a ~50-200 KB por ejecución no es un problema
  a esta escala.
* Las pruebas en el sandbox se hicieron con datos **sintéticos**; ningún resultado de este ADR dice nada sobre
  la rentabilidad de la estrategia en BTCUSDT real.
