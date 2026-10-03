# ADR-017: Ejecución en un entorno de Binance sin dinero real (Fase 14)

* Estado: **Aceptado; implementación diferida** (diseño confirmado por el propietario el 2026-10-02)
* Condición para implementarlo: superar la puerta de estabilidad del paper trading ([PAPER_STABILITY.md](../PAPER_STABILITY.md)),
  por decisión del propietario y conforme al roadmap
* Fase: 14

## Contexto

La Fase 14 envía por primera vez órdenes a Binance, en un entorno sin dinero real, para medir lo que el paper trading no
puede: llenados, latencias, rechazos y reconciliación reales. La constitución exige verificar la documentación oficial vigente
(§12, §14), modelar el ciclo de vida de órdenes y reconciliar resultados inciertos (§15), validar los filtros del exchange
(§19), proteger los secretos (§26) y hacer difícil la ejecución accidental (§17).

## Hechos verificados en la documentación oficial (2026-10-02)

Deben **volver a verificarse** al retomar la fase; Binance los cambia con frecuencia.

* **Spot Demo Mode** (disponible desde 2026-01-29): precios y libros *similares* al exchange real, filtros y límites
  *exactamente* iguales; saldo reiniciable desde la UI. URLs: REST `https://demo-api.binance.com/api`, WebSocket API
  `wss://demo-ws-api.binance.com/ws-api/v3`, streams `wss://demo-stream.binance.com/ws`.
* **Spot Testnet**: precios y libros **independientes** del exchange real; saldos reiniciados cada mes; a veces tiene
  funciones antes que el exchange real. URLs: REST `https://testnet.binance.vision/api`, WebSocket API
  `wss://ws-api.testnet.binance.vision/ws-api/v3`.
* **Datos de usuario:** los endpoints `listenKey` (`POST/PUT/DELETE /api/v3/userDataStream`, `userDataStream.start/ping/stop`)
  fueron **retirados el 2026-02-20**. Los llenados llegan suscribiéndose por la WebSocket API; `userDataStream.subscribe.signature`
  funciona con cualquier tipo de clave (HMAC, RSA, Ed25519) y sin `session.logon`.
* **Firmas REST:** desde 2026-01-15 el payload debe ir *percent-encoded* antes de firmar; si no, `-1022 INVALID_SIGNATURE`.
* **WebSocket:** ping del servidor cada 20 s, pong en menos de 1 minuto; evento `serverShutdown` → reconectar.
* **Órdenes:** la regla de rango de precios de ejecución puede expirar órdenes (`expiryReason`, campo `eR` en
  `executionReport`); los filtros de precio y nocional usan el precio de referencia cuando existe; nuevo estado de símbolo
  `CANCEL_ONLY`; colocar órdenes con éxito tiene peso 0.

## Decisiones

1. **Entorno configurable, Demo Mode por defecto; Testnet como alternativa.** Las señales se calculan con el precio real de
   BTCUSDT y el stop y el objetivo se derivan de él: solo un entorno con precios similares al real (Demo Mode) produce órdenes
   coherentes con esas señales. El modo del sistema sigue siendo `TESTNET` (dinero no real); el entorno concreto se elige con
   `Execution:Environment` (`Demo` | `Testnet`).
2. **Ejecución por REST firmado:** entrada como orden de mercado; al llenarse, protección OCO (stop + objetivo). Firma con el
   *percent-encoding* vigente; clave Ed25519 recomendada, HMAC admitido.
3. **Llenados por el stream de usuario de la WebSocket API** (`userDataStream.subscribe.signature`), con reconexión ante caídas y
   `serverShutdown`.
4. **Reconciliación obligatoria (§15):** al arrancar, tras cada reconexión y ante cualquier resultado incierto (timeout, 5xx,
   desconexión durante el envío) se consulta el estado real de la orden por su `clientOrderId`; nunca se supone éxito ni
   fallo. Mientras exista una orden en estado incierto no se abren posiciones nuevas.
5. **Filtros reales del exchange** (`exchangeInfo`: paso de cantidad, tick de precio, nocional mínimo, rango de precio) aplicados
   antes de enviar (§19); `CANCEL_ONLY` y otros estados no operables bloquean las entradas.
6. **Salvaguardas contra ejecución accidental (§17, §26):** se ejecuta solo con `Trading:Mode=Testnet` **y**
   `Execution:Enabled=true`; el Worker **se niega a arrancar si cualquier URL configurada pertenece al exchange real**; las
   credenciales viven solo en user-secrets o variables de entorno, nunca en logs ni en respuestas de la API; la clave debe
   tener solo permiso de trading Spot, sin retiros.
7. **Medición frente a la simulación:** cada llenado real se registra junto al precio que el modelo de llenado del backtester
   habría supuesto (slippage real frente a simulado), en el diario y en el panel.
8. **Pruebas:** un exchange simulado local reproducirá el comportamiento documentado (firmas, órdenes, OCO, stream de usuario,
   desconexiones, respuestas inciertas) para probar todo, incluida la reconciliación. La prueba contra Demo Mode real la hace el
   propietario.

## Alternativas consideradas

**Solo Testnet.** Es lo que nombra el roadmap, pero sus precios no siguen al exchange real: stops y objetivos calculados con el
precio real quedarían fuera de lugar en su libro. Se mantiene como alternativa para probar funciones nuevas de la API.

**Seguir con `listenKey`.** Retirado por Binance el 2026-02-20.

**Suponer que una orden enviada sin respuesta falló (o se llenó).** Prohibido por §15: lleva a posiciones duplicadas o a
posiciones sin protección.

**Usar una librería de terceros (Binance.Net).** Ya descartado en D-012 para los datos de mercado; para la ejecución se mantiene
el criterio: un cliente propio, pequeño, con el comportamiento documentado y probado, sin dependencias que cambien sin aviso.

## Consecuencias

* La Fase 14 no se implementa hasta registrar en el DECISION_LOG que la puerta de [PAPER_STABILITY.md](../PAPER_STABILITY.md) se
  superó.
* Al retomarla, el primer paso es volver a verificar los hechos de la sección anterior.
* `IExecutionProvider` ya prevé `SynchronizeAsync` como punto de reconciliación (ADR-015): el proveedor real lo implementará
  consultando el exchange.
