using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Delta;
using Delta.Render.Platform.SDL3;
using Delta.Render.RenderGraph;
using Delta.Render.Text;
using Delta.Render.Vulkan;
using Delta.Render.XAML;
using Delta.Shader.Contract;
using Delta.Text;
using Delta.Text.Contract;
using Delta.XAML;
using Delta.XAML.Contract;
using SDL3;

namespace Delta.Render.UI;

internal static class UiRenderRunner
{
    internal static async Task<int> RunAsync(
        string[] args,
        UiRenderHostOptions options,
        IUiFontResolver fontResolver,
        XamlLoadContext? loadContext,
        CancellationToken cancellationToken)
    {
        return await RunAsync(
            args,
            options,
            fontResolver,
            contentFactory: null,
            loadContext,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        UiRenderHostOptions options,
        IUiFontResolver fontResolver,
        UiRenderHostContentFactory contentFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentFactory);
        return await RunAsync(
            args,
            options,
            fontResolver,
            contentFactory,
            loadContext: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(
        string[] args,
        UiRenderHostOptions options,
        IUiFontResolver fontResolver,
        UiRenderHostContentFactory? contentFactory,
        XamlLoadContext? loadContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fontResolver);
        ValidateOptions(options, contentFactory is null);

        var headless = HasFlag(args, "--headless");
        var xamlPath = contentFactory is null
            ? ResolveSourcePath(ParsePath(args, "--xaml", options.DefaultXamlPath))
            : null;
        var width = ParsePositiveUInt(args, "--width", options.Width);
        var height = ParsePositiveUInt(args, "--height", options.Height);
        var dpiScale = ParsePositiveFloat(args, "--dpi", options.HeadlessDpiScale);
        var frameLimit = ParseFrameLimit(args, headless ? options.DefaultHeadlessFrames : 0);
        var watch = HasFlag(args, "--watch");
        var profile = HasFlag(args, "--profile");
        var readbackPath = ParseOptionalPath(args, "--readback");
        var layoutPath = ParseOptionalPath(args, "--layout-json");
        var loadContextValue = loadContext ?? new XamlLoadContext(new EmptyTypeResolver(), new EmptyResourceResolver());

        using var textService = new DeltaTextService();
        IHostDocument documentState;
        if (contentFactory is null)
        {
            var sourcePath = xamlPath ?? throw new InvalidOperationException("A XAML source path is required.");
            documentState = new LoadedDocumentState(
                LoadDocument(sourcePath, textService, fontResolver, in loadContextValue),
                GetPathStamp(sourcePath));
        }
        else
        {
            documentState = new ContentDocumentState(contentFactory(textService, fontResolver));
        }

        using (documentState)
        {
            return await RunHostAsync(
                args,
                options,
                fontResolver,
                textService,
                documentState,
                loadContextValue,
                xamlPath,
                width,
                height,
                dpiScale,
                frameLimit,
                watch,
                profile,
                readbackPath,
                layoutPath,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunHostAsync(
        string[] args,
        UiRenderHostOptions options,
        IUiFontResolver fontResolver,
        ITextService textService,
        IHostDocument documentState,
        XamlLoadContext loadContext,
        string? sourcePath,
        uint width,
        uint height,
        float dpiScale,
        int frameLimit,
        bool watch,
        bool profile,
        string? readbackPath,
        string? layoutPath,
        CancellationToken cancellationToken)
    {
        var headless = HasFlag(args, "--headless");
        await using var renderer = new VulkanRenderer(new VulkanRendererOptions());

        if (headless)
        {
            var extent = new PixelExtent(ScaleToPixels(width, dpiScale), ScaleToPixels(height, dpiScale));
            await using var session = renderer.CreateHeadlessSession(
                extent.Width,
                extent.Height,
                new RenderSessionOptions(EnableProfiling: profile));
            return await RunFramesAsync(
                window: null,
                session,
                documentState,
                textService,
                fontResolver,
                loadContext,
                extent,
                width,
                height,
                dpiScale,
                frameLimit,
                watch,
                profile,
                readbackPath,
                layoutPath,
                sourcePath,
                cancellationToken).ConfigureAwait(false);
        }

        var factory = new Sdl3WindowFactory();
        var createResult = factory.CreateWindow(
            new WindowConfiguration(options.WindowTitle, width, height, options.Resizable, options.HighDpi));
        if (!createResult.Succeeded || createResult.Window is not { } window)
        {
            await Console.Error.WriteLineAsync("Window creation failed.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(createResult.Diagnostics.ToText()).ConfigureAwait(false);
            return 1;
        }

        await using var windowLease = window.ConfigureAwait(false);
        await using var windowSession = renderer.CreateWindowSession(
            window,
            new RenderSessionOptions(EnableProfiling: profile));
        var metrics = window.Metrics;
        var windowExtent = metrics.DrawableExtent;
        if (windowExtent.IsEmpty)
        {
            windowExtent = new PixelExtent(width, height);
        }

        return await RunFramesAsync(
            window,
            windowSession,
            documentState,
            textService,
            fontResolver,
            loadContext,
            windowExtent,
            metrics.Width,
            metrics.Height,
            metrics.DpiScale,
            frameLimit,
            watch,
            profile,
            readbackPath: null,
            layoutPath,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunFramesAsync(
        IRenderWindow? window,
        IRenderFrameSession session,
        IHostDocument documentState,
        ITextService textService,
        IUiFontResolver fontResolver,
        XamlLoadContext loadContext,
        PixelExtent initialExtent,
        uint headlessWidth,
        uint headlessHeight,
        float headlessDpiScale,
        int frameLimit,
        bool watch,
        bool profile,
        string? readbackPath,
        string? layoutPath,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(documentState);
        ArgumentNullException.ThrowIfNull(textService);
        ArgumentNullException.ThrowIfNull(fontResolver);
        var extent = initialExtent;
        session.ResizeTarget(in extent);
        using var textFeature = UiRenderHost.CreateTextFeature(session, textService, extent);
        var uiFeature = UiRenderHost.CreateDisplayListFeature(session, extent, textFeature);
        var clearFeature = new ClearFeature(session.Target, extent, UiRenderHost.CreateSolidRectangleProgram());
        var readbackFeature = window is null && readbackPath is not null
            ? new HeadlessReadbackFeature(session.Target, extent.Width, extent.Height)
            : null;
        var features = CreateFeatures(clearFeature, uiFeature, readbackFeature);
        var graph = session.CreateRenderGraph();
        var timings = new TimingSummary();
        var renderedFrames = 0;
        var clipCount = 0;
        var running = true;
        var diagnosticsPending = layoutPath is not null;

        try
        {
            while (running && (window is null || !window.IsClosed) && renderedFrames < frameLimit)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var metrics = window is { } activeWindow
                    ? ReadWindowFrame(activeWindow, documentState, ref running)
                    : new WindowMetrics(headlessWidth, headlessHeight, headlessDpiScale)
                    {
                        DrawableWidth = extent.Width,
                        DrawableHeight = extent.Height,
                    };
                if (!running)
                {
                    break;
                }

                var nextExtent = metrics.DrawableExtent;
                if (nextExtent.IsEmpty)
                {
                    nextExtent = extent;
                }

                if (nextExtent != extent)
                {
                    session.ResizeTarget(in nextExtent);
                    textFeature.Resize(nextExtent);
                    uiFeature.Dispose();
                    uiFeature = UiRenderHost.CreateDisplayListFeature(session, nextExtent, textFeature);
                    clearFeature = new ClearFeature(session.Target, nextExtent, UiRenderHost.CreateSolidRectangleProgram());
                    readbackFeature = window is null && readbackPath is not null
                        ? new HeadlessReadbackFeature(session.Target, nextExtent.Width, nextExtent.Height)
                        : null;
                    features = CreateFeatures(clearFeature, uiFeature, readbackFeature);
                    extent = nextExtent;
                    diagnosticsPending = layoutPath is not null;
                }

                if (watch && TryReloadDocument(
                        sourcePath,
                        documentState,
                        textService,
                        fontResolver,
                        in loadContext))
                {
                    Console.WriteLine($"[watch] Reloaded {sourcePath}");
                    diagnosticsPending = layoutPath is not null;
                }

                documentState.AdvanceFrame();
                var layoutStart = Stopwatch.GetTimestamp();
                documentState.Document.Layout(new float2(metrics.Width, metrics.Height), metrics.DpiScale);
                var displayList = documentState.Document.BuildDisplayList();
                clipCount = displayList.Clips.Length;
                var uiEnd = Stopwatch.GetTimestamp();
                if (diagnosticsPending && layoutPath is not null)
                {
                    WriteText(layoutPath, documentState.Document.BuildLayoutDiagnosticsJson());
                    diagnosticsPending = false;
                }

                if (!uiFeature.Consume(displayList))
                {
                    await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, uiFeature.Diagnostics)).ConfigureAwait(false);
                    return 1;
                }

                var consumeEnd = Stopwatch.GetTimestamp();
                graph.Build((ulong)renderedFrames, features);
                var buildEnd = Stopwatch.GetTimestamp();
                var result = graph.Execute();
                var executeEnd = Stopwatch.GetTimestamp();
                if (result.Status != RenderGraphExecutionStatus.Submitted)
                {
                    await Console.Error.WriteLineAsync($"Graph execution failed: {result.Status}").ConfigureAwait(false);
                    await Console.Error.WriteLineAsync(result.Diagnostics.ToString()).ConfigureAwait(false);
                    return 1;
                }

                if (profile)
                {
                    timings.Add(
                        uiEnd - layoutStart,
                        consumeEnd - uiEnd,
                        buildEnd - consumeEnd,
                        executeEnd - buildEnd);
                }

                renderedFrames++;
            }

            await Console.Out.WriteLineAsync(
                $"DeltaRender.UI host ({(window is null ? "headless" : "windowed")}): " +
                $"frames={renderedFrames}, visuals={uiFeature.VisualCount}, " +
                $"clips={clipCount}, text={uiFeature.TextCount}").ConfigureAwait(false);
            if (profile)
            {
                await Console.Out.WriteLineAsync(timings.ToReport()).ConfigureAwait(false);
            }

            if (readbackFeature is not null && readbackPath is not null && renderedFrames != 0)
            {
                var pixels = new byte[checked((int)((ulong)extent.Width * extent.Height * 4))];
                if (!readbackFeature.Readback.IsValid || graph.CopyReadback(readbackFeature.Readback, pixels) != pixels.Length)
                {
                    await Console.Error.WriteLineAsync("Headless Vulkan readback did not complete.").ConfigureAwait(false);
                    return 1;
                }

                SavePpm(pixels, extent.Width, extent.Height, readbackPath);
                await Console.Out.WriteLineAsync($"readback: path={Path.GetFullPath(readbackPath)}").ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            uiFeature.Dispose();
        }
    }

    private static IRenderFeature[] CreateFeatures(
        ClearFeature clear,
        UiDisplayListGraphFeature ui,
        HeadlessReadbackFeature? readback) => readback is null ? [clear, ui] : [clear, ui, readback];

    private static WindowMetrics ReadWindowFrame(
        IRenderWindow window,
        IHostDocument documentState,
        ref bool running)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(documentState);
        Sdl3WindowFactory.PumpEvents();
        while (SDL.PollEvent(out var @event))
        {
            var type = (SDL.EventType)@event.Type;
            if (type is SDL.EventType.Quit or SDL.EventType.WindowCloseRequested)
            {
                running = false;
                continue;
            }

            if (type is SDL.EventType.KeyDown or SDL.EventType.KeyUp)
            {
                var key = new UiKeyEvent(
                    type == SDL.EventType.KeyDown ? UiKeyEventKind.Down : UiKeyEventKind.Up,
                    new UiPhysicalKey((uint)@event.Key.Scancode),
                    new UiLogicalKey((uint)@event.Key.Scancode),
                    default,
                    @event.Key.Repeat);
                var input = UiInputEvent.FromKey(in key);
                documentState.HandleInput(in input);
            }
        }

        return window.Metrics;
    }

    private static UiDocument LoadDocument(
        string xamlPath,
        ITextService textService,
        IUiFontResolver fontResolver,
        in XamlLoadContext loadContext)
    {
        var result = new XamlLoader().Load(File.ReadAllText(xamlPath), in loadContext);
        if (!result.Success || result.Root is not { } root)
        {
            var diagnostics = result.Diagnostics.IsEmpty
                ? "unknown XAML load failure"
                : string.Join(Environment.NewLine, result.Diagnostics.ToArray());
            throw new InvalidOperationException($"{xamlPath} failed to load: {diagnostics}");
        }

        return new UiDocument(root, textService, fontResolver);
    }

    private static bool TryReloadDocument(
        string? sourcePath,
        IHostDocument state,
        ITextService textService,
        IUiFontResolver fontResolver,
        in XamlLoadContext loadContext)
    {
        if (sourcePath is null || state is not LoadedDocumentState loadedState)
        {
            return false;
        }

        var nextStamp = GetPathStamp(sourcePath);
        if (nextStamp == -1 || nextStamp == loadedState.SourceStamp)
        {
            return false;
        }

        try
        {
            loadedState.Replace(LoadDocument(sourcePath, textService, fontResolver, in loadContext), nextStamp);
            return true;
        }
        catch (Exception exception)
        {
            loadedState.SourceStamp = nextStamp;
            Console.Error.WriteLine($"[watch] Failed to reload {sourcePath}: {exception.Message}");
            return false;
        }
    }

    private static string ResolveSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string[] candidates = Path.IsPathRooted(path)
            ? [path]
            : [Path.Combine(Environment.CurrentDirectory, path), Path.Combine(AppContext.BaseDirectory, path)];
        for (var i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
            {
                return Path.GetFullPath(candidates[i]);
            }
        }

        throw new FileNotFoundException($"XAML source was not found: {path}");
    }

    private static long GetPathStamp(string path) =>
        File.Exists(path)
            ? unchecked((File.GetLastWriteTimeUtc(path).Ticks * 397L) ^ new FileInfo(path).Length)
            : -1;

    private static uint ScaleToPixels(uint logical, float dpiScale)
    {
        var pixels = checked((double)logical * dpiScale);
        return checked((uint)Maths.Ceil(pixels));
    }

    private static void SavePpm(ReadOnlySpan<byte> pixels, uint width, uint height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory);
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write($"P6\n{width} {height}\n255\n");
        writer.Flush();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            stream.WriteByte(pixels[i]);
            stream.WriteByte(pixels[i + 1]);
            stream.WriteByte(pixels[i + 2]);
        }
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory);
        File.WriteAllText(path, content);
    }

    private static int ParseFrameLimit(string[] args, int fallback)
    {
        var value = ParsePositiveInt(args, "--frames", fallback);
        return value == 0 ? int.MaxValue : value;
    }

    private static int ParsePositiveInt(string[] args, string option, int fallback)
    {
        var value = ParseInt(args, option, fallback);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(option, "The frame count cannot be negative.");
        }

        return value;
    }

    private static int ParseInt(string[] args, string option, int fallback)
    {
        var value = GetOption(args, option);
        return value is null
            ? fallback
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"{option} requires an integer value.", nameof(args));
    }

    private static uint ParsePositiveUInt(string[] args, string option, uint fallback)
    {
        var value = GetOption(args, option);
        return value is null
            ? fallback
            : uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0
                ? parsed
                : throw new ArgumentException($"{option} requires a positive unsigned integer.", nameof(args));
    }

    private static float ParsePositiveFloat(string[] args, string option, float fallback)
    {
        var value = GetOption(args, option);
        return value is null
            ? fallback
            : float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                float.IsFinite(parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{option} requires a positive finite number.", nameof(args));
    }

    private static string ParsePath(string[] args, string option, string fallback) => GetOption(args, option) ?? fallback;

    private static string? ParseOptionalPath(string[] args, string option) => GetOption(args, option);

    private static string? GetOption(string[] args, string option)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(args[i + 1])
                    ? throw new ArgumentException($"{option} requires a non-empty value.", nameof(args))
                    : args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateOptions(UiRenderHostOptions options, bool requiresXamlPath)
    {
        if (requiresXamlPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.DefaultXamlPath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.WindowTitle);
        if (options.Width == 0 || options.Height == 0 || options.DefaultHeadlessFrames < 0 ||
            !float.IsFinite(options.HeadlessDpiScale) || options.HeadlessDpiScale <= 0)
        {
            throw new ArgumentException("Host dimensions, DPI and frame defaults must be valid.", nameof(options));
        }
    }

    private interface IHostDocument : IDisposable
    {
        UiDocument Document { get; }

        void AdvanceFrame();

        void HandleInput(in UiInputEvent input);
    }

    private sealed class LoadedDocumentState(UiDocument document, long sourceStamp) : IHostDocument
    {
        public UiDocument Document { get; private set; } = document;
        internal long SourceStamp { get; set; } = sourceStamp;

        internal void Replace(UiDocument document, long sourceStamp)
        {
            ArgumentNullException.ThrowIfNull(document);
            var previous = Document;
            Document = document;
            SourceStamp = sourceStamp;
            previous.Dispose();
        }

        public void AdvanceFrame()
        {
        }

        public void HandleInput(in UiInputEvent input) => Document.Dispatch(in input);

        public void Dispose() => Document.Dispose();
    }

    private sealed class ContentDocumentState : IHostDocument
    {
        private readonly IUiRenderHostContent _content;

        internal ContentDocumentState(IUiRenderHostContent content)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
        }

        public UiDocument Document => _content.Document;

        public void AdvanceFrame() => _content.AdvanceFrame();

        public void HandleInput(in UiInputEvent input) => _content.HandleInput(in input);

        public void Dispose() => _content.Dispose();
    }

    private sealed class EmptyTypeResolver : IXamlTypeResolver
    {
        public bool TryResolveName(in XamlQualifiedName name, out UiTypeId type)
        {
            type = default;
            return false;
        }

        public bool TryCreate(UiTypeId type, [NotNullWhen(true)] out UiElement? element)
        {
            element = null;
            return false;
        }
    }

    private sealed class EmptyResourceResolver : IUiResourceResolver
    {
        public bool TryResolve(UiResourceId resource, out object? value)
        {
            value = null;
            return false;
        }
    }

    private sealed class ClearFeature(RenderTargetHandle target, PixelExtent extent, GraphicsShaderProgram program) : IRenderFeature
    {
        private readonly RenderTargetHandle _target = target;
        private readonly GraphicsShaderProgram _program = program;
        private readonly ClearPass _pass = new(extent);

        public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
        {
            var target = graph.ImportTarget(_target);
            var pass = graph.AddRasterPass(
                new RasterPassDescription(
                    "DeltaRender.UI.Clear",
                    new RasterPipelineDescription(_program, cullMode: RasterCullMode.None)),
                _pass);
            graph.UseColorAttachment(
                pass,
                0,
                new ColorAttachmentDescription(
                    target,
                    AttachmentLoadOperation.Clear,
                    AttachmentStoreOperation.Store,
                    new ClearColor(0, 0, 0, 1)));
        }
    }

    private sealed class ClearPass(PixelExtent extent) : IRasterPass
    {
        private readonly RenderViewport _viewport = new(0, 0, extent.Width, extent.Height);
        private readonly PixelRect _scissor = new(0, 0, checked((int)extent.Width), checked((int)extent.Height));

        public void Record(IRasterCommandContext commands)
        {
            commands.SetViewport(in _viewport);
            commands.SetScissor(in _scissor);
        }
    }

    private sealed class HeadlessReadbackFeature(RenderTargetHandle target, uint width, uint height) : IRenderFeature
    {
        internal RenderGraphReadbackHandle Readback { get; private set; }

        public void AddPasses(IRenderGraphBuilder graph, ulong frameNumber)
        {
            var texture = graph.ImportTarget(target);
            Readback = graph.ReadbackTexture(
                texture,
                new PixelRect(0, 0, checked((int)width), checked((int)height)));
        }
    }

    private sealed class TimingSummary
    {
        private long _frames;
        private long _ui;
        private long _consume;
        private long _build;
        private long _execute;

        internal void Add(long ui, long consume, long build, long execute)
        {
            _frames++;
            _ui = checked(_ui + ui);
            _consume = checked(_consume + consume);
            _build = checked(_build + build);
            _execute = checked(_execute + execute);
        }

        internal string ToReport() =>
            _frames == 0
                ? "profile: no samples"
                : $"profile: samples={_frames}, ui={ToMilliseconds(_ui / (double)_frames):F3}ms, " +
                  $"consume={ToMilliseconds(_consume / (double)_frames):F3}ms, " +
                  $"graph-build={ToMilliseconds(_build / (double)_frames):F3}ms, " +
                  $"graph-execute={ToMilliseconds(_execute / (double)_frames):F3}ms";

        private static double ToMilliseconds(double ticks) => ticks * 1_000d / Stopwatch.Frequency;
    }
}
