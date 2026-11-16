using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Services;
using Microsoft.Win32;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static void RunShellProtocolTests()
        {
            Test("Default integration health detects every managed command and COM server", () => RegistrySandbox((classes, backup) =>
            {
                string[] paths = {
                    @"Directory\shell\open\command", @"Drive\shell\LeafTabOpen\command", @"Drive\shell\open\command",
                    @"Folder\shell\LeafTabOpen\command", @"Folder\shell\open\command", @"Folder\shell\explore\command",
                    @"Folder\shell\opennewwindow\command",
                    @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\open\command",
                    @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command",
                    @"CLSID\{8E4C7A29-3D51-4F82-9B14-6A5E20C91D73}\LocalServer32",
                    @"CLSID\{8E4C7A29-3D51-4F82-9B14-6A5E20C91D74}\LocalServer32"
                };
                foreach (string path in paths)
                {
                    Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false).Ok);
                    Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe));
                    Registry.CurrentUser.DeleteSubKeyTree(classes + "\\" + path, false);
                    Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe), path + " was not checked.");
                    Check(ShellIntegrationService.RepairRegisteredPathsIfMovedCore(Exe, classes, backup, false));
                    Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe), path + " was not repaired.");
                }
            }));
            Test("Default integration repair preserves another manager's drive verb", () => RegistrySandbox((classes, backup) =>
            {
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false).Ok);
                using (var key = Registry.CurrentUser.OpenSubKey(classes + @"\Drive\shell", true)) key!.SetValue("", "OtherManager");
                Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe));
                Check(!ShellIntegrationService.RepairRegisteredPathsIfMovedCore(Exe, classes, backup, false));
                Check(Read(classes + @"\Drive\shell") == "OtherManager");
            }));
            Test("Shell execute reports callback failures instead of false success", () =>
            {
                var command = new LeafTabExecuteCommand(_ => throw new InvalidOperationException("fixture"), false);
                command.SetParameters(Root);
                Check(command.Execute() < 0);
            });
            Test("Shell parameters preserve select-file semantics and comma-containing paths", () =>
            {
                string folder = MakeDirectory("shell 参数, with spaces");
                string file = Path.Combine(folder, "selected 文件.txt");
                File.WriteAllText(file, "fixture");
                LaunchActionRequest? received = null;
                var command = new LeafTabExecuteCommand(r => received = r, false);
                command.SetParameters("/select,\"" + file + "\"");
                Check(command.Execute() == 0 && received != null && received.Targets.Count == 1);
                Check(received!.Targets[0].FolderOrAlias == folder && received.Targets[0].SelectItemName == Path.GetFileName(file));
            });
            Test("Expired Shell view registration refuses queries without activating", () =>
            {
                int acquired = 0, activated = 0, invalidated = 0;
                var browser = new ShellSelectionBrowser(new IntPtr(123), () => { acquired++; return IntPtr.Zero; },
                    () => activated++, () => false, () => invalidated++);
                Guid service = ShellConstants.SID_STopLevelBrowser, iid = ShellConstants.IID_IShellBrowser;
                Check(browser.QueryService(ref service, ref iid, out IntPtr result) < 0 && result == IntPtr.Zero);
                Check(browser.QueryActiveShellView(out IntPtr view) < 0 && view == IntPtr.Zero);
                var automation = (IShellWebBrowserApp)browser;
                foreach (Action read in new Action[] { () => { _ = automation.HWND; }, () => { _ = automation.Document; }, () => { _ = browser.HWND; } })
                {
                    bool rejected = false;
                    try { read(); } catch (COMException ex) { rejected = ex.ErrorCode == unchecked((int)0x80004002); }
                    Check(rejected, "An expired registration still exposed its activation handle or document.");
                }
                Check(acquired == 0 && activated == 0 && invalidated > 0);
            });
            Test("Shell automation metadata and view queries never request foreground activation", () =>
            {
                int activated = 0;
                var browser = new ShellSelectionBrowser(new IntPtr(123), () => IntPtr.Zero, () => activated++, () => true);
                var automation = (IShellWebBrowserApp)browser;
                Check(automation.HWND == new IntPtr(123) && automation.Document == browser && browser.HWND == 123);
                Check(browser.QueryActiveShellView(out IntPtr view) < 0 && view == IntPtr.Zero);
                Guid service = ShellConstants.SID_STopLevelBrowser, iid = ShellConstants.IID_IShellBrowser;
                Check(browser.QueryService(ref service, ref iid, out IntPtr result) == 0);
                if (result != IntPtr.Zero) Marshal.Release(result);
                Check(activated == 0);
            });
        }

        private static int RunShellSelectionNativeTests()
        {
            int hr = ShellSelectionNative.CoInitializeEx(IntPtr.Zero, 2);
            if (hr < 0) throw new COMException("Cannot initialize native Shell test apartment.", hr);
            var frame = new DispatcherFrame();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var deadline = new Timer(_ => { Console.Error.WriteLine("FAIL: Shell selection native regression exceeded 90 seconds."); Environment.Exit(1); },
                null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyShellSelectionNativeAsync(); }
                catch (Exception ex) { _failed++; Console.WriteLine("FAIL: Native shell selection — " + ex); }
                finally { frame.Continue = false; }
            }));
            try { Dispatcher.PushFrame(frame); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); ShellSelectionNative.CoUninitialize(); }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static async Task VerifyShellSelectionNativeAsync()
        {
            string folder = MakeDirectory("shell-select-native");
            string destination = MakeDirectory("shell-select-destination");
            string first = Path.Combine(folder, "一号, file.txt");
            string second = Path.Combine(folder, "second.txt");
            string childFolder = Path.Combine(folder, "selected folder");
            string afterNavigation = Path.Combine(destination, "destination.txt");
            File.WriteAllText(first, "one"); File.WriteAllText(second, "two"); File.WriteAllText(afterNavigation, "three");
            Directory.CreateDirectory(childFolder);
            IntPtr folderPidl = PidlManager.TryParseLocalOrShellFast(folder);
            IntPtr destinationPidl = PidlManager.TryParseLocalOrShellFast(destination);
            IntPtr parent = IntPtr.Zero, builtHost = IntPtr.Zero;
            ExplorerBrowserHost? host = null;
            using var registration = new ShellViewRegistration();
            object? windowsObject = null;
            try
            {
                parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab hidden Shell selection regression",
                    unchecked((int)0x82000000), -20000, -20000, 960, 640,
                    new IntPtr(-3) /* HWND_MESSAGE: API may restore a normal parent */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Check(parent != IntPtr.Zero && folderPidl != IntPtr.Zero && destinationPidl != IntPtr.Zero);
                host = new ExplorerBrowserHost(folderPidl);
                host.Measure(new Size(960, 640)); host.Arrange(new Rect(0, 0, 960, 640));
                int navigationCount = 0;
                byte[] lastNavigation = Array.Empty<byte>();
                host.NavigationCompleted += (_, e) => { navigationCount++; lastNavigation = PidlManager.ToBytes(e.FolderPidl); };
                builtHost = ((HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(null, parent) })!).Handle;
                host.SyncBrowserRect();
                Check(await WaitNativeConditionAsync(() => navigationCount > 0 && host.DirectUiHwnd != IntPtr.Zero));
                Check(registration.Update(parent, folderPidl, host.AcquireShellViewPointer), "Could not register test view.");
                windowsObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!);
                var windows = (IShellWindowsRegistration)windowsObject!;
                Test("ShellWindows discovers a registered native LeafTab view by its PIDL", () =>
                    Check(FindRegisteredShellWindow(windows, folderPidl) == unchecked((int)parent.ToInt64())));
                // Do not launch the native API if lookup fails: it would open an unrelated
                // desktop window instead of exercising our isolated hidden view.
                Check(FindRegisteredShellWindow(windows, folderPidl) == unchecked((int)parent.ToInt64()));

                foreach (var scenario in new[] {
                    ("absolute file PIDL (cidl=0)", new[] { first }, true),
                    ("parent plus child PIDL (cidl=1)", new[] { second }, false),
                    ("multiple child PIDLs (cidl=2)", new[] { first, second }, false),
                    ("folder item selection", new[] { childFolder }, true)
                })
                {
                    int apiHr = await CallNativeSelectionAsync(folder, scenario.Item2, scenario.Item3);
                    bool selected = await WaitNativeConditionAsync(() => SameSelectedPaths(host.GetSelectedPaths(), scenario.Item2), 2500);
                    Test("SHOpenFolderAndSelectItems completes " + scenario.Item1, () =>
                        Check(apiHr == 0 && selected, "API HRESULT=" + apiHr.ToString("X8") + "; selected=" + selected));
                }

                var browser = NativeField<IExplorerBrowser>(host, "_browser");
                await NavigatePrimaryForTestAsync(browser, destinationPidl, () => navigationCount, () => lastNavigation);
                Check(registration.Update(parent, destinationPidl, host.AcquireShellViewPointer));
                Test("OnNavigate removes the old location and exposes the new active directory", () =>
                {
                    Check(FindRegisteredShellWindow(windows, folderPidl) != unchecked((int)parent.ToInt64()));
                    Check(FindRegisteredShellWindow(windows, destinationPidl) == unchecked((int)parent.ToInt64()));
                });
                int navigatedHr = await CallNativeSelectionAsync(destination, new[] { afterNavigation }, true);
                bool navigatedSelected = await WaitNativeConditionAsync(() => SameSelectedPaths(host.GetSelectedPaths(), new[] { afterNavigation }), 2500);
                Test("Native selection uses the view after folder navigation", () => Check(navigatedHr == 0 && navigatedSelected));
                registration.Dispose();
                Test("Revoking a view removes its location from ShellWindows", () =>
                    Check(FindRegisteredShellWindow(windows, destinationPidl) != unchecked((int)parent.ToInt64())));
                Test("Native selection regression leaves its parent hidden", () => Check(!NativeMethods.IsWindowVisible(parent)));
            }
            finally
            {
                registration.Dispose();
                if (windowsObject != null && Marshal.IsComObject(windowsObject)) Marshal.ReleaseComObject(windowsObject);
                if (host != null)
                {
                    if (builtHost != IntPtr.Zero) typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(host, new object[] { new HandleRef(host, builtHost) });
                    host.Dispose();
                }
                if (parent != IntPtr.Zero) NativeMethods.DestroyWindow(parent);
                PidlManager.Free(ref folderPidl); PidlManager.Free(ref destinationPidl);
            }
        }

        private static int FindRegisteredShellWindow(IShellWindowsRegistration windows, IntPtr pidl)
        {
            object location = PidlManager.ToBytes(pidl), root = null!;
            object? found = windows.FindWindowSW(ref location, ref root, 1, out int hwnd, 1 /* SWFO_NEEDDISPATCH */);
            if (found != null && Marshal.IsComObject(found)) Marshal.ReleaseComObject(found);
            return found == null ? 0 : hwnd;
        }

        private static bool SameSelectedPaths(System.Collections.Generic.List<string> actual, string[] expected) =>
            actual.Count == expected.Length && expected.All(e => actual.Any(a => string.Equals(a, e, StringComparison.OrdinalIgnoreCase)));

        private static Task<int> CallNativeSelectionAsync(string folder, string[] items, bool absoluteSingle) => Task.Run(() =>
        {
            int initialized = ShellSelectionNative.CoInitializeEx(IntPtr.Zero, 0);
            if (initialized < 0) return initialized;
            IntPtr pidl = IntPtr.Zero, array = IntPtr.Zero;
            var children = new IntPtr[items.Length];
            var timer = Stopwatch.StartNew();
            try
            {
                pidl = PidlManager.TryParseLocalOrShellFast(absoluteSingle ? items[0] : folder);
                if (!absoluteSingle)
                {
                    array = Marshal.AllocCoTaskMem(IntPtr.Size * items.Length);
                    for (int i = 0; i < items.Length; i++)
                    {
                        children[i] = PidlManager.TryParseLocalOrShellFast(items[i]);
                        Marshal.WriteIntPtr(array, i * IntPtr.Size, ShellSelectionNative.ILFindLastID(children[i]));
                    }
                }
                int result = ShellSelectionNative.SHOpenFolderAndSelectItems(pidl, absoluteSingle ? 0u : (uint)items.Length, array, 0);
                Console.WriteLine($"Native select: count={(absoluteSingle ? 0 : items.Length)}, hr={result:X8}, elapsedMs={timer.ElapsedMilliseconds}");
                return result;
            }
            finally
            {
                foreach (IntPtr child in children) if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child);
                PidlManager.Free(ref pidl);
                if (array != IntPtr.Zero) Marshal.FreeCoTaskMem(array);
                ShellSelectionNative.CoUninitialize();
            }
        });

        private static class ShellSelectionNative
        {
            [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint mode);
            [DllImport("ole32.dll")] internal static extern void CoUninitialize();
            [DllImport("shell32.dll")] internal static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr items, uint flags);
            [DllImport("shell32.dll")] internal static extern IntPtr ILFindLastID(IntPtr pidl);
        }
    }
}
