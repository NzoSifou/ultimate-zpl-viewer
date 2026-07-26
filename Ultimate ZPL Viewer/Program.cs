using System;
using System.Runtime.CompilerServices;

namespace Ultimate_ZPL_Viewer;

// Custom entry point (DISABLE_XAML_GENERATED_MAIN in the .csproj). It intercepts
// the elevated-helper arguments BEFORE any WinUI code, so the printer
// install/uninstall runs in an elevated instance of this very executable — the
// UAC prompt then names "Ultimate ZPL Viewer" (not PowerShell) and no console window
// appears. Everything else starts the app normally.
public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated helper mode: do the work and exit, no UI. Handled here so that
        // JITting Main never has to resolve the WinUI assembly (which cannot load
        // in the unpackaged, elevated helper context) — the WinUI startup lives in
        // a separate, non-inlined method that only the normal launch path reaches.
        if (args.Length >= 1 && args[0] == VirtualPrinterService.ElevatedInstallArg)
            return VirtualPrinterService.RunElevatedScript(args.Length >= 2 ? args[1] : null);

        StartApp();
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StartApp()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
