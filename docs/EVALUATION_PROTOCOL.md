# Protocolo de evaluación

Cómo se evalúa una estrategia en OMEGA, para que un resultado signifique algo (CLAUDE.md §10, §22). Aplica a
la estrategia base (Fase 6) y a todo modelo posterior, que se comparará contra ella en las **mismas** condiciones.

## 1. Antes de mirar resultados

1. **Importar la historia** que se va a usar (`MarketData:Backfill:HistoryStart`).
2. **Fijar los periodos por escrito, en orden cronológico y sin solaparse:**
   * `development`: donde se explora y se ajusta (por ejemplo, los primeros ~70 %).
   * `holdout`: el periodo final, reservado. Se evalúa **una vez**, al final, con la estrategia congelada.
   Anotar las fechas en el registro de experimentos (`research/experiments/`) antes de la primera ejecución.
3. **Fijar la política de riesgo** (sección `Risk`) y no cambiarla según los resultados; el resultado registra la
   política usada. Comparar estrategias con la **misma** política.
4. **Fijar los costos**: por defecto comisión 0.10 %, spread 1 pb, slippage 2 pb (ver [BACKTESTING.md](BACKTESTING.md)).
5. **No optimizar la estrategia base.** Sus parámetros son convencionales y se fijaron antes de ver datos. Si se
   cambian, es otra estrategia (otra versión) y otro experimento.

## 2. Qué se compara

En cada periodo, con los mismos datos y costos:

| Candidato | Referencias |
|-----------|-------------|
| Estrategia evaluada (base; desde la Fase 9, `model-ev` con un `modelId`) | `buy-and-hold` y, para modelos, `baseline-ema-trend` |

Un candidato solo justifica su complejidad si supera a las referencias **después de costos** y fuera de
muestra. Si el ML no mejora a la estrategia base, no se justifica más complejidad (roadmap, Fase 6).

Un modelo solo se juzga en periodos **posteriores** a su entrenamiento: la API advierte cuando el periodo del
backtest se solapa con el periodo de entrenamiento del modelo (resultado dentro de la muestra).

## 3. Cómo leer un resultado

* **Costos primero.** Mirar `totalFees` frente al beneficio bruto. Una estrategia de alta rotación puede tener un
  win rate alto y perder por comisiones.
* **Significancia.** `statistics.tStatistic` y el intervalo bootstrap de 95 % del retorno medio por operación.
  Si el intervalo incluye 0, el resultado no se distingue del azar. Con menos de 30 operaciones no se interpreta
  (la API lo advierte). Ambos tests suponen operaciones independientes: son un primer filtro, no una prueba.
* **Estrés de costos.** Repetir con costos mayores (por ejemplo comisión 0.20 % y slippage 10 pb). Un resultado
  que desaparece con costos algo peores no es robusto.
* **Drawdown y exposición** junto al retorno: un retorno similar con menor drawdown es preferible.
* **Monte Carlo** ([MONTE_CARLO.md](MONTE_CARLO.md)) sobre el backtest: si el drawdown observado queda por debajo del
  P5 de las permutaciones, la secuencia fue afortunada; si unos pocos pb extra de costo vuelven la pérdida casi
  segura, la ventaja no es robusta. Monte Carlo no añade evidencia: solo muestra el rango que el backtest permite.

## 4. Disciplina del holdout

* Cada ejecución queda registrada (`GET /api/backtests`). La API cuenta cuántas veces se evaluó ya el mismo
  periodo con la misma estrategia y **advierte** al evaluar un `holdout` que ya se miró, incluso con otros costos.
* Si el holdout se "gastó" (se miró y luego se cambió la estrategia), deja de ser fuera de muestra: hay que
  esperar datos nuevos o reservar otro periodo, y anotarlo.
* Ningún resultado de backtest es una promesa de rentabilidad (§1). La siguiente evidencia es el paper trading
  (Fase 12).

## 5. Registro

Cada ejecución guarda: estrategia (nombre, versión, parámetros), versión y hash de features, configuración
completa (costos, tamaño, timeout), huella SHA-256 del dataset, periodo y etiqueta, métricas, estadísticas,
operaciones y equity diaria. Se reproduce exactamente volviendo a ejecutar sobre las mismas velas.
