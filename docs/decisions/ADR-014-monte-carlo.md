# ADR-014: Monte Carlo para análisis de escenarios

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-10-01
* Fase: 11

## Contexto

La Fase 11 pide análisis de escenarios para trayectorias de equity, drawdown, riesgo de ruina, capital terminal y
robustez, sin reemplazar al modelo predictivo. La constitución exige que Monte Carlo no se presente como mecanismo de
predicción (§21).

## Decisiones

1. **Unidad de remuestreo: la operación**, como retorno sobre el equity previo más su fracción de valor comprometido
   (reconstruidos del backtest: con una posición a la vez la cuenta está plana entre operaciones).
2. **Tres métodos:** bootstrap independiente, bootstrap por bloques circulares (sin sesgo de borde) y permutación.
3. **Robustez** por costo extra proporcional al tamaño de cada operación y por operaciones perdidas al azar.
4. **Resultados** como distribuciones y probabilidades (pérdida, ruina "alguna vez", kill switch según la política
   registrada del backtest), estadísticas de la secuencia original y bandas de equity en ≤ 100 puntos.
5. **Determinista** por semilla; sin persistencia (se regenera exactamente). Límites de tamaño (≤ 100 000 caminos,
   ≤ 10 000 operaciones, ≤ 20 millones de pasos) para mantener la petición acotada (~0.3 s para 10 000 × 87).
6. **Avisos obligatorios:** de qué backtest salen los escenarios y que no son una predicción; pocas operaciones; drawdown
   vela a vela más profundo que el medido entre operaciones; backtest sin política de riesgo registrada.
7. En `Omega.Backtesting.MonteCarlo` (análisis de resultados de backtest), sin proyecto nuevo; servicio en
   Application; endpoint `POST /api/backtests/{id}/monte-carlo`.

## Alternativas consideradas

**Remuestrear retornos por vela.** Captura caídas intradía, pero mezcla periodos con y sin posición y rompe la lógica de
barreras de cada operación. La operación es la unidad de decisión del sistema.

**Simular precios (movimiento browniano, GARCH) y repetir la estrategia.** Sería un modelo de mercado con supuestos
propios, más cerca de "predecir" que de analizar lo observado; queda fuera de esta fase.

**Simular el Risk Engine completo en cada camino.** Las operaciones remuestreadas no tienen fechas, así que los límites
diarios no tienen sentido; se informa la probabilidad de alcanzar el drawdown del kill switch.

**Persistir cada análisis.** Innecesario: es determinista y barato de regenerar.

## Consecuencias

* Toda conclusión de Monte Carlo es condicional al backtest de origen: un backtest dentro de la muestra produce
  escenarios dentro de la muestra.
* `BacktestResult.Risk` y `RiskSummary` se declaran nulables: los backtests guardados antes de la Fase 10 no los tienen.
  El compilador señaló un uso que habría fallado con esos runs; quedó corregido.
* En el sandbox (datos sintéticos) la estrategia base mostró un drawdown original de 2.57 % frente a P95 de 8.17 % y una
  probabilidad de pérdida que sube del 55 % al 99.5 % con 5 pb extra por punta: su ventaja no resiste costos.
