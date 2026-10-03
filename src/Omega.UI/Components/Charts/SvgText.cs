using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Omega.UI.Components.Charts;

/// <summary>
/// An SVG &lt;text&gt; element. Written in C# because Razor reserves &lt;text&gt; inside code blocks for its own use.
/// </summary>
public sealed class SvgText : ComponentBase
{
    [Parameter] public double X { get; set; }

    [Parameter] public double Y { get; set; }

    [Parameter] public string? Class { get; set; }

    [Parameter] public string? Anchor { get; set; }

    [Parameter] public string? Value { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.OpenElement(0, "text");
        builder.AddAttribute(1, "x", X.ToString("0.##", CultureInfo.InvariantCulture));
        builder.AddAttribute(2, "y", Y.ToString("0.##", CultureInfo.InvariantCulture));
        if (Class is not null) builder.AddAttribute(3, "class", Class);
        if (Anchor is not null) builder.AddAttribute(4, "text-anchor", Anchor);
        builder.AddContent(5, Value);
        builder.CloseElement();
    }
}
