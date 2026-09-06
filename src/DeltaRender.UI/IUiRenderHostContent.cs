using Delta.Text.Contract;
using Delta.XAML;
using Delta.XAML.Contract;

namespace Delta.Render.UI;

/// <summary>Application-owned retained content hosted by <see cref="UiRenderHost"/>.</summary>
/// <remarks>
/// The host owns the renderer, window, frame graph and display-list features.
/// Content owns one document and may update its model before each frame. The
/// factory is called during setup; the callbacks are not part of DeltaXAML's
/// retained layout or rendering stages.
/// </remarks>
public interface IUiRenderHostContent : IDisposable
{
    /// <summary>Gets the single retained document rendered by the host.</summary>
    UiDocument Document { get; }

    /// <summary>Advances application state before the next layout pass.</summary>
    void AdvanceFrame();

    /// <summary>Receives a neutral input packet from the host platform adapter.</summary>
    void HandleInput(in UiInputEvent input);
}

/// <summary>Creates application content after the host has initialized text services.</summary>
public delegate IUiRenderHostContent UiRenderHostContentFactory(
    ITextService textService,
    IUiFontResolver fontResolver);
