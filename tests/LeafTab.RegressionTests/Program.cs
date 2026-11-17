using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LeafTab.Controls;
using LeafTab.Services;
using Microsoft.Win32;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static int _failed;
        private static int _passed;
        private static readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("LEAFTAB_TEST_ROOT") ?? Path.GetTempPath(), "LeafTab-regression-" + Guid.NewGuid().ToString("N"));
        private static readonly string Exe = typeof(LeafTab.App).Assembly.Location;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Contains("--inspect-shell-selection")) return InspectShellSelection(args);
            if (args.Contains("--child-fail")) return 7;
            if (args.Contains("--child-slow")) { System.Threading.Thread.Sleep(1500); return 0; }
            Directory.CreateDirectory(Root);
            foreach (var assembly in new[] { typeof(LeafTab.App).Assembly, typeof(LeafTab.Setup.SetupOptions).Assembly })
                assembly.GetType("LeafTab.Shared.DiagnosticLog")!.GetProperty("DirectoryPath", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, Path.Combine(Root, "logs"));
            Console.WriteLine("Fixtures: " + Root);
            if (args.Contains("--search-navigation-native")) return RunSearchNavigationNativeTests();
            if (args.Contains("--search-history-native")) return RunSearchHistoryFixNativeTests();
            if (args.Contains("--window-search-native")) return RunWindowSearchSettingsNativeTests();
            if (args.Contains("--focus-navigation-native")) return RunFocusNavigationNativeTests();
            if (args.Contains("--standby-restore-native")) return RunStandbyRestoreNativeTests();
            if (args.Contains("--focus-input-ui")) return RunForegroundFocusInputTests();
            if (args.Contains("--explorer-redirect-native")) return RunExplorerRedirectNativeTests();
            if (args.Contains("--shell-selection-native")) return RunShellSelectionNativeTests();
            if (args.Contains("--external-selection-native")) return RunExternalSelectionNativeTests();
            Test("Preserve a changed external open command", () => RegistrySandbox((classes, backup) =>
            {
                string keyPath = classes + @"\Directory\shell\open\command";
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("", "original-manager");
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false).Ok);
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                {
                    key!.SetValue("", "other-manager");
                    key.DeleteValue("DelegateExecute", false);
                }
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(false, Exe, classes, backup, false).Ok);
                Check(Read(keyPath) == "other-manager", "New owner's command was overwritten.");
            }));
            Test("Old copy cannot unregister a new copy", () => RegistrySandbox((classes, backup) =>
            {
                string second = Path.Combine(Root, "second.exe");
                File.Copy(Exe, second, true);
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false).Ok);
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, second, classes, backup, false).Ok);
                ShellIntegrationService.SetDefaultExplorerEnabledCore(false, Exe, classes, backup, false);
                Check(Read(classes + @"\Directory\shell\LeafTabOpen\command")?.Contains(second) == true);
            }));
            Test("Completion finds a folder beyond the first 400", () =>
            {
                string folder = Path.Combine(Root, "completion");
                Directory.CreateDirectory(folder);
                for (int i = 0; i < 430; i++) Directory.CreateDirectory(Path.Combine(folder, "folder" + i.ToString("D4")));
                string target = Directory.EnumerateDirectories(folder).ElementAt(410);
                var suggestions = QuickLaunchAndInstallService.BuildAddressSuggestions(target, false, Array.Empty<string>(), Array.Empty<string>(), 12);
                Check(suggestions.Any(s => s.InsertText.TrimEnd('\\') == target));
            });
            Test("Tab strip honors minimum width and reports scroll extent", () =>
            {
                var panel = new AdaptiveTabStripPanel { MinTabWidth = 48 };
                for (int i = 0; i < 30; i++) panel.Children.Add(new Border { Height = 40 });
                panel.Measure(new Size(500, 40));
                panel.Arrange(new Rect(0, 0, 500, 40));
                Check(panel.Children[0].RenderSize.Width >= 48);
            });
            Test("Installer propagates a failed child process", () =>
            {
                var method = typeof(LeafTab.Setup.SetupOptions).Assembly.GetType("LeafTab.Setup.SetupEngine")!
                    .GetMethod("RunProcessHiddenAndWait", BindingFlags.NonPublic | BindingFlags.Static)!;
                bool rejected = false;
                try { method.Invoke(null, new object[] { Assembly.GetExecutingAssembly().Location, "--child-fail", 5000 }); }
                catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
                Check(rejected);
            });
            RunAdditionalTests();
            RunExplorerRedirectTests();
            RunShellProtocolTests();
            RunExternalSelectionTests();
            RunWindowAndPreviewFixTests();
            RunSearchFixTests();
            RunLifecycleFixTests();
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        static partial void RunAdditionalTests();

        private static void Test(string name, Action action)
        {
            try { action(); _passed++; Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { _failed++; Console.WriteLine("FAIL: " + name + " — " + ex); }
        }

        private static void Check(bool condition, string message = "Expected condition was not met.")
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string? Read(string path, string name = "")
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }

        private static void RegistrySandbox(Action<string, string> action)
        {
            string path = @"Software\LeafTab\RegressionTests\" + Guid.NewGuid().ToString("N");
            try { action(path + @"\Classes", path + @"\Backup"); }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        }
    }
}
