using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// Makes the app behave like a browser: opening a second document does not start a
/// second copy of the program, it hands the file to the copy already on screen,
/// which then decides — per the user's settings — whether it becomes a tab or a
/// window of its own. Windows all living in one process is also what makes
/// dragging a tab from one to another possible at all.
/// </summary>
internal static class InstanceRouter
{
    // Per-user: two sessions on the same machine must not talk to each other. The
    // name has to be built from stable characters only — string.GetHashCode is
    // randomised per process in .NET, so every instance would pick a DIFFERENT pipe
    // and never find each other.
    private static string PipeName =>
        "UltimateZplViewer.Instance." +
        new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());

    private static DispatcherQueue? _queue;

    /// <summary>
    /// Sends this launch's arguments to the instance already running. Returns false
    /// when nobody answered, in which case the caller should just open normally.
    /// </summary>
    public static bool HandOff(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(JsonSerializer.Serialize(args));
            return true;
        }
        catch
        {
            // No server, or it died between the mutex check and here: fall back to
            // opening a window in this process rather than losing the file.
            return false;
        }
    }

    /// <summary>Starts listening for later launches. Called once, by the first instance.</summary>
    public static void Listen(DispatcherQueue queue)
    {
        _queue = queue;
        _ = Task.Run(ServerLoopAsync);
    }

    private static async Task ServerLoopAsync()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var payload = await reader.ReadToEndAsync();
                var args = JsonSerializer.Deserialize<string[]>(payload);
                if (args is { Length: > 0 }) _queue?.TryEnqueue(() => Dispatch(args));
            }
            catch
            {
                // A malformed or interrupted client must never take the app down.
                await Task.Delay(200);
            }
        }
    }

    // Runs on the UI thread: applies the user's preferences to place the incoming
    // documents, and always leaves the target window in front.
    //
    // A launch that asks for a LAYOUT (--show, --hide) or for a window of its own
    // gets one: a forced layout describes a window, and bending an existing one to
    // it would change a window the user already arranged. Everything else lands
    // where the settings say, as tabs of the window already open when they say so.
    private static void Dispatch(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        // Anything but a window launch was dealt with by the launching process, which
        // only hands over what it could not do itself; a malformed line has already
        // been reported there.
        if (parsed.Kind != CommandKind.Gui) return;

        var options = parsed.Gui with { RestoreSession = false };
        var settings = AppSettings.Load();
        var target = WindowManager.Active ?? WindowManager.Windows.FirstOrDefault();
        var files = options.Files.Where(File.Exists).ToList();
        options = options with { Files = files };

        if (files.Count == 0)
        {
            // Launched with no file: a fresh window, or just a nudge back to the one
            // already open — unless the launch asked for something only a new window
            // can give.
            bool wantsOwn = options.NewWindow || options.ForcedLayout || options.EditMode is not null
                            || options.Document.ViewRotate is not null;
            if (!wantsOwn && settings.LaunchWithoutFile == "focus" && target is not null) target.BringToFront();
            else WindowManager.Open(options);
            return;
        }

        if (options.NewWindow || options.ForcedLayout
            || settings.OpenFromExplorer == "window" || target?.Page is not { } page)
        {
            WindowManager.Open(options);
            return;
        }

        _ = page.OpenFromLaunchAsync(options);
        target.BringToFront();
    }
}
