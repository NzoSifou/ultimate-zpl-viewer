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

    // Renaming has two faces. The simple one is what most people want: the name,
    // in the language the application is in. The advanced one is the profile's
    // whole set of names — one line per language, a code and a name — and the
    // language to fall back on when the one on screen has none.
    private async Task RenameProfileAsync()
    {
        var profile = ProfileService.Find(_settings.ActiveProfile);
        if (profile is null) return;
        var current = LocalizationService.CurrentCode;
        var installed = LocalizationService.AvailableLanguages();
        string LanguageName(string code) => installed
            .Where(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.DisplayName).FirstOrDefault() ?? code;

        // ── Simple ───────────────────────────────────────────────────────────
        var simpleBox = new TextBox
        {
            Text = profile.DisplayName(current),
            Header = ProfL("nameLabel"),
            MaxLength = 80,
        };
        var simplePanel = new StackPanel { Spacing = 12 };
        simplePanel.Children.Add(new TextBlock
        {
            Text = ProfL("renameBody").Replace("{language}", LanguageName(current)),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
        });
        simplePanel.Children.Add(simpleBox);
        var showAdvanced = new HyperlinkButton { Content = ProfL("advancedShow"), Padding = new Thickness(0) };
        simplePanel.Children.Add(showAdvanced);

        // ── Advanced ─────────────────────────────────────────────────────────
        var rows = new List<(TextBox Code, TextBox Name, Grid Row)>();
        var rowsPanel = new StackPanel { Spacing = 6 };
        var fallback = new ComboBox { Header = ProfL("fallbackLabel"), MinWidth = 220 };
        ToolTipService.SetToolTip(fallback, TipBlock(ProfL("fallbackTip")));
        var fallbackCodes = new List<string>();
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
        };
        bool advanced = false;
        bool refreshingFallback = false;
        string? wantedFallback = profile.FallbackLanguage;

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        var codeHead = new TextBlock { Text = ProfL("codeColumn"), FontSize = 12, Opacity = 0.7 };
        var nameHead = new TextBlock { Text = ProfL("nameColumn"), FontSize = 12, Opacity = 0.7 };
        Grid.SetColumn(nameHead, 1);
        header.Children.Add(codeHead);
        header.Children.Add(nameHead);

        var add = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = ProfL("addLanguage") } },
            },
        };
        var showSimple = new HyperlinkButton { Content = ProfL("advancedHide"), Padding = new Thickness(0) };

        var advancedPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        advancedPanel.Children.Add(new TextBlock
        {
            Text = ProfL("advancedBody"), TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
        });
        advancedPanel.Children.Add(header);
        advancedPanel.Children.Add(new ScrollViewer
        {
            Content = rowsPanel, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        advancedPanel.Children.Add(add);
        advancedPanel.Children.Add(fallback);
        advancedPanel.Children.Add(new TextBlock
        {
            Text = ProfL("installed").Replace("{list}",
                string.Join(", ", installed.Select(l => $"{l.Code} ({l.DisplayName})"))),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7,
        });
        advancedPanel.Children.Add(showSimple);

        var content = new StackPanel { Spacing = 12, MinWidth = 420 };
        content.Children.Add(simplePanel);
        content.Children.Add(advancedPanel);
        content.Children.Add(error);

        var dialog = CreateDialog(ProfL("renameTitle"), content, ProfL("renameButton"), ProfL("cancel"));

        // ── Behaviour ────────────────────────────────────────────────────────
        List<(string Code, string Name)> FilledRows() => rows
            .Select(r => (Code: r.Code.Text.Trim(), Name: r.Name.Text.Trim()))
            .Where(r => r.Code.Length > 0 || r.Name.Length > 0)
            .ToList();

        void RefreshFallback()
        {
            var codes = FilledRows().Select(r => r.Code)
                .Where(IsLanguageCode)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keep = fallback.SelectedIndex >= 0 && fallback.SelectedIndex < fallbackCodes.Count
                ? fallbackCodes[fallback.SelectedIndex] : wantedFallback;
            refreshingFallback = true;
            fallbackCodes.Clear();
            fallbackCodes.AddRange(codes);
            fallback.ItemsSource = codes.Select(c => installed.Any(l => string.Equals(l.Code, c, StringComparison.OrdinalIgnoreCase))
                ? $"{c} — {LanguageName(c)}" : c).ToList();
            int index = codes.FindIndex(c => string.Equals(c, keep, StringComparison.OrdinalIgnoreCase));
            fallback.SelectedIndex = index >= 0 ? index : (codes.Count > 0 ? 0 : -1);
            refreshingFallback = false;
        }

        void Validate()
        {
            string? message = null;
            if (!advanced)
            {
                if (string.IsNullOrWhiteSpace(simpleBox.Text)) message = "";
            }
            else
            {
                var filled = FilledRows();
                var incomplete = filled.FirstOrDefault(r => r.Code.Length == 0 || r.Name.Length == 0);
                var badCode = filled.FirstOrDefault(r => r.Code.Length > 0 && !IsLanguageCode(r.Code));
                var twice = filled.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                                  .FirstOrDefault(g => g.Key.Length > 0 && g.Count() > 1);
                if (filled.Count == 0) message = ProfL("errEmpty");
                else if (incomplete != default) message = ProfL("errIncomplete");
                else if (badCode != default) message = ProfL("errCode").Replace("{code}", badCode.Code);
                else if (twice is not null) message = ProfL("errTwice").Replace("{code}", twice.Key);
                else if (fallback.SelectedIndex < 0) message = ProfL("errEmpty");
            }
            dialog.IsPrimaryButtonEnabled = message is null;
            error.Text = message ?? "";
            error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        void AddRow(string code, string name)
        {
            var codeBox = new TextBox { Text = code, PlaceholderText = "fr", MaxLength = 16 };
            var nameBox = new TextBox { Text = name, PlaceholderText = ProfL("nameColumn"), MaxLength = 80 };
            var remove = new Button
            {
                Width = 34, Height = 32, MinWidth = 0, Padding = new Thickness(0),
                Content = new FontIcon { Glyph = "", FontSize = 12 },
            };
            ToolTipService.SetToolTip(remove, TipBlock(ProfL("removeLanguage")));
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            Grid.SetColumn(nameBox, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(codeBox);
            row.Children.Add(nameBox);
            row.Children.Add(remove);
            var entry = (codeBox, nameBox, row);
            rows.Add(entry);
            rowsPanel.Children.Add(row);
            codeBox.TextChanged += (_, _) => { RefreshFallback(); Validate(); };
            nameBox.TextChanged += (_, _) => Validate();
            remove.Click += (_, _) =>
            {
                rows.Remove(entry);
                rowsPanel.Children.Remove(row);
                RefreshFallback();
                Validate();
            };
        }

        void EnterAdvanced()
        {
            // The simple field is the current language's line: whatever was typed
            // there comes along.
            rows.Clear();
            rowsPanel.Children.Clear();
            var names = profile.Names.ToList();
            var typed = simpleBox.Text.Trim();
            if (!names.Any(n => string.Equals(n.Key, current, StringComparison.OrdinalIgnoreCase)))
                AddRow(current, typed);
            foreach (var (code, name) in names)
                AddRow(code, string.Equals(code, current, StringComparison.OrdinalIgnoreCase) && typed.Length > 0 ? typed : name);
            advanced = true;
            simplePanel.Visibility = Visibility.Collapsed;
            advancedPanel.Visibility = Visibility.Visible;
            RefreshFallback();
            Validate();
        }

        void EnterSimple()
        {
            var line = rows.FirstOrDefault(r => string.Equals(r.Code.Text.Trim(), current, StringComparison.OrdinalIgnoreCase));
            if (line.Name is not null) simpleBox.Text = line.Name.Text.Trim();
            // What was set up in the advanced view is what the profile has now.
            profile = profile with
            {
                Names = FilledRows().Where(r => r.Code.Length > 0 && r.Name.Length > 0)
                    .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase),
                FallbackLanguage = fallback.SelectedIndex >= 0 ? fallbackCodes[fallback.SelectedIndex] : profile.FallbackLanguage,
            };
            advanced = false;
            advancedPanel.Visibility = Visibility.Collapsed;
            simplePanel.Visibility = Visibility.Visible;
            Validate();
        }

        showAdvanced.Click += (_, _) => EnterAdvanced();
        showSimple.Click += (_, _) => EnterSimple();
        add.Click += (_, _) => { AddRow("", ""); rows[^1].Code.Focus(FocusState.Programmatic); Validate(); };
        simpleBox.TextChanged += (_, _) => Validate();
        fallback.SelectionChanged += (_, _) =>
        {
            if (refreshingFallback) return;
            if (fallback.SelectedIndex >= 0) wantedFallback = fallbackCodes[fallback.SelectedIndex];
            Validate();
        };
        dialog.Opened += (_, _) => { simpleBox.Focus(FocusState.Programmatic); simpleBox.SelectAll(); };
        Validate();

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        if (advanced)
        {
            var names = FilledRows().ToDictionary(r => r.Code, r => r.Name, StringComparer.OrdinalIgnoreCase);
            ProfileService.SetNames(_settings.ActiveProfile, names, fallbackCodes[fallback.SelectedIndex]);
        }
        else
        {
            ProfileService.SetNames(_settings.ActiveProfile,
                new Dictionary<string, string>(profile.Names, StringComparer.OrdinalIgnoreCase) { [current] = simpleBox.Text.Trim() },
                profile.FallbackLanguage ?? current);
        }
        RefreshProfilePickersEverywhere();
    }

    // A language tag as the language files use them: "fr", "en", "fr-CA", "zh-Hant".
    private static bool IsLanguageCode(string code)
        => System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$");

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
