using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        // This opt-in test owns a hidden native parent, never calls Window.Show,
        // and sends no desktop input. Real Shell navigation callbacks still run.
        private static int RunSearchNavigationNativeTests()
        {
            int oleHr = SearchNavNative.OleInitialize(IntPtr.Zero);
            if (oleHr < 0) throw new COMException("Cannot initialize the native Shell test apartment.", oleHr);
            var frame = new DispatcherFrame();
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var deadline = new Timer(_ =>
            {
                Console.Error.WriteLine("FAIL: Native search navigation test timed out after 60 seconds.");
                Environment.Exit(1);
            }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyNativeSearchNavigationAsync(); }
                catch (Exception ex) { _failed++; Console.WriteLine("FAIL: Native search navigation setup — " + ex); }
                finally { frame.Continue = false; }
            }));
            try { Dispatcher.PushFrame(frame); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
                SearchNavNative.OleUninitialize();
            }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static async Task VerifyNativeSearchNavigationAsync()
        {
            string source = Path.Combine(Root, "search-navigation", "source");
            string destination = Path.Combine(Root, "search-navigation", "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(source, "士兵属性.txt"), "search fixture");
            File.WriteAllText(Path.Combine(source, "士兵等级.txt"), "search fixture");
            Directory.CreateDirectory(Path.Combine(source, "士兵文件夹"));
            File.WriteAllText(Path.Combine(destination, "destination-marker.txt"), "navigation fixture");

            uint processId = (uint)Process.GetCurrentProcess().Id;
            bool testTookForeground = false;
            var focusMonitor = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            focusMonitor.Tick += (_, _) =>
            {
                SearchNavNative.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint foregroundProcess);
                testTookForeground |= foregroundProcess == processId;
            };
            focusMonitor.Start();
            IntPtr parent = IntPtr.Zero;
            IntPtr sourcePidl = PidlManager.TryParseLocalOrShellFast(source);
            IntPtr destinationPidl = PidlManager.TryParseLocalOrShellFast(destination);
            IntPtr thisPcPidl = PidlManager.CreateThisPcPidl();
            IntPtr childFolderPidl = PidlManager.TryParseLocalOrShellFast(Path.Combine(source, "士兵文件夹"));
            ExplorerBrowserHost? host = null;
            IntPtr builtHost = IntPtr.Zero;
            try
            {
                Check(sourcePidl != IntPtr.Zero && destinationPidl != IntPtr.Zero && thisPcPidl != IntPtr.Zero && childFolderPidl != IntPtr.Zero);
                parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab hidden search regression",
                    unchecked((int)0x82000000), -20000, -20000, 960, 640,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Check(parent != IntPtr.Zero, "Hidden native test parent could not be created.");
                host = new ExplorerBrowserHost(sourcePidl);
                host.Measure(new Size(960, 640));
                host.Arrange(new Rect(0, 0, 960, 640));
                byte[] lastNavigation = Array.Empty<byte>();
                int navigationCount = 0;
                host.NavigationCompleted += (_, e) =>
                {
                    lastNavigation = PidlManager.ToBytes(e.FolderPidl);
                    navigationCount++;
                };
                var built = (HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(null, parent) })!;
                builtHost = built.Handle;
                host.SyncBrowserRect();
                Check(await WaitNativeConditionAsync(() => navigationCount > 0 && host.DirectUiHwnd != IntPtr.Zero),
                    "Initial native source view did not become ready.");
                var primary = NativeField<IExplorerBrowser>(host, "_browser");
                IntPtr browseHost = NativeField<IntPtr>(host, "_browseHostHwnd");
                IntPtr originalTree = FindNativeChild(browseHost, "SysTreeView32");
                Check(originalTree != IntPtr.Zero, "The ordinary browser did not create its native navigation tree.");

                await StartNativeSearchAsync(host, sourcePidl, source);
                IntPtr searchHost = NativeField<IntPtr>(host, "_searchHostHwnd");
                var results = NativeField<IExplorerBrowser>(host, "_searchBrowser");
                results.GetOptions(out EXPLORER_BROWSER_OPTIONS searchOptions);
                primary.GetOptions(out EXPLORER_BROWSER_OPTIONS activeOptions);
                Console.WriteLine($"Native options: primary={activeOptions}, search={searchOptions}");
                Test("Search retains the ordinary navigation tree and creates no results navigation tree", () =>
                {
                    Check(FindNativeChild(browseHost, "SysTreeView32") == originalTree, "Search replaced the ordinary navigation tree.");
                    Check(HasVisibleStyle(browseHost), "Search hid the ordinary browser and its navigation pane.");
                    Check(FindNativeChild(searchHost, "SysTreeView32") == IntPtr.Zero, "The temporary results container still has a navigation tree.");
                    Check((searchOptions & EXPLORER_BROWSER_OPTIONS.EBO_SHOWFRAMES) == 0, "Search still asks Shell to display a navigation pane.");
                    Check(HasVisibleStyle(searchHost), "Search result content is hidden.");
                    Check(!HasVisibleStyle(GetNativeViewWindow(primary)),
                        "The covered source list is still reachable behind search results.");
                });
                Test("Search content covers the file view without covering navigation", () => AssertNativeSearchBounds(primary, searchHost, originalTree));

                NativeMethods.MoveWindow(builtHost, 0, 0, 1180, 720, false);
                host.SyncBrowserRect();
                await Task.Delay(100);
                Test("Search content stays aligned after resizing the host", () => AssertNativeSearchBounds(primary, searchHost, originalTree));

                // A splitter drag changes the native file view's rectangle without
                // resizing the outer HwndHost. Exercise that notification directly.
                IntPtr nativeView = GetNativeViewWindow(primary);
                IntPtr nativeParent = SearchNavNative.GetParent(nativeView);
                Check(NativeMethods.GetWindowRect(nativeView, out RECT originalViewRect));
                Check(NativeMethods.GetWindowRect(searchHost, out RECT originalSearchRect));
                var parentOrigin = new POINT(0, 0);
                Check(NativeMethods.ClientToScreen(nativeParent, ref parentOrigin));
                int viewX = originalViewRect.Left - parentOrigin.X;
                int viewY = originalViewRect.Top - parentOrigin.Y;
                NativeMethods.MoveWindow(nativeView, viewX + 40, viewY, originalViewRect.Width - 40, originalViewRect.Height, false);
                await Task.Delay(100);
                Test("Native splitter-style layout updates the overlay without an outer host resize", () =>
                {
                    Check(NativeMethods.GetWindowRect(searchHost, out RECT movedSearchRect));
                    Check(movedSearchRect.Left == originalSearchRect.Left + 40,
                        "The search overlay did not follow the native file-view layout notification.");
                    AssertNativeSearchBounds(primary, searchHost, originalTree);
                });
                NativeMethods.MoveWindow(nativeView, viewX, viewY, originalViewRect.Width, originalViewRect.Height, false);
                await Task.Delay(80);

                // Select a real native tree item, without UI Automation or SendInput.
                // Some Shell builds intentionally ignore programmatic selection; report
                // that separately and always exercise the native browser callback below.
                string thisPcName = PidlManager.GetDisplayInfo(thisPcPidl).Title;
                IntPtr thisPcItem = FindTreeItem(originalTree, thisPcName);
                int beforeTree = navigationCount;
                bool treeNavigated = false;
                if (thisPcItem != IntPtr.Zero)
                {
                    SearchNavNative.SendMessage(originalTree, 0x110B, new IntPtr(9), thisPcItem);
                    treeNavigated = await WaitNativeConditionAsync(() => navigationCount > beforeTree && PidlBytesEqual(lastNavigation, thisPcPidl), 2000);
                }
                Console.WriteLine("Native tree selection: " + (treeNavigated ? "navigation callback received" : "not dispatched by this Shell build; direct native-browser navigation follows"));
                if (treeNavigated)
                {
                    Test("Selecting a native left navigation item exits search and reveals its destination", () =>
                    {
                        Check(!host.IsShowingSearchResults && !HasVisibleStyle(searchHost) && HasVisibleStyle(browseHost));
                    });
                    await NavigatePrimaryForTestAsync(primary, sourcePidl, () => navigationCount, () => lastNavigation);
                    await StartNativeSearchAsync(host, sourcePidl, source);
                }

                // Calling host.NavigateToPidl would preemptively exit search and mask
                // the defect. Invoke the native primary browser, as its navigation tree does.
                await NavigatePrimaryForTestAsync(primary, destinationPidl, () => navigationCount, () => lastNavigation);
                Test("Native primary navigation exits search and shows the destination folder", () =>
                {
                    Check(!host.IsShowingSearchResults, "Native primary navigation left search mode enabled.");
                    Check(HasVisibleStyle(browseHost) && !HasVisibleStyle(searchHost), "Search overlay still obscures the newly navigated folder.");
                    Check(HasVisibleStyle(GetNativeViewWindow(primary)), "The destination file view was not restored.");
                    Check(host.GetDisplayedItemNames(20).Exists(n => n.StartsWith("destination-marker", StringComparison.OrdinalIgnoreCase)),
                        "The active content is not the destination folder.");
                    Check(FindNativeChild(browseHost, "SysTreeView32") == originalTree, "Normal navigation unnecessarily replaced the navigation tree.");
                });

                await NavigatePrimaryForTestAsync(primary, sourcePidl, () => navigationCount, () => lastNavigation);
                await StartNativeSearchAsync(host, sourcePidl, source);
                await NavigatePrimaryForTestAsync(primary, sourcePidl, () => navigationCount, () => lastNavigation);
                Test("Navigating to the search source itself exits search", () =>
                {
                    Check(!host.IsShowingSearchResults && !HasVisibleStyle(searchHost), "Selecting the current source folder failed to exit search.");
                    primary.GetOptions(out EXPLORER_BROWSER_OPTIONS optionsAfterNavigation);
                    Check((optionsAfterNavigation & EXPLORER_BROWSER_OPTIONS.EBO_ALWAYSNAVIGATE) == 0,
                        "Search-only navigation options leaked into ordinary browsing.");
                });

                await StartNativeSearchAsync(host, sourcePidl, source);
                int repeatCount = await host.StartSearchAsync("士兵等级", sourcePidl, source);
                Test("Repeated search reuses content without adding a navigation tree", () =>
                {
                    Check(repeatCount == 1 && host.IsShowingSearchResults);
                    Check(FindNativeChild(NativeField<IntPtr>(host, "_searchHostHwnd"), "SysTreeView32") == IntPtr.Zero);
                    Check(FindNativeChild(browseHost, "SysTreeView32") == originalTree && HasVisibleStyle(browseHost));
                    AssertNativeSearchBounds(primary, searchHost, originalTree);
                });
                bool exited = host.ExitSearchMode();
                Test("Clearing search restores the source folder and normal navigation options", () =>
                {
                    Check(exited && !host.IsShowingSearchResults && !HasVisibleStyle(searchHost) && HasVisibleStyle(browseHost));
                    Check(HasVisibleStyle(GetNativeViewWindow(primary)), "Clearing search did not restore the source file view.");
                    primary.GetOptions(out EXPLORER_BROWSER_OPTIONS optionsAfterExit);
                    Check((optionsAfterExit & EXPLORER_BROWSER_OPTIONS.EBO_ALWAYSNAVIGATE) == 0);
                });

                await StartNativeSearchAsync(host, sourcePidl, source);
                Check(await host.SelectChildItemWhenReadyAsync("士兵属性.txt"), "Could not select the fixture file in the native results view.");
                int fileDefaultResult = InvokeSearchDefaultCommand(host, results);
                Test("Search file default commands remain available to the Windows Shell", () =>
                {
                    Check(fileDefaultResult == 1 && host.IsShowingSearchResults,
                        "A regular file default command was consumed as folder navigation.");
                });
                Check(await host.SelectChildItemWhenReadyAsync("士兵文件夹"), "Could not select the fixture folder in the native results view.");
                int beforeDefault = navigationCount;
                int folderDefaultResult = InvokeSearchDefaultCommand(host, results);
                bool folderOpened = await WaitNativeConditionAsync(() => navigationCount > beforeDefault && PidlBytesEqual(lastNavigation, childFolderPidl));
                Test("Opening a search result folder navigates in place and exits search", () =>
                {
                    Check(folderDefaultResult == ShellConstants.S_OK && folderOpened,
                        "The results-view folder default command did not navigate to the selected folder.");
                    Check(!host.IsShowingSearchResults && !HasVisibleStyle(searchHost) && HasVisibleStyle(browseHost));
                });
                Test("Native search regression keeps its parent hidden and never takes foreground focus", () =>
                {
                    Check(!NativeMethods.IsWindowVisible(parent), "The diagnostic parent became visible.");
                    SearchNavNative.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint foregroundProcess);
                    Check(!testTookForeground && foregroundProcess != processId,
                        "The hidden native regression took foreground focus.");
                });
            }
            finally
            {
                focusMonitor.Stop();
                if (host != null)
                {
                    if (builtHost != IntPtr.Zero)
                        typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(host, new object[] { new HandleRef(host, builtHost) });
                    host.Dispose();
                }
                if (parent != IntPtr.Zero) NativeMethods.DestroyWindow(parent);
                PidlManager.Free(ref sourcePidl);
                PidlManager.Free(ref destinationPidl);
                PidlManager.Free(ref thisPcPidl);
                PidlManager.Free(ref childFolderPidl);
            }
        }

        private static async Task StartNativeSearchAsync(ExplorerBrowserHost host, IntPtr sourcePidl, string source)
        {
            int count = await host.StartSearchAsync("士兵", sourcePidl, source);
            Check(count == 3 && host.IsShowingSearchResults, $"Expected three fixture search results; got {count}.");
            host.SyncBrowserRect();
            await Task.Delay(80);
        }

        private static async Task NavigatePrimaryForTestAsync(IExplorerBrowser browser, IntPtr pidl, Func<int> count, Func<byte[]> current)
        {
            int before = count();
            int hr = browser.BrowseToIDList(pidl, ShellConstants.SBSP_ABSOLUTE | ShellConstants.SBSP_SAMEBROWSER);
            Check(hr >= 0, "Native navigation failed with HRESULT " + hr.ToString("X8"));
            Check(await WaitNativeConditionAsync(() => count() > before && PidlBytesEqual(current(), pidl)), "Native navigation did not publish the requested destination.");
            await Task.Delay(80);
        }

        private static async Task<bool> WaitNativeConditionAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.ElapsedMilliseconds < timeoutMs) await Task.Delay(25);
            return condition();
        }

        private static bool PidlBytesEqual(byte[] actual, IntPtr expected)
        {
            IntPtr actualPidl = PidlManager.FromBytes(actual);
            try { return actualPidl != IntPtr.Zero && PidlManager.AreEqual(actualPidl, expected); }
            finally { PidlManager.Free(ref actualPidl); }
        }

        private static T NativeField<T>(ExplorerBrowserHost host, string name) =>
            (T)typeof(ExplorerBrowserHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

        private static int InvokeSearchDefaultCommand(ExplorerBrowserHost host, IExplorerBrowser results)
        {
            object site = NativeField<object>(host, "_searchPaneSite");
            MethodInfo? command = site.GetType().GetMethod("OnDefaultCommand", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Check(command != null, "The search site does not provide an in-place default command handler.");
            Guid iid = ShellConstants.IID_IShellView;
            Check(results.GetCurrentView(ref iid, out IntPtr viewPtr) == ShellConstants.S_OK && viewPtr != IntPtr.Zero);
            try { return (int)command!.Invoke(site, new object[] { viewPtr })!; }
            finally { Marshal.Release(viewPtr); }
        }

        private static IntPtr GetNativeViewWindow(IExplorerBrowser browser)
        {
            Guid iid = ShellConstants.IID_IShellView;
            Check(browser.GetCurrentView(ref iid, out IntPtr viewPtr) == ShellConstants.S_OK && viewPtr != IntPtr.Zero);
            var view = (IShellView)Marshal.GetObjectForIUnknown(viewPtr);
            try
            {
                Check(view.GetWindow(out IntPtr hwnd) == ShellConstants.S_OK && hwnd != IntPtr.Zero);
                return hwnd;
            }
            finally { Marshal.ReleaseComObject(view); Marshal.Release(viewPtr); }
        }

        private static bool HasVisibleStyle(IntPtr hwnd) =>
            hwnd != IntPtr.Zero && (SearchNavNative.GetWindowLong(hwnd, -16) & 0x10000000) != 0;

        private static IntPtr FindNativeChild(IntPtr parent, string className)
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumChildWindows(parent, (child, _) =>
            {
                var name = new StringBuilder(128);
                NativeMethods.GetClassNameW(child, name, name.Capacity);
                if (!string.Equals(name.ToString(), className, StringComparison.Ordinal)) return true;
                found = child;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static void AssertNativeSearchBounds(IExplorerBrowser primary, IntPtr searchHost, IntPtr tree)
        {
            Guid iid = ShellConstants.IID_IShellView;
            Check(primary.GetCurrentView(ref iid, out IntPtr viewPtr) == ShellConstants.S_OK && viewPtr != IntPtr.Zero);
            var view = (IShellView)Marshal.GetObjectForIUnknown(viewPtr);
            try
            {
                Check(view.GetWindow(out IntPtr viewHwnd) == ShellConstants.S_OK);
                Check(NativeMethods.GetWindowRect(viewHwnd, out RECT fileRect));
                Check(NativeMethods.GetWindowRect(searchHost, out RECT resultRect));
                Check(NativeMethods.GetWindowRect(tree, out RECT treeRect));
                Check(resultRect.Width > 100 && resultRect.Height > 100, "Search result view has no usable size.");
                Check(Math.Abs(resultRect.Left - fileRect.Left) <= 2 && Math.Abs(resultRect.Top - fileRect.Top) <= 2,
                    "Search results are not aligned with the native file-view origin.");
                Check(Math.Abs(resultRect.Right - fileRect.Right) <= 2 && resultRect.Bottom <= fileRect.Bottom + 2,
                    "Search results extend beyond the native file-view bounds.");
                Check(resultRect.Left >= treeRect.Right - 2, "Search results cover the left navigation pane.");
            }
            finally { Marshal.ReleaseComObject(view); Marshal.Release(viewPtr); }
        }

        private static IntPtr FindTreeItem(IntPtr tree, string text)
        {
            var pending = new Queue<IntPtr>();
            IntPtr root = SearchNavNative.SendMessage(tree, 0x110A, IntPtr.Zero, IntPtr.Zero);
            if (root != IntPtr.Zero) pending.Enqueue(root);
            int visited = 0;
            while (pending.Count > 0 && visited++ < 1000)
            {
                IntPtr item = pending.Dequeue();
                IntPtr buffer = Marshal.AllocHGlobal(512 * sizeof(char));
                try
                {
                    Marshal.WriteInt16(buffer, 0);
                    var data = new SearchNavNative.TreeItem { mask = 1, hItem = item, pszText = buffer, cchTextMax = 512 };
                    if (SearchNavNative.GetTreeItem(tree, 0x113E, IntPtr.Zero, ref data) != IntPtr.Zero &&
                        string.Equals(Marshal.PtrToStringUni(buffer), text, StringComparison.CurrentCultureIgnoreCase)) return item;
                }
                finally { Marshal.FreeHGlobal(buffer); }
                IntPtr sibling = SearchNavNative.SendMessage(tree, 0x110A, new IntPtr(1), item);
                IntPtr child = SearchNavNative.SendMessage(tree, 0x110A, new IntPtr(4), item);
                if (sibling != IntPtr.Zero) pending.Enqueue(sibling);
                if (child != IntPtr.Zero) pending.Enqueue(child);
            }
            return IntPtr.Zero;
        }

        private static class SearchNavNative
        {
            [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
            [DllImport("ole32.dll")] internal static extern void OleUninitialize();
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
            [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
            [DllImport("user32.dll", EntryPoint = "SendMessageW")] internal static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
            internal static extern IntPtr GetTreeItem(IntPtr hwnd, uint msg, IntPtr wParam, ref TreeItem item);
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct TreeItem
            {
                internal uint mask;
                internal IntPtr hItem;
                internal uint state;
                internal uint stateMask;
                internal IntPtr pszText;
                internal int cchTextMax;
                internal int iImage;
                internal int iSelectedImage;
                internal int cChildren;
                internal IntPtr lParam;
            }
        }
    }
}
