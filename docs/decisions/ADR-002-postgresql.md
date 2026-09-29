# ADR-002: Persistencia en PostgreSQL

* Estado: Aceptado (decisiones delegadas por el propietario; revisables)
* Fecha: 2026-09-28
* Fase: 2

## Contexto

La constitución fija PostgreSQL como base de datos inicial (§23). La Fase 2 debe persistir velas, eventos de
mercado cuando se justifique y eventos de sistema, con configuración, migraciones, repositorios e integridad
de datos. Los datos persistidos serán la base de la investigación (features, backtests, modelos), así que un
error silencioso aquí contamina todo lo posterior.

## Decisiones

1. **Driver Npgsql con SQL explícito**, sin ORM.
2. **Migraciones como scripts SQL versionados** (`0001_initial_schema.sql`, …) embebidos en `Omega.Infrastructure`
   y aplicados por un migrador propio.
3. **Qué se persiste:** velas cerradas y eventos de sistema. **No** se persisten los mensajes crudos del stream.
4. **Abstracciones en `Omega.Core`** (`ICandleStore`, `ISystemEventStore`, `PersistenceException`); implementación
   en `Omega.Infrastructure`; orquestación en el nuevo `Omega.Application` (D-015).
5. **La ingesta reanuda desde la última vela guardada**, de modo que los huecos entre reinicios se detectan.
6. **Nunca perder una vela en silencio:** fallos transitorios de escritura se reintentan con backoff; fallos no
   transitorios detienen el Worker.

## Diseño

**Esquema (versión 1).**

* `candles`: clave primaria `(symbol, interval_code, open_time)`. Precios y volúmenes `numeric` sin
  precisión fija (exactos, sin redondeo de punto flotante). Tiempos `timestamptz` (UTC). Linaje: `source` y
  `observed_at`. Restricciones `CHECK` que replican las invariantes de `Candle.Create` (precios positivos,
  `high`/`low` contienen `open`/`close`, `close_time > open_time`, volúmenes no negativos): la base es la última
  línea de defensa si alguien escribe sin pasar por el dominio.
* `system_events`: append-only, con tipo, severidad, mensaje y `details jsonb`. Índices por tiempo y por tipo.
* `schema_migrations`: versión, nombre, checksum SHA-256 y fecha de aplicación.

**Velas inmutables.** Una vela se escribe una vez (`ON CONFLICT DO NOTHING`). Si llega otra con la misma clave:
idéntica → `AlreadyStored`; distinta → `Conflict`, se conserva la guardada y se registra `CANDLE_CONFLICT`.

**Migrador.**

* Lock consultivo de PostgreSQL: dos procesos que arrancan a la vez no aplican nada dos veces.
* Cada script se aplica en su propia transacción junto con su registro en `schema_migrations`.
* Checksum con finales de línea normalizados (un checkout con CRLF en Windows no cambia el checksum).
* El arranque se detiene si un script aplicado cambió, si la base tiene una versión que el código no conoce, o
  si hay migraciones pendientes y `Database:ApplyMigrationsOnStartup` es `false` (valor por defecto; `true`
  solo en `appsettings.Development.json`).

**Errores.** Los repositorios traducen las excepciones de Npgsql a `PersistenceException` con `IsTransient`
(conexión perdida, timeout → transitorio; restricción violada → no transitorio). La capa de aplicación decide
reintentar sin conocer la base de datos.

**Eventos de sistema:** `INGESTION_STARTED/STOPPED`, `MARKET_DATA_CONNECTED/DISCONNECTED`,
`MARKET_DATA_GAP_DETECTED`, `CANDLE_CONFLICT`. Se guardan en modo best effort: si la base falla, quedan en
los logs y la ingesta de velas sigue. Los intentos de conexión ("Connecting") no se guardan (ruido).

**Secretos.** La cadena de conexión solo llega por `dotnet user-secrets` o por la variable
`Database__ConnectionString`. Los mensajes de validación nunca la repiten. El `docker compose` de desarrollo
toma la contraseña de `docker/.env` (ignorado por git) y publica el puerto solo en `127.0.0.1`.

## Alternativas consideradas

**Entity Framework Core (+ proveedor Npgsql).** Migraciones generadas, LINQ y cambio de proveedor sencillo.
No se elige porque: el modelo de datos es pequeño y de series temporales (inserciones idempotentes, lecturas por
rango), donde el SQL explícito es más claro; las migraciones generadas desde el modelo ocultan el esquema real
(tipos `numeric`, restricciones, índices), que aquí es parte de la integridad de los datos; y añade una capa
de dependencias grande. Puede reconsiderarse si el modelo crece mucho (órdenes, posiciones, backtests).

**Dapper.** Reduce el código de mapeo. No se elige por ahora: el mapeo son unas pocas líneas por tabla y no
justifica otra dependencia.

**DbUp / FluentMigrator para migraciones.** Herramientas maduras. El migrador propio son ~150 líneas probadas
contra PostgreSQL real, sin dependencias, con las garantías exactas que necesitamos (lock, checksum,
transacción). Si las necesidades crecen (rollbacks, varios esquemas), se evalúa migrar a una de ellas.

**Persistir los mensajes crudos del stream.** Llegan cada 2 s por vela en curso y no aportan información que la
vela cerrada no tenga para la estrategia actual (solo velas cerradas generan señales, §13). Se reconsidera si
una fase necesita reproducir el stream exacto.

**TimescaleDB.** Útil para volúmenes grandes de series temporales. BTCUSDT 5m son ~105 000 filas por año:
PostgreSQL estándar con la clave primaria actual basta.

## Consecuencias

* El Worker necesita PostgreSQL para arrancar. Sin base de datos o con el esquema desactualizado, no arranca.
* Los tests de persistencia corren contra PostgreSQL real (una base temporal por test). Sin la variable
  `OMEGA_TEST_POSTGRES` se reportan como **omitidos**, nunca como aprobados.
* Nuevos cambios de esquema = nuevo script numerado. Editar un script aplicado detiene el arranque.
* Los huecos se detectan también entre reinicios y quedan registrados. **Rellenarlos** (REST histórico de
  Binance) sigue pendiente; los huecos se pueden reconstruir en cualquier momento a partir de la tabla `candles`.
* Los tiempos se guardan con precisión de microsegundos (PostgreSQL); los de las velas son de milisegundos, así
  que no hay pérdida.
