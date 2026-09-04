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
        ApplyEditorOptions();           // carries readOnly
        // Leaving edit mode drops the selection: its frame and its handles have no
        // meaning in a mode where nothing can be moved.
        if (_editMode) UpdateInspectFrame();
        else ClearInspectSelection();
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
        // The glyph has to be told which foreground it now sits on.
        var onInk = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        var offInk = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        ViewModeIcon.Foreground = _editMode ? offInk : onInk;
        EditModeIcon.Foreground = _editMode ? onInk : offInk;
        ToolTipService.SetToolTip(ViewModeButton, TipBlock(LocalizationService.Get("mode.view")));
        ToolTipService.SetToolTip(EditModeButton, TipBlock(LocalizationService.Get("mode.edit")));
    }

    // Air between the plate and the window's right edge. Wider than the gap above
    // it: that edge is a hard boundary, and the preview's vertical scrollbar
    // appears in the same strip.
    private const double ModeSwitchRightMargin = 24;

    /// <summary>
    /// Keeps the switch clear of the horizontal ruler, which overlays the top of
    /// the preview rather than taking layout space: without this the plate sits on
    /// the graduations.
    /// </summary>
    private void UpdateModeSwitchMargin()
    {
        double top = 10 + (_settings.ShowRulerHorizontal ? RulerBandDip : 0);
        // The right margin has to be repeated here. This assignment REPLACES the one
        // the XAML set, and leaving it at zero is what glued the plate to the edge.
        ModeSwitch.Margin = new Thickness(0, top, ModeSwitchRightMargin, 0);
    }

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
