# Catálogo de features

Conjunto activo: **`features-v1`** (Fase 4). La definición ejecutable está en
`src/Omega.Features/Indicators/Features.cs`; la API la publica en `GET /api/features/catalog` junto con el hash
del conjunto. Diseño y decisiones: [ADR-009](decisions/ADR-009-feature-engine.md).

## Reglas comunes

* **Solo velas cerradas y sin mirar al futuro.** El valor en la vela *t* se calcula con las velas que terminan en
  *t* y nada posterior. El vector está disponible desde el **cierre** de *t* (`AvailableAtUtc`); usarlo para una
  decisión anterior sería look-ahead.
* **Ventana exacta.** Cada feature depende únicamente de sus últimas `Lookback` velas. Los promedios recursivos
  (EMA, Wilder) se inicializan **dentro** de esa ventana, así que el valor es idéntico en backtest y en vivo sin
  importar cuánto histórico se cargó. Las ventanas son lo bastante largas para que el efecto de la
  inicialización sea despreciable (peso residual del orden de e⁻¹⁰), pero los valores pueden diferir
  ligeramente de plataformas de gráficos que usan todo el histórico. Es deliberado.
* **Contigüidad.** Si la ventana contiene un hueco no se calcula nada: mezclar velas no consecutivas produciría
  valores falsos.
* **Calentamiento.** Sin historia suficiente, el valor es `null`, nunca 0.
* **Indefinidos.** Si un valor no está definido matemáticamente (por ejemplo desviación estándar 0), es `null`.
  Nunca se emiten NaN ni infinitos.
* **Escala.** Algunos features están en nivel de precio (no estacionarios). Son válidos para reglas simples
  (estrategia base), pero deben normalizarse antes de usarse en modelos de ML.
* **Parámetros.** Los periodos (14, 20, 12/26/9…) son valores habituales de partida, **no** valores optimizados.
  Cambiarlos es un experimento y requiere una nueva versión del conjunto.

## Features de `features-v1`

### `log_return_1`, `log_return_3`, `log_return_12`

* **Propósito:** cambio de precio en las últimas *k* velas (1, 3, 12); momentum sin escala.
* **Fórmula:** `ln(close[t] / close[t-k])`.
* **Lookback:** k + 1 velas → `log_return_1`: 2 velas, `log_return_3`: 4 velas, `log_return_12`: 13 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo. Nunca debe alinearse con la etiqueta de la vela *t* (eso sería el retorno futuro).

### `sma_20`

* **Propósito:** nivel de precio suavizado; base de reglas de tendencia.
* **Fórmula:** `media(close[t-19..t])`.
* **Lookback:** 20 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo. Nivel de precio: normalizar antes de ML.

### `ema_20`, `ema_50`

* **Propósito:** nivel de precio suavizado con más peso en lo reciente.
* **Fórmula:** EMA con α = 2/(N+1), inicializada con la media de los primeros N cierres de la ventana; después
  `ema = α·close + (1-α)·ema`.
* **Lookback:** 5·N velas → `ema_20`: 100 velas, `ema_50`: 250 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo; inicializada dentro de la ventana. Nivel de precio: normalizar antes de ML.

### `dist_ema_20`

* **Propósito:** cuánto se aleja el precio de su tendencia, como fracción (+ arriba, − abajo); sin escala.
* **Fórmula:** `close[t] / ema_20[t] - 1`.
* **Lookback:** 100 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo (mismos datos que `ema_20`).

### `rsi_14`

* **Propósito:** balance entre subidas y bajadas recientes (0-100); momentum de sobrecompra/sobreventa.
* **Fórmula:** `cambio = close[i] - close[i-1]`; medias de Wilder (α = 1/14, inicializadas con la media de los
  primeros 14 valores) de `max(cambio, 0)` y `max(-cambio, 0)`; `RSI = 100 - 100/(1 + ganancia/pérdida)`.
  100 si la pérdida media es 0; `null` si ambas son 0.
* **Lookback:** 1 + 14 + 10·14 = 155 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo.

### `atr_14`

* **Propósito:** rango típico de la vela incluyendo saltos; volatilidad en unidades de precio (stops, tamaño).
* **Fórmula:** `TR = max(high-low, |high-close[i-1]|, |low-close[i-1]|)`; ATR = media de Wilder (α = 1/14,
  inicializada con la media de los primeros 14 TR).
* **Lookback:** 155 velas.
* **Datos requeridos:** high, low, close.
* **Riesgo de leakage:** bajo. Unidades de precio: dividir por el cierre antes de ML.

### `macd_line`, `macd_signal`, `macd_histogram`

* **Propósito:** momentum como distancia entre tendencia rápida (EMA 12) y lenta (EMA 26); la señal (EMA 9 de la
  línea) marca cambios; el histograma mide su aceleración.
* **Fórmula:** `línea = EMA12(close) - EMA26(close)`; `señal = EMA9(línea)` inicializada con la media de los
  primeros 9 valores de la línea; `histograma = línea - señal`. Todas inicializadas dentro de la ventana.
* **Lookback:** 5·26 + 5·9 = 175 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo. Unidades de precio: normalizar antes de ML.

### `volatility_20`

* **Propósito:** dispersión reciente de los retornos por vela (no anualizada); régimen de volatilidad sin escala.
* **Fórmula:** desviación estándar muestral (n−1) de `ln(close[i]/close[i-1])` sobre los últimos 20 retornos.
* **Lookback:** 21 velas.
* **Datos requeridos:** close.
* **Riesgo de leakage:** bajo.

### `volume`

* **Propósito:** participación durante la vela, en unidades del activo base (BTC en BTCUSDT).
* **Fórmula:** `base_volume[t]`.
* **Lookback:** 1 vela.
* **Datos requeridos:** base_volume.
* **Riesgo de leakage:** bajo, siempre que la vela esté cerrada (el volumen de una vela en curso es incompleto).
  No estacionario: normalizar antes de ML.

### `volume_zscore_20`

* **Propósito:** qué tan inusual es el volumen de *t* frente a la actividad reciente; sin escala.
* **Fórmula:** `(volume[t] - media(volume[t-20..t-1])) / desvEstMuestral(volume[t-20..t-1])`; `null` si la
  desviación es 0.
* **Lookback:** 21 velas.
* **Datos requeridos:** base_volume.
* **Riesgo de leakage:** bajo. La base excluye la propia vela *t*.

### `adx_14`

* **Propósito:** fuerza de la tendencia (0-100) sin importar su dirección; distingue mercados en tendencia de
  mercados laterales.
* **Fórmula:** `+DM = up > down y up > 0 ? up : 0`, `-DM = down > up y down > 0 ? down : 0`
  (`up = high - high[i-1]`, `down = low[i-1] - low`); medias de Wilder (α = 1/14, inicializadas) de +DM, −DM y TR;
  `+DI = 100·media(+DM)/media(TR)`, `-DI` igual; `DX = 100·|+DI − −DI|/(+DI + −DI)` (0 si la suma es 0);
  `ADX` = media de Wilder de DX inicializada con la media de los primeros 14 DX.
* **Lookback:** 155 + 14 = 169 velas.
* **Datos requeridos:** high, low, close.
* **Riesgo de leakage:** bajo.

## Verificación

Los valores se contrastan en cada vela con una implementación independiente en Python
(`tests/Omega.Features.Tests/TestData/generate_reference.py`), sobre 400 velas sintéticas deterministas.
Para regenerar la referencia tras cambiar una definición (lo que implica una nueva versión del conjunto):

```bash
cd tests/Omega.Features.Tests/TestData && python3 generate_reference.py
```

Rendimiento medido: un año de velas de 5 m (~105 000) en ~4.5 s; un vector en vivo en < 1 ms.
