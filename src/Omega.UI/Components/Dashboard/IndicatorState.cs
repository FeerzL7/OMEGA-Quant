namespace Omega.UI.Components.Dashboard;

/// <summary>
/// Visual state of a status indicator. Presentation-only; it is not a domain
/// concept. <see cref="NotAvailable"/> is the zero value so an indicator never
/// looks healthy unless it has been explicitly told so.
/// </summary>
public enum IndicatorState
{
    NotAvailable = 0,
    Healthy = 1,
    Degraded = 2,
    Down = 3,
}
