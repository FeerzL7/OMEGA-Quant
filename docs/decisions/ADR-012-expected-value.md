# ADR-012: Valor esperado y estrategia guiada por modelo

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-10-01
* Fase: 9

## Contexto

La constitución prohíbe asumir que probabilidad es rentabilidad (§7): una decisión debe considerar probabilidad
calibrada, valor esperado, comisiones, spread y slippage. La Fase 9 pide `EV = P(win)·Gain − P(loss)·Loss − Costs`
y evaluar la estrategia por EV, no por "confianza > 85 %". Es también la primera vez que un modelo de ML se ejecuta
dentro del sistema C#.

## Decisiones

1. **Fórmula** (retornos sobre el valor de la posición; `a = ATR14 / precio`):
   `EV = p·(g·a) + (1 − p)·(l·a) − [mercado + p·comisión + (1 − p)·mercado]`, donde `p` es la probabilidad
   **calibrada**, `mercado` = comisión + medio spread + slippage (entrada y salidas por stop o timeout) y la salida
   por take profit paga solo comisión (orden límite), igual que en el backtester.
2. **Magnitudes estimadas, no supuestas:** `g` y `l` son el resultado medio, en múltiplos de ATR, de los TP_FIRST y
   del resto (SL y timeouts) en el periodo de **desarrollo** (`outcome_profile.json`). Suponer que todo no-TP es un
   stop completo subestimaría el EV (los timeouts pierden menos o ganan).
3. **Regla de decisión:** largo solo si `EV > minExpectedReturn` (0 por defecto: cualquier ventaja positiva después de
   costos). El umbral es un parámetro registrado; subirlo es un experimento.
4. **Estrategia `model-ev`** (`ModelExpectedValueStrategy`): ONNX → calibración → EV, con las barreras y el horizonte de
   la etiqueta con la que se entrenó el modelo; la posición se deja a sus barreras (como en la etiqueta). Cada señal
   registra P cruda, P calibrada y el desglose del EV. Errores de entrada o de modelo → `NO_TRADE` con su motivo.
5. **Paquete de modelo** (`ModelPackage`): `model.onnx` (verificado contra el hash del manifiesto), `manifest.json`,
   `calibration.json`, `outcome_profile.json`. Si falta algo, no se carga.
6. **Inferencia con `Microsoft.ML.OnnxRuntime` 1.30.0**, la misma versión de runtime que el entorno de Python. Las
   salidas se leen según su tipo declarado (un Random Forest con entradas `float64` devuelve probabilidades `float32`).
7. **Paridad Python ↔ C#:** paquetes de referencia (regresión logística y Random Forest) generados con el código del
   pipeline; C# reproduce probabilidad cruda, calibrada y EV con diferencia ≤ 10⁻¹².
8. **Evaluación económica con el protocolo existente:** `POST /api/backtests` con `strategy: "model-ev"` y `modelId`, en
   los mismos periodos y costos que la estrategia base y buy & hold. Las evaluaciones se cuentan **por modelo**, y la
   API advierte si el periodo se solapa con el de entrenamiento (resultado dentro de la muestra).

## Alternativas consideradas

**Umbral de probabilidad (p > 0.6, p > 0.85).** Ignora el tamaño de las barreras relativo a los costos: con un ATR
pequeño, incluso p = 0.9 pierde dinero después de costos (hay un test que lo demuestra). Prohibido por §7 y §9.

**Modelo multiclase (TP, SL, TIMEOUT) para el EV.** Más preciso en teoría, pero triplica lo que hay que calibrar y
validar. El perfil empírico captura el efecto medio de los timeouts; se puede revisar con datos reales.

**Suponer todo no-TP = stop completo.** Cota inferior simple; descartada por pesimista (descartaría operaciones con EV
positivo). Disponible como prueba de estrés futura.

**Inferencia en C# reimplementando los modelos.** Factible para regresión logística, no para árboles; ONNX cubre todos
con el mismo código y la paridad se verifica.

## Consecuencias

* Toda comparación modelo vs. estrategia base se hace con el mismo motor, costos, periodos y conteo de evaluaciones.
* El perfil de resultados se estima con los mismos datos de entrenamiento del modelo: es una estimación dentro de la
  muestra de **magnitudes** (no de probabilidades); conviene revisar que se mantenga en el holdout.
* El Risk Engine (Fase 10) recibirá las señales `model-ev` con su EV; el tamaño de la posición sigue siendo
  responsabilidad del riesgo, no de la estrategia.
* En las pruebas del sandbox (datos **sintéticos** deterministas) los modelos superaron a la base y a buy & hold en el
  holdout, con 21-22 operaciones. Con menos de 30 operaciones y precios artificialmente predecibles, eso no dice nada
  sobre BTCUSDT real.
