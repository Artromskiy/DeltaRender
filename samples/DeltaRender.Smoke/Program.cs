using System;
using System.Linq;
using System.Threading.Tasks;
using DVG.Render.Core;
using DVG.Render.Platform.SDL3;
using DVG.Render.Vulkan;

namespace DVG.Render.Smoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var headless = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
        if (headless)
        {
            var probe = VulkanEnvironmentProbe.CheckHeadless();
            Console.WriteLine(probe.Diagnostics.ToText());
            return probe.Usable ? 0 : 1;
        }

        var factory = new Sdl3WindowFactory();
        var result = factory.CreateWindow(new WindowConfiguration("DeltaRender Smoke", 960, 540, true, true));

        if (!result.Success || result.Window is null)
        {
            Console.Error.WriteLine("Window creation failed");
            Console.Error.WriteLine(result.Diagnostics.ToText());
            return 1;
        }

        await using var window = result.Window;

        try
        {
            var renderer = new VulkanRenderer(new VulkanRendererOptions());
            await using var _ = renderer;

            await using var session = renderer.CreateWindowSession(window);
            session.RenderClearFrame(0.1f, 0.12f, 0.2f, 1f);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Renderer initialization failed:");
            Console.Error.WriteLine(ex);
            return 1;
        }

        return 0;
    }
}
