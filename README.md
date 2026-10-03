# OMEGA Quant

Plataforma de investigación y ejecución de trading cuantitativo.

El objetivo inicial **no** es un bot que gane dinero de forma autónoma, sino un sistema capaz de responder,
con supuestos explícitos y validación histórica y fuera de muestra, si una hipótesis de trading muestra un
comportamiento estadísticamente significativo **después de costos y restricciones de riesgo**. Ningún modelo,
señal, probabilidad o backtest se asume rentable.

Las reglas de desarrollo del proyecto están en [`CLAUDE.md`](CLAUDE.md).

## Estado

**Próximo paso: verificar la estabilidad del paper trading con datos reales** ([`docs/PAPER_STABILITY.md`](docs/PAPER_STABILITY.md)).
La Fase 14 (ejecución en Binance Demo Mode / Testnet) está diseñada
([ADR-017](docs/decisions/ADR-017-exchange-execution-demo-testnet.md)) pero no se implementará hasta superar esa puerta.

**Fase 13 — Panel de monitoreo.** Panel en Blazor (Interactive Server) con mercado, velas, señal, probabilidades, EV,
riesgo, posiciones, órdenes, salud del sistema, eventos, sesiones de paper (con kill switch) y backtests. Lee el sistema
solo a través de la API. Ver [`docs/DASHBOARD.md`](docs/DASHBOARD.md).

**Fase 12 — Paper trading.** El Worker en modo `Paper` ejecuta el pipeline completo sobre cada vela cerrada (features →
estrategia → riesgo → decisión → ejecución simulada → diario), con las mismas reglas de llenado que el backtester,
estado persistido y kill switch operable desde la API. Ver [`docs/PAPER_TRADING.md`](docs/PAPER_TRADING.md).

**Fase 11 — Monte Carlo.** Análisis de escenarios sobre las operaciones de un backtest (bootstrap, bloques,
permutación, estrés de costos): rango de resultados, drawdown, rachas, ruina y probabilidad de que salte el kill
switch. No es una predicción. Ver [`docs/MONTE_CARLO.md`](docs/MONTE_CARLO.md).

**Fase 10 — Risk Engine.** Un motor de riesgo independiente del modelo aprueba y dimensiona cada entrada (riesgo
por operación, exposición, pérdida diaria, drawdown, racha, spread, slippage, integridad de datos) y tiene un kill
switch que solo una persona puede rearmar. Ver [`docs/RISK_MODEL.md`](docs/RISK_MODEL.md).

**Fase 9 — Valor esperado.** Los modelos se ejecutan en C# (ONNX), su probabilidad calibrada se convierte en valor
esperado después de costos y la estrategia `model-ev` solo entra con EV positivo. Se compara en backtest contra la
estrategia base y buy & hold con el mismo protocolo.

**Fase 8 — Calibración.** Calibración de probabilidades (Platt, isotónica o ninguna) elegida por su efecto
fuera de muestra, análisis de fiabilidad y calibrador exportado junto al modelo para que C# lo aplique.

**Fase 7 — Machine learning.** Datasets con features y etiquetas triple barrera calculados por el propio
sistema (`POST /api/datasets`) y pipeline de investigación en Python (`research/ml`): validación cronológica con
purging, regresión logística, Random Forest y LightGBM, comparación con la estrategia base y exportación ONNX.
Guía: [`docs/MACHINE_LEARNING.md`](docs/MACHINE_LEARNING.md).

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
* `POST /api/backtests/{id}/monte-carlo`: escenarios sobre las operaciones de ese backtest (ver
  [`docs/MONTE_CARLO.md`](docs/MONTE_CARLO.md)).
* `GET /api/paper/sessions` (y `/{name}`, `/trades`, `/decisions`, `/orders`, `/commands`): sesiones de paper trading.
  `POST /api/paper/sessions/{name}/kill-switch` activa o rearma el kill switch (motivo y autor obligatorios).
  **La API no tiene autenticación: exponla solo en `localhost`.**
* `GET /api/market/{symbol}/{interval}/candles`, `GET /api/system/health`, `GET /api/system/events`: datos del panel.
* `GET /api/risk/limits`: política de riesgo vigente (sección `Risk` de la configuración). En
  `POST /api/backtests`, `"risk": { "maxDailyLoss": 0.01, ... }` cambia límites solo para esa ejecución.
* `GET /api/models`: modelos registrados y si están listos para el valor esperado (ONNX, calibración y perfil).
* `POST /api/datasets`: exporta un dataset de investigación (features + etiquetas) a `research/datasets/<id>/`.
  Ver [`docs/MACHINE_LEARNING.md`](docs/MACHINE_LEARNING.md).
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
  En el Worker: `Backtest` solo ingiere datos; `Paper` añade el paper trading (sección `Paper`);
  `Testnet` y `Live` hacen que se niegue a arrancar, porque su ejecución aún no existe (fases 14-16).
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
* [`docs/MACHINE_LEARNING.md`](docs/MACHINE_LEARNING.md)
* [`docs/RISK_MODEL.md`](docs/RISK_MODEL.md)
* [`docs/MONTE_CARLO.md`](docs/MONTE_CARLO.md)
* [`docs/PAPER_TRADING.md`](docs/PAPER_TRADING.md)
* [`docs/DASHBOARD.md`](docs/DASHBOARD.md)
* [`docs/PAPER_STABILITY.md`](docs/PAPER_STABILITY.md)
* [`docs/decisions/`](docs/decisions/)
