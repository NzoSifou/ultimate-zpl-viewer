using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ultimate_ZPL_Viewer;

// ── View / edit mode ────────────────────────────────────────────────────────
// Two modes, one switch. VIEW is what the application has always been: the ZPL is
// shown but read-only, and a click on the preview points at the code behind an
// element. EDIT unlocks the text and turns the preview into a canvas.
//
// The mode belongs to the DOCUMENT, not the window: one tab can be open for
// reading while another is being drawn. It is mirrored to and from DocTab.EditMode
// exactly the way _isDirty is.
public sealed partial class PreviewPage
{
    // The active document's mode.
    private bool _editMode;

    internal bool EditMode => _editMode;

    /// <summary>The mode a document opens in, per the user's setting.</summary>
    private bool InitialEditMode => _settings.StartMode switch
    {
        1 => true,                      // always edit
        2 => _settings.LastModeEdit,    // whatever was in use last
        _ => false,                     // always view (default)
    };

    private void InitModeSwitch()
    {
        ViewModeButton.Click += (_, _) => SetMode(false);
        EditModeButton.Click += (_, _) => SetMode(true);
        _editMode = InitialEditMode;
        UpdateModeSwitchMargin();
        ApplyMode();
    }

    /// <summary>Switches the active document to the given mode.</summary>
    private void SetMode(bool edit)
    {
        if (_editMode == edit) return;
        _editMode = edit;
        if (_activeTab is not null) _activeTab.EditMode = edit;
        // Only worth writing to disk when the setting actually reads it back.
        if (_settings.StartMode == 2)
        {
            _settings.LastModeEdit = edit;
            _settings.Save();
        }
        ApplyMode();
    }

    /// <summary>Brings a tab back to its own mode when it becomes the active one.</summary>
    private void RestoreTabMode()
    {
        _editMode = _activeTab?.EditMode ?? InitialEditMode;
        ApplyMode();
    }

    private void ApplyMode()
    {
        ApplyModeButtons();
        ApplyEditToolbar();
        UpdatePreviewCursor();      // the same pointer means different things per mode
        ApplyEditorOptions();           // carries readOnly
        // Leaving edit mode drops the selection: its frame and its handles have no
        // meaning in a mode where nothing can be moved.
        if (_editMode) UpdateInspectFrame();
        else { EndInPlace(); ClearInspectSelection(); }
    }

    // The active icon is FILLED with the accent colour. The accent button style is
    // swapped in rather than a hand-painted background: it tracks a custom accent
    // on its own, and keeps the hover and pressed states a local Background would
    // flatten. Both states get a REAL style — assigning null instead of the default
    // one drops every value the style carried, CornerRadius included.
    private void ApplyModeButtons()
    {
        var on = (Style)Application.Current.Resources["AccentButtonStyle"];
        var off = (Style)Application.Current.Resources["DefaultButtonStyle"];
        ViewModeButton.Style = _editMode ? off : on;
        EditModeButton.Style = _editMode ? on : off;
        // The glyphs take the foreground of the button they sit in, which each
        // style has just set with a theme resource: right in either theme, and it
        // follows a theme change on its own.
        ViewModeIcon.ClearValue(IconElement.ForegroundProperty);
        EditModeIcon.ClearValue(IconElement.ForegroundProperty);
        ToolTipService.SetToolTip(ViewModeButton, TipBlock(LocalizationService.Get("mode.view")));
        ToolTipService.SetToolTip(EditModeButton, TipBlock(LocalizationService.Get("mode.edit")));
    }

    /// <summary>
    /// Both floating plates are placed by the same code (PreviewPage.Plates.cs):
    /// their position is a setting now, and one of the things it has to answer for
    /// is the ruler, which overlays the top and left of the preview rather than
    /// taking layout space — without stepping over its band the plate would sit on
    /// the graduations.
    /// </summary>
    private void UpdateModeSwitchMargin() => ApplyPlatePlacement();

    // WinUI never shows a ToolTip instance handed straight to ToolTipService, and
    // it clips popups to the window: a plain wrapped TextBlock, capped, is what
    // actually appears.
    private static TextBlock TipBlock(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 260,
    };
}
