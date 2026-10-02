# ADR-011: Calibración de probabilidades

* Estado: Aceptado (decisiones tomadas por delegación del propietario; revisables)
* Fecha: 2026-10-01
* Fase: 8

## Contexto

La confianza cruda de un modelo no es una probabilidad fiable (CLAUDE.md §9). La Fase 8 pide probabilidades
crudas y calibradas, Brier, log loss, análisis de fiabilidad y comparar crudo contra calibrado. Los métodos
candidatos son Platt (sigmoide) e isotónica, y el sistema debe **evaluar** si calibrar mejora la fiabilidad, no
suponerlo. La Fase 9 usará la probabilidad calibrada para el valor esperado.

## Decisiones

1. **Solo predicciones fuera de muestra.** Los calibradores se ajustan sobre `oos_predictions.csv` (walk-forward
   de la Fase 7), nunca sobre predicciones de datos de entrenamiento, donde todo modelo parece más seguro.
2. **Evaluación anidada:** para cada fold *j ≥ 2* se ajusta con los folds anteriores (con purging de etiquetas
   abiertas al inicio de *j*) y se evalúa en *j*. Así se mide si calibrar ayuda **fuera de muestra**.
3. **Métodos `none`, `platt`, `isotonic`; gana el menor log loss del walk-forward**; casi-empates
   (≤ 10⁻⁴) van al más simple. `none` puede ganar y es un resultado legítimo.
4. **Fiabilidad:** 10 bins de igual frecuencia con intervalo de Wilson 95 %, ECE, MCE y descomposición de Brier
   (fiabilidad, resolución, incertidumbre), para crudo y calibrado.
5. **Salida acotada:** toda probabilidad calibrada se recorta a [0.001, 0.999] (un calibrador no debe afirmar
   certeza; con valores 0/1 el valor esperado de la Fase 9 sería degenerado). Platt recorta la entrada a
   [10⁻⁶, 1 − 10⁻⁶] antes del logit.
6. **Empaquetado:** el calibrador elegido se reajusta con todas las predicciones fuera de muestra y se guarda como
   `calibration.json` (`omega-calibration-v1`) junto al `model.onnx` registrado. Modelos no registrados (Fase 7)
   se evalúan pero no se empaquetan.
7. **Aplicación en C#:** `ProbabilityCalibrator` (`Omega.Strategy.Calibration`) carga y valida el JSON y aplica
   las mismas fórmulas; un test de paridad compara 1 141 casos generados por el código de Python (tolerancia 10⁻¹²).
8. **Holdout:** solo con `--evaluate-holdout` y si el experimento guardó predicciones del holdout; cada juicio
   queda registrado y se advierte si se repite.

## Alternativas consideradas

**`CalibratedClassifierCV` de scikit-learn.** Calibra con validación cruzada no temporal por defecto y mezcla el
calibrador dentro del modelo; habría que exportarlo a ONNX junto con el modelo. Un calibrador separado, ajustado
sobre predicciones walk-forward y exportado como JSON transparente, respeta la cronología y se puede inspeccionar.

**Elegir por ECE.** El ECE depende del número y del tipo de bins y puede mejorarse empeorando la discriminación;
el log loss es una regla de puntuación propia que premia probabilidades correctas. El ECE se reporta igualmente.

**Siempre calibrar (por ejemplo, siempre isotónica).** La isotónica sobreajusta con pocos datos y un calibrador
aprendido sobre unos modelos puede no transferirse a otros. La evaluación anidada decide.

**Calibración beta, temperature scaling, bins por histograma.** Opciones válidas; se agregan si Platt e isotónica
resultan insuficientes con datos reales.

## Consecuencias

* El calibrador se aprende con predicciones de los modelos de cada fold y se aplica al modelo final, entrenado con
  todo el desarrollo. Es la aproximación habitual; si el modelo final es mucho más (o menos) seguro que los de los
  folds, el calibrador puede no transferirse, y la evaluación del holdout lo revelaría.
* La Fase 9 cargará `model.onnx` + `calibration.json` como un paquete: probabilidad cruda → calibrada → valor esperado.
* En las pruebas del sandbox (datos sintéticos) ningún calibrador mejoró fuera de muestra y se eligió `none` en
  los tres modelos: Platt e isotónica, aprendidos sobre modelos de folds tempranos (menos seguros), empeoraban a
  los posteriores. Es el comportamiento esperado de la evaluación anidada, no un fallo.
