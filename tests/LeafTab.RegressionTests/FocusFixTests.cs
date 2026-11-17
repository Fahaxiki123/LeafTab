using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static int RunFocusNavigationNativeTests()
        {
            int ole = FocusProbeNative.OleInitialize(IntPtr.Zero);
            if (ole < 0) throw new COMException("OLE initialization failed", ole);
            try { return RunFocusDispatcherTest(VerifyFocusNavigationNativeAsync); }
            finally { FocusProbeNative.OleUninitialize(); }
        }

        private static int RunFocusDispatcherTest(Func<Task> verify)
        {
            var frame = new DispatcherFrame();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var deadline = new Timer(_ => Environment.Exit(9), null, 60000, Timeout.Infinite);
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await verify(); }
                catch (Exception ex) { _failed++; Console.WriteLine("FAIL: Focus/native setup: " + ex); }
                finally { frame.Continue = false; }
            }));
            try { Dispatcher.PushFrame(frame); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static async Task VerifyFocusNavigationNativeAsync()
        {
            string a = Path.Combine(Root, "focus-navigation", "A");
            string b = Path.Combine(Root, "focus-navigation", "B");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "original.txt"), "fixture");
            Directory.CreateDirectory(Path.Combine(a, "新建文件夹"));
            Directory.CreateDirectory(Path.Combine(b, "新建文件夹"));
            Directory.CreateDirectory(Path.Combine(b, "新建文件夹 (2)"));
            IntPtr pa = PidlManager.TryParseLocalOrShellFast(a), pb = PidlManager.TryParseLocalOrShellFast(b);
            IntPtr parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab hidden focus regression",
                unchecked((int)0x82000000), -20000, -20000, 960, 640, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var host = new ExplorerBrowserHost(pa);
            IntPtr built = IntPtr.Zero;
            bool tookForeground = false;
            uint ownProcess = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            monitor.Tick += (_, _) =>
            {
                FocusProbeNative.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint foreground);
                tookForeground |= foreground == ownProcess;
            };
            monitor.Start();
            try
            {
                host.Measure(new Size(960, 640)); host.Arrange(new Rect(0, 0, 960, 640));
                built = ((HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(null, parent) })!).Handle;
                Check(await WaitNativeConditionAsync(() => host.IsFolderNavigationComplete(pa)), "Initial folder did not load.");
                Check(await host.SelectChildItemWhenReadyAsync("original.txt"));
                host.GetSelectionStatus(true);
                Check(host.GetCachedSingleSelection().FileSystemPath == Path.Combine(a, "original.txt"));
                File.Move(Path.Combine(a, "original.txt"), Path.Combine(a, "renamed.md"));
                Check(await WaitNativeConditionAsync(() => host.GetDisplayedItemNames(20).Any(n => n.StartsWith("renamed", StringComparison.Ordinal))),
                    "Shell did not observe the fixture rename.");
                typeof(ExplorerBrowserHost).GetMethod("TrySelectChildByName", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { "renamed.md", false, true, true });
                host.GetSelectionStatus(true);
                Test("Single selection refreshes its path after a rename at the same index", () =>
                    Check(host.GetCachedSingleSelection().FileSystemPath == Path.Combine(a, "renamed.md"),
                        "Actual cached path: " + host.GetCachedSingleSelection().FileSystemPath));

                var delivered = new List<byte[]>();
                host.NavigationCompleted += (_, args) => delivered.Add(PidlManager.ToBytes(args.FolderPidl));
                typeof(ExplorerBrowserHost).GetMethod("OnNativeNavigationComplete", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { pa });
                host.NavigateToPidl(pb);
                Check(await WaitNativeConditionAsync(() => host.IsFolderNavigationComplete(pb)));
                await Task.Delay(80);
                Test("A queued old navigation completion cannot overwrite the newly requested folder", () =>
                    Check(delivered.Count > 0 && delivered.All(bytes => PidlBytesEqual(bytes, pb)),
                        "Events=" + delivered.Count + "; old=" + delivered.Count(bytes => PidlBytesEqual(bytes, pa))));
                typeof(ExplorerBrowserHost).GetMethod("OnNativeNavigationComplete", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { pa });
                typeof(ExplorerBrowserHost).GetMethod("OnNativeNavigationFailed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { pa });
                await Task.Delay(50);
                Test("Late completion and failure from the previous folder cannot replace an already completed newer navigation", () =>
                    Check(host.IsFolderNavigationComplete(pb) && delivered.All(bytes => PidlBytesEqual(bytes, pb))));

                host.NavigateToPidl(pa);
                Task<bool> selecting = host.SelectChildItemWhenReadyAsync("新建文件夹");
                Check(await selecting);
                Test("Pending folder selection waits for the requested view despite an old same-name child", () =>
                    Check(host.GetSelectedPaths().Single() == Path.Combine(a, "新建文件夹"),
                        "Actual selection: " + string.Join(",", host.GetSelectedPaths())));

                var dispatcherContext = SynchronizationContext.Current;
                var held = new HeldFocusContinuation();
                SynchronizationContext.SetSynchronizationContext(held);
                Task<(bool Succeeded, string? Message)> creating;
                try { creating = host.TryCreateNewItemAsync(NewItemKind.Folder, a); }
                finally { SynchronizationContext.SetSynchronizationContext(dispatcherContext); }
                host.NavigateToPidl(pb);
                Check(await WaitNativeConditionAsync(() => host.IsFolderNavigationComplete(pb) && held.Callback != null));
                held.Callback!(held.State);
                var created = await creating;
                Test("New-item completion cannot select or rename a same-name child in another folder", () =>
                    Check(created.Succeeded && Directory.Exists(Path.Combine(a, "新建文件夹 (2)")) && host.GetItemCounts().SelectedItems == 0));
                Test("Focus/navigation regression stays hidden and never takes foreground", () =>
                    Check(!NativeMethods.IsWindowVisible(parent) && !tookForeground));
            }
            finally
            {
                monitor.Stop();
                if (built != IntPtr.Zero) typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(host, built) });
                host.Dispose(); NativeMethods.DestroyWindow(parent);
                PidlManager.Free(ref pa); PidlManager.Free(ref pb);
            }
        }

        private sealed class HeldFocusContinuation : SynchronizationContext
        {
            internal SendOrPostCallback? Callback;
            internal object? State;
            public override void Post(SendOrPostCallback callback, object state) { State = state; Callback = callback; }
        }

        private static class FocusProbeNative
        {
            [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
            [DllImport("ole32.dll")] internal static extern void OleUninitialize();
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        }
    }
}
