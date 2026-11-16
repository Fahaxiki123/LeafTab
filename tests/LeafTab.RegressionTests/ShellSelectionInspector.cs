using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LeafTab.Interop;
using LeafTab.Services;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        // Opt-in, read-only inspection of an explicitly supplied fixture folder.
        // This does not navigate, select, activate, show or send input to any window.
        private static int InspectShellSelection(string[] args)
        {
            int switchIndex = Array.IndexOf(args, "--inspect-shell-selection");
            if (switchIndex < 0 || switchIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --inspect-shell-selection FOLDER [expected absolute selected paths...]");
                return 2;
            }

            IntPtr folderPidl = IntPtr.Zero, viewPointer = IntPtr.Zero, persistPointer = IntPtr.Zero, actualFolderPidl = IntPtr.Zero;
            object? windowsObject = null, found = null;
            IFolderView2? view = null;
            IPersistFolder2? persist = null;
            IShellItemArray? selection = null;
            int initialized = InspectorNative.CoInitializeEx(IntPtr.Zero, 2);
            if (initialized < 0)
            {
                Console.Error.WriteLine("COM initialization failed: " + initialized.ToString("X8"));
                return 1;
            }
            IntPtr foregroundBefore = NativeMethods.GetForegroundWindow();
            try
            {
                string folderArgument = args[switchIndex + 1];
                if (!Path.IsPathRooted(folderArgument)) throw new ArgumentException("The fixture folder must be absolute.");
                string folder = InspectorNormalizePath(folderArgument);
                string[] expected = args.Skip(switchIndex + 2).Select(path =>
                {
                    if (!Path.IsPathRooted(path)) throw new ArgumentException("Expected selection paths must be absolute.");
                    string normalized = InspectorNormalizePath(path);
                    if (!InspectorIsDirectChild(normalized, folder))
                        throw new ArgumentException("Expected selection must belong to the explicit fixture folder.");
                    return normalized;
                }).ToArray();

                int parseHr = NativeMethods.SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _);
                if (parseHr < 0 || folderPidl == IntPtr.Zero) throw new COMException("Fixture folder could not be parsed.", parseHr);
                windowsObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!);
                var windows = (IShellWindowsRegistration)windowsObject!;
                object location = PidlManager.ToBytes(folderPidl), root = null!;
                found = windows.FindWindowSW(ref location, ref root, 1, out int rawHwnd, 1);
                bool directPidlLookup = found != null && rawHwnd != 0;
                if (!directPidlLookup)
                {
                    InspectorReleaseCom(found);
                    found = InspectorFindByFolderPath(windows, folder, out rawHwnd);
                }
                if (found == null || rawHwnd == 0)
                {
                    Console.WriteLine("INSPECT: no registered Shell window for the explicit fixture folder.");
                    return 1;
                }

                IntPtr hwnd = new IntPtr(rawHwnd);
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                Console.WriteLine("FixtureFolder=" + folder);
                Console.WriteLine("DirectPidlLookup=" + directPidlLookup + "; Lookup=" +
                    (directPidlLookup ? "FindWindowSW" : "registered-view filesystem path"));
                Console.WriteLine("HWND=" + hwnd.ToInt64() + "; PID=" + pid +
                    "; Visible=" + NativeMethods.IsWindowVisible(hwnd) + "; Minimized=" + NativeMethods.IsIconic(hwnd) +
                    "; Foreground=" + (foregroundBefore == hwnd));

                var provider = (LeafTab.Interop.IServiceProvider)found;
                Guid viewService = ShellConstants.IID_IFolderView2, viewId = ShellConstants.IID_IFolderView2;
                int queryHr = provider.QueryService(ref viewService, ref viewId, out viewPointer);
                if (queryHr < 0 || viewPointer == IntPtr.Zero) throw new COMException("Registered window does not expose its native folder view.", queryHr);
                view = (IFolderView2)Marshal.GetObjectForIUnknown(viewPointer);

                // Prove the view still belongs to the fixture before reading names.
                Guid persistId = ShellConstants.IID_IPersistFolder2;
                int folderHr = view.GetFolder(ref persistId, out persistPointer);
                if (folderHr < 0 || persistPointer == IntPtr.Zero) throw new COMException("Cannot verify the view's directory.", folderHr);
                persist = (IPersistFolder2)Marshal.GetObjectForIUnknown(persistPointer);
                int actualHr = persist.GetCurFolder(out actualFolderPidl);
                if (actualHr < 0 || actualFolderPidl == IntPtr.Zero || !InspectorPidlMatchesFolder(actualFolderPidl, folder))
                    throw new InvalidOperationException("The registered view moved away from the fixture; selection was not read.");
                Console.WriteLine("RegisteredFolderPidlMatchesParsedPidl=" + PidlManager.AreEqual(folderPidl, actualFolderPidl));

                int selectionHr = view.GetSelection(false, out selection);
                if (selectionHr < 0) throw new COMException("Could not read selection.", selectionHr);
                var actual = new List<string>();
                if (selection != null)
                {
                    int countHr = selection.GetCount(out uint count);
                    if (countHr < 0 || count > 4096) throw new InvalidOperationException("Selection count is unavailable or exceeds inspector output limits.");
                    for (uint i = 0; i < count; i++)
                    {
                        IShellItem? item = null;
                        try
                        {
                            int itemHr = selection.GetItemAt(i, out item);
                            if (itemHr < 0 || item == null) throw new COMException("Could not read selected item.", itemHr);
                            string? path = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Selection contains a non-filesystem item.");
                            string normalized = InspectorNormalizePath(path!);
                            if (!InspectorIsDirectChild(normalized, folder))
                                throw new InvalidOperationException("Selection is outside the explicit fixture; names were not printed.");
                            actual.Add(normalized);
                        }
                        finally { InspectorReleaseCom(item); }
                    }
                }

                Console.WriteLine("SelectedCount=" + actual.Count);
                foreach (string selected in actual)
                    Console.WriteLine("SelectedName=" + Path.GetFileName(selected) + "; SelectedPath=" + selected);
                bool hasExpectation = expected.Length > 0;
                bool matches = !hasExpectation || (actual.Count == expected.Length &&
                    new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase).SetEquals(expected));
                Console.WriteLine("Expectation=" + (hasExpectation ? (matches ? "MATCH" : "MISMATCH") : "not supplied"));
                Console.WriteLine("ForegroundUnchangedDuringRead=" + (NativeMethods.GetForegroundWindow() == foregroundBefore));
                return matches ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("INSPECT FAILED: " + ex.GetType().Name + "; HRESULT=" + ex.HResult.ToString("X8") + "; " + ex.Message);
                return 1;
            }
            finally
            {
                InspectorReleaseCom(selection);
                InspectorReleaseCom(persist);
                if (persistPointer != IntPtr.Zero) Marshal.Release(persistPointer);
                InspectorReleaseCom(view);
                if (viewPointer != IntPtr.Zero) Marshal.Release(viewPointer);
                InspectorReleaseCom(found);
                InspectorReleaseCom(windowsObject);
                PidlManager.Free(ref actualFolderPidl);
                PidlManager.Free(ref folderPidl);
                InspectorNative.CoUninitialize();
            }
        }

        private static string InspectorNormalizePath(string path)
        {
            string full = Path.GetFullPath(path).Replace('/', '\\');
            string root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd('\\') : full;
        }

        private static bool InspectorIsDirectChild(string item, string folder)
        {
            string? parent = Path.GetDirectoryName(item);
            return parent != null && string.Equals(InspectorNormalizePath(parent), folder, StringComparison.OrdinalIgnoreCase);
        }

        // Known folders can have both a namespace PIDL and a filesystem PIDL.
        // A failed exact ShellWindows lookup must not be mistaken for no registered
        // view. Enumerate metadata only and read selection only after a path match.
        private static object? InspectorFindByFolderPath(IShellWindowsRegistration windows, string folder, out int hwnd)
        {
            hwnd = 0;
            object? matching = null;
            int count = Math.Min(windows.Count, 1024);
            for (int index = 0; index < count; index++)
            {
                object? candidate = null;
                IntPtr candidateViewPointer = IntPtr.Zero, candidatePersistPointer = IntPtr.Zero, candidatePidl = IntPtr.Zero;
                IFolderView2? candidateView = null;
                IPersistFolder2? candidatePersist = null;
                try
                {
                    candidate = windows.Item(index);
                    if (candidate is not LeafTab.Interop.IServiceProvider provider) continue;
                    Guid viewService = ShellConstants.IID_IFolderView2, viewId = ShellConstants.IID_IFolderView2;
                    if (provider.QueryService(ref viewService, ref viewId, out candidateViewPointer) < 0 || candidateViewPointer == IntPtr.Zero) continue;
                    candidateView = (IFolderView2)Marshal.GetObjectForIUnknown(candidateViewPointer);
                    Guid persistId = ShellConstants.IID_IPersistFolder2;
                    if (candidateView.GetFolder(ref persistId, out candidatePersistPointer) < 0 || candidatePersistPointer == IntPtr.Zero) continue;
                    candidatePersist = (IPersistFolder2)Marshal.GetObjectForIUnknown(candidatePersistPointer);
                    if (candidatePersist.GetCurFolder(out candidatePidl) < 0 || candidatePidl == IntPtr.Zero ||
                        !InspectorPidlMatchesFolder(candidatePidl, folder)) continue;
                    if (candidate is not IShellWebBrowserApp browser) continue;
                    IntPtr candidateHwnd = browser.HWND;
                    if (candidateHwnd == IntPtr.Zero) continue;

                    // Prefer LeafTab when another manager also has the fixture open.
                    // No unrelated window names, locations or processes are printed.
                    NativeMethods.GetWindowThreadProcessId(candidateHwnd, out uint candidatePid);
                    string? executable = NativeMethods.TryGetProcessImagePath(candidatePid);
                    bool isLeafTab = string.Equals(Path.GetFileName(executable), "LeafTab.exe", StringComparison.OrdinalIgnoreCase);
                    if (matching == null || isLeafTab)
                    {
                        InspectorReleaseCom(matching);
                        matching = candidate;
                        candidate = null;
                        hwnd = unchecked((int)candidateHwnd.ToInt64());
                        if (isLeafTab) break;
                    }
                }
                catch (COMException) { }
                catch (InvalidCastException) { }
                finally
                {
                    PidlManager.Free(ref candidatePidl);
                    InspectorReleaseCom(candidatePersist);
                    if (candidatePersistPointer != IntPtr.Zero) Marshal.Release(candidatePersistPointer);
                    InspectorReleaseCom(candidateView);
                    if (candidateViewPointer != IntPtr.Zero) Marshal.Release(candidateViewPointer);
                    InspectorReleaseCom(candidate);
                }
            }
            return matching;
        }

        private static bool InspectorPidlMatchesFolder(IntPtr pidl, string folder)
        {
            Guid itemId = ShellConstants.IID_IShellItem;
            IShellItem? item = null;
            try
            {
                if (NativeMethods.SHCreateItemFromIDList(pidl, ref itemId, out item) < 0 || item == null) return false;
                string? path = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                return !string.IsNullOrWhiteSpace(path) && string.Equals(InspectorNormalizePath(path!), folder, StringComparison.OrdinalIgnoreCase);
            }
            finally { InspectorReleaseCom(item); }
        }

        private static void InspectorReleaseCom(object? value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private static class InspectorNative
        {
            [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint mode);
            [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        }
    }
}
