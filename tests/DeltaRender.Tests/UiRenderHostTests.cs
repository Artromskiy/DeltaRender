using Delta;
using Delta.Render.Text;
using Delta.Render.UI;
using Delta.Text.Contract;
using Delta.XAML;
using Delta.XAML.Contract;
using Xunit;

namespace Delta.Render.Tests;

public sealed class UiRenderHostTests
{
    [Fact]
    public void ResourceRegistryRegistersAnalyticTextOuterShadow()
    {
        var catalog = new UiResourceCatalog();
        var effectSet = catalog.RegisterEffects(
            new UiResourceId(Guid.NewGuid()),
            new UiTextEffects
            {
                OuterShadow = new(new(20, 30, 40), 4, new float2(1, 2)),
            });

#pragma warning disable CS0618 // The compatibility host still owns catalog-to-renderer registration.
        var registry = UiRenderHost.CreateResourceRegistry(catalog);
#pragma warning restore CS0618

        Assert.True(registry.TryResolveTextEffectSet(effectSet, out var variant, out var resource));
        Assert.Equal(GlyphImageMode.Sdf, variant.Mode);
        Assert.Equal(TextShaderPath.OuterShadow, variant.Path);
        Assert.Equal(effectSet, resource.Set);
    }
}
