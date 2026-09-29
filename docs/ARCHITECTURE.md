# Arquitectura de OMEGA Quant

Estado: Fase 3 completada. Este documento describe la estructura que existe hoy y las reglas que deben mantenerse
en las fases siguientes. Las decisiones mayores están en [`decisions/`](decisions/) y en
[`DECISION_LOG.md`](DECISION_LOG.md).

## 1. Estilo arquitectónico

OMEGA es un **monolito modular**: una sola solución .NET, varios proyectos con responsabilidades separadas y
dependencias explícitas. No hay microservicios, colas de mensajes, Redis ni buses distribuidos, y no se
introducirán sin un requisito técnico demostrado (CLAUDE.md §2.3).

## 2. Pipeline canónico

```text
Market Data → Features → Model → Probability Calibration → Market Regime
→ Expected Value → Risk → Decision → Execution → Monitoring → Evaluation
```

La UI observa este pipeline y envía comandos controlados a través de la API. **Nunca lo reemplaza ni lo
atraviesa por fuera.**

## 3. Proyectos

| Proyecto               | Tipo                | Responsabilidad                                                                 | Contenido actual |
|------------------------|---------------------|----------------------------------------------------------------------------------|---------------------|
| `Omega.Core`           | Librería            | Entidades, value objects, enums, contratos y reglas de dominio fundamentales.     | `TradingMode`, `SignalDirection`, `Result`/`Error`, `TradingOptions`, `Candle`, `CandleInterval`, `SystemEvent`, `MarketState` + `MarketStateEvaluator`, contratos de persistencia (`ICandleStore`, `ISystemEventStore`), `ExponentialBackoff` |
| `Omega.MarketData`     | Librería            | Conexión a datos de mercado de Binance, normalización, velas, validación.        | Stream de velas cerradas y fuente REST histórica de Binance Spot (ADR-003, ADR-008) |
| `Omega.Features`       | Librería            | Cálculo y validación de features e indicadores.                                  | Vacío |
| `Omega.Strategy`       | Librería            | Inferencia, probabilidad, calibración, régimen, valor esperado, señales. No define tamaño de posición. | Vacío |
| `Omega.Risk`           | Librería            | Límites, tamaño de posición, exposición, drawdown, rechazo de operaciones.       | Vacío |
| `Omega.Execution`      | Librería            | Órdenes, ciclo de vida, proveedores de ejecución, filtros del exchange.          | Vacío |
| `Omega.Backtesting`    | Librería            | Simulación histórica, costos, métricas, curva de equity.                         | Vacío |
| `Omega.Application`    | Librería            | Orquestación del pipeline; usa módulos de dominio y abstracciones de Core.        | Ingesta (stream → persistencia), relleno de huecos, estado de mercado y monitor de frescura |
| `Omega.Infrastructure` | Librería            | PostgreSQL, repositorios, integraciones externas, persistencia.                  | Npgsql: migrador, repositorios (ADR-002); composición compartida de persistencia y opciones validadas |
| `Omega.Api`            | Host ASP.NET Core   | Frontera HTTP del sistema: consultas y comandos controlados.                     | `/health`, `/api/system/status`, `/api/market/{symbol}/{interval}/state` |
| `Omega.UI`             | Host Blazor         | Presentación e interacción. Sin lógica de trading.                               | Panel placeholder sin datos |
| `Omega.Worker`         | Host de servicio    | Procesos en segundo plano (datos, features, estrategia, paper trading, monitoreo). | Aplica migraciones, aloja la ingesta y el monitor de frescura |

## 4. Reglas de dependencia

Grafo actual de referencias entre proyectos de producción:

```text
Omega.MarketData ─┐   (+ paquete Microsoft.Extensions.Logging.Abstractions)
Omega.Features ───┤
Omega.Strategy ───┤
Omega.Risk ───────┤
Omega.Execution ──┼──► Omega.Core   (sin referencias a otros proyectos ni paquetes)
Omega.Backtesting ┤
Omega.Infrastructure  (+ paquete Npgsql; implementa las abstracciones de Core)
Omega.Api ────────┤
Omega.Worker ─────┘

Omega.Infrastructure (+ Microsoft.Extensions.Options.ConfigurationExtensions: composición compartida)
Omega.Application ──► Omega.MarketData, Omega.Core
Omega.Worker ───────► Omega.Application, Omega.MarketData, Omega.Infrastructure   (raíz de composición)
Omega.Api ──────────► Omega.Application, Omega.Infrastructure                     (raíz de composición)

Omega.UI           (sin referencias a proyectos; hablará con Omega.Api por HTTP)
```

Reglas, todas verificadas automáticamente en `tests/Omega.Integration.Tests/Architecture`:

1. `Omega.Core` no referencia ningún proyecto ni paquete.
2. `Omega.UI` no referencia MarketData, Features, Strategy, Risk, Execution, Backtesting, Infrastructure ni Worker,
   ni paquetes de Binance, PostgreSQL/EF Core o ML.
3. Los módulos de dominio (MarketData, Features, Strategy, Risk, Execution, Backtesting) no referencian
   Infrastructure ni ningún host (Api, UI, Worker). Infrastructure implementa abstracciones definidas en el
   dominio, no al revés.
4. Ningún proyecto referencia a un host, salvo su propio proyecto de test (`Omega.Api.Tests` → `Omega.Api`).
5. Ningún proyecto de producción referencia un proyecto de test.
6. El grafo de referencias no tiene ciclos.
7. Todo proyecto de `src/` y `tests/` pertenece a `OMEGA.sln`.
8. Orden del pipeline: MarketData → Features → Strategy → Risk → Execution. Una etapa puede referenciar etapas
   anteriores, nunca posteriores (por ejemplo, MarketData no puede referenciar Strategy ni Execution).
9. `Omega.Application` no referencia Infrastructure ni hosts: trabaja contra abstracciones.
10. `Omega.Infrastructure` no referencia Application ni hosts.
11. Solo `Omega.Infrastructure` referencia paquetes de base de datos (Npgsql, EF Core, Dapper).

Las referencias entre módulos de dominio (por ejemplo, Strategy → Features) se agregarán en la fase que las
necesite, no antes.

## 5. Por qué `Omega.UI` está separado de `Omega.Api`

Son dos hosts distintos con responsabilidades distintas:

* **`Omega.Api` es la frontera del sistema.** Es el único punto por el que algo externo (incluida la UI)
  consulta el estado o emite comandos. Ahí vivirán la validación de comandos, la autorización y la entrada al
  pipeline de aplicación. Si mañana se agrega un script de investigación o una segunda UI, usarán la misma
  frontera y las mismas reglas.
* **`Omega.UI` solo presenta.** Si la UI estuviera dentro de la API, o referenciara los proyectos de dominio,
  un componente visual podría llamar directamente a un servicio de ejecución o de datos y saltarse la frontera.
  La separación hace que esa llamada sea **imposible de compilar**, no solo "desaconsejada": la UI no tiene
  referencias a proyectos, y un test falla si alguien las agrega.
* Separar los hosts también permite desplegar, reiniciar o escalar la UI sin tocar el proceso que en el futuro
  mantendrá conexiones con el exchange.

## 6. Por qué la UI no puede hablar con Binance ni con la capa de ejecución

```text
INCORRECTO:  UI → Binance
INCORRECTO:  UI → Execution Provider

CORRECTO:    UI → API → Application → Risk Engine → Decision Engine → Execution Provider → Binance
```

* **Controles de riesgo.** Toda orden, incluida una manual, debe pasar por el Risk Engine (límites de pérdida,
  exposición, drawdown, spread, slippage, integridad de datos, kill switch). Un camino directo desde la UI sería
  una ruta sin esos controles. Por la misma razón no existirá un botón de "forzar operación".
* **Credenciales.** Las claves privadas de Binance solo pueden existir en procesos de servidor. El navegador
  nunca las recibe; la UI ni siquiera tiene acceso a la configuración que las contiene.
* **Una sola fuente de verdad.** El estado de órdenes, posiciones y datos de mercado lo mantiene el backend. Si
  la UI consultara al exchange por su cuenta podría mostrar un estado distinto al que usa el motor de decisiones.
* **Auditoría.** Pasar por la API permite registrar cada comando con su contexto (quién, cuándo, en qué modo,
  qué decidió el Risk Engine).

## 7. Hosts

* **API**: ASP.NET Core minimal APIs. `/health` usa el sistema de health checks integrado (liveness del
  proceso). `/api/system/status` expone el modo configurado. `/api/market/{symbol}/{interval}/state` expone el
  estado de mercado (400 con entrada inválida, 503 si la base no está disponible, sin detalles internos). Al
  arrancar verifica el esquema de la base, igual que el Worker.
* **UI**: Blazor Web App con renderizado estático en servidor (sin interactividad). El modo interactivo se
  decidirá cuando exista la primera función que lo requiera (ver ADR-006).
* **Worker**: raíz de composición. Al arrancar verifica el esquema de la base (y aplica migraciones si
  `Database:ApplyMigrationsOnStartup` lo permite); si no coincide con el código, termina con código 1.
  Después aloja `MarketDataIngestionService`. Ninguna vela llega todavía a estrategia, riesgo ni ejecución.

## 7.1 Datos de mercado (Fase 1)

Detalle y justificación en [ADR-003](decisions/ADR-003-binance.md).

* Contrato: `IMarketDataStream.ReadEventsAsync` entrega `CandleClosedEvent`, `DataGapDetectedEvent` y
  `ConnectionStatusChangedEvent`. La cancelación termina la secuencia sin excepción.
* Garantías: solo velas cerradas; una sola vez y en orden de apertura, también entre reconexiones; los
  mensajes inválidos se descartan; los huecos se reportan (no se rellenan); reconexión con backoff tras fallos,
  silencio, `serverShutdown` o cerca del límite de 24 h.
* Lo específico de Binance (DTOs, parser, normalizador) es `internal` en `Omega.MarketData.Binance`; el resto
  del sistema solo ve `Candle` y los eventos.
* El transporte está detrás de `IWebSocketTransport`, lo que permite probar reconexiones sin red.
* `ReadEventsAsync` recibe la última vela conocida (la última persistida): no la repite y reporta como hueco lo
  que falte desde ella, también entre reinicios.

## 7.2 Estado de mercado y relleno de huecos (Fase 3)

Detalle y justificación en [ADR-008](decisions/ADR-008-market-state-and-backfill.md).

* `MarketState` (Core): frescura `NO_DATA`/`FRESH`/`STALE`, antigüedad de los datos, huecos en la ventana de
  integridad (24 h) e `IsReliable`. Strategy y Risk deberán tratar `IsReliable = false` como bloqueo.
* Relleno de huecos por REST (`data-api.binance.vision`, `GET /api/v3/klines`) al detectarlos y al arrancar
  (7 días), best effort. La vela en formación que devuelve REST nunca se acepta.
* `CLOCK_SKEW_DETECTED` cuando el reloj local va atrasado respecto al exchange.
* Agregación de velas: no implementada (no requerida todavía).

## 7.3 Persistencia (Fase 2)

Detalle y justificación en [ADR-002](decisions/ADR-002-postgresql.md).

```text
IMarketDataStream ──► MarketDataIngestionService (Omega.Application) ──► ICandleStore / ISystemEventStore (Omega.Core)
                                                                                   ▲ implementan
                                                              PostgresCandleStore / PostgresSystemEventStore (Omega.Infrastructure)
```

* Tablas: `candles` (inmutables, clave `(symbol, interval_code, open_time)`, `numeric` exacto, restricciones
  que replican las invariantes del dominio), `system_events` (append-only, `jsonb`) y `schema_migrations`.
* Migraciones: scripts SQL numerados en `src/Omega.Infrastructure/Persistence/Migrations/Scripts`, embebidos
  y aplicados por `DatabaseMigrator` (lock consultivo, transacción por script, checksum verificado).
* Errores de base de datos → `PersistenceException(IsTransient)`. La ingesta reintenta las escrituras de velas
  transitorias con backoff y se detiene ante errores no transitorios. Los eventos de sistema son best effort.

## 8. Configuración

Secciones previstas por la constitución: `Omega`, `MarketData`, `Trading`, `Risk`, `Database`, `Binance`.
Existen **`Trading`**, **`MarketState`** y **`Database`** (API y Worker) y **`MarketData`** (Worker):

```json
{
  "Trading": { "Mode": "Backtest" },
  "MarketData": {
    "StreamBaseUrl": "wss://data-stream.binance.vision",
    "Symbol": "BTCUSDT",
    "Interval": "FiveMinutes",
    "ConnectTimeout": "00:00:10",
    "ReceiveIdleTimeout": "00:00:30",
    "ReconnectInitialDelay": "00:00:01",
    "ReconnectMaxDelay": "00:01:00",
    "MaxConnectionLifetime": "23:00:00",
    "RestBaseUrl": "https://data-api.binance.vision",
    "RestRequestTimeout": "00:00:15",
    "ClockSkewTolerance": "00:00:02",
    "Backfill": { "Enabled": true, "StartupLookback": "7.00:00:00", "MaxAttempts": 3 }
  },
  "MarketState": {
    "FreshnessGracePeriod": "00:01:00",
    "IntegrityWindow": "1.00:00:00",
    "EvaluationInterval": "00:00:30"
  },
  "Database": {
    "ApplyMigrationsOnStartup": false
  }
}
```

* `Database:ConnectionString` **no** está en ningún `appsettings`: se define con `dotnet user-secrets` (desarrollo)
  o con la variable de entorno `Database__ConnectionString`. `ApplyMigrationsOnStartup` es `true` solo en
  `appsettings.Development.json`.

* `TradingOptions` vive en `Omega.Core` como clase simple, sin dependencia de frameworks.
* El valor por defecto es `Backtest` (valor 0 del enum): una configuración ausente nunca activa un modo que
  toque un exchange.
* API y Worker validan la configuración al arrancar (`ValidateOnStart`); un valor inválido detiene el arranque
  con todos los errores encontrados. `MarketData` exige `wss://`, símbolo en mayúsculas y una vida de conexión
  menor a 24 h.
* `MarketData` nunca contiene credenciales: los streams de mercado son públicos.
* Las demás secciones se agregarán en la fase que las use. No se crean secciones vacías.
* Secretos: `dotnet user-secrets` en desarrollo (el Worker ya tiene `UserSecretsId`), variables de entorno o
  gestor de secretos en otros entornos. Nunca en `appsettings.json` ni en git.

## 9. Cuestiones resueltas

**P-001. ¿Dónde vive la capa de aplicación?** *Resuelta en la Fase 1 (D-015); `Omega.Application` creado en la Fase 2.* El diagrama de la constitución muestra una "Application Layer"
entre la API y los módulos de dominio, pero la estructura de proyectos no incluye un `Omega.Application`. Alguien
tiene que orquestar el pipeline (datos → features → modelo → ... → ejecución) y esa orquestación la usarán tanto
la API como el Worker. Alternativas:

1. Crear `Omega.Application` (referencia los módulos de dominio; Api y Worker lo referencian).
2. Poner la orquestación en `Omega.Worker` y hacer que la API solo lea estado persistido.
3. Poner la orquestación en `Omega.Api` y que el Worker la invoque.

Decisión: opción 1, porque evita duplicar la orquestación entre dos hosts y mantiene los hosts delgados.
`Omega.Application` se creó en la Fase 2, cuando la ingesta empezó a encadenar datos de mercado y persistencia.
