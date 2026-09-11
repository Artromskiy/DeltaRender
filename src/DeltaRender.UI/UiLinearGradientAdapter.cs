using Delta;
using Delta.Render.XAML;
using Delta.XAML;
using Delta.XAML.Contract;

namespace Delta.Render.UI;

public static class UiLinearGradientAdapter
{
    public static void RegisterLinearGradient(
        UiDisplayListResourceRegistry registry,
        UiResourceId resourceId,
        UiLinearGradient gradient)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var sourceStops = gradient.Stops.Span;
        var stops = new UiLinearGradientStop[sourceStops.Length];
        for (var index = 0; index < stops.Length; index++)
        {
            var stop = sourceStops[index];
            stops[index] = new(stop.Offset, new float4(
                stop.Color.R / 255f, stop.Color.G / 255f,
                stop.Color.B / 255f, stop.Color.A / 255f));
        }

        registry.RegisterLinearGradient(new UiLinearGradientResource(
            resourceId,
            new float2(gradient.StartX, gradient.StartY),
            new float2(gradient.EndX, gradient.EndY),
            PaintUnits.Logical,
            stops)
        {
            IsRelativeToBounds = gradient.IsRelativeToBounds,
            AngleDegrees = gradient.AngleDegrees,
            OutlineColor = new float4(
                gradient.OutlineColor.R / 255f,
                gradient.OutlineColor.G / 255f,
                gradient.OutlineColor.B / 255f,
                gradient.OutlineColor.A / 255f),
            OutlineWidth = gradient.OutlineWidth,
        });
    }
}
