using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Services;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        // Opt-in end-to-end bridge test. Only public Shell APIs are used; no desktop input,
        // registry association changes, or actions against the user's existing windows.
        internal static int RunExplorerRedirectNativeTests()
        {
            string fixtures = Path.Combine(Root, "explorer-redirect");
            string previous = Path.Combine(fixtures, "existing-before-listener");
            string rejected = Path.Combine(fixtures, "delivery-rejected");
            string accepted = Path.Combine(fixtures, "delivery-accepted");
            string changed = Path.Combine(fixtures, "selection-changed-during-delivery");
            string disabled = Path.Combine(fixtures, "disabled-listener");
            string[] folders = { previous, rejected, accepted, changed, disabled };
            foreach (string folder in folders)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "定位,目标.txt"), "fixture A");
                File.WriteAllText(Path.Combine(folder, "第二个.txt"), "fixture B");
            }
            int ole = RedirectNative.OleInitialize(IntPtr.Zero);
            if (ole < 0) throw new COMException("Could not initialize Shell test COM apartment.", ole);
            var requests = new ConcurrentQueue<LaunchActionRequest>();
            int enabled = 1;
            ExplorerWindowRedirectService? bridge = null;
            try
            {
                LaunchFixtureExplorer(previous);
                WaitForRedirectCondition(() => HasFixtureExplorer(previous), "Initial owned Explorer window was not created.");
                bridge = new ExplorerWindowRedirectService(request =>
                {
                    if (request.Targets.Count != 1 || !folders.Contains(request.Targets[0].FolderOrAlias, StringComparer.OrdinalIgnoreCase))
                        return Task.FromResult(false); // Never act on unrelated windows opened during this test.
                    requests.Enqueue(request);
                    string path = request.Targets[0].FolderOrAlias;
                    if (string.Equals(path, changed, StringComparison.OrdinalIgnoreCase))
                        SelectFixtureItem(changed, "第二个.txt", 29);
                    return Task.FromResult(!string.Equals(path, rejected, StringComparison.OrdinalIgnoreCase));
                }, () => Volatile.Read(ref enabled) != 0);
                Check(bridge.Start(), "Explorer redirect hook could not start.");

                Test("Native bridge excludes Explorer windows that predate its start", () =>
                {
                    LaunchFixtureExplorer(previous);
                    Thread.Sleep(1200);
                    Check(!requests.Any(r => r.Targets[0].FolderOrAlias == previous));
                    Check(HasFixtureExplorer(previous), "An existing Explorer window was closed.");
                });
                Test("Native bridge reads explorer.exe /select and preserves source on delivery failure", () =>
                {
                    LaunchFixtureExplorer(rejected);
                    WaitForRedirectCondition(() => requests.Any(r => r.Targets[0].FolderOrAlias == rejected), "No /select request reached the redirect callback.");
                    LaunchActionRequest request = requests.First(r => r.Targets[0].FolderOrAlias == rejected);
                    Check(request.Targets[0].SelectItemNames.SequenceEqual(new[] { "定位,目标.txt" }));
                    Check(HasFixtureExplorer(rejected), "A failed delivery closed the source window.");
                });
                Test("Native bridge closes its source only after successful delivery", () =>
                {
                    LaunchFixtureExplorer(accepted);
                    WaitForRedirectCondition(() => requests.Any(r => r.Targets[0].FolderOrAlias == accepted), "No successful redirect request was observed.");
                    LaunchActionRequest request = requests.First(r => r.Targets[0].FolderOrAlias == accepted);
                    Check(request.Targets[0].SelectItemNames.SequenceEqual(new[] { "定位,目标.txt" }));
                    WaitForRedirectCondition(() => !HasFixtureExplorer(accepted), "Successful delivery did not close the owned source window.");
                });
                Test("Native bridge preserves a source whose selection changes during delivery", () =>
                {
                    LaunchFixtureExplorer(changed);
                    WaitForRedirectCondition(() => requests.Any(r => r.Targets[0].FolderOrAlias == changed), "No changed-selection callback was observed.");
                    Thread.Sleep(400);
                    Check(HasFixtureExplorer(changed), "A source with changed selection was closed.");
                });
                Test("Native bridge leaves new Explorer windows alone while disabled", () =>
                {
                    Volatile.Write(ref enabled, 0);
                    LaunchFixtureExplorer(disabled);
                    WaitForRedirectCondition(() => HasFixtureExplorer(disabled), "Disabled-mode source did not open.");
                    Thread.Sleep(1200);
                    Check(!requests.Any(r => r.Targets[0].FolderOrAlias == disabled));
                    Check(HasFixtureExplorer(disabled));
                });
            }
            catch (Exception ex) { _failed++; Console.WriteLine("FAIL: Native Explorer bridge setup — " + ex); }
            finally
            {
                bridge?.Dispose();
                // All matching windows point inside this run's random fixture root.
                // Unrelated Explorer windows and user data are never closed or removed.
                foreach (string folder in folders) CloseFixtureExplorers(folder);
                RedirectNative.OleUninitialize();
            }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void LaunchFixtureExplorer(string folder)
        {
            using Process? process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                "/select,\"" + Path.Combine(folder, "定位,目标.txt") + "\"") { UseShellExecute = false });
            Console.WriteLine("WAIT_FOREGROUND: " + Path.GetFileName(folder) + " | " + folder);
        }

        private static void WaitForRedirectCondition(Func<bool> condition, string failure)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 20000)
            {
                if (condition()) return;
                Thread.Sleep(80);
            }
            throw new InvalidOperationException(failure);
        }

        private static bool HasFixtureExplorer(string folder)
        {
            var matches = GetFixtureExplorers(folder);
            try { return matches.Count != 0; }
            finally { foreach (object match in matches) RedirectRelease(match); }
        }

        private static void CloseFixtureExplorers(string folder)
        {
            foreach (object window in GetFixtureExplorers(folder))
            {
                try { RedirectCall(window, "Quit"); }
                catch { }
                finally { RedirectRelease(window); }
            }
        }

        private static List<object> GetFixtureExplorers(string folderPath)
        {
            var matches = new List<object>();
            object? windows = null;
            try
            {
                windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
                int count = Convert.ToInt32(RedirectGet(windows!, "Count"), CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    object? window = null, document = null, folder = null, self = null;
                    try
                    {
                        window = RedirectCall(windows!, "Item", i);
                        if (window == null) continue;
                        string? executable = RedirectGet(window, "FullName") as string;
                        if (!string.Equals(Path.GetFileName(executable), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        document = RedirectGet(window, "Document");
                        folder = document == null ? null : RedirectGet(document, "Folder");
                        self = folder == null ? null : RedirectGet(folder, "Self");
                        string? path = self == null ? null : RedirectGet(self, "Path") as string;
                        if (!string.Equals(path?.TrimEnd('\\'), folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                        matches.Add(window);
                        window = null;
                    }
                    catch { }
                    finally { RedirectRelease(self); RedirectRelease(folder); RedirectRelease(document); RedirectRelease(window); }
                }
            }
            finally { RedirectRelease(windows); }
            return matches;
        }

        private static void SelectFixtureItem(string folderPath, string name, int flags)
        {
            var windows = GetFixtureExplorers(folderPath);
            try
            {
                Check(windows.Count == 1, "Expected one owned fixture window.");
                object? document = null, folder = null, item = null;
                try
                {
                    document = RedirectGet(windows[0], "Document");
                    folder = RedirectGet(document!, "Folder");
                    item = RedirectCall(folder!, "ParseName", name);
                    RedirectCall(document!, "SelectItem", item!, flags);
                }
                finally { RedirectRelease(item); RedirectRelease(folder); RedirectRelease(document); }
            }
            finally { foreach (object window in windows) RedirectRelease(window); }
        }

        private static object? RedirectGet(object target, string name) => target.GetType().InvokeMember(
            name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
        private static object? RedirectCall(object target, string name, params object[] args) => target.GetType().InvokeMember(
            name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);
        private static void RedirectRelease(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private static class RedirectNative
        {
            [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
            [DllImport("ole32.dll")] internal static extern void OleUninitialize();
        }
    }
}
