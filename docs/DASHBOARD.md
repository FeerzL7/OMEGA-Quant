# Panel de monitoreo (Fase 13)

Decisiones en [ADR-016](decisions/ADR-016-monitoring-dashboard.md) y [ADR-007](decisions/ADR-007-realtime-ui.md).

## Ejecutar

```bash
dotnet run --project src/Omega.Api                      # obligatorio: el panel solo lee la API
Trading__Mode=Paper dotnet run --project src/Omega.Worker  # para ver señal, riesgo y órdenes de paper trading
dotnet run --project src/Omega.UI                       # http://localhost:5090
```

> **Seguridad:** ni la UI ni la API tienen autenticación todavía. Ejecútalas solo en `localhost` (Fase 15).

## Vistas

| Ruta | Contenido |
|------|-----------|
| `/` | Mercado (último cierre, frescura, integridad), gráfico de velas con la posición abierta (entrada, stop, objetivo), señal (dirección, acción, P cruda, P calibrada, EV, motivo), riesgo (equity, P&L del día, drawdown, exposición, kill switch, racha, límites), operaciones y decisiones recientes, órdenes vivas, salud del sistema y eventos. `?session=nombre` elige la sesión de paper. |
| `/paper` | Sesiones de paper trading |
| `/paper/{nombre}` | Resumen, **kill switch** (motivo, autor y confirmación obligatorios), comandos, diario con probabilidades y EV, operaciones, órdenes con su ciclo de vida |
| `/backtests` | Backtests registrados |
| `/backtests/{id}` | Métricas, curvas de equity y drawdown, avisos, operaciones, Monte Carlo a pedido |

## Cómo leerlo

* **Todas las horas son UTC.** Un valor ausente es "—", nunca 0.
* **Frescura de datos:** "al día" hasta un intervalo + 1 minuto después del cierre de la última vela; "retrasado" hasta 3
  intervalos; después, "desactualizado".
* **Salud del sistema:** cada componente muestra cómo se juzgó (pasa el cursor sobre el detalle). La API no ve
  directamente al Worker ni a la conexión con Binance: los infiere de la frescura de las velas y de la última actualización
  de la sesión de paper.
* **Régimen de mercado: "No implementado".** Ningún componente lo calcula todavía; el panel no lo inventa.
* **Si la API no responde**, el indicador superior dice "API no disponible" y cada panel muestra "No disponible": no se
  muestran datos viejos como si fueran actuales.
* El **kill switch** desde la UI solo encola un comando; el Worker lo aplica en su siguiente ciclo y la tabla de comandos
  muestra el resultado.

## Configuración (Omega.UI)

| Clave | Defecto | Significado |
|-------|---------|-------------|
| `Api:BaseUrl` | `http://localhost:5080/` | Dirección de Omega.Api |
| `Dashboard:RefreshInterval` | 5 s | Cada cuánto se consultan los datos |
| `Dashboard:ApiTimeout` | 4 s | Debe ser menor que el refresco |
| `Dashboard:Symbol` / `Interval` | BTCUSDT / 5m | Mercado mostrado |
| `Dashboard:ChartCandles` | 150 | Velas en el gráfico |
| `Dashboard:FreshnessGrace` | 1 min | Tolerancia de frescura |

## Endpoints añadidos en la API

* `GET /api/market/{symbol}/{interval}/candles?limit=150` — últimas velas cerradas, de la más antigua a la más reciente.
* `GET /api/system/health` — base de datos, datos de mercado, Worker de paper y modelo, con estado, detalle y base del juicio.
* `GET /api/system/events?limit=50` — eventos recientes del sistema.

## Contratos

`tests/Contracts/*.json` son respuestas de ejemplo generadas desde los tipos reales de la API. Si un cambio en la API altera
su forma, `Omega.Api.Tests` falla; tras revisar el cambio, regenera con `OMEGA_UPDATE_CONTRACTS=1 dotnet test
tests/Omega.Api.Tests` y comprueba que `Omega.UI.Tests` sigue leyéndolas.
