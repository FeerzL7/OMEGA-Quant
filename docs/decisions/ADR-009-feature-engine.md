# ADR-009: Motor de features

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-09-29
* Fase: 4

## Contexto

La Fase 4 introduce los features (retornos, SMA/EMA, RSI, ATR, MACD, volumen, z-score de volumen,
volatilidad, distancia a la EMA, fuerza de tendencia), cada uno con nombre, propósito, fórmula, lookback,
datos requeridos y riesgo de leakage. Los mismos features alimentarán el backtester (Fase 5), la estrategia
base (Fase 6), los datasets de ML (Fase 7) y el pipeline en vivo (Fase 12). La constitución prohíbe la
información futura en los features y exige registrar el conjunto usado en cada experimento (§10, §22).

## Decisiones

1. **Features sin estado sobre una ventana fija.** Cada feature recibe exactamente sus `Lookback` velas
   cerradas terminando en *t* y nada más. Los promedios recursivos (EMA, Wilder) se inicializan dentro de la
   ventana (EMA: 5·N velas; Wilder: 1 + 11·N; peso residual de la inicialización ≈ e⁻¹⁰).
2. **Ventana contigua obligatoria**: un hueco en la ventana impide el cálculo (error, no valores parciales).
3. **`null` para calentamiento e indefinidos**, nunca 0, NaN ni infinito.
4. **Conjunto versionado e identificado por hash** (`features-v1`, SHA-256 de las definiciones).
5. **`double` para los features**; los precios siguen siendo `decimal` en el dominio y se convierten una vez.
6. **No se persisten los features.** Se recalculan de forma determinista desde las velas.
7. **Verificación contra una implementación independiente** (Python) en cada vela, más pruebas específicas de
   leakage.
8. **Exposición:** `GET /api/features/catalog` y `GET /api/market/{symbol}/{interval}/features/latest`.

## Por qué ventanas fijas

Con estado incremental (un EMA que se actualiza desde el arranque), el valor en *t* depende de cuándo
arrancó el proceso o de cuánto histórico se cargó: el mismo instante daría valores distintos en backtest y en
vivo, y un experimento no sería reproducible. Con ventana fija, el valor es función pura de las velas
`t-L+1..t`. El costo es recalcular la ventana en cada vela: medido, ~4.5 s por un año de velas de 5 m y
< 1 ms por vector en vivo, aceptable para el volumen de OMEGA.

Consecuencia deliberada: los valores difieren ligeramente de plataformas de gráficos que usan todo el
histórico. La diferencia está acotada por la longitud de la ventana y es documentada.

## Garantías verificadas por tests

* Coincidencia con la referencia independiente para los 16 features en las 400 velas (tolerancia relativa 10⁻⁹).
* Alterar velas **futuras** no cambia ningún valor pasado.
* Alterar velas **anteriores** a la ventana declarada no cambia el valor (el lookback es exacto).
* El cálculo en serie y el cálculo puntual coinciden.
* Un hueco dentro de la ventana se rechaza; uno anterior a la ventana no afecta; en una serie, reinicia el
  calentamiento.
* Mercados degenerados (precio constante, solo subidas) no producen NaN.
* `docs/FEATURES.md` documenta cada feature con su lookback exacto (test de sincronización).
* Pruebas de mutación: cambiar el suavizado de Wilder, la base del z-score o la inicialización de la EMA, o
  introducir look-ahead (usar la vela siguiente), hace fallar la suite.

## Alternativas consideradas

**Librerías de indicadores (TA-Lib, Skender.Stock.Indicators, etc.).** Ahorran código y están probadas, pero
calculan con estado sobre toda la serie (el problema de reproducibilidad descrito arriba), añaden
dependencias y no controlamos sus definiciones exactas (inicialización, casos límite). Siguen siendo útiles
como verificación cruzada adicional si se quisiera.

**Calcular los features en Python.** La investigación de ML será en Python (Fase 7), pero el pipeline en vivo
es C#. Tener una sola implementación canónica (C#) evita que el modelo se entrene con features distintos a
los que verá en producción. Python los consumirá exportados desde C# o verificados contra la misma referencia;
se decidirá en la Fase 7 (ADR-004).

**Persistir los features.** Útil si el cálculo fuera caro o no determinista; no es el caso. Se puede añadir
una caché si una fase lo necesita, siempre registrando la versión y el hash del conjunto.

## Consecuencias

* Añadir o cambiar un feature exige una **nueva versión** del conjunto (el hash cambia), regenerar la
  referencia Python y actualizar `docs/FEATURES.md`.
* Los features en nivel de precio (SMA, EMA, ATR, MACD, volumen) deben normalizarse antes de ML.
* Un hueco en los datos deja sin features las siguientes `MaxLookback` velas (250 = ~21 h en 5 m) hasta que
  se rellene; el relleno automático de la Fase 3 lo mitiga.
* Los periodos son valores de partida estándar, no optimizados; optimizarlos es un experimento (§22).
