# Arquitectura de OMEGA Quant

Estado: Fase 13 completada. Este documento describe la estructura que existe hoy y las reglas que deben mantenerse
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
| `Omega.Core`           | Librería            | Entidades, value objects, enums, contratos y reglas de dominio fundamentales.     | `TradingMode`, `SignalDirection`, `Result`/`Error`, `TradingOptions`, `Candle`, `CandleInterval`, `Signal` + `NoTradeReason`, `SystemEvent`, `MarketState` + `MarketStateEvaluator`, contratos de persistencia (`ICandleStore`, `ISystemEventStore`), `ExponentialBackoff` |
| `Omega.MarketData`     | Librería            | Conexión a datos de mercado de Binance, normalización, velas, validación.        | Stream de velas cerradas y fuente REST histórica de Binance Spot (ADR-003, ADR-008) |
| `Omega.Features`       | Librería            | Cálculo y validación de features e indicadores.                                  | `FeatureEngine`, conjunto `features-v1` (16 features, ADR-009) |
| `Omega.Strategy`       | Librería            | Inferencia, probabilidad, calibración, régimen, valor esperado, señales. No define tamaño de posición. | `IStrategy`; base y benchmark (ADR-010); `ProbabilityCalibrator` (ADR-011); `ModelPackage` (ONNX), EV y `model-ev` (ADR-012) |
| `Omega.Risk`           | Librería            | Límites, tamaño de posición, exposición, drawdown, rechazo de operaciones.       | `RiskManager`, `RiskLimits`, `KillSwitch` (ADR-013) |
| `Omega.Execution`      | Librería            | Órdenes, ciclo de vida, proveedores de ejecución, filtros del exchange.          | `Order`, `CandleFillModel`, `IExecutionProvider`, `PaperExecutionProvider`, contrato de persistencia del paper (ADR-015) |
| `Omega.Backtesting`    | Librería            | Simulación histórica, costos, métricas, curva de equity.                         | `BacktestEngine`, costos, métricas, estadísticas, `IBacktestRunStore`, `TripleBarrierLabeler`, Monte Carlo (ADR-005, ADR-010, ADR-004, ADR-014) |
| `Omega.Application`    | Librería            | Orquestación del pipeline; usa módulos de dominio y abstracciones de Core.        | Ingesta, relleno de huecos, estado de mercado, monitor de frescura, `FeatureService`, `BacktestService`, `DatasetBuilder` |
| `Omega.Infrastructure` | Librería            | PostgreSQL, repositorios, integraciones externas, persistencia.                  | Npgsql: migrador, repositorios de velas, eventos y backtests (ADR-002); composición compartida |
| `Omega.Api`            | Host ASP.NET Core   | Frontera HTTP del sistema: consultas y comandos controlados.                     | `/health`, `/api/system/status`, `/api/market/{symbol}/{interval}/state`, `/api/features/catalog`, `/api/market/{symbol}/{interval}/features/latest`, `/api/strategies`, `/api/backtests`, `/api/datasets`, `/api/models`, `/api/risk/limits` |
| `Omega.UI`             | Host Blazor         | Presentación e interacción. Sin lógica de trading.                               | Panel de monitoreo (Interactive Server, ADR-016): vistas de mercado, señal, riesgo, paper, backtests y salud |
| `Omega.Worker`         | Host de servicio    | Procesos en segundo plano (datos, features, estrategia, paper trading, monitoreo). | Aplica migraciones; ingesta y monitor de frescura; en modo Paper, `PaperTradingWorker`; rechaza Testnet/Live |

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

Omega.Infrastructure ──► Omega.Backtesting, Omega.Execution (implementa IBacktestRunStore, IPaperTradingStore) (+ Options.ConfigurationExtensions)
Omega.Strategy ─────► Omega.Features, Omega.Core
Omega.Backtesting ──► Omega.Strategy, Omega.Features, Omega.Risk, Omega.Execution, Omega.Core
Omega.Application ──► Omega.MarketData, Omega.Features, Omega.Strategy, Omega.Backtesting, Omega.Risk, Omega.Execution, Omega.Core
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
* **UI**: Blazor Web App en modo **Interactive Server** (ADR-016). Lee el sistema solo por HTTP desde Omega.Api
  (`OmegaApiClient`) y refresca cada 5 s (ADR-007); sin referencias a proyectos.
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

## 7.2 Panel de monitoreo (Fase 13)

Decisiones en [ADR-016](decisions/ADR-016-monitoring-dashboard.md) y [ADR-007](decisions/ADR-007-realtime-ui.md);
guía en [DASHBOARD.md](DASHBOARD.md).

* `Omega.UI`: Interactive Server; `OmegaApiClient` (nunca lanza excepciones a los componentes), DTOs propios,
  `LivePanel` (refresco periódico), gráficos SVG (`Presentation.Charts`), vistas `/`, `/paper`, `/backtests`.
* `Omega.Api`: velas recientes, salud del sistema (`SystemHealthService` en Application) y eventos.
* Contratos API ↔ UI verificados con muestras JSON generadas desde los tipos reales (`tests/Contracts`).

## 7.3 Paper trading (Fase 12)

Decisiones en [ADR-015](decisions/ADR-015-paper-trading.md); guía en [PAPER_TRADING.md](PAPER_TRADING.md).

* `Omega.Execution`: ciclo de vida de órdenes, `CandleFillModel` (compartido con el backtester), `IExecutionProvider` y
  `PaperExecutionProvider`; contrato `IPaperTradingStore`.
* `PaperTradingEngine` (Application): mismo orden de eventos por vela que el backtester, una transacción por vela, cursor
  persistido, comandos del kill switch. Paridad exacta con el backtester verificada por test.
* `PaperTradingWorker` (Worker, solo en modo Paper) y `/api/paper/...` (consultas y comando del kill switch).
* Migración 0003: sesiones, órdenes y eventos, operaciones, diario y comandos.

## 7.4 Monte Carlo (Fase 11)

Decisiones en [ADR-014](decisions/ADR-014-monte-carlo.md); guía en [MONTE_CARLO.md](MONTE_CARLO.md).

* `Omega.Backtesting.MonteCarlo`: remuestreo de las operaciones de un backtest (bootstrap, bloques, permutación),
  robustez por costos y operaciones perdidas, distribuciones, ruina y probabilidad del kill switch. Determinista.
* `MonteCarloService` (Application) y `POST /api/backtests/{id}/monte-carlo`. Solo lectura; no se persiste.

## 7.5 Risk Engine (Fase 10)

Decisiones en [ADR-013](decisions/ADR-013-risk-engine.md); modelo en [RISK_MODEL.md](RISK_MODEL.md).

* `Omega.Risk` (solo referencia `Omega.Core`): `RiskLimits`, `RiskManager` (controles de entrada, tamaño, estado
  diario, racha), `KillSwitch` (automático por drawdown, manual, sin rearme automático).
* `Omega.Backtesting` → `Omega.Risk`: cada entrada simulada se aprueba y dimensiona ahí; las salidas no se bloquean.
* Política en la sección `Risk` de la configuración de la API (validada al arrancar), `GET /api/risk/limits` y
  cambios por ejecución en `POST /api/backtests`.

## 7.6 Valor esperado (Fase 9)

Decisiones en [ADR-012](decisions/ADR-012-expected-value.md).

* `Omega.Strategy.Models`: `ModelPackage` (ONNX Runtime 1.30.0, hash verificado, calibración y perfil de resultados),
  `ExpectedValueCalculator` y `ModelExpectedValueStrategy` (largo solo si EV > umbral, con probabilidad calibrada).
  `Omega.Strategy` es el único proyecto con el paquete de ONNX Runtime; la UI lo tiene prohibido por test.
* `TradingCosts` (Core): los mismos costos para decidir (EV) y para simular (backtest).
* `BacktestService` acepta `model-ev` + `modelId`, usa las barreras y el horizonte del modelo, cuenta las evaluaciones
  por modelo y advierte del solapamiento con el periodo de entrenamiento.

## 7.7 Calibración (Fase 8)

Decisiones en [ADR-011](decisions/ADR-011-probability-calibration.md).

* `research/ml/omega_ml/calibration.py`: Platt, isotónica o ninguno, elegido por log loss en un walk-forward
  anidado sobre predicciones fuera de muestra; análisis de fiabilidad; `calibration.json` junto al modelo ONNX.
* `ProbabilityCalibrator` (`Omega.Strategy.Calibration`) aplica el calibrador en C#, con paridad verificada contra
  Python. Salida siempre en [0.001, 0.999].

## 7.8 Machine learning (Fase 7)

Decisiones en [ADR-004](decisions/ADR-004-ml-python-onnx.md); guía en [MACHINE_LEARNING.md](MACHINE_LEARNING.md).

* C# es la única implementación de features y etiquetas. `DatasetBuilder` (Application) exporta
  `omega-dataset-v1` (CSV + manifiesto con hash) por `POST /api/datasets`; `TripleBarrierLabeler` (Backtesting)
  usa las reglas del backtester.
* `research/ml/omega_ml` (Python, fuera de la solución .NET): verificación del dataset, walk-forward con purging,
  modelos (LR → RF → LightGBM), métricas contra referencia y contra la regla base, registro de experimentos y
  exportación ONNX con paridad exacta.
* La inferencia ONNX en C# llegará cuando un modelo participe en decisiones (Fases 9 y 12).

## 7.9 Estrategia base y evaluación (Fase 6)

Decisiones en [ADR-010](decisions/ADR-010-baseline-and-evaluation.md); protocolo en [EVALUATION_PROTOCOL.md](EVALUATION_PROTOCOL.md).

* `StrategyCatalog` (Application): `baseline-ema-trend` (candidata) y `buy-and-hold` (benchmark), cada una con su
  configuración por defecto.
* `BacktestService`: carga el calentamiento antes del periodo, ejecuta, guarda en `backtest_runs` y avisa si un
  `holdout` ya fue evaluado.
* API: `POST /api/backtests`, `GET /api/backtests`, `GET /api/backtests/{id}`, `GET /api/strategies`. Solo lee
  velas y escribe el registro del experimento; no toca el exchange.

## 7.10 Backtesting (Fase 5)

Reglas en [BACKTESTING.md](BACKTESTING.md); decisiones en [ADR-005](decisions/ADR-005-backtesting.md).

* `IStrategy` (Strategy) recibe solo la vela que cerró, sus features y la posición; devuelve una `Signal` (Core).
* `BacktestEngine`: decisión al cierre, ejecución a la apertura siguiente; SL/TP intravela conservadores; costos
  de comisión, spread y slippage; Spot solo largo; resultado reproducible (hash de dataset y de features).
* El modelo de fills se extraerá detrás de `IExecutionProvider` en la Fase 12.
* Carga histórica opcional al arrancar el Worker (`MarketData:Backfill:HistoryStart`).

## 7.11 Features (Fase 4)

Detalle en [ADR-009](decisions/ADR-009-feature-engine.md) y catálogo en [FEATURES.md](FEATURES.md).

* `FeatureEngine` calcula un `FeatureSet` versionado (`features-v1`, hash SHA-256) sobre velas cerradas. Cada
  feature usa exactamente sus `Lookback` velas terminando en *t* (sin estado, idéntico en backtest y en vivo).
* Ventana contigua obligatoria; `null` durante el calentamiento o si el valor no está definido.
* `FeatureVector.AvailableAtUtc` = cierre de la vela *t*: usarlo antes es look-ahead.
* `ComputeSeries` produce un vector por vela para backtests y datasets (un hueco reinicia el calentamiento).
* Los features no se persisten: se recalculan desde las velas.

## 7.12 Estado de mercado y relleno de huecos (Fase 3)

Detalle y justificación en [ADR-008](decisions/ADR-008-market-state-and-backfill.md).

* `MarketState` (Core): frescura `NO_DATA`/`FRESH`/`STALE`, antigüedad de los datos, huecos en la ventana de
  integridad (24 h) e `IsReliable`. Strategy y Risk deberán tratar `IsReliable = false` como bloqueo.
* Relleno de huecos por REST (`data-api.binance.vision`, `GET /api/v3/klines`) al detectarlos y al arrancar
  (7 días), best effort. La vela en formación que devuelve REST nunca se acepta.
* `CLOCK_SKEW_DETECTED` cuando el reloj local va atrasado respecto al exchange.
* Agregación de velas: no implementada (no requerida todavía).

## 7.13 Persistencia (Fase 2)

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
