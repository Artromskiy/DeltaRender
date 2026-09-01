using Delta.XAML.Contract;

namespace Delta.Render.Tests;

internal static class UiDisplayListTestFactory
{
    internal static UiDisplayList Create(
        ReadOnlySpan<UiVisualDraw> visuals,
        ReadOnlySpan<UiClipRegion> clips,
        ReadOnlySpan<UiTextDraw> text,
        ReadOnlySpan<UiDrawRef> order,
        ReadOnlySpan<UiElementIdentity> identities = default)
    {
        if (identities.Length == 0 && order.Length != 0)
        {
            var generated = new UiElementIdentity[order.Length];
            for (var index = 0; index < generated.Length; index++)
            {
                generated[index] = new UiElementIdentity((uint)index + 1, 1, 1);
            }

            identities = generated;
        }

        return new UiDisplayList(visuals, clips, text, order, identities);
    }
}
