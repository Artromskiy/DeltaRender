using System.Diagnostics.CodeAnalysis;
using Delta;
using Delta.Render;
using SDL3;

namespace Delta.Render.Platform.SDL3;
using Maths = global::Delta.Maths;

internal static class Sdl3Runtime
{
    private static bool _initialized;
    private static bool _vulkanLibraryLoaded;
    private static int _windowCount;

    public static bool IsAvailable => true;

    public static void PumpEvents()
    {
        if (_initialized)
        {
            SDL.PumpEvents();
        }
    }

    public static bool TryGetError(out string? error)
    {
        error = SDL.GetError();
        return !string.IsNullOrWhiteSpace(error);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryInitialize(out RenderDiagnosticBag diagnostics)
    {
        diagnostics = new RenderDiagnosticBag();
        if (_initialized)
        {
            return true;
        }

        try
        {
            if (!SDL.Init(SDL.InitFlags.Video))
            {
                AddSdlError(diagnostics, "SDL-INIT", "SDL video initialization failed.");
                return false;
            }

            if (OperatingSystem.IsMacOS() && !_vulkanLibraryLoaded)
            {
                var libraryPath = Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib");
                if (!SDL.VulkanLoadLibrary(libraryPath))
                {
                    diagnostics.Add(RenderDiagnosticSeverity.Fatal, "SDL-VULKAN", $"SDL Vulkan loader failed: {libraryPath}.");
                    AddSdlError(diagnostics, "SDL-VULKAN", "SDL Vulkan loader returned failure.");
                    return false;
                }

                _vulkanLibraryLoaded = true;
            }

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Fatal, "SDL-INIT", $"SDL initialization threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryCreateWindow(string title, uint width, uint height, bool resizable, bool highDpi, out ulong windowHandle, out RenderDiagnosticBag diagnostics)
    {
        windowHandle = 0;
        diagnostics = new RenderDiagnosticBag();

        try
        {
            var flags = SDL.WindowFlags.Vulkan;
            if (resizable)
            {
                flags |= SDL.WindowFlags.Resizable;
            }

            if (highDpi)
            {
                flags |= SDL.WindowFlags.HighPixelDensity;
            }

            var handle = SDL.CreateWindow(title, (int)width, (int)height, flags);
            if (handle == IntPtr.Zero)
            {
                AddSdlError(diagnostics, "SDL-WINDOW", "SDL window creation returned a null handle.");
                return false;
            }

            windowHandle = unchecked((ulong)handle.ToInt64());
            _windowCount++;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-WINDOW", $"SDL window creation threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryGetWindowMetrics(
        ulong windowHandle,
        out WindowMetrics metrics,
        out RenderDiagnosticBag diagnostics)
    {
        metrics = default;
        diagnostics = new RenderDiagnosticBag();

        try
        {
            var handle = new IntPtr(unchecked((long)windowHandle));
            if (!SDL.GetWindowSize(handle, out var logicalWidth, out var logicalHeight) || logicalWidth <= 0 || logicalHeight <= 0)
            {
                AddSdlError(diagnostics, "SDL-METRICS", "SDL window logical size query failed.");
                return false;
            }

            if (!SDL.GetWindowSizeInPixels(handle, out var drawableWidth, out var drawableHeight) || drawableWidth <= 0 || drawableHeight <= 0)
            {
                AddSdlError(diagnostics, "SDL-METRICS", "SDL window drawable pixel size query failed.");
                return false;
            }

            var dpiScale = SDL.GetWindowPixelDensity(handle);
            if (!float.IsFinite(dpiScale) || dpiScale <= 0)
            {
                dpiScale = Maths.Max(
                    (float)drawableWidth / logicalWidth,
                    (float)drawableHeight / logicalHeight);
            }

            if (!float.IsFinite(dpiScale) || dpiScale <= 0)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-METRICS", "SDL returned an invalid window pixel density.");
                return false;
            }

            metrics = new WindowMetrics(
                checked((uint)logicalWidth),
                checked((uint)logicalHeight),
                dpiScale)
            {
                DrawableWidth = checked((uint)drawableWidth),
                DrawableHeight = checked((uint)drawableHeight),
            };
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-METRICS", $"SDL window metrics query threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryDestroyWindow(ulong windowHandle)
    {
        if (windowHandle == 0)
        {
            return true;
        }

        try
        {
            SDL.DestroyWindow(new IntPtr(unchecked((long)windowHandle)));
            if (_windowCount > 0 && --_windowCount == 0)
            {
                if (_vulkanLibraryLoaded)
                {
                    SDL.VulkanUnloadLibrary();
                    _vulkanLibraryLoaded = false;
                }
                SDL.Quit();
                _initialized = false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryGetRequiredInstanceExtensions(out string[] extensionNames, out RenderDiagnosticBag diagnostics)
    {
        extensionNames = Array.Empty<string>();
        diagnostics = new RenderDiagnosticBag();

        try
        {
            var names = SDL.VulkanGetInstanceExtensions(out var count);
            if (names is null || count == 0 || count > names.Length)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-EXTENSIONS", "SDL returned no Vulkan instance extensions.");
                return false;
            }

            extensionNames = names
                .Take((int)count)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            if (extensionNames.Length == 0)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-EXTENSIONS", "SDL returned only empty Vulkan instance extension names.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-EXTENSIONS", $"SDL Vulkan extension query threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryCreateSurface(ulong windowHandle, ulong vkInstance, ulong allocatorAddress, out ulong surfaceHandle, out RenderDiagnosticBag diagnostics)
    {
        surfaceHandle = 0;
        diagnostics = new RenderDiagnosticBag();

        try
        {
            if (!SDL.VulkanCreateSurface(
                    new IntPtr(unchecked((long)windowHandle)),
                    new IntPtr(unchecked((long)vkInstance)),
                    new IntPtr(unchecked((long)allocatorAddress)),
                    out var surface))
            {
                AddSdlError(diagnostics, "SDL-SURFACE", "SDL Vulkan surface creation failed.");
                return false;
            }

            if (surface == IntPtr.Zero)
            {
                diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-SURFACE", "SDL Vulkan surface creation returned a null handle.");
                return false;
            }

            surfaceHandle = unchecked((ulong)surface.ToInt64());
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, "SDL-SURFACE", $"SDL Vulkan surface creation threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "SDL3-CS is a native FFI boundary; Try* converts binding failures into renderer diagnostics.")]
    public static bool TryDestroySurface(ulong vulkanInstance, ulong surfaceHandle)
    {
        if (vulkanInstance == 0 || surfaceHandle == 0)
        {
            return true;
        }

        try
        {
            SDL.VulkanDestroySurface(
                new IntPtr(unchecked((long)vulkanInstance)),
                new IntPtr(unchecked((long)surfaceHandle)),
                IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddSdlError(RenderDiagnosticBag diagnostics, string code, string fallback)
    {
        diagnostics.Add(RenderDiagnosticSeverity.Error, code, fallback);
        if (TryGetError(out var error) && error is not null)
        {
            diagnostics.Add(RenderDiagnosticSeverity.Error, code, error);
        }
    }
}
