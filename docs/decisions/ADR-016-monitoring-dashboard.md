# ADR-016: Panel de monitoreo

* Estado: Aceptado (diseño presentado y confirmado por el propietario antes de implementarlo)
* Fecha: 2026-10-02
* Fase: 13

## Contexto

La Fase 13 implementa el primer panel completo: precio, velas, señal, probabilidad, EV, régimen, riesgo, posiciones,
órdenes, salud del sistema y logs. La UI está aislada del dominio (no referencia proyectos, ADR-006) y todo pasa por la API.
Quedaban pendientes desde la Fase 0 el modo interactivo de Blazor (ADR-006) y el tiempo real (ADR-007).

## Decisiones

1. **Blazor Interactive Server** (cierra la decisión pendiente de ADR-006): herramienta interna de pocos usuarios, el código
   de la UI no se envía al navegador, y el servidor de la UI es el único que habla con la API.
2. **Refresco por consulta a la API cada 5 s** (ADR-007); sin hub de SignalR mientras nada requiera menos de un segundo.
3. **Gráficos SVG generados por componentes Blazor**, con la geometría en C# puro y probada: sin librerías JavaScript ni CDN.
   El eje de precios usa el rango de los datos más 5 % y lo dice; los niveles de una posición (entrada, stop, objetivo)
   amplían el rango para ser siempre visibles.
4. **Contratos verificados:** la UI define sus propios DTOs; `Omega.Api.Tests` genera muestras JSON desde los tipos reales de
   la API con sus opciones de serialización (`tests/Contracts`), y `Omega.UI.Tests` exige poder leerlas. Un cambio de forma
   en la API rompe un test, no un panel.
5. **Endpoints nuevos de solo lectura:** velas recientes, salud del sistema (base de datos, datos de mercado, Worker de paper,
   modelo; cada juicio indica en qué se basa) y eventos del sistema.
6. **Honestidad de datos (§25):** el cliente de la API nunca lanza excepciones a los componentes (devuelve "disponible",
   "no encontrado", "rechazado" o "no disponible"); un valor ausente se muestra como "—", nunca como 0; cada vista muestra
   cuándo se actualizó y la frescura de las velas; si la API no responde, se dice y no se muestran números viejos; el régimen
   de mercado se muestra como "No implementado".
7. **El kill switch desde la UI** solo envía el comando a la API (motivo, autor y confirmación explícita obligatorios); lo
   aplica el Worker.
8. **Pruebas sin bUnit** (no disponible): lógica de presentación y cliente en `Omega.UI.Tests`; páginas verificadas de
   extremo a extremo pidiendo el HTML prerenderizado a la UI en marcha con API, Worker y PostgreSQL reales.

## Alternativas consideradas

**Interactive WebAssembly.** Descarga mayor, código de la UI visible en el cliente y CORS hacia la API; sin ventaja para pocos
usuarios internos.

**Librería de gráficos JavaScript (por ejemplo, de velas financieras) vía interop.** Más interacción (zoom, cursor), a cambio
de una dependencia externa, interop y pruebas más difíciles. Se reconsiderará si el análisis visual lo exige.

**Proyecto de contratos compartido entre API y UI.** Eliminaría la duplicación de DTOs, pero daría a la UI una referencia de
proyecto y acoplaría su compilación a la de la API; los tests de contrato dan la misma garantía sin ese acoplamiento.

## Consecuencias

* La UI necesita la API en marcha; sin ella, cada panel dice "No disponible".
* El registro interno de `HttpClient` queda en `Warning` (evita dos líneas por petición cada 5 s); el cliente registra una
  advertencia concisa cuando la API no responde.
* **La UI no tiene autenticación** (como la API): debe correr solo en `localhost` hasta la Fase 15.
* En el sandbox la interactividad en vivo (refresco y clics) no pudo verificarse con un navegador: la compilación del sandbox
  no incluye `blazor.web.js`. Sí se verificó el HTML prerenderizado con datos reales y la lógica de cada pieza por separado.
