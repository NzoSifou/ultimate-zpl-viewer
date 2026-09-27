using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Ultimate_ZPL_Viewer;

// ── Settings profiles ───────────────────────────────────────────────────────
// The picker at the foot of the settings column, and what it does. A profile is
// the whole set of preferences (ProfileService): there is nothing to save — a
// change lands in the active profile as it is made — and choosing another one
// loads it at once, in every window.
public sealed partial class PreviewPage
{
    private ComboBox? _profilePicker;
    private List<ProfileInfo> _profiles = new();
    private bool _fillingProfiles;

    private static string ProfL(string key) => LocalizationService.Get("settings.profiles." + key);

    /// <summary>(Re)builds the picker from the profiles on disk.</summary>
    private void BuildProfileFooter()
    {
        _profiles = ProfileService.List();
        var lang = LocalizationService.CurrentCode;

        _fillingProfiles = true;
        _profilePicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = _profiles.Select(p => p.DisplayName(lang)).ToList(),
            SelectedIndex = Math.Max(0, _profiles.FindIndex(p => p.Id == _settings.ActiveProfile)),
        };
        ToolTipService.SetToolTip(_profilePicker, TipBlock(ProfL("pickerTip")));
        _profilePicker.SelectionChanged += (_, _) =>
        {
            if (_fillingProfiles || _profilePicker.SelectedIndex < 0) return;
            var id = _profiles[_profilePicker.SelectedIndex].Id;
            if (id != _settings.ActiveProfile) SwitchProfile(id);
        };
        _fillingProfiles = false;

        var more = new Button
        {
            Width = 34, Height = 32, MinWidth = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 14 },
        };
        ToolTipService.SetToolTip(more, TipBlock(ProfL("menuTip")));
        more.Flyout = BuildProfileMenu();

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_profilePicker);
        Grid.SetColumn(more, 1);
        row.Children.Add(more);

        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8, 12, 14) };
        panel.Children.Add(new TextBlock { Text = ProfL("label"), FontSize = 12, Opacity = 0.7 });
        panel.Children.Add(row);
        SettingsNav.PaneFooter = panel;
    }

    private MenuFlyout BuildProfileMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        MenuFlyoutItem Item(string key, string glyph, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = ProfL(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { await ShowMessageAsync(ProfL("failedTitle"), ex.Message); }
            };
            return item;
        }

        menu.Items.Add(Item("new", "", NewProfileAsync));
        menu.Items.Add(Item("duplicate", "", DuplicateProfileAsync));
        menu.Items.Add(Item("rename", "", RenameProfileAsync));
        var delete = Item("delete", "", DeleteProfileAsync);
        // There has to be a profile to be in.
        if (_profiles.Count <= 1)
        {
            delete.IsEnabled = false;
            ToolTipService.SetToolTip(delete, TipBlock(ProfL("lastTip")));
        }
        menu.Items.Add(delete);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("export", "", ExportProfileAsync));
        menu.Items.Add(Item("import", "", ImportProfileAsync));
        return menu;
    }

    private string ActiveProfileName()
        => (_profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfile)
            ?? ProfileService.Find(_settings.ActiveProfile))?.DisplayName(LocalizationService.CurrentCode) ?? "";

    // ── Actions ──────────────────────────────────────────────────────────────

    private async Task NewProfileAsync()
    {
        var name = await AskProfileNameAsync(ProfL("newTitle"), ProfL("newBody"), ProfL("newName"), ProfL("create"));
        if (name is null) return;
        SwitchProfile(ProfileService.Create(name));
    }

    private async Task DuplicateProfileAsync()
    {
        var source = ActiveProfileName();
        var name = await AskProfileNameAsync(ProfL("duplicateTitle"),
            ProfL("duplicateBody").Replace("{name}", source),
            ProfL("copyName").Replace("{name}", source), ProfL("create"));
        if (name is null) return;
        // Everything the active profile has is already on disk: it is saved as
        // it changes.
        SwitchProfile(ProfileService.Duplicate(_settings.ActiveProfile, name));
    }

    private async Task RenameProfileAsync()
    {
        var code = LocalizationService.CurrentCode;
        var language = LocalizationService.AvailableLanguages()
            .Where(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.DisplayName)
            .FirstOrDefault() ?? code;
        var name = await AskProfileNameAsync(ProfL("renameTitle"),
            ProfL("renameBody").Replace("{language}", language),
            ActiveProfileName(), ProfL("renameButton"));
        if (name is null) return;
        ProfileService.Rename(_settings.ActiveProfile, name);
        RefreshProfilePickersEverywhere();
    }

    private async Task DeleteProfileAsync()
    {
        if (_profiles.Count <= 1) return;
        var dialog = CreateDialog(ProfL("deleteTitle"),
            new TextBlock { Text = ProfL("deleteBody").Replace("{name}", ActiveProfileName()), TextWrapping = TextWrapping.Wrap },
            ProfL("deleteButton"), ProfL("cancel"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        // Out of it first, into the next one on the list, then it goes.
        var doomed = _settings.ActiveProfile;
        var next = _profiles.First(p => p.Id != doomed).Id;
        SwitchProfile(next);
        ProfileService.Delete(doomed);
        RefreshProfilePickersEverywhere();
    }

    private async Task ExportProfileAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.SuggestedFileName = SafeFileName(ActiveProfileName());
        picker.FileTypeChoices.Add(ProfL("fileType"), new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await FileIO.WriteTextAsync(file, ProfileService.Export(_settings.ActiveProfile));
    }

    private async Task ImportProfileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        string id;
        try
        {
            id = ProfileService.Import(await FileIO.ReadTextAsync(file), Path.GetFileNameWithoutExtension(file.Name));
        }
        catch (FormatException ex)
        {
            await ShowMessageAsync(ProfL("importFailedTitle"),
                ex.Message == "json" ? ProfL("importFailedJson") : ProfL("importFailedFormat"));
            return;
        }
        SwitchProfile(id);
    }

    private void SwitchProfile(string id)
    {
        ProfileService.Switch(_settings, id);
        ApplySettingsEverywhere();
    }

    // ── Asking for a name ────────────────────────────────────────────────────

    private async Task<string?> AskProfileNameAsync(string title, string body, string suggested, string confirm)
    {
        var box = new TextBox
        {
            Text = suggested,
            Header = ProfL("nameLabel"),
            MaxLength = 80,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        content.Children.Add(box);

        var dialog = CreateDialog(title, content, confirm, ProfL("cancel"));
        void Validate() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        box.TextChanged += (_, _) => Validate();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(box.Text)) return;
            e.Handled = true;
            dialog.Hide();
            _nameConfirmedByEnter = true;
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        Validate();

        _nameConfirmedByEnter = false;
        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary && !_nameConfirmedByEnter) return null;
        var name = box.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    private bool _nameConfirmedByEnter;

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "profil" : cleaned;
    }

    // ── Applying a whole set of settings ─────────────────────────────────────

    /// <summary>Every window takes up the settings as they now stand.</summary>
    private static void ApplySettingsEverywhere()
    {
        foreach (var window in WindowManager.Windows.ToList())
            window.Page?.ApplyAllSettings();
    }

    private static void RefreshProfilePickersEverywhere()
    {
        foreach (var window in WindowManager.Windows.ToList())
            window.Page?.BuildProfileFooter();
    }

    /// <summary>
    /// Puts every setting into effect at once — after a profile switch or a reset,
    /// when all of them may have changed together. Each one is applied the way its
    /// own control applies it; what belongs to an open document (its density, its
    /// zoom, its mode) is left as it is.
    /// </summary>
    internal void ApplyAllSettings()
    {
        // The language first: everything after it draws text.
        LocalizationService.SetLanguage(_settings.Language);

        Root.RequestedTheme = _settings.ToElementTheme();
        (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.SetTheme(_settings.ToElementTheme());
        ApplyPreviewTheme();
        ApplyAccentFromSettings();
        if (_editorReady) ApplyEditorTheme();

        // A window opened with --show / --hide keeps the layout it was asked for.
        if (!_suppressLayoutPersist)
        {
            _toolbarVisible = _settings.ToolbarVisible;
            _editorVisible = _settings.EditorVisible;
        }
        ApplyToolbarVisibility();
        ApplyEditorLayout();
        RebuildToolbar();
        ApplyPrintButtonTooltip();

        ApplyEditorOptions();
        PostToEditor("{\"type\":\"setLineNumbers\",\"show\":" + (_settings.ShowLineNumbers ? "true" : "false") + "}");
        RunStaticAnalysis();

        ApplyPlatePlacement();
        ApplyModeButtons();
        ApplyToolButtons();
        UpdateInspectFrameThickness();

        UpdateSizeBoxes();
        UpdateSizeBoxLocks();
        DrawPreviewGrid();
        ApplyRulerVisibility();
        DrawRulers();
        ApplyPreviewCaptionVisibility();
        UpdatePreviewCaption();
        RefreshAllTabHeaders();
        if (_homeVisible) BuildHomePage();

        // Strings everywhere, the settings pages and the profile picker rebuilt
        // from the new values.
        ApplyLanguageLive();
    }
}
