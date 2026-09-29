# ADR-003: Datos de mercado de Binance Spot

* Estado: Aceptado (decisiones delegadas por el propietario al inicio de la Fase 1)
* Fecha: 2026-09-28
* Fase: 1

## Contexto

La Fase 1 debe entregar un stream de velas **cerradas** de BTCUSDT 5m desde Binance Spot, con ciclo de vida
de conexión, reconexión, cancelación, normalización y logging. La constitución exige verificar la
documentación oficial vigente (§12), usar el timestamp del exchange normalizado a UTC (§13) y manejar fallos,
duplicados, mensajes inválidos, huecos y heartbeats (§14).

Datos de la documentación oficial de Spot ("WebSocket Streams", consultada el 2026-09-28) que condicionan el
diseño:

* Endpoints: `wss://stream.binance.com:9443` / `:443`; `wss://data-stream.binance.vision` sirve **solo** datos de
  mercado (sin user data stream).
* Stream `<symbol>@kline_<interval>` (símbolo en minúsculas), actualización cada 2000 ms para intervalos
  distintos de `1s`. El payload incluye `t`/`T` (apertura/cierre en ms UTC), OHLCV como strings y `x`
  (vela cerrada).
* Una conexión es válida 24 horas. El servidor envía un ping cada 20 s y desconecta si no recibe pong en un
  minuto; el pong debe copiar el payload del ping. Los pongs no solicitados no evitan la desconexión.
* Evento `serverShutdown` antes de un cierre del servidor: reconectar cuanto antes.
* Límites: 5 mensajes entrantes por segundo (PING/PONG cuentan); 300 conexiones cada 5 minutos por IP.

Estos valores pueden cambiar; deben revisarse contra la documentación oficial antes de fases que dependan de
ellos (en especial la 14 y la 15).

## Decisiones

1. **Cliente propio** sobre `System.Net.WebSockets.ClientWebSocket` y `System.Text.Json`, sin paquetes de terceros.
2. **Endpoint por defecto `wss://data-stream.binance.vision`**, configurable (`MarketData:StreamBaseUrl`,
   solo `wss://`).
3. **Huecos: detectar y reportar, no rellenar.** Un hueco genera `DataGapDetectedEvent` (integridad no
   garantizada para ese periodo). El relleno por REST se hará cuando exista persistencia (Fases 2-3).
   *Estado tras la Fase 2: los huecos se persisten como eventos de sistema. Fase 3: relleno por REST implementado (ADR-008).*
4. **Solo velas cerradas** (`x = true`) salen del adaptador. Las actualizaciones de la vela en curso se descartan.

## Diseño

```text
ClientWebSocket ─► IWebSocketTransport ─► BinanceStreamMessageParser ─► BinanceKlineNormalizer
                                                                           │ (Binance: alineación, T = t + intervalo − 1 ms)
                                                                           ▼
                                                                  Candle.Create (invariantes de dominio, Omega.Core)
                                                                           ▼
                                                                  ClosedCandleSequencer (duplicados, desorden, huecos)
                                                                           ▼
                                                     IMarketDataStream → CandleClosed / DataGapDetected / ConnectionStatusChanged
```

* **Heartbeats:** la implementación de WebSocket de .NET responde automáticamente a los ping con un pong que
  copia el payload, siempre que haya una lectura pendiente (OMEGA siempre tiene una). El keep-alive del
  cliente está desactivado: los pongs no solicitados no evitan la desconexión y cuentan para el límite de
  mensajes.
* **Conexión muerta:** si no llega ningún mensaje en `ReceiveIdleTimeout` (30 s por defecto, 15 veces la
  cadencia documentada), la conexión se reemplaza.
* **Reconexión:** backoff exponencial con jitter ("equal jitter": entre d/2 y d, d = min(1 s · 2ⁿ, 60 s)). Se
  reinicia cuando una conexión recibió datos. `serverShutdown` provoca reconexión inmediata.
* **Límite de 24 h:** pasado `MaxConnectionLifetime` (23 h), la conexión se renueva **justo después de cerrar
  una vela**, cuando la siguiente está a un intervalo completo, en lugar de esperar a que el exchange la corte
  en un momento arbitrario.
* **Valores:** precios y volúmenes como `decimal` (se reciben como texto; nunca `double`). Timestamps del
  exchange convertidos a `DateTimeOffset` UTC.
* **Mensajes inválidos:** se registran y se descartan; nunca producen una vela ni tumban la conexión.

## Alternativas consideradas

**Librería de terceros (por ejemplo Binance.Net).** Ahorra código y cubre muchos endpoints. No se elige porque
esta fase es precisamente la capa de integridad: con una librería, las reglas de reconexión, duplicados y
heartbeats quedan en código ajeno, difícil de probar con nuestras garantías, y se añade una dependencia amplia
(con cuentas y órdenes) para usar un solo stream público. Se puede reconsiderar para REST/órdenes en las
Fases 14-15.

**`wss://stream.binance.com:9443`.** Endpoint principal, igualmente válido para market data. Se prefiere el
de solo datos de mercado para que, por construcción, este componente no tenga acceso a nada relacionado con
cuentas.

**Rellenar huecos por REST en la Fase 1.** Sin persistencia no hay dónde reconciliar el histórico, y añadiría
un segundo cliente (REST) fuera del alcance de la fase.

## Consecuencias

* No hay credenciales en esta fase: los streams de mercado son públicos. La sección `MarketData` nunca debe
  contener claves.
* ~~Al arrancar no se sabe si faltan velas anteriores (no hay histórico persistido); la detección de huecos
  cubre lo ocurrido **durante** la ejecución.~~ *Actualizado en la Fase 2: el stream recibe la última vela
  persistida y detecta también los huecos entre reinicios (ADR-002).*
* Una vela perdida justo en una reconexión se detecta como hueco con la vela siguiente, no antes (hasta 5 min).
* La conexión real con Binance no se prueba en CI; los tests usan un transporte simulado. La verificación en
  vivo es manual (README).
