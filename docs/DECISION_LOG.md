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

## ADRs

| ADR | Tema | Estado |
|-----|------|--------|
| ADR-001 | .NET | No redactado (ver D-001) |
| ADR-002 | PostgreSQL | No redactado (Fase 2) |
| [ADR-003](decisions/ADR-003-binance.md) | Datos de mercado de Binance Spot | Aceptado |
| ADR-004 | ML con Python y ONNX | No redactado (Fase 7) |
| ADR-005 | Backtesting | No redactado (Fase 5) |
| [ADR-006](decisions/ADR-006-blazor-ui.md) | UI con Blazor | Aceptado |
| ADR-007 | Actualizaciones en tiempo real de la UI | No redactado (cuando exista la primera función en tiempo real) |
