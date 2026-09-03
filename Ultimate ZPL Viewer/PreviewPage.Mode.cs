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
        ApplyEditorOptions();           // carries readOnly
        // Leaving edit mode drops the selection: its frame and its handles have no
        // meaning in a mode where nothing can be moved.
        if (_editMode) UpdateInspectFrame();
        else ClearInspectSelection();
    }

    // The active icon is marked by an accent OUTLINE, the way the Windows snipping
    // toolbar marks its current tool. A filled accent background was the other
    // option, but sitting right beside its neighbour it reads as "held down"
    // rather than as "selected".
    private void ApplyModeButtons()
    {
        var accent = new SolidColorBrush(AccentColor());
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ViewModeChip.BorderBrush = _editMode ? clear : accent;
        EditModeChip.BorderBrush = _editMode ? accent : clear;
        ToolTipService.SetToolTip(ViewModeButton, TipBlock(LocalizationService.Get("mode.view")));
        ToolTipService.SetToolTip(EditModeButton, TipBlock(LocalizationService.Get("mode.edit")));
    }

    /// <summary>
    /// Keeps the switch clear of the horizontal ruler, which overlays the top of
    /// the preview rather than taking layout space: without this the plate sits on
    /// the graduations.
    /// </summary>
    private void UpdateModeSwitchMargin()
    {
        double top = 10 + (_settings.ShowRulerHorizontal ? RulerBandDip : 0);
        ModeSwitch.Margin = new Thickness(0, top, 0, 0);
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
