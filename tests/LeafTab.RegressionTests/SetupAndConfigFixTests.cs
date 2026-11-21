using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LeafTab.Services;
using LeafTab.Setup;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static void RunSetupAndConfigFixTests()
        {
            Test("Migration and uninstall preserve other applications' valid JSON", () =>
            {
                string install = MakeDirectory("shared-tools"), data = MakeDirectory("tools-userdata");
                string primary = Path.Combine(install, "config.json");
                const string otherConfig = "{\"Version\":1,\"Bookmarks\":[],\"Theme\":\"light\"}";
                File.WriteAllText(primary, otherConfig);
                string destination = AppConfigService.ResolveConfigFilePath(install, data);
                Check(!File.Exists(destination), "Unrelated JSON was migrated as LeafTab settings.");
                Check(AppConfigService.Save(ConfigWithBookmark("legacy"), primary + ".bak"));
                destination = AppConfigService.ResolveConfigFilePath(install, data);
                Check(AppConfigService.Load(destination).Bookmarks.Single().Title == "legacy");
                SetupEngine.RemoveKnownUserData(install, data);
                Check(File.ReadAllText(primary) == otherConfig && !File.Exists(primary + ".bak"),
                    "Uninstalling with user data removal must clean migrated LeafTab backups while keeping other apps' config.");
                Check(!File.Exists(destination));

                // 便携目录下即便主 config.json 因断电损坏，只要同目录存在有效 LeafTab .bak 签名就应一并清理
                File.WriteAllText(Path.Combine(install, ".portable"), "");
                string brokenPortable = MakeDirectory("broken-portable");
                File.WriteAllText(Path.Combine(brokenPortable, ".portable"), "");
                string brokenPrimary = Path.Combine(brokenPortable, "config.json");
                File.WriteAllText(brokenPrimary, "{ broken json");
                Check(AppConfigService.Save(ConfigWithBookmark("portable-bak"), brokenPrimary + ".bak"));
                SetupEngine.RemoveKnownUserData(brokenPortable, data, includeSharedData: false);
                Check(!File.Exists(brokenPrimary) && !File.Exists(brokenPrimary + ".bak"));

                Check(AppConfigService.Save(ConfigWithBookmark("legacy"), primary + ".bak"));
                SetupEngine.RemoveKnownUserData(install, data, includeSharedData: false);
                Check(File.ReadAllText(primary) == otherConfig && !File.Exists(primary + ".bak"));
                SetupEngine.RemoveKnownUserData(install, install, includeSharedData: false);
                Check(File.ReadAllText(primary) == otherConfig);
            });
            Test("OwnsSharedUserData excludes portable registered directories and LegacyConfigFile bounds large files", () =>
            {
                string portableReg = MakeDirectory("portable-registered");
                string regularReg = MakeDirectory("regular-registered");
                string defaultDir = MakeDirectory("default-install");
                Check(SetupEngine.OwnsSharedUserData(regularReg, regularReg, defaultDir));
                File.WriteAllText(Path.Combine(portableReg, ".portable"), "");
                Check(!SetupEngine.OwnsSharedUserData(portableReg, portableReg, defaultDir));
                Check(SetupEngine.OwnsSharedUserData(defaultDir, null, defaultDir));

                Type legacyType = typeof(LeafTab.App).Assembly.GetType("LeafTab.Shared.LegacyConfigFile")!;
                MethodInfo hasFormat = legacyType.GetMethod("HasLeafTabFormat", BindingFlags.NonPublic | BindingFlags.Static)!;
                string huge = Path.Combine(MakeDirectory("oversized-json"), "config.json");
                using (var fs = new FileStream(huge, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.SetLength(17L * 1024 * 1024);
                }
                Check(!(bool)hasFormat.Invoke(null, new object[] { huge })!);
                File.Delete(huge);
            });
            Test("Portable cleanup keeps oversized, unreadable and valid third-party JSON with a signed backup", () =>
            {
                string folder = MakeDirectory("portable-foreign-json"), shared = MakeDirectory("unrelated-profile");
                File.WriteAllText(Path.Combine(folder, ".portable"), "");
                string primary = Path.Combine(folder, "config.json"), backup = primary + ".bak", temp = primary + ".tmp";
                using (var writer = new StreamWriter(primary, false, new UTF8Encoding(false)))
                {
                    writer.Write("{}");
                    string padding = new string(' ', 16384);
                    for (int i = 0; i < 1024; i++) writer.Write(padding);
                }
                Check(AppConfigService.Save(ConfigWithBookmark("owned"), backup));
                File.WriteAllText(temp, "{\"owner\":\"other\"}");
                using (File.Open(temp, FileMode.Open, FileAccess.Read, FileShare.Delete))
                    SetupEngine.RemoveKnownUserData(folder, shared, includeSharedData: false);
                Check(File.Exists(temp) && new FileInfo(primary).Length == 16L * 1024 * 1024 + 2 && !File.Exists(backup));

                const string metadata = "{\"__type\":12,\"other\":[true,null,-1.2e3,{\"quote\":\"\\\"\"}]}";
                foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode })
                {
                    File.WriteAllText(primary, metadata, encoding);
                    byte[] original = File.ReadAllBytes(primary);
                    Check(AppConfigService.Save(ConfigWithBookmark("owned"), backup));
                    SetupEngine.RemoveKnownUserData(folder, shared, includeSharedData: false);
                    Check(File.ReadAllBytes(primary).SequenceEqual(original) && !File.Exists(backup));
                }
            });
            Test("Portable cleanup checks original JSON types and legacy bookmark and tab paths", () =>
            {
                string folder = MakeDirectory("portable-signature-types");
                string shared = MakeDirectory("unused-signature-profile");
                string primary = Path.Combine(folder, "config.json"), backup = primary + ".bak";
                File.WriteAllText(Path.Combine(folder, ".portable"), "");
                const string flags = "\"BookmarksInitialized\":true,\"ShowBookmarkBar\":false,\"ShowPreviewPane\":true,";
                const string arrays = "\"Bookmarks\":[],\"OpenTabs\":[]";
                string[] foreign =
                {
                    "{" + flags + "\"Bookmarks\":{},\"OpenTabs\":{}}",
                    "{" + flags + "\"Bookmarks\":[true],\"OpenTabs\":[2]}",
                    "{\"BookmarksInitialized\":\"true\",\"ShowBookmarkBar\":false,\"ShowPreviewPane\":true," + arrays + "}",
                    "{" + flags + "\"Bookmarks\":[{\"url\":\"elsewhere\"}],\"OpenTabs\":[]}",
                    "{" + flags + "\"Bookmarks\":[],\"OpenTabs\":[{\"CurrentPath\":42}]}"
                };
                const string legacy = "{" + flags + "\"Bookmarks\":[{\"Title\":\"docs\",\"TargetPath\":\"docs\"}]," +
                    "\"OpenTabs\":[{\"CurrentPath\":\"docs\",\"HistoryPaths\":[],\"HistoryIndex\":0}]}";
                foreach (string other in foreign)
                {
                    File.WriteAllText(primary, other);
                    File.WriteAllText(backup, legacy);
                    SetupEngine.RemoveKnownUserData(folder, shared, includeSharedData: false);
                    Check(File.ReadAllText(primary) == other && !File.Exists(backup));
                }
                File.WriteAllText(primary, "{" + flags + arrays + "}");
                SetupEngine.RemoveKnownUserData(folder, shared, includeSharedData: false);
                Check(!File.Exists(primary), "Empty bookmark and tab lists are valid LeafTab settings.");
            });
            Test("An unregistered custom install removes its signed legacy settings and preserves shared data", () =>
            {
                string folder = MakeDirectory("unregistered-custom"), shared = MakeDirectory("other-install-data");
                string source = Path.Combine(folder, "config.json"), unrelated = source + ".bak";
                Check(AppConfigService.Save(ConfigWithBookmark("legacy"), source));
                File.WriteAllText(unrelated, "{\"anotherApplication\":true}");
                string migrated = AppConfigService.ResolveConfigFilePath(folder, shared);
                byte[] expected = File.ReadAllBytes(migrated);
                bool ownsShared = SetupEngine.OwnsSharedUserData(folder, null, MakeDirectory("default-programs"));
                Check(!ownsShared);
                SetupEngine.RemoveKnownUserData(folder, shared, ownsShared);
                Check(!File.Exists(source) && File.Exists(unrelated) && File.ReadAllBytes(migrated).SequenceEqual(expected));
            });
            Test("First save after recovery keeps the readable backup", () =>
            {
                string primary = Path.Combine(MakeDirectory("recovered-save"), "config.json");
                string backup = primary + ".bak";
                Check(AppConfigService.Save(ConfigWithBookmark("before"), backup));
                byte[] previousBackup = File.ReadAllBytes(backup);
                File.WriteAllText(primary, "broken JSON");
                var restored = AppConfigService.Load(primary);
                Check(restored.Bookmarks.Single().Title == "before");
                restored.Bookmarks[0].Title = "after";
                Check(AppConfigService.Save(restored, primary));
                Check(File.ReadAllBytes(backup).SequenceEqual(previousBackup));
                Check(AppConfigService.Load(primary).Bookmarks.Single().Title == "after");
                restored.Bookmarks[0].Title = "latest";
                Check(AppConfigService.Save(restored, primary));
                Check(AppConfigService.Load(backup).Bookmarks.Single().Title == "after");
            });
            Test("Installation folder picker keeps the selected drive rooted", () =>
            {
                string root = Path.GetPathRoot(Root)!;
                string expected = File.Exists(Path.Combine(root, "LeafTab.exe")) ? root : Path.Combine(root, "LeafTab");
                Check(SetupEngine.NormalizeChosenFolder(root) == expected, "Selected drive root became drive-relative.");
                string existing = Path.Combine(MakeDirectory("parent-install"), "LeafTab");
                Check(SetupEngine.NormalizeChosenFolder(existing) == Path.GetFullPath(existing), "An existing LeafTab folder was nested twice.");
                Check(SetupEngine.NormalizeChosenFolder(existing + @"\") == Path.GetFullPath(existing), "Trailing separator was not trimmed.");
            });
            Test("Setup blocks close during work and displays errors next to editable options", () =>
            {
                var window = new SetupWindow(new SetupOptions { InstallDir = "" });
                var busy = typeof(SetupWindow).GetField("_isBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var closing = typeof(SetupWindow).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    busy.SetValue(window, true);
                    var args = new CancelEventArgs();
                    closing.Invoke(window, new object[] { args });
                    Check(args.Cancel);
                    busy.SetValue(window, false);
                    window.PrimaryActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.InstallConfigPanel.Visibility == Visibility.Visible);
                    Check(window.InstallErrorText.Visibility == Visibility.Visible && window.InstallErrorText.Text.Contains("安装路径不能为空"));
                    Check(!(bool)busy.GetValue(window)! && window.CancelButton.IsEnabled &&
                          window.TopCloseButton.IsEnabled && window.PrimaryActionButton.IsEnabled);
                    args = new CancelEventArgs();
                    closing.Invoke(window, new object[] { args });
                    Check(!args.Cancel);
                }
                finally { busy.SetValue(window, false); window.Close(); }
            });
            Test("The setup wizard closes after a successful launch and stays open when launch is unchecked", () =>
            {
                foreach (bool launch in new[] { true, false })
                {
                    bool closed = false, installed = false;
                    var options = new SetupOptions { InstallDir = Root, LaunchAfterInstall = launch };
                    var window = new SetupWindow(options, (chosen, _) =>
                    {
                        installed = true;
                        Check(chosen.LaunchAfterInstall == launch);
                        return Task.FromResult((true, "", Path.Combine(Root, "LeafTab.exe")));
                    });
                    window.Closed += (_, _) => closed = true;
                    try
                    {
                        window.PrimaryActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(installed && closed == launch);
                        if (!launch)
                        {
                            window.PrimaryActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check(closed);
                        }
                    }
                    finally { if (!closed) window.Close(); }
                }
            });
        }
    }
}
