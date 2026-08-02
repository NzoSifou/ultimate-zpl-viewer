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
    // document, and always leaves the target window in front.
    private static void Dispatch(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        var settings = AppSettings.Load();
        var target = WindowManager.Active ?? WindowManager.Windows.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(options.FilePath) || !File.Exists(options.FilePath))
        {
            // Launched with no file: either a fresh empty window or just a nudge
            // back to the one already open.
            if (settings.LaunchWithoutFile == "focus" && target is not null) target.BringToFront();
            else WindowManager.Open(options with { RestoreSession = false });
            return;
        }

        if (settings.OpenFromExplorer == "window" || target?.Page is not { } page)
        {
            WindowManager.Open(options with { RestoreSession = false });
            return;
        }

        _ = page.OpenFileFromAnotherLaunchAsync(options.FilePath);
        target.BringToFront();
    }
}
