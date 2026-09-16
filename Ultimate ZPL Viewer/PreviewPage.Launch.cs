using System;
using System.Linq;
using System.Threading.Tasks;

namespace Ultimate_ZPL_Viewer;

// ── What a launch asks of the documents it opens ────────────────────────────
// A document given on the command line can come with a density to read it at, a
// mode to open it in and a transform to run on it. The window that opens with it
// takes all of that in OnNavigatedTo; a window that is already open and receives
// it from a second launch takes it here.
public sealed partial class PreviewPage
{
    // Set while the editor's first text is on its way (OnEditorReady).
    private bool _editorTextPending;

    /// <summary>
    /// Opens the documents a later launch handed over, as tabs of this window, each
    /// read, set and transformed the way that launch asked.
    /// </summary>
    internal async Task OpenFromLaunchAsync(LaunchOptions options)
    {
        foreach (var path in options.Files)
            await OpenPathAsync(path, options.Document, options.EditMode);

        // The turn of the view belongs to the window, not to a document: asked for
        // with these files, it is what this window shows them with.
        if (options.Document.ViewRotate is { } angle) SetViewRotation(angle);
    }

    /// <summary>The "Tourner" rotation, set from outside the button — the box follows.</summary>
    private void SetViewRotation(double angle)
    {
        _rotationDegrees = Math.Clamp(((angle % 360) + 360) % 360, 0, 359.99);
        if (RotationBox != null) RotationBox.Text = _rotationDegrees.ToString("0.##");
        RefreshPreview(SizeUpdate.KeepCurrent);
    }

    /// <summary>
    /// Puts the active document in a mode the command line asked for. Not through
    /// SetMode: that remembers the choice, and a launch shapes a session only.
    /// </summary>
    private void ForceTabMode(bool edit)
    {
        _editMode = edit;
        if (_activeTab is not null) _activeTab.EditMode = edit;
        ApplyMode();
    }

    /// <summary>
    /// Runs the transform a launch asked for on the active document, once — and only
    /// once the editor holds that document, so that the change goes through the same
    /// door as a click on "Transformer": one edit, undone by one Ctrl+Z, density and
    /// size included.
    /// </summary>
    /// <remarks>
    /// "Holds" means the editor has reported the state the document starts in. That
    /// report comes back a moment after the text is sent; a transform run before it
    /// would be filed as the starting state, and Ctrl+Z would then bring the text
    /// back without the density. So this is also called each time a state is filed.
    /// </remarks>
    private void RunPendingTransform()
    {
        if (!_editorReady || _activeTab?.PendingTransform is not { } wanted) return;
        if (_editorTextPending || _activeTab.StateByVersion.Count == 0) return;
        _activeTab.PendingTransform = null;
        double current = _model.DeclaredDpmm ?? SelectedDpmm;
        ApplyTransform(current, wanted.TransformDpmm, wanted.TransformRotate, wanted.RoundUp);
    }
}
