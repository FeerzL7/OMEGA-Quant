# OMEGA Quant

Plataforma de investigación y ejecución de trading cuantitativo.

El objetivo inicial **no** es un bot que gane dinero de forma autónoma, sino un sistema capaz de responder,
con supuestos explícitos y validación histórica y fuera de muestra, si una hipótesis de trading muestra un
comportamiento estadísticamente significativo **después de costos y restricciones de riesgo**. Ningún modelo,
señal, probabilidad o backtest se asume rentable.

Las reglas de desarrollo del proyecto están en [`CLAUDE.md`](CLAUDE.md).

## Estado

**Fase 6 — Estrategia base.** Benchmark sin ML (`baseline-ema-trend`) y referencia `buy-and-hold`, evaluables
por la API con costos, significancia estadística y registro de cada ejecución. Protocolo:
[`docs/EVALUATION_PROTOCOL.md`](docs/EVALUATION_PROTOCOL.md).

**Fase 5 — Backtester.** Simulador por eventos sin look-ahead (decisión al cierre, ejecución a la apertura
siguiente), con comisiones, spread, slippage, SL/TP conservadores, tamaño por riesgo, curva de equity, drawdown y
métricas; resultados reproducibles. Reglas: [`docs/BACKTESTING.md`](docs/BACKTESTING.md). Aún no hay estrategia
real (Fase 6).

**Fase 4 — Features.** Sobre la base de la Fase 3, OMEGA calcula 16 features (retornos, SMA/EMA, RSI, ATR,
MACD, volatilidad, volumen, z-score de volumen, distancia a la EMA, ADX) sin mirar al futuro, versionados y
verificados contra una implementación independiente. Catálogo: [`docs/FEATURES.md`](docs/FEATURES.md).

**Fase 3 — Estado de mercado.** El Worker recibe en tiempo real las velas **cerradas** de BTCUSDT 5m desde
Binance Spot (solo datos públicos de mercado), las guarda en PostgreSQL, **rellena los huecos** desde el
histórico REST de Binance y vigila la frescura de los datos. La API expone el estado de mercado (frescura,
huecos, si los datos son confiables). No hay estrategia, riesgo, ML ni ejecución. Ver [`docs/DEVELOPMENT_ROADMAP.md`](docs/DEVELOPMENT_ROADMAP.md).

## Requisitos

* .NET SDK 10.0.100 o superior dentro de la banda 10.0.x (fijado en `global.json`, `rollForward: latestFeature`).
* Acceso a nuget.org para restaurar paquetes.
* PostgreSQL 17 (o compatible) para el Worker y para los tests de persistencia. La forma más simple es Docker:

```bash
cp docker/.env.example docker/.env          # edita la contraseña
docker compose -f docker/docker-compose.yml up -d
```

## Comandos

```bash
dotnet restore
dotnet build
dotnet test
```

Los tests de persistencia usan un PostgreSQL real (cada test crea y borra su propia base temporal). Sin la
variable `OMEGA_TEST_POSTGRES` aparecen como **omitidos**. Para ejecutarlos (PowerShell / bash):

```powershell
$env:OMEGA_TEST_POSTGRES = "Host=localhost;Port=5432;Database=postgres;Username=omega;Password=<tu contraseña>"
dotnet test
```

```bash
OMEGA_TEST_POSTGRES="Host=localhost;Port=5432;Database=postgres;Username=omega;Password=<tu contraseña>" dotnet test
```

Ejecutar los hosts (perfil `http` de `launchSettings.json`):

| Host          | Comando                                      | URL                    |
|---------------|----------------------------------------------|------------------------|
| API           | `dotnet run --project src/Omega.Api`         | http://localhost:5080  |
| Panel (UI)    | `dotnet run --project src/Omega.UI`          | http://localhost:5090  |
| Worker        | `dotnet run --project src/Omega.Worker`      | (sin HTTP; logs en consola) |

Endpoints actuales de la API:

* `GET /health`: liveness del proceso. No verifica Binance, base de datos ni modelos (no existen todavía).
* `GET /api/system/status`: servicio, modo de trading configurado y hora UTC del servidor.
* `GET /api/strategies`: estrategias evaluables (la base y los benchmarks) con su configuración por defecto.
* `POST /api/backtests`: ejecuta y guarda un backtest sobre las velas guardadas. Ejemplo:

```bash
curl -X POST http://localhost:5080/api/backtests -H "Content-Type: application/json" -d '{
  "strategy": "baseline-ema-trend", "symbol": "BTCUSDT", "interval": "5m",
  "fromUtc": "2025-01-01T00:00:00Z", "toUtc": "2025-10-01T00:00:00Z", "periodLabel": "development" }'
```

  Opcionales para estrés de costos: `feeRate`, `spreadBps`, `slippageBps`. La respuesta incluye métricas,
  estadísticas, operaciones, equity diaria y avisos (por ejemplo, si un `holdout` ya fue evaluado).
* `GET /api/backtests` y `GET /api/backtests/{id}`: historial de ejecuciones y detalle.
* `GET /api/features/catalog`: conjunto de features activo (versión, hash y ficha de cada feature).
* `GET /api/market/BTCUSDT/5m/features/latest`: features de la última vela cerrada guardada (`null` mientras se
  calientan; 422 si falta una vela en la ventana necesaria).
* `GET /api/market/BTCUSDT/5m/state`: estado de mercado (última vela cerrada, frescura `FRESH`/`STALE`/`NO_DATA`,
  huecos en las últimas 24 h e `isReliable`). La API también necesita la cadena de conexión:

```bash
dotnet user-secrets set "Database:ConnectionString" "Host=localhost;Port=5432;Database=omega;Username=omega;Password=<tu contraseña>" --project src/Omega.Api
```

### Ejecutar el Worker (ingesta en vivo)

Una sola vez, guarda la cadena de conexión como secreto de desarrollo (nunca en `appsettings`):

```bash
dotnet user-secrets set "Database:ConnectionString" "Host=localhost;Port=5432;Database=omega;Username=omega;Password=<tu contraseña>" --project src/Omega.Worker
dotnet run --project src/Omega.Worker
```

En Development el Worker aplica las migraciones al arrancar. Deberías ver `Applied migration 0001_initial_schema`
(solo la primera vez), `Market-data connection Connected` y, al cerrar cada vela de 5 minutos (hora UTC),
`Candle stored BTCUSDT FiveMinutes ...`. Si hubo un hueco (por ejemplo, el Worker estuvo detenido), verás
`Market-data gap: ...` seguido de `Gap BTCUSDT ...: N missing, N stored`. Para revisar lo guardado:

```sql
SELECT open_time, close_price, base_volume, trade_count, source FROM candles ORDER BY open_time DESC LIMIT 10;
SELECT occurred_at, event_type, severity, message FROM system_events ORDER BY id DESC LIMIT 20;
```

Fuera de Development (`Database:ApplyMigrationsOnStartup=false`), si hay migraciones pendientes el Worker no
arranca y lo indica.

**Histórico para backtests.** Para importar velas cerradas desde una fecha (una sola vez; es idempotente y se
reanuda si se interrumpe):

```bash
dotnet run --project src/Omega.Worker -- --MarketData:Backfill:HistoryStart=2025-01-01T00:00:00Z
```

Un año de velas de 5 m son ~105 000 velas (~106 peticiones REST de peso 2).

## Estructura

```text
OMEGA/
├── CLAUDE.md                  Constitución de desarrollo
├── OMEGA.sln
├── global.json                Versión del SDK
├── nuget.config               Única fuente de paquetes: nuget.org
├── Directory.Build.props      Ajustes comunes (net10.0, nullable, warnings como errores)
├── Directory.Packages.props   Versiones centralizadas de paquetes
├── docs/                      Arquitectura, roadmap, decisiones (ADR)
├── src/                       Proyectos de producción
├── tests/                     Proyectos de test
├── research/                  Investigación (notebooks, experimentos; datasets y modelos no se versionan)
├── scripts/
└── docker/
```

La responsabilidad de cada proyecto y las reglas de dependencia están en
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Configuración y secretos

* Datos de mercado: sección `MarketData` del Worker (endpoint, símbolo, intervalo, tiempos de conexión y
  reconexión). Ver [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) §8. No contiene credenciales.
* Base de datos: sección `Database`. `ConnectionString` solo por user-secrets o `Database__ConnectionString`.
* Modo de trading: sección `Trading`, clave `Mode` (`Backtest`, `Paper`, `Testnet`, `Live`).
  Si falta, el valor es `Backtest`. Un valor inválido impide que la API y el Worker arranquen.
  Se puede sobrescribir con variables de entorno, por ejemplo `Trading__Mode=Paper`.
* Los secretos **nunca** se versionan. En desarrollo se usarán `dotnet user-secrets`; en otros entornos,
  variables de entorno o un gestor de secretos. `.gitignore` excluye `.env*`, `secrets.json`,
  `appsettings.*.local.json` y certificados. El único secreto hasta ahora es la contraseña de PostgreSQL (user-secrets, variable de entorno o `docker/.env`).
* El navegador nunca recibirá credenciales de Binance.

## Documentación

* [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
* [`docs/DEVELOPMENT_ROADMAP.md`](docs/DEVELOPMENT_ROADMAP.md)
* [`docs/DECISION_LOG.md`](docs/DECISION_LOG.md)
* [`docs/FEATURES.md`](docs/FEATURES.md)
* [`docs/BACKTESTING.md`](docs/BACKTESTING.md)
* [`docs/EVALUATION_PROTOCOL.md`](docs/EVALUATION_PROTOCOL.md)
* [`docs/decisions/`](docs/decisions/)
