# Registro de decisiones

Decisiones tomadas o propuestas durante el desarrollo. Las decisiones mayores tienen un ADR en
[`decisions/`](decisions/). Estados: **Aceptada** (confirmada por el propietario), **Adoptada en fase N**
(aplicada para poder avanzar, revisable), **Propuesta** (requiere confirmación), **Pendiente** (sin decidir).

| ID     | Fecha      | Decisión | Estado | Referencia |
|--------|------------|----------|--------|------------|
| D-001  | 2026-09-28 | Target framework **.NET 10 (LTS)**. El soporte de .NET 8 termina en noviembre de 2026; .NET 10 tiene soporte hasta noviembre de 2028. | Propuesta (pendiente ADR-001) | — |
| D-002  | 2026-09-28 | **Blazor** para la UI inicial. | Aceptada (roadmap del propietario) | [ADR-006](decisions/ADR-006-blazor-ui.md) |
| D-003  | 2026-09-28 | En la Fase 0 la UI usa **renderizado estático en servidor**. El modo interactivo (Server, WebAssembly o Auto) se decide con la primera función que lo necesite. | Pendiente | [ADR-006](decisions/ADR-006-blazor-ui.md) |
| D-004  | 2026-09-28 | `Omega.UI` **no referencia ningún proyecto**; se comunicará con el sistema solo por HTTP con `Omega.Api`. Reforzado por tests. | Adoptada en Fase 0 | [ARCHITECTURE §4-6](ARCHITECTURE.md) |
| D-005  | 2026-09-28 | Reproducibilidad del build: `global.json` (SDK 10.0.100, `latestFeature`), Central Package Management (`Directory.Packages.props`), `nuget.config` con nuget.org como única fuente, warnings tratados como errores. | Adoptada en Fase 0 | — |
| D-006  | 2026-09-28 | Framework de tests **xUnit 2.9.3** con `Microsoft.NET.Test.Sdk` y `xunit.runner.visualstudio` (versiones de la plantilla oficial del SDK 10). Alternativas: xUnit v3, NUnit, MSTest. | Adoptada en Fase 0 | — |
| D-007  | 2026-09-28 | Las reglas de dependencia se verifican con tests que leen los `.csproj` (`Omega.Integration.Tests/Architecture`), sin paquetes adicionales. | Adoptada en Fase 0 | [ARCHITECTURE §4](ARCHITECTURE.md) |
| D-008  | 2026-09-28 | Valores cero seguros en enums: `TradingMode.Backtest = 0`; `SignalDirection` no define 0, así un valor sin inicializar no se interpreta como `Long`. | Adoptada en Fase 0 | `src/Omega.Core/Trading` |
| D-009  | 2026-09-28 | Formato de solución `.sln` clásico (no `.slnx`) por compatibilidad con más versiones de Visual Studio y otras herramientas. | Adoptada en Fase 0 | — |
| D-010  | 2026-09-28 | Documentación en español; identificadores y comentarios de código en inglés; `CLAUDE.md` y la definición de fases del roadmap conservan el texto original en inglés. | Propuesta | — |
| D-011  | 2026-09-28 | Solo se crea la sección de configuración `Trading`; las demás (`MarketData`, `Risk`, `Database`, `Binance`, `Omega`) se agregan en la fase que las use. | Adoptada en Fase 0 | [ARCHITECTURE §8](ARCHITECTURE.md) |
| P-001  | 2026-09-28 | Ubicación de la capa de aplicación (orquestación del pipeline). | Resuelta por D-015 | [ARCHITECTURE §9](ARCHITECTURE.md) |
| D-012  | 2026-09-28 | Cliente WebSocket propio (`ClientWebSocket` + `System.Text.Json`), sin librerías de terceros para Binance. | Aceptada (delegada por el propietario) | [ADR-003](decisions/ADR-003-binance.md) |
| D-013  | 2026-09-28 | Endpoint por defecto `wss://data-stream.binance.vision` (solo datos de mercado), configurable y restringido a `wss://`. | Aceptada (delegada por el propietario) | [ADR-003](decisions/ADR-003-binance.md) |
| D-014  | 2026-09-28 | Huecos de datos: en la Fase 1 se detectan y reportan (`DataGapDetectedEvent`); el relleno por REST llega con la persistencia. | Aceptada (delegada por el propietario) | [ADR-003](decisions/ADR-003-binance.md) |
| D-015  | 2026-09-28 | Capa de aplicación: nuevo proyecto `Omega.Application` (Api y Worker lo referencian), creado en la primera fase que encadene dos o más módulos. | Aceptada (delegada por el propietario) | [ARCHITECTURE §9](ARCHITECTURE.md) |
| D-016  | 2026-09-28 | `Candle` y `CandleInterval` viven en `Omega.Core` (los consumirán Features, Strategy y Backtesting); los DTOs de Binance son `internal` en `Omega.MarketData`. | Adoptada en Fase 1 | `src/Omega.Core/MarketData` |
| D-017  | 2026-09-28 | Regla de arquitectura: ninguna etapa del pipeline (MarketData → Features → Strategy → Risk → Execution) referencia una etapa posterior. Verificada por test. | Adoptada en Fase 1 | [ARCHITECTURE §4](ARCHITECTURE.md) |
| D-018  | 2026-09-28 | Persistencia con Npgsql 10 y SQL explícito, sin ORM. | Aceptada (delegada por el propietario) | [ADR-002](decisions/ADR-002-postgresql.md) |
| D-019  | 2026-09-28 | Migraciones: scripts SQL numerados y embebidos + migrador propio (lock consultivo, transacción por script, checksum SHA-256 con finales de línea normalizados). Aplicación automática solo si `Database:ApplyMigrationsOnStartup` (true solo en Development). | Aceptada (delegada por el propietario) | [ADR-002](decisions/ADR-002-postgresql.md) |
| D-020  | 2026-09-28 | Se persisten velas cerradas (inmutables, con linaje `source`/`observed_at`) y eventos de sistema; no los mensajes crudos del stream. | Aceptada (delegada por el propietario) | [ADR-002](decisions/ADR-002-postgresql.md) |
| D-021  | 2026-09-28 | Abstracciones de persistencia en `Omega.Core`; errores traducidos a `PersistenceException(IsTransient)`. Escrituras de velas: reintento con backoff si es transitorio, parada si no. Eventos de sistema: best effort. | Adoptada en Fase 2 | [ADR-002](decisions/ADR-002-postgresql.md) |
| D-022  | 2026-09-28 | La ingesta reanuda desde la última vela persistida; los huecos entre reinicios se detectan y registran. El relleno de huecos (REST histórico) queda pendiente. | Adoptada en Fase 2 | [ADR-002](decisions/ADR-002-postgresql.md) |
| D-023  | 2026-09-28 | PostgreSQL de desarrollo con `docker compose` (imagen `postgres:17`, puerto solo en 127.0.0.1, contraseña en `docker/.env` ignorado por git). Tests de persistencia contra PostgreSQL real, omitidos si no se define `OMEGA_TEST_POSTGRES`. | Adoptada en Fase 2 | [README](../README.md) |
| D-024  | 2026-09-28 | `ReconnectBackoff` pasa a `Omega.Core.Resilience.ExponentialBackoff`, compartido por reconexión y reintentos de escritura. | Adoptada en Fase 2 | `src/Omega.Core/Resilience` |
| D-025  | 2026-09-29 | Relleno de huecos por REST (`data-api.binance.vision`) en la Fase 3: al detectarlos y al arrancar (7 días), best effort, respetando 429/418 y `Retry-After`. Desactivable (`MarketData:Backfill:Enabled`). | Aceptada (delegada por el propietario) | [ADR-008](decisions/ADR-008-market-state-and-backfill.md) |
| D-026  | 2026-09-29 | Estado de mercado como evaluación pura en Core. Frescura: siguiente vela no vencida más 60 s de gracia. Integridad: sin huecos en 24 h. `IsReliable` = ambas. | Adoptada en Fase 3 | [ADR-008](decisions/ADR-008-market-state-and-backfill.md) |
| D-027  | 2026-09-29 | Desfase de reloj: se registra `CLOCK_SKEW_DETECTED` y la vela se acepta (el timestamp del exchange manda). Velas REST: solo cerradas hace al menos 2 s. | Adoptada en Fase 3 | [ADR-008](decisions/ADR-008-market-state-and-backfill.md) |
| D-028  | 2026-09-29 | Agregación de velas: no se implementa hasta que una fase requiera otro intervalo. | Adoptada en Fase 3 | [ADR-008](decisions/ADR-008-market-state-and-backfill.md) |
| D-029  | 2026-09-29 | La API accede a PostgreSQL (solo lectura en esta fase) y expone `GET /api/market/{symbol}/{interval}/state`. Composición de persistencia y opciones validadas centralizada en `Omega.Infrastructure`. | Adoptada en Fase 3 | [ARCHITECTURE §7](ARCHITECTURE.md) |
| D-030  | 2026-09-29 | Numeración: ADR-008 para esta fase, porque ADR-004 queda reservado a ML según el roadmap. | Adoptada en Fase 3 | — |
| D-031  | 2026-09-29 | Features sin estado sobre ventana fija (`Lookback` exacto); promedios recursivos inicializados dentro de la ventana (EMA 5·N, Wilder 1 + 11·N). | Aceptada (delegada por el propietario) | [ADR-009](decisions/ADR-009-feature-engine.md) |
| D-032  | 2026-09-29 | Ventana contigua obligatoria; `null` para calentamiento e indefinidos; nunca NaN/infinito. | Adoptada en Fase 4 | [ADR-009](decisions/ADR-009-feature-engine.md) |
| D-033  | 2026-09-29 | Conjunto `features-v1` (16 features) versionado con hash SHA-256 de sus definiciones. Parámetros estándar, no optimizados. | Adoptada en Fase 4 | [FEATURES.md](FEATURES.md) |
| D-034  | 2026-09-29 | Implementación canónica de features en C#; verificada contra una implementación independiente en Python. Los features no se persisten. | Adoptada en Fase 4 | [ADR-009](decisions/ADR-009-feature-engine.md) |
| D-035  | 2026-09-29 | Endpoints `GET /api/features/catalog` y `GET /api/market/{symbol}/{interval}/features/latest` (422 si la ventana tiene un hueco). | Adoptada en Fase 4 | [README](../README.md) |

## ADRs

| ADR | Tema | Estado |
|-----|------|--------|
| ADR-001 | .NET | No redactado (ver D-001) |
| [ADR-002](decisions/ADR-002-postgresql.md) | Persistencia en PostgreSQL | Aceptado |
| [ADR-003](decisions/ADR-003-binance.md) | Datos de mercado de Binance Spot | Aceptado |
| ADR-004 | ML con Python y ONNX | No redactado (Fase 7) |
| ADR-005 | Backtesting | No redactado (Fase 5) |
| [ADR-006](decisions/ADR-006-blazor-ui.md) | UI con Blazor | Aceptado |
| ADR-007 | Actualizaciones en tiempo real de la UI | No redactado (cuando exista la primera función en tiempo real) |
| [ADR-008](decisions/ADR-008-market-state-and-backfill.md) | Estado de mercado, frescura y relleno de huecos | Aceptado |
| [ADR-009](decisions/ADR-009-feature-engine.md) | Motor de features | Aceptado |
