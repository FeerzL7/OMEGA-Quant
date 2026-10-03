# ADR-007: Actualizaciones en tiempo real de la UI

* Estado: Aceptado (confirmado por el propietario antes de implementarlo)
* Fecha: 2026-10-02
* Fase: 13

## Contexto

El roadmap pide que la UI no consulte constantemente cada valor si un stream en tiempo real es más apropiado, y que
SignalR se introduzca solo cuando la primera función en tiempo real lo requiera, "no como decoración arquitectónica"
(roadmap §6). El panel de la Fase 13 muestra datos que cambian a ritmo de velas de 5 minutos: precio de cierre, señal,
riesgo, órdenes y salud.

## Decisión

**Sin hub de SignalR en la API por ahora.** El servidor de la UI (Blazor Interactive Server, ADR-016) consulta la API por
REST cada `Dashboard:RefreshInterval` (5 s por defecto) y el circuito propio de Blazor lleva el cambio al navegador.

## Alternativas consideradas

**Hub de SignalR en `Omega.Api` con notificaciones del Worker.** Latencia de milisegundos, pero exige un canal del Worker a
la API (otro proceso), un hub, reconexión y pruebas de entrega, para datos que cambian cada 5 minutos: complejidad sin
beneficio hoy.

**Server-Sent Events.** Más simple que SignalR, mismo problema: no hay nada que empujar más rápido que lo que la consulta
periódica ya entrega.

## Consecuencias

* Demora máxima ≈ intervalo de refresco + latencia de la API (segundos), aceptable para velas de 5 minutos.
* Carga: unas 10 peticiones por vista abierta cada 5 s; trivial para una herramienta interna de pocos usuarios.
* Se revisará cuando aparezca una función que necesite menos de un segundo (libro de órdenes, llenados de Testnet, alertas
  inmediatas). Entonces se decidirá el mecanismo (probablemente SignalR) con su propia justificación.
