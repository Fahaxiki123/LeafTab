using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Services;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static void RunExternalSelectionTests()
        {
            Test("External select accepts a separated comma switch and preserves Chinese commas", () =>
            {
                var request = LaunchRequestParser.Parse(new[] { "/select,", @"D:\测试, folder\file, 一.txt" });
                Check(request.Targets.Count == 1);
                Check(request.Targets[0].FolderOrAlias == @"D:\测试, folder");
                Check(request.Targets[0].SelectItemName == "file, 一.txt");
            });
            Test("Multiple external selections in one folder retain all names", () =>
            {
                var request = LaunchRequestParser.Parse(new[] { @"/select,D:\Folder\一.txt", @"/select,D:\Folder\two.txt" });
                Check(request.Targets.Count == 1);
                Check(request.Targets[0].SelectItemNames.SequenceEqual(new[] { "一.txt", "two.txt" }));
            });
            Test("External select keeps UNC folders and root targets intact", () =>
            {
                var request = LaunchRequestParser.Parse(new[] { @"/n,/e,/select,\\server\share\folder name\child, 一.txt" });
                Check(request.Targets.Single().FolderOrAlias == @"\\server\share\folder name");
                Check(request.Targets.Single().SelectItemName == "child, 一.txt");
                var root = LaunchRequestParser.Parse(new[] { @"/select,D:\" }).Targets.Single();
                Check(root.FolderOrAlias == @"D:\" && root.SelectItemNames.Count == 0);
                var share = LaunchRequestParser.Parse(new[] { @"/select,\\server\share\" }).Targets.Single();
                Check(share.FolderOrAlias == @"\\server\share\" && share.SelectItemNames.Count == 0);
            });
            Test("IPC round trip merges multi-selection and retains separate folders", () =>
            {
                var request = new LaunchActionRequest();
                request.Targets.Add(new LaunchTargetItem(@"D:\中文, folder", selectItemNames: new[] { "一, 空格.txt", "second.txt", "SECOND.TXT" }));
                request.Targets.Add(new LaunchTargetItem(@"\\server\share", "other.txt"));
                var decoded = LaunchRequestCodec.Decode(LaunchRequestCodec.Encode(request))!;
                Check(decoded.Targets.Count == 2);
                Check(decoded.Targets[0].SelectItemNames.SequenceEqual(new[] { "一, 空格.txt", "second.txt" }));
                Check(decoded.Targets[1].FolderOrAlias == @"\\server\share");
            });
            Test("IPC bounds count selection frames even after targets would merge", () =>
            {
                string target = "TARGET|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Root)) + "|" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("one.txt")) + "|\n";
                string payload = "LEAFTAB_IPC_V1\n" + string.Concat(Enumerable.Repeat(target, 129));
                bool rejected = false;
                try { LaunchRequestCodec.Decode(Encoding.UTF8.GetBytes(payload)); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Repeated selections bypassed the target-frame limit.");
            });
            Test("IPC preserves all 128 selected names and rejects a 129th", () =>
            {
                var request = new LaunchActionRequest();
                string[] names = Enumerable.Range(0, 128).Select(i => "选择, " + i + ".txt").ToArray();
                request.Targets.Add(new LaunchTargetItem(Root, selectItemNames: names));
                var decoded = LaunchRequestCodec.Decode(LaunchRequestCodec.Encode(request))!;
                Check(decoded.Targets.Single().SelectItemNames.SequenceEqual(names));
                request.Targets[0] = new LaunchTargetItem(Root, selectItemNames: names.Concat(new[] { "one-more.txt" }));
                bool rejected = false;
                try { LaunchRequestCodec.Encode(request); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected);
            });
            Test("Selection confirmation rejects a partial or unrelated selection", () =>
            {
                Check(!ExplorerBrowserHost.SelectionContainsAll(new[] { "a.txt", "b.txt" }, new[] { @"D:\x\a.txt" }));
                Check(!ExplorerBrowserHost.SelectionContainsAll(new[] { "a.txt" }, new[] { @"D:\x\b.txt" }));
                Check(ExplorerBrowserHost.SelectionContainsAll(new[] { "a.txt", "B.txt" }, new[] { @"D:\x\a.txt", @"D:\x\b.txt" }));
            });
        }

        private static int RunExternalSelectionNativeTests()
        {
            int oleHr = SelectionNative.OleInitialize(IntPtr.Zero);
            if (oleHr < 0) throw new COMException("Cannot initialize native selection tests.", oleHr);
            var frame = new DispatcherFrame();
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var deadline = new Timer(_ => Environment.Exit(1), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyExternalSelectionNativeAsync(); }
                catch (Exception ex) { _failed++; Console.WriteLine("FAIL: Native selection setup — " + ex); }
                finally { frame.Continue = false; }
            }));
            try { Dispatcher.PushFrame(frame); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
                SelectionNative.OleUninitialize();
            }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static async Task VerifyExternalSelectionNativeAsync()
        {
            string source = MakeDirectory("external-selection-native");
            string destination = MakeDirectory("external-selection-destination");
            File.WriteAllText(Path.Combine(source, "一, 空格.txt"), "one");
            File.WriteAllText(Path.Combine(source, "second.txt"), "two");
            Directory.CreateDirectory(Path.Combine(source, "child-folder"));
            File.WriteAllText(Path.Combine(destination, "second.txt"), "destination marker");
            IntPtr sourcePidl = PidlManager.TryParseLocalOrShellFast(source);
            IntPtr destinationPidl = PidlManager.TryParseLocalOrShellFast(destination);
            IntPtr parent = IntPtr.Zero;
            ExplorerBrowserHost? host = null;
            try
            {
                parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab hidden selection regression",
                    unchecked((int)0x82000000), -20000, -20000, 960, 640, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Check(parent != IntPtr.Zero && sourcePidl != IntPtr.Zero && destinationPidl != IntPtr.Zero);
                host = new ExplorerBrowserHost(sourcePidl);
                host.Measure(new Size(960, 640));
                host.Arrange(new Rect(0, 0, 960, 640));
                typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(null, parent) });
                Check(await host.WaitForFolderNavigationAsync(sourcePidl), "Initial folder navigation did not complete.");

                bool multi = await host.SelectChildItemsWhenReadyAsync(new[] { "一, 空格.txt", "second.txt", "child-folder" });
                Test("Native multi-selection retains files and a folder", () =>
                    Check(multi && host.GetSelectedPaths().Count == 3));

                var delayedCreation = Task.Run(async () =>
                {
                    await Task.Delay(1100);
                    string latePath = Path.Combine(source, "late-enumeration.txt");
                    File.WriteAllText(latePath, "late");
                    NativeMethods.SHChangeNotifyPath(NativeMethods.SHCNE_CREATE,
                        NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT, latePath, null);
                });
                var lateTimer = Stopwatch.StartNew();
                bool late = await host.SelectChildItemWhenReadyAsync("late-enumeration.txt");
                await delayedCreation;
                Test("Native file selection survives enumeration beyond the former 720ms limit", () =>
                    Check(late && lateTimer.ElapsedMilliseconds >= 1000 && host.GetSelectedPaths().Single().EndsWith("late-enumeration.txt")));

                using (var cancelled = new CancellationTokenSource(120))
                {
                    var cancelTimer = Stopwatch.StartNew();
                    bool selected = await host.SelectChildItemsWhenReadyAsync(new[] { "not-created.txt" }, cancelled.Token);
                    Test("Native selection respects cancellation promptly", () => Check(!selected && cancelTimer.ElapsedMilliseconds < 1000));
                }

                Task<bool> older = host.SelectChildItemWhenReadyAsync("missing-old-request.txt");
                await Task.Delay(100);
                bool newer = await host.SelectChildItemWhenReadyAsync("second.txt");
                bool olderResult = await older;
                Test("A newer selection cancels the old retry without clearing the newer result", () =>
                    Check(newer && !olderResult && host.GetSelectedPaths().Single().EndsWith("second.txt")));

                Task<bool> stale = host.SelectChildItemWhenReadyAsync("missing-before-navigation.txt");
                await Task.Delay(100);
                Check(host.NavigateToPidl(destinationPidl));
                bool staleResult = await stale;
                Check(await host.WaitForFolderNavigationAsync(destinationPidl));
                Test("Navigation cancels an outstanding selection request", () => Check(!staleResult && host.GetSelectedPaths().Count == 0));
                bool mismatch = await host.SelectChildItemsWhenReadyAsync(new[] { "second.txt", "missing.txt" }, timeoutMs: 160);
                Test("Native selection never reports success for a partial selection", () => Check(!mismatch));
            }
            finally
            {
                host?.Dispose();
                if (parent != IntPtr.Zero) NativeMethods.DestroyWindow(parent);
                PidlManager.Free(ref sourcePidl);
                PidlManager.Free(ref destinationPidl);
            }
        }

        private static class SelectionNative
        {
            [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
            [DllImport("ole32.dll")] internal static extern void OleUninitialize();
        }
    }
}
