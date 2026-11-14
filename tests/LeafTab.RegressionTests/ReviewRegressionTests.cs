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
                Check(LeafTab.Setup.SetupEngine.AppVersion == "0.6.2");
                Check(typeof(LeafTab.App).Assembly.GetName().Version == new Version(0, 6, 2, 0));
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
