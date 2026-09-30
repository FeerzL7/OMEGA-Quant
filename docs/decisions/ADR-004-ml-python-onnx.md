# ADR-004: Machine learning con Python y ONNX

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-09-30
* Fase: 7

## Contexto

La Fase 7 introduce el aprendizaje automático: generación de datasets, etiquetas triple barrera, entrenamiento,
validación, prueba fuera de muestra y versionado de modelos, empezando por regresión logística y comparando con
Random Forest y LightGBM (roadmap). La investigación es en Python; el sistema en vivo es C#. La constitución
prohíbe leakage, splits aleatorios en series temporales, información futura en features, preprocesamiento
ajustado con datos futuros y optimizar sobre el test final (§10).

## Decisiones

1. **Una sola implementación de features y etiquetas: C#.** Python no recalcula nada: consume un dataset
   exportado (`POST /api/datasets`, formato `omega-dataset-v1`: CSV + manifiesto JSON). El id del dataset es el
   SHA-256 de su contenido y Python lo verifica antes de usarlo. Así el modelo se entrena con exactamente los
   features que verá en vivo.
2. **Etiquetas triple barrera con las reglas del backtester** (`TripleBarrierLabeler`): decisión al cierre de
   *t*, entrada en la apertura de *t+1*, stop `close − 2·ATR14`, objetivo `close + 3·ATR14`, horizonte 48 velas,
   SL primero si ambas barreras están en una vela, gaps como en el backtester. Precios brutos (los costos son
   de la Fase 9). Un test verifica que cada etiqueta coincide con la operación que el backtester simula.
   Objetivo binario inicial: `1 = TP_FIRST`, `0 = SL_FIRST o TIMEOUT`.
3. **Entradas del modelo:** solo los 8 features sin escala de `features-v1`. Los de nivel de precio (SMA, EMA,
   ATR, MACD, volumen) no son estacionarios; normalizarlos sería una nueva versión del conjunto, en C#.
4. **Validación cronológica con purging.** Walk-forward de ventana expansiva dentro del periodo de desarrollo; se
   eliminan del entrenamiento las muestras cuya etiqueta sigue abierta cuando empieza el periodo de prueba. El
   escalado va dentro del pipeline (se ajusta solo con datos de entrenamiento).
5. **Holdout disciplinado.** Solo se evalúa con `--evaluate-holdout`; cada evaluación queda registrada y se
   advierte si el mismo holdout ya se miró.
6. **Modelos con hiperparámetros fijos y conservadores**, sin búsqueda (buscar sobre los datos de evaluación es
   una fuente clásica de sobreajuste). Cambiarlos es un nuevo experimento.
7. **Qué mide la Fase 7:** poder predictivo fuera de muestra (log loss, Brier, AUC) frente a una referencia sin
   habilidad (la tasa base de cada entrenamiento) y frente a la regla de la estrategia base con **la misma
   cobertura** (precisión de las mismas cantidad de señales). La calibración es la Fase 8 y el valor económico
   con backtest, la Fase 9.
8. **Versionado en ONNX con paridad exacta.** Cada modelo final se exporta a ONNX con manifiesto (dataset,
   features, etiqueta, periodo, hiperparámetros, semilla, versiones). Solo se registra si el ONNX reproduce el
   modelo en **todas** las muestras de desarrollo (tolerancia 10⁻⁶). Los modelos de scikit-learn se exportan con
   entradas `float64` (paridad exacta); el conversor de LightGBM solo admite `float32` y sus árboles pueden tomar
   otra rama, así que LightGBM se evalúa pero **no se registra** mientras su exportación no sea exacta.
9. **Inferencia ONNX en C#:** se implementará cuando un modelo participe en decisiones (Fase 9 en backtests,
   Fase 12 en paper trading), con `Microsoft.ML.OnnxRuntime`.
10. **Entorno reproducible:** versiones exactas en `research/ml/requirements.txt`; semilla fija; el registro de
    cada experimento incluye las versiones de las librerías.

## Alternativas consideradas

**Recalcular features en Python (pandas/TA-Lib).** Rápido para investigar, pero crea dos implementaciones que
divergen sin avisar (inicializaciones, casos límite, redondeos): el modelo se evaluaría con features distintos a
los de producción. Descartado.

**Entrenar en C# (ML.NET).** Una sola pila, pero el ecosistema de investigación (scikit-learn, LightGBM, análisis)
es mucho más sólido en Python y el roadmap fija Python para investigación.

**Servir el modelo con Python (un servicio aparte).** Añade un proceso, una red y un punto de falla al pipeline en
vivo, contra el monolito modular (§2.3). ONNX permite ejecutar en el mismo proceso de C#.

**Validación cruzada aleatoria (k-fold).** Mezcla pasado y futuro: prohibida por la constitución (§10).

**Aceptar LightGBM con tolerancia mayor (≈0.05).** Las diferencias se concentran justo en muestras cercanas a los
umbrales; desplegar un modelo distinto al evaluado invalida la evaluación. Alternativas futuras si LightGBM
resulta el mejor: otro conversor, inferencia nativa de LightGBM en C#, o reentrenar con umbrales representables.

**Parquet en vez de CSV.** Más compacto y tipado, pero requiere dependencias en ambos lados; a esta escala
(~100 000 filas por año) el CSV con manifiesto y hash es suficiente y fácil de inspeccionar.

## Consecuencias

* Para investigar se necesita la API (exportación) y el entorno de Python de `research/ml`.
* Cambiar features o etiquetas se hace en C# y produce un dataset con otro id; los experimentos anteriores
  siguen citando el suyo.
* Los resultados de la Fase 7 hablan de capacidad de predicción, no de rentabilidad.
* Las pruebas del sandbox usaron precios **sintéticos deterministas**, fáciles de predecir; sus métricas (AUC ≈ 0.94)
  no dicen nada sobre BTCUSDT real. Lo que sí validan: que el pipeline no encuentra habilidad en ruido puro y
  que un leakage deliberado sí la fabrica (y los tests lo detectan).
