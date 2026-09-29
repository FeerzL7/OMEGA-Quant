# OMEGA Quant

Plataforma de investigación y ejecución de trading cuantitativo.

El objetivo inicial **no** es un bot que gane dinero de forma autónoma, sino un sistema capaz de responder,
con supuestos explícitos y validación histórica y fuera de muestra, si una hipótesis de trading muestra un
comportamiento estadísticamente significativo **después de costos y restricciones de riesgo**. Ningún modelo,
señal, probabilidad o backtest se asume rentable.

Las reglas de desarrollo del proyecto están en [`CLAUDE.md`](CLAUDE.md).

## Estado

**Fase 0 — Arquitectura y repositorio.** Existe la estructura de la solución, los hosts arrancan y los tests
de arquitectura protegen las dependencias. No hay integración con Binance, base de datos, estrategia, riesgo,
ML ni ejecución. Ver [`docs/DEVELOPMENT_ROADMAP.md`](docs/DEVELOPMENT_ROADMAP.md).

## Requisitos

* .NET SDK 10.0.100 o superior dentro de la banda 10.0.x (fijado en `global.json`, `rollForward: latestFeature`).
* Acceso a nuget.org para restaurar paquetes.

## Comandos

```bash
dotnet restore
dotnet build
dotnet test
```

Ejecutar los hosts (perfil `http` de `launchSettings.json`):

| Host          | Comando                                      | URL                    |
|---------------|----------------------------------------------|------------------------|
| API           | `dotnet run --project src/Omega.Api`         | http://localhost:5080  |
| Panel (UI)    | `dotnet run --project src/Omega.UI`          | http://localhost:5090  |
| Worker        | `dotnet run --project src/Omega.Worker`      | (sin HTTP)             |

Endpoints actuales de la API:

* `GET /health`: liveness del proceso. No verifica Binance, base de datos ni modelos (no existen todavía).
* `GET /api/system/status`: servicio, modo de trading configurado y hora UTC del servidor.

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

* Modo de trading: sección `Trading`, clave `Mode` (`Backtest`, `Paper`, `Testnet`, `Live`).
  Si falta, el valor es `Backtest`. Un valor inválido impide que la API y el Worker arranquen.
  Se puede sobrescribir con variables de entorno, por ejemplo `Trading__Mode=Paper`.
* Los secretos **nunca** se versionan. En desarrollo se usarán `dotnet user-secrets`; en otros entornos,
  variables de entorno o un gestor de secretos. `.gitignore` excluye `.env*`, `secrets.json`,
  `appsettings.*.local.json` y certificados. En la fase 0 no existe ningún secreto.
* El navegador nunca recibirá credenciales de Binance.

## Documentación

* [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
* [`docs/DEVELOPMENT_ROADMAP.md`](docs/DEVELOPMENT_ROADMAP.md)
* [`docs/DECISION_LOG.md`](docs/DECISION_LOG.md)
* [`docs/decisions/`](docs/decisions/)
