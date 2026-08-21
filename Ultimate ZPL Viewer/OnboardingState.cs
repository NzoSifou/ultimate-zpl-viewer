using System;
using System.IO;
using System.Text.Json;

namespace Ultimate_ZPL_Viewer;

// Whether the first-run wizard still has to be shown, and how far it got.
//
// This lives in its OWN file next to the settings rather than inside them, for two
// reasons: the uninstaller deletes it (so a reinstall shows the wizard again) while
// leaving the user's settings, language files and colour scheme untouched; and the
// font step restarts the application, so the position in the flow has to survive a
// process restart.
internal sealed class OnboardingState
{
    public bool Completed { get; set; }

    // The step to resume on after the restart that follows a font installation.
    public int Step { get; set; }

    // What the user did, so the wizard can be resumed and the summary stays honest
    // across the restart. Values are the Outcome constants below.
    public string Fonts { get; set; } = Outcome.Pending;
    public string Printer { get; set; } = Outcome.Pending;
    public string Association { get; set; } = Outcome.Pending;

    public static class Outcome
    {
        public const string Pending = "pending";
        public const string Already = "already";   // nothing to do, it was already in place
        public const string Done = "done";         // the user did it during the wizard
        public const string Skipped = "skipped";
        public const string Failed = "failed";
    }

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultimate ZPL Viewer", "onboarding.json");

    public static OnboardingState Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<OnboardingState>(File.ReadAllText(Path)) ?? new OnboardingState();
        }
        catch { /* unreadable → treat as a first run rather than crash */ }
        return new OnboardingState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch { /* best effort: never take the app down over this */ }
    }
}
