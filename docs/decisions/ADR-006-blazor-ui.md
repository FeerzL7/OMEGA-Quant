# ADR-006: Blazor para la interfaz visual

* Estado: Aceptado
* Fecha: 2026-09-28
* Fase: 0

## Contexto

OMEGA necesita un panel visual para:

* Monitoreo de mercado (precio, velas, volumen, spread, régimen).
* Señales (dirección, probabilidad cruda y calibrada, valor esperado, versión del modelo).
* Riesgo (equity, P&L diario, drawdown, exposición, límites, rechazos, kill switch).
* Posiciones y órdenes.
* Resultados de backtests.
* Salud del sistema (conexión con el exchange, base de datos, modelo, worker, errores).

Es una herramienta **interna**: pocos usuarios (inicialmente el propietario), alta densidad de información,
prioridad en claridad de estado sobre estética. El backend es C#/.NET con ASP.NET Core.

La UI no puede contener lógica de trading ni acceder a Binance, PostgreSQL, modelos, credenciales o
proveedores de ejecución. Todo pasa por `Omega.Api` (ver [ARCHITECTURE.md](../ARCHITECTURE.md)).

## Decisión

Usar **Blazor** (Blazor Web App de ASP.NET Core) para la UI inicial, en un proyecto separado (`Omega.UI`) que
no referencia proyectos de dominio ni de infraestructura.

En la Fase 0 la aplicación usa **renderizado estático en servidor**, sin modo interactivo, porque solo muestra
un placeholder. La elección del modo interactivo queda **pendiente** (ver abajo).

## Razones

* **Consistencia de ecosistema.** Todo el sistema de producción queda en C#/.NET: un solo lenguaje, un solo
  SDK, un solo sistema de build y de paquetes, las mismas herramientas de depuración y análisis.
* **Integración con ASP.NET Core.** Blazor es parte de ASP.NET Core: hosting, configuración, logging,
  inyección de dependencias y autenticación funcionan igual que en `Omega.Api`.
* **Menos fragmentación tecnológica.** No se agrega Node.js, npm, un bundler ni un segundo gestor de
  dependencias con su propio ciclo de actualizaciones de seguridad.
* **Reuso de conocimiento .NET.** El proyecto lo desarrolla una sola persona con base en C#; aprender y mantener
  un segundo stack (TypeScript + framework) tiene un costo real que no aporta a la investigación cuantitativa.
* **Adecuado para un panel interno.** El caso de uso (pocos usuarios, red controlada, tablas, indicadores y
  gráficos) no necesita las ventajas en las que un framework de JavaScript es claramente superior (SEO,
  audiencias masivas, ecosistema enorme de componentes de consumo).

## Alternativas consideradas

**React (o similar, con TypeScript).** Ecosistema de componentes y librerías de gráficos financieros más grande
y maduro que el de Blazor, y más desarrolladores disponibles. No se elige ahora porque introduce un segundo
lenguaje, un segundo toolchain y contratos duplicados entre C# y TypeScript, para un panel interno de un solo
desarrollador. Sería la opción razonable si el panel se convierte en un producto para muchos usuarios o si una
librería de gráficos de JavaScript resulta indispensable (aunque Blazor puede integrar librerías JS mediante
interop).

**Angular.** Framework completo y estructurado, apropiado para equipos grandes. Mismos costos de segundo stack
que React, con más ceremonia; no aporta ventajas específicas para este caso.

**WPF (escritorio).** Buen rendimiento y controles densos, adecuado para estaciones de trading de escritorio.
No se elige porque solo funciona en Windows, no permite acceder al panel desde otro equipo o el celular sin
herramientas adicionales, y encaja peor con una arquitectura donde la API es la frontera del sistema. También
tiende a mezclar la lógica de la aplicación con la presentación si no se cuida.

Blazor no es superior de forma universal: su ecosistema de componentes es más pequeño, el modo WebAssembly
tiene una descarga inicial mayor, y el modo Server depende de una conexión persistente. Se elige porque su
balance de costos es el mejor **para este proyecto en este momento**.

## Consecuencias

* La UI se escribe en Razor/C# y se construye con el mismo `dotnet build`.
* La separación UI/API se mantiene por referencias de proyecto (la UI no tiene ninguna) y se verifica con tests;
  no depende del modo de renderizado elegido.
* Los gráficos financieros (velas, curvas de equity) probablemente requerirán una librería JavaScript vía
  interop o un componente Blazor de terceros. Se evaluará en la Fase 13; cualquier dependencia nueva requiere
  justificación.
* Si en el futuro se cambia de tecnología de UI, la API no cambia: por eso la UI no comparte código con el
  dominio.

## Decisión pendiente: modo de renderizado interactivo

> **Resuelta en la Fase 13:** Interactive Server, con refresco por consulta a la API. Ver
> [ADR-016](ADR-016-monitoring-dashboard.md) y [ADR-007](ADR-007-realtime-ui.md).

Se tomará cuando aparezca la primera función que requiera interactividad o actualizaciones en vivo (a más
tardar en la Fase 12 o 13), junto con ADR-007 (tiempo real).

| Modo | A favor | En contra |
|------|---------|-----------|
| Interactive Server | Sin descarga de runtime al navegador; el código de la UI no se envía al cliente; natural para pocos usuarios internos. | Requiere conexión persistente (circuito SignalR interno de Blazor) por usuario; si se cae, la UI se congela hasta reconectar. |
| Interactive WebAssembly | Funciona en el navegador; el servidor de la UI solo entrega archivos. | Descarga inicial mayor; todo el código de la UI es visible para el cliente; requiere CORS o un proxy hacia la API. |
| Auto | Combina ambos. | Más complejidad; no se justifica para un panel interno. |

Recomendación preliminar: **Interactive Server**, por ser una herramienta interna de pocos usuarios. Nota: el
circuito SignalR que usa Blazor Server es un detalle interno del framework y es distinto de la decisión de usar
SignalR (u otro mecanismo) para enviar datos en tiempo real desde el backend, que corresponde a ADR-007.
