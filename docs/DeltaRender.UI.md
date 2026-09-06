# DeltaRender.UI

`DeltaRender.UI` is the reusable host bundle for DeltaXAML applications. It
brings the published DeltaXAML, DeltaRender, Vulkan/SDL3, DeltaText and
shader-contract dependencies together, and packages the runtime UI/text
adapters plus the generated UI/text shader assemblies.

The package does not contain samples or discover sample files. A consumer owns
its `.dxaml` and generated document artifact, then uses `UiRenderHost` to create
the matching text and display-list features:

```csharp
using var textFeature = UiRenderHost.CreateTextFeature(session, textService, extent);
using var uiFeature = UiRenderHost.CreateDisplayListFeature(session, extent, textFeature);
```

For the standard path, the host also owns the SDL/Vulkan lifetime, XAML loading,
window event pump, resize/DPI propagation, one retained document, graph loop,
watch reload and optional headless readback. A sample supplies only its XAML
source and font registrations:

```csharp
var options = new UiRenderHostOptions("Main.dxaml", "My UI", Width: 1280, Height: 860);
return await UiRenderHost.RunAsync(args, options, fonts);
```

Dynamic samples use the same host with an application-owned content hook. The
factory runs once during setup; the sample keeps its model and calls its own
document update code before each host layout pass:

```csharp
return await UiRenderHost.RunAsync(
    args,
    new UiRenderHostOptions(string.Empty, "My UI", Width: 1280, Height: 860),
    fonts,
    (textService, _) => new MyRenderContent(textService, fonts));
```

`IUiRenderHostContent` exposes only one `UiDocument`, `AdvanceFrame()` and
neutral `UiInputEvent` delivery. It does not expose Vulkan resources or create
a second retained tree. The host package copies its generated shader assets to
the consumer output through its transitive build integration.

The command-line host accepts `--headless`, `--frames N`, `--xaml path`,
`--width N`, `--height N`, `--dpi N`, `--watch`, `--profile`, `--readback path`
and `--layout-json path`. The default headless target uses the configured
logical dimensions multiplied by the configured DPI scale; windowed metrics come
from SDL. `DeltaXAML` remains renderer-independent, and the host does not
reference samples or create a second UI tree/property engine.
