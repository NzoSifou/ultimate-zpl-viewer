using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// Sends raw data straight to a Windows printer through the spooler ("RAW"
// datatype). This is the standard way to print ZPL: the label printer receives
// the commands untouched and renders them itself — no driver rasterization.
public static class RawPrinterService
{
    public static void SendRaw(string printerName, string data, string documentName)
    {
        var bytes = Encoding.UTF8.GetBytes(data);

        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var docInfo = new DOC_INFO_1
            {
                pDocName    = documentName,
                pOutputFile = null,
                pDatatype   = "RAW",
            };

            if (StartDocPrinter(printer, 1, ref docInfo) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                if (!StartPagePrinter(printer))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var unmanaged = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                    if (!WritePrinter(printer, unmanaged, bytes.Length, out var written) || written != bytes.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally
                {
                    Marshal.FreeHGlobal(unmanaged);
                    EndPagePrinter(printer);
                }
            }
            finally
            {
                EndDocPrinter(printer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
}
