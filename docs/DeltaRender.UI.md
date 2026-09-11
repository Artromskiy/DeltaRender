# DeltaRender.UI

`DeltaRender.UI` packages the runtime UI/text adapters and generated UI/text
shader assemblies. `UiRenderHost` is an obsolete compatibility runner. New
applications own their render session and frame loop, then compose
`TextRenderFeature`, `UiDisplayListResourceRegistry` and
`UiDisplayListGraphFeature` directly.

The package does not contain samples or discover sample files. A consumer owns
its `.dxaml`, generated document artifact, renderer lifetime and frame loop.
The following compatibility factory calls remain available only for migration:

```csharp
using var textFeature = UiRenderHost.CreateTextFeature(session, textService, extent);
using var uiFeature = UiRenderHost.CreateDisplayListFeature(session, extent, textFeature);
```

The obsolete standard host owns the SDL/Vulkan lifetime, XAML loading,
window event pump, resize/DPI propagation, one retained document, graph loop,
watch reload and optional headless readback. A sample supplies only its XAML
source and font registrations:

```csharp
var options = new UiRenderHostOptions("Main.dxaml", "My UI", Width: 1280, Height: 860);
return await UiRenderHost.RunAsync(args, options, fonts);
```

Legacy dynamic samples can use the same host with an application-owned content hook. The
factory runs once during setup; the sample keeps its model and calls its own
document update code before each host layout pass:

```csharp
return await UiRenderHost.RunAsync(
    args,
    new UiRenderHostOptions(string.Empty, "My UI", Width: 1280, Height: 860),
    fonts,
    (textService, _) => new MyRenderContent(textService, fonts));
```

Visual resources are registered by the renderer owner. `RegisterImage` stores
opaque session-owned texture/sampler handles without taking ownership. A
`UiLinearGradientResource` copies two to four validated stops at registration;
its identity is the `UiVisualDraw.Resource` value. The same resource shape
represents radial gradients with `IsRadial`, normalized centers/radii for
`Percent`, or logical/device geometry. Percent radii are resolved independently
against visual width and height before the generated gradient shader runs. The
prepared image and linear/radial-gradient programs consume only generated ABI
packers. Unregistering or clearing a registration does not release session
resources; the session owns their lifetime.

`IUiRenderHostContent` exposes only one `UiDocument`, `AdvanceFrame()` and
neutral `UiInputEvent` delivery. It does not expose Vulkan resources or create
a second retained tree. The host package copies its generated shader assets to
the consumer output through its transitive build integration.

## Shader manifest ownership

`tools/DeltaRender.UIShaders/UiShaderVariants.json` and
`src/DeltaRender.Text/TextShaderVariants.json` are source-owned registrations
for the UI and text provider projects. The build generates the C# program
types and validated shader artifacts into `obj`/`bin`; those outputs are not a
runtime registry. `DeltaShader` validates the manifests against the generated
programs and artifacts during the build. Runtime code uses the generated
program and `VertexAbi`/`FragmentAbi` accessors directly; it does not read JSON
manifests, resolve source paths or probe compiler output.

The command-line host accepts `--headless`, `--frames N`, `--xaml path`,
`--width N`, `--height N`, `--dpi N`, `--watch`, `--profile`, `--readback path`
and `--layout-json path`. The default headless target uses the configured
logical dimensions multiplied by the configured DPI scale; windowed metrics come
from SDL. `DeltaXAML` remains renderer-independent, and the host does not
reference samples or create a second UI tree/property engine.
