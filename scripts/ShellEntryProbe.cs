// Standalone .NET Framework probe for the *external* Windows Shell entry points.
// Compile with csc /platform:x64 or /platform:x86. It changes no associations.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class ShellEntryProbe
{
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint attributesOut);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr parent, uint count, IntPtr[] children, uint flags);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ShellEntryProbe shell|explorer|api|multi PATH [CHILD_PATH ...]");
            return 2;
        }
        Console.WriteLine(DateTime.UtcNow.ToString("O") + " mode=" + args[0] + " bitness=" + (IntPtr.Size * 8));
        try
        {
            if (args[0] == "shell")
            {
                Process.Start(new ProcessStartInfo(args[1]) { UseShellExecute = true, Verb = "open" });
                return 0;
            }
            if (args[0] == "explorer")
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    "/select,\"" + args[1] + "\"") { UseShellExecute = false });
                return 0;
            }
            if (args[0] != "api" && args[0] != "multi") return 2;
            int initialized = CoInitializeEx(IntPtr.Zero, 2);
            Marshal.ThrowExceptionForHR(initialized);
            try
            {
                IntPtr parent = Parse(args[1]);
                var absoluteChildren = new IntPtr[Math.Max(0, args.Length - 2)];
                var relativeChildren = new IntPtr[absoluteChildren.Length];
                try
                {
                    for (int i = 0; i < absoluteChildren.Length; i++)
                    {
                        absoluteChildren[i] = Parse(args[i + 2]);
                        relativeChildren[i] = ILFindLastID(absoluteChildren[i]);
                    }
                    var timer = Stopwatch.StartNew();
                    int hr = SHOpenFolderAndSelectItems(parent, (uint)relativeChildren.Length,
                        relativeChildren.Length == 0 ? null : relativeChildren, 0);
                    Console.WriteLine("SHOpenFolderAndSelectItems=0x" + hr.ToString("X8") + " elapsedMs=" + timer.ElapsedMilliseconds);
                    return hr >= 0 ? 0 : 1;
                }
                finally
                {
                    foreach (IntPtr child in absoluteChildren) if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child);
                    Marshal.FreeCoTaskMem(parent);
                }
            }
            finally { CoUninitialize(); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            return 1;
        }
    }

    private static IntPtr Parse(string path)
    {
        IntPtr pidl;
        uint attributes;
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes));
        return pidl;
    }
}
