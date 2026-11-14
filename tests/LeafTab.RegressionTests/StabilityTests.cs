using System;
using System.IO;
using System.Linq;
using System.Windows;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Views;
using Microsoft.Win32;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        static partial void RunAdditionalTests()
        {
            Test("Missing app recovery restores explicit verbs and Win+E", () => RegistrySandbox((classes, backup) =>
            {
                string original = "original-folder-command";
                string command = classes + @"\Directory\shell\open\command";
                using (var key = Registry.CurrentUser.CreateSubKey(command)) key.SetValue("", original);
                string missingExe = Path.Combine(Root, "removed.exe");
                File.Copy(Exe, missingExe, true);
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, missingExe, classes, backup, false);
                File.Delete(missingExe);
                LeafTab.Setup.SetupEngine.FallbackCleanOrphanedShellRegistry(missingExe, classes, backup);
                Check(Read(command) == original);
                Check(Read(command, "DelegateExecute") == null);
                Check(Read(classes + @"\Folder\shell\open\command") == null);
                Check(Read(classes + @"\CLSID\{8E4C7A29-3D51-4F82-9B14-6A5E20C91D73}\LocalServer32") == null);
            }));
            Test("Win+E preserves a command changed by another manager", () => RegistrySandbox((classes, backup) =>
            {
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false);
                string command = classes + @"\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\open\command";
                using (var key = Registry.CurrentUser.OpenSubKey(command, true))
                {
                    key!.SetValue("", "other-manager"); key.SetValue("DelegateExecute", "{OTHER}");
                }
                ShellIntegrationService.SetDefaultExplorerEnabledCore(false, Exe, classes, backup, false);
                Check(Read(command) == "other-manager" && Read(command, "DelegateExecute") == "{OTHER}");
            }));
            Test("Writable install directory migrates without deleting source", () =>
            {
                string source = MakeDirectory("legacy"), destination = MakeDirectory("userdata");
                var config = ConfigWithBookmark("legacy-bookmark");
                Check(AppConfigService.Save(config, Path.Combine(source, "config.json")));
                string resolved = AppConfigService.ResolveConfigFilePath(source, destination);
                Check(resolved == Path.Combine(destination, "config.json"));
                Check(AppConfigService.Load(resolved).Bookmarks.Single().Title == "legacy-bookmark");
                Check(File.Exists(Path.Combine(source, "config.json")));
                Check(AppConfigService.ResolveConfigFilePath(source, destination) == resolved);
            });
            Test("Valid empty destination is preserved during migration", () =>
            {
                string source = MakeDirectory("conflict-source"), destination = MakeDirectory("conflict-destination");
                AppConfigService.Save(ConfigWithBookmark("old"), Path.Combine(source, "config.json"));
                AppConfigService.Save(new AppConfig { BookmarksInitialized = true }, Path.Combine(destination, "config.json"));
                string resolved = AppConfigService.ResolveConfigFilePath(source, destination);
                Check(AppConfigService.Load(resolved).Bookmarks.Count == 0);
                Check(AppConfigService.Load(Path.Combine(source, "config.json")).Bookmarks.Count == 1);
            });
            Test("Migration can recover a valid legacy backup", () =>
            {
                string source = MakeDirectory("backup-source"), destination = MakeDirectory("backup-destination");
                File.WriteAllText(Path.Combine(source, "config.json"), "invalid-json");
                AppConfigService.Save(ConfigWithBookmark("backup"), Path.Combine(source, "config.json.bak"));
                Check(AppConfigService.Load(AppConfigService.ResolveConfigFilePath(source, destination)).Bookmarks.Single().Title == "backup");
            });
            Test("Portable storage requires an explicit marker", () =>
            {
                string source = MakeDirectory("portable");
                File.WriteAllText(Path.Combine(source, ".portable"), "");
                Check(AppConfigService.ResolveConfigFilePath(source, MakeDirectory("portable-destination")) == Path.Combine(source, "config.json"));
            });
            Test("Uninstall removes both config locations and preserves other files", () =>
            {
                string install = MakeDirectory("uninstall"), data = MakeDirectory("uninstall-data");
                foreach (string dir in new[] { install, data })
                {
                    File.WriteAllText(Path.Combine(dir, "config.json"), "data");
                    File.WriteAllText(Path.Combine(dir, "config.json.bak"), "data");
                    File.WriteAllText(Path.Combine(dir, "keep.txt"), "user document");
                }
                LeafTab.Setup.SetupEngine.RemoveKnownUserData(install, data);
                Check(!File.Exists(Path.Combine(install, "config.json")) && !File.Exists(Path.Combine(data, "config.json.bak")));
                Check(File.Exists(Path.Combine(install, "keep.txt")) && File.Exists(Path.Combine(data, "keep.txt")));
            });
            Test("Two windows retain simultaneous bookmark additions and deletions", () =>
            {
                if (Application.Current == null)
                {
                    new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LeafTab;component/Views/Icons.xaml", UriKind.Relative) });
                }
                AppConfig? saved = null;
                var store = new AppConfigStore(new AppConfig { BookmarksInitialized = true }, cfg => { saved = cfg; return true; });
                var a = new MainWindow(store) { Left = 0, Top = 0 };
                var b = new MainWindow(store) { Left = 0, Top = 0 };
                try
                {
                    var field = typeof(MainWindow).GetField("_bookmarks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var first = (System.Collections.ObjectModel.ObservableCollection<BookmarkItemViewModel>)field.GetValue(a)!;
                    var second = (System.Collections.ObjectModel.ObservableCollection<BookmarkItemViewModel>)field.GetValue(b)!;
                    first.Add(new BookmarkItemViewModel { Title = "A", TargetPath = Root });
                    second.Add(new BookmarkItemViewModel { Title = "B", TargetPath = Path.Combine(Root, "B") });
                    Check(first.Count == 2 && second.Count == 2 && ReferenceEquals(first, second));
                    Check(store.Flush() && saved!.Bookmarks.Count == 2);
                    second.RemoveAt(0); first.Add(new BookmarkItemViewModel { Title = "C", TargetPath = Path.Combine(Root, "C") });
                    Check(store.Flush() && saved!.Bookmarks.Select(x => x.Title).SequenceEqual(new[] { "B", "C" }));
                }
                finally { a.PrepareForForceExit(); b.PrepareForForceExit(); a.Close(); b.Close(); store.Flush(); }
            });
            RunProtocolAndPackagingTests();
            RunReviewRegressionTests();
        }

        static partial void RunProtocolAndPackagingTests();
        static partial void RunReviewRegressionTests();

        private static string MakeDirectory(string name)
        {
            string path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path;
        }

        private static AppConfig ConfigWithBookmark(string title) => new AppConfig
        {
            BookmarksInitialized = true,
            Bookmarks = new System.Collections.Generic.List<BookmarkConfigItem> { new BookmarkConfigItem { Title = title, TargetPath = Root } }
        };
    }
}
