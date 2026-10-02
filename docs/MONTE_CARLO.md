# Monte Carlo (Fase 11)

Decisiones en [ADR-014](decisions/ADR-014-monte-carlo.md). Código: `src/Omega.Backtesting/MonteCarlo`.

**Monte Carlo no predice el mercado** (CLAUDE.md §21). Recombina las operaciones de un backtest para mostrar qué tan
amplio podría ser el rango de resultados, drawdowns y rachas **con esas mismas operaciones**. No contiene información
que el backtest no tuviera, y hereda todos sus defectos (periodo, costos, dentro o fuera de muestra).

## Uso

```bash
curl -X POST http://localhost:5080/api/backtests/<id>/monte-carlo -H "Content-Type: application/json" -d '{
  "method": "block-bootstrap", "paths": 10000, "blockLength": 5 }'
```

| Campo | Defecto | Significado |
|-------|---------|-------------|
| `method` | `bootstrap` | `bootstrap` (operaciones independientes), `block-bootstrap` (bloques consecutivos circulares: conserva rachas y dependencia de corto plazo), `shuffle` (mismas operaciones en otro orden) |
| `paths` | 10 000 | Caminos simulados (100 a 100 000) |
| `horizonTrades` | las del backtest | Operaciones por camino (hasta 10 000; `shuffle` exige las del backtest) |
| `blockLength` | 5 | Longitud de bloque para `block-bootstrap` |
| `ruinLevel` | 0.5 | Ruina: el equity **llegó alguna vez** a esta fracción del capital inicial |
| `extraCostBps` | 0 | Robustez: costo extra por punta, en pb del valor de la posición |
| `skipProbability` | 0 | Robustez: probabilidad de que una operación no se ejecute |
| `seed` | 20261001 | Semilla; la misma semilla da exactamente el mismo resultado |

Nada se guarda: el resultado se regenera con (backtest, parámetros, semilla).

## Qué devuelve

* Distribuciones (media, mínimo, P5, P25, P50, P75, P95, máximo) de capital final, retorno final, **profundidad**
  del drawdown máximo (0.12 = 12 % bajo el máximo) y racha de pérdidas más larga.
* Probabilidad de pérdida, de ruina y de que el **kill switch** se active (drawdown ≥ `MaxDrawdown` de la política de
  riesgo con la que se hizo el backtest; no disponible para backtests anteriores a la Fase 10).
* Estadísticas de la secuencia original, medidas igual que los caminos, y el drawdown vela a vela del backtest.
* Bandas de equity (P5 a P95) en hasta 100 puntos del horizonte, para graficar.
* Supuestos y avisos de lectura.

## Cómo leerlo

* **¿La secuencia original fue afortunada?** Compara su drawdown con los percentiles de `shuffle` y `bootstrap`. Si el
  original está por debajo de P5, el backtest muestra un camino favorable que no conviene esperar.
* **¿Resiste costos?** Repite con `extraCostBps` 2-10. Si la probabilidad de pérdida se dispara, la ventaja es menor que
  la incertidumbre de los costos.
* **¿Cuánto drawdown planear?** Usa P95 (no la mediana) y recuerda que es una **cota inferior**: se mide entre
  operaciones y no ve las caídas dentro de una posición abierta.
* **Horizontes largos** (`horizonTrades` > operaciones del backtest) muestran cómo crece el riesgo acumulado; la
  probabilidad de que salte el kill switch suele crecer con el horizonte.
* Con menos de 30 operaciones, las distribuciones dependen de muy pocos resultados: la API lo advierte.
* `block-bootstrap` no siempre da caminos peores que `bootstrap`: depende de si las pérdidas del backtest vienen en
  rachas. Comparar ambos dice algo sobre la dependencia entre operaciones.

## Supuestos

1. Los escenarios solo recombinan operaciones del backtest.
2. `bootstrap` y `shuffle` tratan las operaciones como intercambiables; `block-bootstrap` conserva dependencia solo
   hasta la longitud de bloque.
3. El equity se mide entre operaciones (las profundidades son cotas inferiores).
4. No se simulan los límites diarios ni de racha del Risk Engine (las operaciones remuestreadas no tienen fecha); el
   kill switch se evalúa como "el drawdown alcanzó el límite".
5. Cada operación conserva su retorno relativo al equity previo (dimensionamiento fraccional): los resultados se
   componen.
