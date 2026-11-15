using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LeafTab.Controls;
using LeafTab.Models;
using LeafTab.Services;
using Microsoft.CSharp;
using Microsoft.Win32;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        static partial void RunReviewRegressionTests()
        {
            Test("Shell menu state belongs to the executable that registered it", () => RegistrySandbox((classes, backup) =>
            {
                string other = Path.Combine(MakeDirectory("menu-owner"), "LeafTab.exe");
                File.Copy(Exe, other);
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, other, classes, backup, false).Ok);
                Check(ShellIntegrationService.SetContextMenuEnabledCore(true, other, classes, false).Ok);
                Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe));
                Check(!ShellIntegrationService.IsContextMenuEnabledCore(classes, Exe));
                Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, other));
                Check(ShellIntegrationService.IsContextMenuEnabledCore(classes, other));
                ShellIntegrationService.SetDefaultExplorerEnabledCore(false, Exe, classes, backup, false);
                ShellIntegrationService.SetContextMenuEnabledCore(false, Exe, classes, false);
                Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, other));
                Check(ShellIntegrationService.IsContextMenuEnabledCore(classes, other));
                ShellIntegrationService.SetDefaultExplorerEnabledCore(false, other, classes, backup, false);
                ShellIntegrationService.SetContextMenuEnabledCore(false, other, classes, false);
                Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, other));
                Check(!ShellIntegrationService.IsContextMenuEnabledCore(classes, other));
            }));
            Test("Menu state rejects modified command arguments and delegates", () => RegistrySandbox((classes, backup) =>
            {
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false);
                ShellIntegrationService.SetContextMenuEnabledCore(true, Exe, classes, false);
                using (var key = Registry.CurrentUser.OpenSubKey(classes + @"\Directory\shell\LeafTabOpen\command", true))
                    key!.SetValue("", "\"" + Exe + "\" --custom");
                using (var key = Registry.CurrentUser.OpenSubKey(classes + @"\Directory\shell\OpenInLeafTab\command", true))
                    key!.SetValue("DelegateExecute", "{OTHER}");
                Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe));
                Check(!ShellIntegrationService.IsContextMenuEnabledCore(classes, Exe));
            }));
            Test("An edited context menu cannot block moved default-explorer repair", () => RegistrySandbox((classes, backup) =>
            {
                string previous = Path.Combine(MakeDirectory("moved-owner"), "LeafTab.exe");
                File.Copy(Exe, previous);
                ShellIntegrationService.SetDefaultExplorerEnabledCore(true, previous, classes, backup, false);
                ShellIntegrationService.SetContextMenuEnabledCore(true, previous, classes, false);
                string context = classes + @"\Directory\shell\OpenInLeafTab\command";
                using (var key = Registry.CurrentUser.OpenSubKey(context, true)) key!.SetValue("", "other-manager --custom");
                File.Delete(previous);
                Check(ShellIntegrationService.RepairRegisteredPathsIfMovedCore(Exe, classes, backup, false));
                Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, Exe));
                Check(Read(classes + @"\CLSID\" + ShellIntegrationService.LeafTabExecuteClsidString + @"\LocalServer32") == "\"" + Exe + "\" --com-server");
                Check(Read(context) == "other-manager --custom", "Edited context menu was overwritten.");
            }));
            Test("New-tab button follows open progress and tabs recover after resize", () =>
            {
                var factory = new FrameworkElementFactory(typeof(AdaptiveTabStripPanel));
                factory.SetValue(FrameworkElement.HeightProperty, 40.0);
                var items = new ItemsControl { ItemsPanel = new ItemsPanelTemplate(factory) };
                var first = new Border { Height = 40 };
                items.Items.Add(first);
                var scroll = new ScrollViewer
                {
                    Content = items, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false
                };
                var plus = new Border { Width = 28, Height = 28 };
                var track = new TabBarTrackPanel();
                track.Children.Add(scroll); track.Children.Add(plus);
                void Layout(double width)
                {
                    track.Measure(new Size(width, 40));
                    track.Arrange(new Rect(0, 0, width, 40));
                    track.UpdateLayout();
                }
                Layout(1000);
                double originalX = plus.TranslatePoint(new Point(), track).X;
                var opening = new Border { Height = 40 };
                AdaptiveTabStripPanel.SetOpenProgress(opening, 0.05);
                items.Items.Add(opening);
                Layout(1000);
                double initialX = plus.TranslatePoint(new Point(), track).X;
                Check(initialX > originalX && initialX < originalX + 30, $"Opening tab jumped from {originalX} to {initialX}.");
                AdaptiveTabStripPanel.SetOpenProgress(opening, 0.5);
                Layout(1000);
                double halfwayX = plus.TranslatePoint(new Point(), track).X;
                Check(halfwayX > initialX && halfwayX < originalX + 150);
                AdaptiveTabStripPanel.SetOpenProgress(opening, 1);
                Layout(1000);
                Check(Math.Abs(plus.TranslatePoint(new Point(), track).X - originalX - 192) < 1);
                Layout(260);
                Check(first.ActualWidth < 192);
                Layout(1000);
                Check(Math.Abs(first.ActualWidth - 192) < 1, "Tabs retained the old narrow viewport.");
                Check(double.IsNaN(scroll.Width), "Measurement set an explicit ScrollViewer width.");
                for (int i = 0; i < 30; i++) items.Items.Add(new Border { Height = 40 });
                Layout(500);
                Check(first.ActualWidth >= 48 && scroll.ScrollableWidth > 0);
            });
            Test("Scoped quit reaches its executable and sends nothing to another copy", () =>
            {
                foreach (bool sameExecutable in new[] { false, true })
                {
                    using var client = new SingleInstanceService("Regression." + Guid.NewGuid().ToString("N"));
                    string pipeName = (string)typeof(SingleInstanceService).GetField("_pipeName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    Task connection = server.WaitForConnectionAsync();
                    Task<byte[]> received = Task.Run(async () =>
                    {
                        await connection;
                        using var data = new MemoryStream();
                        await server.CopyToAsync(data);
                        return data.ToArray();
                    });
                    string expected = sameExecutable ? Assembly.GetExecutingAssembly().Location : Exe;
                    bool sent = client.TryForwardToPrimary(new LaunchActionRequest { IsQuitProcess = true }, 1500, expected);
                    Check(received.Wait(3000), "Quit client did not close its pipe.");
                    Check(sent == sameExecutable);
                    if (sameExecutable) Check(LaunchRequestCodec.Decode(received.Result)!.IsQuitProcess);
                    else Check(received.Result.Length == 0, "Quit was sent to a different executable.");
                }
            });
            Test("Installer takeover quits unregistered copies using each copy's client", () =>
            {
                string template = BuildQuitFixture();
                string install = MakeDirectory("stop-target"), portable = MakeDirectory("stop-portable");
                string installedExe = Path.Combine(install, "LeafTab.exe"), portableExe = Path.Combine(portable, "LeafTab.exe");
                File.Copy(template, installedExe); File.Copy(template, portableExe);
                using var installed = StartQuitFixture(installedExe);
                using var downloaded = StartQuitFixture(portableExe);
                try
                {
                    // Scope the process inventory to fixtures: never send quit to the user's app.
                    LeafTab.Setup.SetupEngine.StopRunningLeafTabProcesses(installedExe, false,
                        new[] { Process.GetProcessById(installed.Id), Process.GetProcessById(downloaded.Id) });
                    Check(installed.WaitForExit(1000) && !downloaded.HasExited);
                    LeafTab.Setup.SetupEngine.StopRunningLeafTabProcesses(Path.Combine(Root, "new-install", "LeafTab.exe"), true,
                        new[] { Process.GetProcessById(downloaded.Id) });
                    Check(downloaded.WaitForExit(1000), "Unregistered portable copy kept running during takeover.");
                    Check(File.Exists(Path.Combine(install, "quit")) && File.Exists(Path.Combine(portable, "quit")));
                }
                finally
                {
                    File.WriteAllText(Path.Combine(install, "quit"), "");
                    File.WriteAllText(Path.Combine(portable, "quit"), "");
                    installed.WaitForExit(2000); downloaded.WaitForExit(2000);
                }
            });
            Test("Default explorer status message reflects installer automatic recovery", () => RegistrySandbox((classes, backup) =>
            {
                var res = ShellIntegrationService.SetDefaultExplorerEnabledCore(true, Exe, classes, backup, false);
                Check(res.Ok && !res.Message.Contains("删除程序前") && res.Message.Contains("卸载"), res.Message);
            }));
            Test("Setup version metadata matches main executable version", () =>
            {
                Check(LeafTab.Setup.SetupEngine.AppVersion == "0.6.4");
                Check(typeof(LeafTab.App).Assembly.GetName().Version == Version.Parse(LeafTab.Setup.SetupEngine.AppVersion + ".0"));
            });
            Test("Breadcrumb blank hit filter distinguishes blank chrome from interactive buttons", () =>
            {
                var rootBorder = new Border();
                var grid = new Grid();
                rootBorder.Child = grid;
                var rootBtnPath = new System.Windows.Shapes.Path();
                var rootBtn = new Button { Content = rootBtnPath };
                var sepPath = new System.Windows.Shapes.Path();
                var crumbText = new TextBlock { Text = "Demo" };
                var crumbBtn = new Button { Content = crumbText };
                var itemsPanel = new StackPanel();
                itemsPanel.Children.Add(crumbBtn);
                var scroll = new ScrollViewer { Content = itemsPanel };
                grid.Children.Add(rootBtn);
                grid.Children.Add(sepPath);
                grid.Children.Add(scroll);

                Check(LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(scroll, rootBorder));
                Check(LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(itemsPanel, rootBorder));
                Check(LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(grid, rootBorder));
                Check(LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(sepPath, rootBorder));
                Check(LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(rootBorder, rootBorder));
                Check(!LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(crumbText, rootBorder));
                Check(!LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(crumbBtn, rootBorder));
                Check(!LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(rootBtnPath, rootBorder));
                Check(!LeafTab.Views.MainWindow.ShouldEnterAddressEditFromBreadcrumbClick(rootBtn, rootBorder));
            });
            Test("Built-in preview renders Markdown richly without locking the file", () =>
            {
                string dir = MakeDirectory("preview-md");
                string mdPath = Path.Combine(dir, "00_项目上下文.md");
                string mdContent =
                    "---\nowner: LeafTab\n---\n\n" +
                    "# 项目概览\n\n" +
                    "这是 **加粗**、*斜体*、`内联代码` 与 [链接](https://github.com/Fahaxiki123/LeafTab)。\n\n" +
                    "- [x] 完成任务\n" +
                    "- [ ] 待办事项\n\n" +
                    "| 模块 | 状态 |\n| --- | --- |\n| 预览 | 就绪 |\n\n" +
                    "```json\n{\n  \"ok\": true\n}\n```\n";
                File.WriteAllText(mdPath, mdContent, new UTF8Encoding(false));

                FilePreviewData data = FilePreviewService.LoadPreviewAsync(mdPath, "00_项目上下文", false, "Markdown 源文件", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(data.Kind == FilePreviewKind.Markdown, $"Expected Markdown, got {data.Kind}");
                Check(!data.IsTruncated);
                Check(data.TextContent.Contains("# 项目概览"));

                FlowDocument doc = FilePreviewService.BuildMarkdownDocument(data.TextContent);
                Check(doc.Blocks.OfType<Paragraph>().Any(), "Missing rendered heading/paragraphs.");
                Check(doc.Blocks.OfType<Table>().Any(), "Missing rendered Markdown table.");
                Check(doc.Blocks.OfType<Section>().Count() >= 2, "Missing frontmatter or code block section.");

                File.Delete(mdPath);
                Check(!File.Exists(mdPath), "Preview held a file lock after completion.");
            });
            Test("Built-in preview truncates oversized UTF-8 safely and decodes ANSI/GBK text", () =>
            {
                string dir = MakeDirectory("preview-enc");
                string bigUtf8 = Path.Combine(dir, "large.json");
                string repeated = new string('中', 40000); // 120,000 UTF-8 bytes (> 96 KB, cuts mid-3-byte char!)
                File.WriteAllText(bigUtf8, repeated, new UTF8Encoding(false));

                FilePreviewData utfData = FilePreviewService.LoadPreviewAsync(bigUtf8, "large.json", false, "JSON 文件", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(utfData.Kind == FilePreviewKind.Text, $"Expected Text, got {utfData.Kind}");
                Check(utfData.IsTruncated, "Expected large file to be truncated at 96 KB.");
                Check(utfData.EncodingName == "UTF-8", $"Expected UTF-8, got {utfData.EncodingName}");
                Check(utfData.TextContent.IndexOf('\uFFFD') < 0, "Truncation split produced replacement characters.");

                string ansiTxt = Path.Combine(dir, "ansi_note.txt");
                string sample = "中文预览配置测试内容";
                Encoding ansi = Encoding.Default;
                File.WriteAllBytes(ansiTxt, ansi.GetBytes(sample));
                FilePreviewData ansiData = FilePreviewService.LoadPreviewAsync(ansiTxt, "ansi_note.txt", false, "文本文档", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(ansiData.Kind == FilePreviewKind.Text);
                Check(ansiData.TextContent == sample, $"Decoded '{ansiData.TextContent}' != '{sample}'");
            });
            Test("Built-in preview decodes PNG dimensions without holding a file handle", () =>
            {
                string dir = MakeDirectory("preview-img");
                string imgPath = Path.Combine(dir, "sample.png");
                var wb = new WriteableBitmap(32, 24, 96, 96, PixelFormats.Bgra32, null);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(wb));
                using (var fs = File.Create(imgPath))
                {
                    encoder.Save(fs);
                }

                FilePreviewData data = FilePreviewService.LoadPreviewAsync(imgPath, "sample.png", false, "PNG 图像", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(data.Kind == FilePreviewKind.Image, $"Expected Image, got {data.Kind}");
                Check(data.ImagePixelWidth == 32 && data.ImagePixelHeight == 24, $"Unexpected size {data.ImagePixelWidth}x{data.ImagePixelHeight}");
                Check(data.Image != null && data.Image.IsFrozen, "Image must be frozen for safe UI binding.");

                File.Delete(imgPath);
                Check(!File.Exists(imgPath), "Image preview held a file lock after completion.");
            });
            Test("Built-in preview falls back to metadata card for binary payloads", () =>
            {
                string dir = MakeDirectory("preview-bin");
                string binPath = Path.Combine(dir, "archive.unknown");
                File.WriteAllBytes(binPath, new byte[] { 0x50, 0x4B, 0x00, 0x00, 0x12, 0x34, 0x00, 0x7F });

                FilePreviewData data = FilePreviewService.LoadPreviewAsync(binPath, "archive.unknown", false, "未知文件", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(data.Kind == FilePreviewKind.MetadataCard, $"Expected MetadataCard, got {data.Kind}");
                Check(data.CanOpenWithDefaultApp);
                Check(data.MetadataRows.Any(r => r.Label == "路径" && r.Value == binPath));
            });
            Test("Preview resolves icon and type name off the UI thread and skips network stalls on UNC paths", () =>
            {
                string dir = MakeDirectory("preview-bg-icon");
                string notePath = Path.Combine(dir, "readme.txt");
                File.WriteAllText(notePath, "background icon check");

                FilePreviewData data = FilePreviewService.LoadPreviewAsync(
                    notePath,
                    "readme.txt",
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                Check(data.Kind == FilePreviewKind.Text, $"Expected Text, got {data.Kind}");
                Check(data.HeaderIcon != null && data.HeaderIcon.IsFrozen, "Expected background-resolved frozen HeaderIcon.");
                Check(!string.IsNullOrWhiteSpace(data.Subtitle), "Expected background-resolved type name in Subtitle.");

                var sw = Stopwatch.StartNew();
                var uncIcon = LeafTab.Interop.PidlManager.GetLargeIconAndTypeName(@"\\192.0.2.1\unreachable_share\notes.txt", false);
                sw.Stop();
                Check(sw.ElapsedMilliseconds < 1500, $"UNC attribute-based icon lookup took {sw.ElapsedMilliseconds}ms.");
                Check(!string.IsNullOrWhiteSpace(uncIcon.TypeName));
            });
            Test("Image preview caps tall image height and guards against pixel-count bombs", () =>
            {
                string dir = MakeDirectory("preview-img-bounds");

                // 1. 64 x 20,000 tall PNG (~5 KB on disk): must decode scaled to height <= 1600 instead of full 20,000 px
                string tallPath = Path.Combine(dir, "tall_strip.png");
                var tallBitmap = BitmapSource.Create(
                    64,
                    20000,
                    96,
                    96,
                    PixelFormats.Gray8,
                    null,
                    new byte[64 * 20000],
                    64);
                var tallEncoder = new PngBitmapEncoder();
                tallEncoder.Frames.Add(BitmapFrame.Create(tallBitmap));
                using (var fs = File.Create(tallPath))
                {
                    tallEncoder.Save(fs);
                }

                FilePreviewData tallData = FilePreviewService.LoadPreviewAsync(tallPath, "tall_strip.png", false, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(tallData.Kind == FilePreviewKind.Image, $"Expected Image for tall PNG, got {tallData.Kind}");
                Check(tallData.ImagePixelWidth == 64 && tallData.ImagePixelHeight == 20000,
                    $"Expected original dimensions 64x20000, got {tallData.ImagePixelWidth}x{tallData.ImagePixelHeight}");
                Check(tallData.Image != null && tallData.Image.IsFrozen, "Decoded tall image must be frozen.");
                Check(tallData.Image!.PixelHeight == 1600,
                    $"Expected tall image height capped at 1600, got {tallData.Image.PixelHeight}");

                // 2. 10,000 x 10,000 monochrome PNG (~15 KB on disk, 100 MP > 67.1 MP cap): must fall back to MetadataCard without full decode
                string bombPath = Path.Combine(dir, "pixel_bomb.png");
                int bombStride = (10000 + 7) / 8;
                var bombBitmap = BitmapSource.Create(
                    10000,
                    10000,
                    96,
                    96,
                    PixelFormats.BlackWhite,
                    null,
                    new byte[bombStride * 10000],
                    bombStride);
                var bombEncoder = new PngBitmapEncoder();
                bombEncoder.Frames.Add(BitmapFrame.Create(bombBitmap));
                using (var fs = File.Create(bombPath))
                {
                    bombEncoder.Save(fs);
                }

                FilePreviewData bombData = FilePreviewService.LoadPreviewAsync(bombPath, "pixel_bomb.png", false, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(bombData.Kind == FilePreviewKind.MetadataCard && bombData.Image == null,
                    $"Expected MetadataCard fallback for 100 MP image, got {bombData.Kind}");
                Check(bombData.ImagePixelWidth == 10000 && bombData.ImagePixelHeight == 10000,
                    $"Expected inspected dimensions 10000x10000, got {bombData.ImagePixelWidth}x{bombData.ImagePixelHeight}");
                Check(bombData.MetadataRows.Any(r => r.Label == "尺寸" && r.Value.Contains("10000 × 10000")),
                    "MetadataCard should still report inspected image dimensions.");

                File.Delete(tallPath);
                File.Delete(bombPath);
                Check(!File.Exists(tallPath) && !File.Exists(bombPath), "Bounded image preview held a file handle.");
            });
            Test("Installer fault injection rolls back files, shortcuts, associations, uninstall entry, and directories", () => RegistrySandbox((classes, backup) =>
            {
                static void ApplySandboxAssociations(string exe, bool enableDefault, bool enableContext, string cls, string bak)
                {
                    if (enableDefault)
                        Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, exe, cls, bak, notifyShell: false).Ok);
                    if (enableContext)
                        Check(ShellIntegrationService.SetContextMenuEnabledCore(true, exe, cls, notifyShell: false).Ok);
                }

                // Case 1: Fresh install fails after writing associations, shortcuts, and Uninstall registry
                using (RegistryKey dirShell = Registry.CurrentUser.CreateSubKey(classes + @"\Directory\shell"))
                {
                    dirShell.SetValue("", "PriorCustomExplorer", RegistryValueKind.String);
                }

                string freshParent = MakeDirectory("setup-fresh-parent");
                string freshSubParent = Path.Combine(freshParent, "NestedSegment");
                string freshInstallDir = Path.Combine(freshSubParent, "LeafTab");
                string programsDir = MakeDirectory("setup-programs");
                string desktopDir = MakeDirectory("setup-desktop");
                string uninstallKeyPath = classes + @"\MockUninstall\LeafTab";

                var freshOpts = new LeafTab.Setup.SetupOptions
                {
                    InstallDir = freshInstallDir,
                    SetDefaultExplorer = true,
                    AddContextMenu = true,
                    CreateShortcuts = true,
                    RegisterUninstaller = true,
                    LaunchAfterInstall = false,
                    IsSilent = true
                };

                var freshEnv = new LeafTab.Setup.InstallEnvironment
                {
                    ClassesRoot = classes,
                    BackupRoot = backup,
                    UninstallRegistryKeyPath = uninstallKeyPath,
                    ProgramsShortcutDirectory = programsDir,
                    DesktopShortcutDirectory = desktopDir,
                    SelfExecutablePath = Exe,
                    SkipProcessStop = true,
                    RegisterAssociationsOverride = ApplySandboxAssociations,
                    FaultAfterStep = step =>
                    {
                        if (step == "uninstall-registry")
                            throw new InvalidOperationException("Injected late failure during fresh install.");
                    }
                };

                var freshRes = LeafTab.Setup.SetupEngine.ExecuteInstallCoreAsync(freshOpts, freshEnv).GetAwaiter().GetResult();
                Check(!freshRes.Ok, "Expected fault-injected fresh install to return Ok=false.");
                Check(!File.Exists(Path.Combine(freshInstallDir, "LeafTab.exe")), "Fresh install left LeafTab.exe behind.");
                Check(!File.Exists(Path.Combine(freshInstallDir, LeafTab.Setup.SetupEngine.UninstallerExeName)), "Fresh install left uninstaller behind.");
                Check(!Directory.Exists(freshInstallDir) && !Directory.Exists(freshSubParent), "Fresh install left newly created empty directories behind.");
                Check(!File.Exists(Path.Combine(programsDir, LeafTab.Setup.SetupEngine.ShortcutFileName)), "Fresh install left Start Menu shortcut behind.");
                Check(!File.Exists(Path.Combine(desktopDir, LeafTab.Setup.SetupEngine.ShortcutFileName)), "Fresh install left Desktop shortcut behind.");
                using (RegistryKey? uninst = Registry.CurrentUser.OpenSubKey(uninstallKeyPath))
                {
                    Check(uninst == null, "Fresh install left Uninstall registry key behind.");
                }
                Check(!ShellIntegrationService.IsDefaultExplorerEnabledCore(classes), "Fresh install left Default Explorer registration behind.");
                Check(!ShellIntegrationService.IsContextMenuEnabledCore(classes), "Fresh install left Context Menu registration behind.");
                Check(Read(classes + @"\Directory\shell") == "PriorCustomExplorer", "Fresh install rollback lost prior Directory\\shell default verb.");

                // Case 2: Upgrade install fails after modifying binaries, disabling prior associations, and overwriting shortcuts & Uninstall key
                string upgradeDir = MakeDirectory("setup-upgrade");
                string upgradeExe = Path.Combine(upgradeDir, "LeafTab.exe");
                string upgradeUninst = Path.Combine(upgradeDir, LeafTab.Setup.SetupEngine.UninstallerExeName);

                byte[] priorExeBytes = File.ReadAllBytes(Exe).Concat(Encoding.ASCII.GetBytes("PRIOR_V061_EXE")).ToArray();
                byte[] priorUninstBytes = Encoding.ASCII.GetBytes("PRIOR_V061_UNINSTALLER_STUB");
                File.WriteAllBytes(upgradeExe, priorExeBytes);
                File.WriteAllBytes(upgradeUninst, priorUninstBytes);

                ApplySandboxAssociations(upgradeExe, enableDefault: true, enableContext: true, classes, backup);

                string priorDesktopLnk = Path.Combine(desktopDir, LeafTab.Setup.SetupEngine.ShortcutFileName);
                byte[] priorLnkBytes = new byte[] { 0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00 };
                File.WriteAllBytes(priorDesktopLnk, priorLnkBytes);

                using (RegistryKey priorUninstKey = Registry.CurrentUser.CreateSubKey(uninstallKeyPath))
                {
                    priorUninstKey.SetValue("DisplayVersion", "0.6.1", RegistryValueKind.String);
                    priorUninstKey.SetValue("InstallLocation", upgradeDir, RegistryValueKind.String);
                }

                var upgradeOpts = new LeafTab.Setup.SetupOptions
                {
                    InstallDir = upgradeDir,
                    SetDefaultExplorer = false, // disables prior default during upgrade before failing!
                    AddContextMenu = false,     // disables prior context menu during upgrade before failing!
                    CreateShortcuts = true,
                    RegisterUninstaller = true,
                    LaunchAfterInstall = false,
                    IsSilent = true
                };

                var upgradeEnv = new LeafTab.Setup.InstallEnvironment
                {
                    ClassesRoot = classes,
                    BackupRoot = backup,
                    UninstallRegistryKeyPath = uninstallKeyPath,
                    ProgramsShortcutDirectory = programsDir,
                    DesktopShortcutDirectory = desktopDir,
                    SelfExecutablePath = Exe,
                    SkipProcessStop = true,
                    RegisterAssociationsOverride = ApplySandboxAssociations,
                    FaultAfterStep = step =>
                    {
                        if (step == "uninstall-registry")
                            throw new InvalidOperationException("Injected late failure during upgrade install.");
                    }
                };

                var upgradeRes = LeafTab.Setup.SetupEngine.ExecuteInstallCoreAsync(upgradeOpts, upgradeEnv).GetAwaiter().GetResult();
                Check(!upgradeRes.Ok, "Expected fault-injected upgrade install to return Ok=false.");
                Check(File.ReadAllBytes(upgradeExe).SequenceEqual(priorExeBytes), "Upgrade rollback failed to restore prior LeafTab.exe.");
                Check(File.ReadAllBytes(upgradeUninst).SequenceEqual(priorUninstBytes), "Upgrade rollback failed to restore prior uninstaller.");
                Check(!File.Exists(upgradeExe + ".previous") && !File.Exists(upgradeUninst + ".previous"), "Upgrade rollback left .previous files behind.");
                Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, upgradeExe), "Upgrade rollback failed to re-restore prior Default Explorer ownership.");
                Check(ShellIntegrationService.IsContextMenuEnabledCore(classes, upgradeExe), "Upgrade rollback failed to re-restore prior Context Menu ownership.");
                Check(File.ReadAllBytes(priorDesktopLnk).SequenceEqual(priorLnkBytes), "Upgrade rollback failed to restore prior Desktop shortcut bytes.");
                Check(!File.Exists(Path.Combine(programsDir, LeafTab.Setup.SetupEngine.ShortcutFileName)), "Upgrade rollback left newly created Start Menu shortcut behind.");
                using (RegistryKey? restoredUninst = Registry.CurrentUser.OpenSubKey(uninstallKeyPath))
                {
                    Check(restoredUninst != null && (restoredUninst.GetValue("DisplayVersion") as string) == "0.6.1",
                        "Upgrade rollback failed to restore prior Uninstall DisplayVersion=0.6.1.");
                }
            }));
            Test("Changing install directory A -> B and failing preserves A's system associations and backups", () => RegistrySandbox((classes, backup) =>
            {
                static void ApplySandboxAssociations(string exe, bool enableDefault, bool enableContext, string cls, string bak)
                {
                    if (enableDefault)
                        Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(true, exe, cls, bak, notifyShell: false).Ok);
                    if (enableContext)
                        Check(ShellIntegrationService.SetContextMenuEnabledCore(true, exe, cls, notifyShell: false).Ok);
                }

                using (RegistryKey dirShell = Registry.CurrentUser.CreateSubKey(classes + @"\Directory\shell"))
                {
                    dirShell.SetValue("", "OriginalSystemExplorerVerb", RegistryValueKind.String);
                }

                string dirA = MakeDirectory("install-dir-A");
                string exeA = Path.Combine(dirA, "LeafTab.exe");
                File.Copy(Exe, exeA);
                ApplySandboxAssociations(exeA, enableDefault: true, enableContext: true, classes, backup);

                Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, exeA));
                Check(ShellIntegrationService.IsContextMenuEnabledCore(classes, exeA));

                string programsDir = MakeDirectory("ab-programs");
                string desktopDir = MakeDirectory("ab-desktop");
                string uninstallKeyPath = classes + @"\MockUninstall\LeafTab";

                foreach ((string failStep, bool bDefault, bool bContext) in new[]
                {
                    ("associations", true, true),
                    ("uninstall-registry", true, true),
                    ("associations", false, true)
                })
                {
                    string dirB = Path.Combine(MakeDirectory("install-dir-B-parent"), "LeafTabB_" + Guid.NewGuid().ToString("N"));
                    var optsB = new LeafTab.Setup.SetupOptions
                    {
                        InstallDir = dirB,
                        SetDefaultExplorer = bDefault,
                        AddContextMenu = bContext,
                        CreateShortcuts = true,
                        RegisterUninstaller = true,
                        LaunchAfterInstall = false,
                        IsSilent = true
                    };

                    var envB = new LeafTab.Setup.InstallEnvironment
                    {
                        ClassesRoot = classes,
                        BackupRoot = backup,
                        UninstallRegistryKeyPath = uninstallKeyPath,
                        ProgramsShortcutDirectory = programsDir,
                        DesktopShortcutDirectory = desktopDir,
                        SelfExecutablePath = Exe,
                        SkipProcessStop = true,
                        RegisterAssociationsOverride = ApplySandboxAssociations,
                        FaultAfterStep = step =>
                        {
                            if (step == failStep)
                                throw new InvalidOperationException($"Injected A->B failure after {step}.");
                        }
                    };

                    var resB = LeafTab.Setup.SetupEngine.ExecuteInstallCoreAsync(optsB, envB).GetAwaiter().GetResult();
                    Check(!resB.Ok, "Expected A->B fault-injected install to fail.");
                    Check(!Directory.Exists(dirB), $"Failed A->B install left target directory {dirB} behind.");
                    Check(ShellIntegrationService.IsDefaultExplorerEnabledCore(classes, exeA),
                        $"Directory A lost Default Explorer ownership after B failed at '{failStep}' (bDefault={bDefault}).");
                    Check(ShellIntegrationService.IsContextMenuEnabledCore(classes, exeA),
                        $"Directory A lost Context Menu ownership after B failed at '{failStep}' (bContext={bContext}).");
                    Check(Read(classes + @"\CLSID\" + ShellIntegrationService.LeafTabExecuteClsidString + @"\LocalServer32") == "\"" + exeA + "\" --com-server",
                        "Directory A's COM LocalServer32 registration was not preserved.");
                    Check(Read(classes + @"\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\open\command") == "\"" + exeA + "\" \"此电脑\"",
                        "Directory A's Win+E CLSID command was not preserved.");
                }

                // Confirm BackupRoot was also preserved intact so uninstalling A still restores OriginalSystemExplorerVerb
                Check(ShellIntegrationService.SetDefaultExplorerEnabledCore(false, exeA, classes, backup, notifyShell: false).Ok);
                Check(Read(classes + @"\Directory\shell") == "OriginalSystemExplorerVerb",
                    "Pre-A backup verb was lost during A->B rollback.");
            }));
            Test("Rollback never overwrites current binaries with stale .previous files left from prior runs", () => RegistrySandbox((classes, backup) =>
            {
                string dir = MakeDirectory("stale-previous-guard");
                string targetExe = Path.Combine(dir, "LeafTab.exe");
                string targetUninst = Path.Combine(dir, LeafTab.Setup.SetupEngine.UninstallerExeName);
                string staleExePrev = targetExe + ".previous";
                string staleUninstPrev = targetUninst + ".previous";

                byte[] currentExeBytes = Encoding.ASCII.GetBytes("MZ_CURRENT_ACTIVE_BINARY_V2");
                byte[] currentUninstBytes = Encoding.ASCII.GetBytes("MZ_CURRENT_ACTIVE_UNINSTALLER_V2");
                byte[] staleExeBytes = Encoding.ASCII.GetBytes("MZ_STALE_LEFTOVER_FROM_LAST_WEEK_V0");
                byte[] staleUninstBytes = Encoding.ASCII.GetBytes("MZ_STALE_UNINSTALLER_FROM_LAST_WEEK_V0");

                File.WriteAllBytes(targetExe, currentExeBytes);
                File.WriteAllBytes(targetUninst, currentUninstBytes);
                File.WriteAllBytes(staleExePrev, staleExeBytes);
                File.WriteAllBytes(staleUninstPrev, staleUninstBytes);

                var opts = new LeafTab.Setup.SetupOptions
                {
                    InstallDir = dir,
                    SetDefaultExplorer = false,
                    AddContextMenu = false,
                    CreateShortcuts = false,
                    RegisterUninstaller = false,
                    LaunchAfterInstall = false,
                    IsSilent = true
                };

                // 1. Failure at stop-processes before binary extraction starts
                var earlyFailEnv = new LeafTab.Setup.InstallEnvironment
                {
                    ClassesRoot = classes,
                    BackupRoot = backup,
                    SelfExecutablePath = Exe,
                    SkipProcessStop = true,
                    FaultAfterStep = step =>
                    {
                        if (step == "stop-processes")
                            throw new IOException("Simulated running instance refusal before extraction.");
                    }
                };
                var r1 = LeafTab.Setup.SetupEngine.ExecuteInstallCoreAsync(opts, earlyFailEnv).GetAwaiter().GetResult();
                Check(!r1.Ok);
                Check(File.ReadAllBytes(targetExe).SequenceEqual(currentExeBytes),
                    "Early failure before extraction overwrote current LeafTab.exe with stale .previous file!");
                Check(File.ReadAllBytes(targetUninst).SequenceEqual(currentUninstBytes),
                    "Early failure before extraction overwrote current uninstaller with stale .previous file!");

                // 2. Failure inside PayloadExtractorOverride before replacing targetExe
                var midExtractFailEnv = new LeafTab.Setup.InstallEnvironment
                {
                    ClassesRoot = classes,
                    BackupRoot = backup,
                    SelfExecutablePath = Exe,
                    SkipProcessStop = true,
                    PayloadExtractorOverride = _ => throw new InvalidDataException("Simulated payload verification failure.")
                };
                var r2 = LeafTab.Setup.SetupEngine.ExecuteInstallCoreAsync(opts, midExtractFailEnv).GetAwaiter().GetResult();
                Check(!r2.Ok);
                Check(File.ReadAllBytes(targetExe).SequenceEqual(currentExeBytes),
                    "Payload extraction failure overwrote current LeafTab.exe with stale .previous file!");
                Check(File.ReadAllBytes(targetUninst).SequenceEqual(currentUninstBytes),
                    "Payload extraction failure overwrote current uninstaller with stale .previous file!");

                // 3. Real ExtractEmbeddedPayloadToTarget failing on locked targetExe while stale .previous exists
                using (var heldLock = new FileStream(targetExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var lockedEnv = new LeafTab.Setup.InstallEnvironment
                    {
                        ClassesRoot = classes,
                        BackupRoot = backup,
                        SelfExecutablePath = Exe,
                        SkipProcessStop = true
                    };
                    var r3 = LeafTab.Setup.SetupEngine.ExecuteInstallCoreAsync(opts, lockedEnv).GetAwaiter().GetResult();
                    Check(!r3.Ok);
                }
                Check(File.ReadAllBytes(targetExe).SequenceEqual(currentExeBytes),
                    "Locked-binary File.Replace failure corrupted or overwrote current LeafTab.exe with stale .previous!");
                Check(Directory.GetFiles(dir, "*.installing.tmp").Length == 0,
                    "Rollback left temporary .installing.tmp files in install directory.");
            }));
            Test("Toolbar Refresh button and F5 share RefreshActiveTab and reset preview selection cache", () =>
            {
                var win = new LeafTab.Views.MainWindow(skipSessionPersistence: true, deferInitialFallbackTab: true);
                try
                {
                    FieldInfo previewKeyField = typeof(LeafTab.Views.MainWindow)
                        .GetField("_lastPreviewSelectionKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    FieldInfo selectedTabField = typeof(LeafTab.Views.MainWindow)
                        .GetField("_selectedTab", BindingFlags.NonPublic | BindingFlags.Instance)!;

                    IntPtr pidl = LeafTab.Interop.PidlManager.CreateThisPcPidl();
                    try
                    {
                        var tab = new LeafTab.Models.ExplorerTabItem(pidl);
                        try
                        {
                            selectedTabField.SetValue(win, tab);
                            previewKeyField.SetValue(win, "ONE|C:\\demo\\notes.md|notes.md|False");

                            var refreshBtn = (Button)win.FindName("RefreshButton");
                            Check(refreshBtn != null, "RefreshButton not found in MainWindow template.");
                            refreshBtn!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                            string keyAfterToolbarClick = (string)previewKeyField.GetValue(win)!;
                            Check(keyAfterToolbarClick != "ONE|C:\\demo\\notes.md|notes.md|False",
                                "Clicking toolbar RefreshButton failed to reset _lastPreviewSelectionKey.");
                        }
                        finally
                        {
                            selectedTabField.SetValue(win, null);
                            tab.Dispose();
                        }
                    }
                    finally
                    {
                        LeafTab.Interop.PidlManager.Free(ref pidl);
                    }
                }
                finally
                {
                    win.PrepareForForceExit();
                    win.Close();
                }
            });

            Test("Synthetic 3500-row Markdown table auto-switches to fast raw preview and caps FlowDocument build latency", () =>
            {
                string dir = MakeDirectory("preview-md-dense-table");
                string mdPath = Path.Combine(dir, "dense_3500_rows.md");
                var sb = new StringBuilder(90 * 1024);
                sb.AppendLine("# 合成压测表格");
                sb.AppendLine();
                sb.AppendLine("| 序号 | 模块 | 状态 |");
                sb.AppendLine("| --- | --- | --- |");
                for (int i = 0; i < 3500; i++)
                {
                    sb.Append("| ").Append(i.ToString("D4")).Append(" | item_").Append(i.ToString("D4")).AppendLine(" | ok |");
                }
                File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(false));
                long fileLen = new FileInfo(mdPath).Length;
                Check(fileLen > 70 * 1024 && fileLen < FilePreviewService.MaxTextPreviewBytes,
                    $"Expected ~75-90 KB un-truncated Markdown file, got {fileLen} bytes.");

                var loadSw = Stopwatch.StartNew();
                FilePreviewData data = FilePreviewService.LoadPreviewAsync(
                    mdPath,
                    "dense_3500_rows.md",
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                loadSw.Stop();

                Check(data.Kind == FilePreviewKind.Markdown, $"Expected Markdown, got {data.Kind}");
                Check(!data.IsTruncated, "Expected file under 96 KB not to be byte-truncated.");
                Check(data.MarkdownAutoRawFallback, "Expected 3500-row table to trigger MarkdownAutoRawFallback.");
                Check(!string.IsNullOrWhiteSpace(data.MarkdownComplexityNotice), "Expected non-empty MarkdownComplexityNotice.");
                Check(data.ParsedMarkdown != null && data.ParsedMarkdown.ExceedsComplexityBudget, "Expected pre-parsed Markdown AST with budget flag.");

                var buildSw = Stopwatch.StartNew();
                FlowDocument doc = FilePreviewService.BuildMarkdownDocument(data.ParsedMarkdown!);
                buildSw.Stop();

                Table renderedTable = doc.Blocks.OfType<Table>().Single();
                int totalRenderedRows = renderedTable.RowGroups.Sum(g => g.Rows.Count);
                Check(totalRenderedRows <= FilePreviewService.MaxMarkdownTableRows + 2,
                    $"Expected capped table rows <= {FilePreviewService.MaxMarkdownTableRows + 2}, got {totalRenderedRows}");
                Check(buildSw.ElapsedMilliseconds < 350,
                    $"Expected capped FlowDocument build < 350ms, took {buildSw.ElapsedMilliseconds}ms.");
            });

            Test("Preferences persist PreviewPaneWidthDip, opt-in RestorePreviousTabs, and legacy JSON defaults", () =>
            {
                string dir = MakeDirectory("config-prefs-v063");
                string legacyPath = Path.Combine(dir, "legacy.json");
                File.WriteAllText(legacyPath, "{\"Version\":1,\"BookmarksInitialized\":true}", new UTF8Encoding(false));

                AppConfig legacyLoaded = AppConfigService.Load(legacyPath);
                Check(Math.Abs(legacyLoaded.PreviewPaneWidthDip - 340.0) < 0.1,
                    $"Expected default PreviewPaneWidthDip=340, got {legacyLoaded.PreviewPaneWidthDip}");
                Check(!legacyLoaded.RestorePreviousTabs, "Expected RestorePreviousTabs=false by default.");
                Check(legacyLoaded.EnableWarmStandby, "Expected EnableWarmStandby=true by default.");

                // When RestorePreviousTabs is false, OpenTabs must be cleared on Flush
                string roundTripPath = Path.Combine(dir, "roundtrip.json");
                var defaultStore = new AppConfigStore(
                    new AppConfig
                    {
                        BookmarksInitialized = true,
                        OpenTabs = new System.Collections.Generic.List<SavedTabConfigItem>
                        {
                            new SavedTabConfigItem { CurrentPath = Root }
                        }
                    },
                    cfg => AppConfigService.Save(cfg, roundTripPath));
                defaultStore.UpdatePreferences(true, true, true, 468.0, restorePreviousTabs: false);
                Check(defaultStore.Flush());
                AppConfig afterDisabled = AppConfigService.Load(roundTripPath);
                Check(Math.Abs(afterDisabled.PreviewPaneWidthDip - 468.0) < 0.1);
                Check(!afterDisabled.RestorePreviousTabs && afterDisabled.OpenTabs.Count == 0,
                    "Expected OpenTabs cleared when RestorePreviousTabs is false.");

                // When RestorePreviousTabs is true, OpenTabs and SelectedTabIndex must round-trip cleanly
                string restorePath = Path.Combine(dir, "restore_enabled.json");
                var restoreStore = new AppConfigStore(
                    new AppConfig { BookmarksInitialized = true },
                    cfg => AppConfigService.Save(cfg, restorePath));
                restoreStore.UpdatePreferences(false, true, true, 420.0, restorePreviousTabs: true);
                restoreStore.UpdateSavedTabs(new[]
                {
                    new SavedTabConfigItem
                    {
                        CurrentPath = Root,
                        HistoryPaths = new System.Collections.Generic.List<string> { Root },
                        HistoryIndex = 0
                    },
                    new SavedTabConfigItem
                    {
                        CurrentPath = dir,
                        HistoryPaths = new System.Collections.Generic.List<string> { Root, dir },
                        HistoryIndex = 1
                    }
                }, selectedTabIndex: 1);
                Check(restoreStore.Flush());

                AppConfig reloaded = AppConfigService.Load(restorePath);
                Check(reloaded.RestorePreviousTabs);
                Check(Math.Abs(reloaded.PreviewPaneWidthDip - 420.0) < 0.1);
                Check(reloaded.OpenTabs.Count == 2 && reloaded.SelectedTabIndex == 1);
                Check(reloaded.OpenTabs[1].CurrentPath == dir && reloaded.OpenTabs[1].HistoryIndex == 1);
            });

            Test("Global dispatcher exception handler logs diagnostics and suppresses modal dialog in warm standby", () =>
            {
                string logSandbox = MakeDirectory("diag-log-sandbox");
                string originalLogDir = LeafTab.App.DiagnosticLogDirectoryPath;
                try
                {
                    LeafTab.App.DiagnosticLogDirectoryPath = logSandbox;

                    bool standbyResetInvoked = false;
                    bool suppressedInStandby = LeafTab.App.HandleDispatcherUnhandledExceptionCore(
                        new InvalidOperationException("synthetic-standby-fault"),
                        isSelfTest: false,
                        visibleWindowsCount: 0,
                        resetHiddenStandbyHosts: () => standbyResetInvoked = true);

                    Check(suppressedInStandby, "Expected modal dialog suppressed when visibleWindowsCount == 0.");
                    Check(standbyResetInvoked, "Expected hidden standby prewarmed hosts reset callback invoked.");

                    bool suppressedWhenVisible = LeafTab.App.HandleDispatcherUnhandledExceptionCore(
                        new ArgumentException("synthetic-visible-fault"),
                        isSelfTest: false,
                        visibleWindowsCount: 1,
                        resetHiddenStandbyHosts: null);
                    Check(!suppressedWhenVisible, "Expected visible window fault not to be silently suppressed.");

                    string logFile = Path.Combine(logSandbox, "diagnostic.log");
                    Check(File.Exists(logFile), "Expected diagnostic.log to be written.");
                    string logText = File.ReadAllText(logFile, Encoding.UTF8);
                    Check(logText.Contains("dispatcher-unhandled InvalidOperationException"),
                        $"Expected InvalidOperationException entry in diagnostic.log, got: {logText}");
                    Check(logText.Contains("dispatcher-unhandled ArgumentException"),
                        $"Expected ArgumentException entry in diagnostic.log, got: {logText}");
                }
                finally
                {
                    LeafTab.App.DiagnosticLogDirectoryPath = originalLogDir;
                }
            });

            Test("Multi-window complete exit saves unified session across all open windows instead of overwriting with last window", () =>
            {
                string dirA = MakeDirectory("multiwin-exit-A");
                string dirB = MakeDirectory("multiwin-exit-B");
                string configPath = Path.Combine(MakeDirectory("multiwin-exit-cfg"), "config.json");

                var sharedStore = new AppConfigStore(
                    new AppConfig
                    {
                        BookmarksInitialized = true,
                        RestorePreviousTabs = true
                    },
                    cfg => AppConfigService.Save(cfg, configPath));

                var winA = new LeafTab.Views.MainWindow(sharedStore);
                var winB = new LeafTab.Views.MainWindow(sharedStore);
                (IntPtr pidlA, _) = LeafTab.Interop.PidlManager.TryParseAddressAsync(dirA, CancellationToken.None).GetAwaiter().GetResult();
                (IntPtr pidlB, _) = LeafTab.Interop.PidlManager.TryParseAddressAsync(dirB, CancellationToken.None).GetAwaiter().GetResult();

                try
                {
                    Check(pidlA != IntPtr.Zero && pidlB != IntPtr.Zero, "Expected valid PIDLs for dirA and dirB.");
                    winA.SetRestorePreviousTabs(true);
                    winB.SetRestorePreviousTabs(true);

                    var tabA = new ExplorerTabItem(pidlA);
                    var tabB = new ExplorerTabItem(pidlB);
                    winA.Tabs.Add(tabA);
                    winB.Tabs.Add(tabB);

                    FieldInfo selectedTabField = typeof(LeafTab.Views.MainWindow)
                        .GetField("_selectedTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    selectedTabField.SetValue(winA, tabA);
                    selectedTabField.SetValue(winB, tabB);

                    // Execute the exact ForceExitAllWindows unified save + sequential window close sequence
                    var windows = new[] { winA, winB };
                    LeafTab.Views.MainWindow.SaveUnifiedSessionBeforeExit(windows, preferredWindow: winB);
                    foreach (var w in windows)
                    {
                        w.PrepareForForceExit(skipPerWindowSave: true);
                        w.Close();
                    }

                    AppConfig persisted = AppConfigService.Load(configPath);
                    Check(persisted.RestorePreviousTabs, "Expected RestorePreviousTabs=true.");
                    Check(persisted.OpenTabs.Count == 2,
                        $"Expected both Window A and Window B tabs persisted (2 total), got {persisted.OpenTabs.Count}");
                    Check(string.Equals(persisted.OpenTabs[0].CurrentPath.TrimEnd('\\'), dirA.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                        $"Expected first saved tab '{dirA}', got '{persisted.OpenTabs[0].CurrentPath}'");
                    Check(string.Equals(persisted.OpenTabs[1].CurrentPath.TrimEnd('\\'), dirB.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                        $"Expected second saved tab '{dirB}', got '{persisted.OpenTabs[1].CurrentPath}'");
                    Check(persisted.SelectedTabIndex == 1,
                        $"Expected preferred window B's active tab selected (index 1), got {persisted.SelectedTabIndex}");
                }
                finally
                {
                    LeafTab.Interop.PidlManager.Free(ref pidlA);
                    LeafTab.Interop.PidlManager.Free(ref pidlB);
                    try { if (!winA.IsClosingOrClosed) { winA.PrepareForForceExit(true); winA.Close(); } } catch { }
                    try { if (!winB.IsClosingOrClosed) { winB.PrepareForForceExit(true); winB.Close(); } } catch { }
                }
            });

            Test("Markdown 20000-line hard-break document strictly caps inline nodes and layout latency on manual render", () =>
            {
                string dir = MakeDirectory("preview-md-20k-hardbreaks");
                string mdTwoSpacePath = Path.Combine(dir, "hardbreaks_20k.md");
                var sb = new StringBuilder(82 * 1024);
                for (int i = 0; i < 20_000; i++)
                {
                    sb.Append("a  \n"); // 4 bytes * 20,000 = 80,000 bytes (< 96 KB preview limit)
                }
                File.WriteAllText(mdTwoSpacePath, sb.ToString(), new UTF8Encoding(false));
                long byteLen = new FileInfo(mdTwoSpacePath).Length;
                Check(byteLen == 80_000, $"Expected 80,000-byte Markdown fixture, got {byteLen}.");

                FilePreviewData data = FilePreviewService.LoadPreviewAsync(
                    mdTwoSpacePath,
                    "hardbreaks_20k.md",
                    false,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                Check(data.Kind == FilePreviewKind.Markdown && data.MarkdownAutoRawFallback,
                    "Expected 20,000-line hard-break Markdown to auto-fallback to raw mode.");
                Check(data.ParsedMarkdown != null && data.ParsedMarkdown.ExceedsComplexityBudget,
                    "Expected ExceedsComplexityBudget=true.");
                Check(data.ParsedMarkdown!.RenderedInlineCount <= FilePreviewService.MaxMarkdownInlineNodes,
                    $"Expected AST RenderedInlineCount <= {FilePreviewService.MaxMarkdownInlineNodes}, got {data.ParsedMarkdown.RenderedInlineCount}");

                // Simulate manual click on "渲染": materialize FlowDocument + run full WPF FlowDocumentScrollViewer layout
                var layoutSw = Stopwatch.StartNew();
                FlowDocument doc = FilePreviewService.BuildMarkdownDocument(data.ParsedMarkdown);
                int totalWpfInlines = CountFlowDocumentInlines(doc);
                var viewer = new FlowDocumentScrollViewer
                {
                    Width = 360,
                    Height = 640,
                    Document = doc
                };
                viewer.Measure(new Size(360, 640));
                viewer.Arrange(new Rect(0, 0, 360, 640));
                viewer.UpdateLayout();
                layoutSw.Stop();

                Check(totalWpfInlines <= FilePreviewService.MaxMarkdownInlineNodes + 2,
                    $"Expected materialized WPF Inline count <= {FilePreviewService.MaxMarkdownInlineNodes + 2}, got {totalWpfInlines}");
                Check(layoutSw.ElapsedMilliseconds < 450,
                    $"Expected manual render + FlowDocumentScrollViewer layout < 450ms, took {layoutSw.ElapsedMilliseconds}ms");

                // Also verify CommonMark trailing-backslash hard breaks ("a\\\n")
                var backslashSb = new StringBuilder(62 * 1024);
                for (int i = 0; i < 20_000; i++)
                {
                    backslashSb.Append("a\\\n");
                }
                MarkdownParsedDocument parsedBackslash = FilePreviewService.ParseMarkdownStructure(backslashSb.ToString());
                FlowDocument backslashDoc = FilePreviewService.BuildMarkdownDocument(parsedBackslash);
                int backslashInlines = CountFlowDocumentInlines(backslashDoc);
                Check(parsedBackslash.ExceedsComplexityBudget &&
                      backslashInlines <= FilePreviewService.MaxMarkdownInlineNodes + 2,
                    $"Expected trailing-backslash hard breaks capped <= {FilePreviewService.MaxMarkdownInlineNodes + 2}, got {backslashInlines}");
            });
        }

        private static int CountFlowDocumentInlines(FlowDocument doc)
        {
            int count = 0;
            void WalkBlocks(BlockCollection blocks)
            {
                foreach (Block b in blocks)
                {
                    switch (b)
                    {
                        case Paragraph p:
                            WalkInlines(p.Inlines);
                            break;
                        case Section s:
                            WalkBlocks(s.Blocks);
                            break;
                        case List list:
                            foreach (ListItem li in list.ListItems)
                                WalkBlocks(li.Blocks);
                            break;
                        case Table tbl:
                            foreach (TableRowGroup rg in tbl.RowGroups)
                                foreach (TableRow row in rg.Rows)
                                    foreach (TableCell cell in row.Cells)
                                        WalkBlocks(cell.Blocks);
                            break;
                    }
                }
            }

            void WalkInlines(InlineCollection inlines)
            {
                foreach (Inline inl in inlines)
                {
                    count++;
                    if (inl is Span span)
                    {
                        WalkInlines(span.Inlines);
                    }
                }
            }

            WalkBlocks(doc.Blocks);
            return count;
        }

        private static string BuildQuitFixture()
        {
            string exe = Path.Combine(MakeDirectory("quit-fixture"), "LeafTab.exe");
            const string source = @"
using System; using System.IO; using System.Threading;
class QuitFixture {
    static void Main(string[] args) {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string quit = Path.Combine(root, ""quit"");
        if (args.Length > 0 && args[0] == ""--quit"") { File.WriteAllText(quit, """"); return; }
        File.WriteAllText(Path.Combine(root, ""ready""), """");
        for (int i = 0; i < 1000 && !File.Exists(quit); i++) Thread.Sleep(20);
    }
}";
            using var compiler = new CSharpCodeProvider();
            var parameters = new CompilerParameters { GenerateExecutable = true, OutputAssembly = exe, CompilerOptions = "/target:winexe /platform:x64" };
            CompilerResults result = compiler.CompileAssemblyFromSource(parameters, source);
            Check(!result.Errors.HasErrors, string.Join("; ", result.Errors.Cast<CompilerError>()));
            return exe;
        }

        private static Process StartQuitFixture(string exe)
        {
            var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            string ready = Path.Combine(Path.GetDirectoryName(exe)!, "ready");
            Check(SpinWait.SpinUntil(() => File.Exists(ready) || process.HasExited, 3000) && File.Exists(ready));
            return process;
        }
    }
}
