using System;
using System.Collections.Generic;
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
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Views;

namespace LeafTab.Diagnostics
{
    internal static class SelfTestRunner
    {
        private sealed class ResourceSnapshot
        {
            public string Label { get; set; } = string.Empty;
            public int TabCount { get; set; }
            public double WorkingSetMb { get; set; }
            public double PrivateBytesMb { get; set; }
            public uint GdiObjects { get; set; }
            public uint UserObjects { get; set; }
        }

        public static async Task RunAndExitAsync(MainWindow window, string outputPath)
        {
            var log = new StringBuilder();
            try
            {
                await Task.Delay(900);

                string exePath = Assembly.GetExecutingAssembly().Location;
                long exeBytes = File.Exists(exePath) ? new FileInfo(exePath).Length : 0;
                string projectDir = ResolveProjectDirectory();

                log.AppendLine("# LeafTab Stage 1 & Stage 2 Automated Diagnostic Telemetry");
                log.AppendLine($"- Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                log.AppendLine($"- OSVersion: {Environment.OSVersion} (64BitOS={Environment.Is64BitOperatingSystem}, 64BitProc={Environment.Is64BitProcess})");
                log.AppendLine($"- CLR Version: {Environment.Version}");
                log.AppendLine($"- Executable: {exePath} ({exeBytes / 1024.0:F1} KB)");
                log.AppendLine($"- ResolvedProjectDir: {projectDir}");
                log.AppendLine();

                // ==================== 1. SPECIAL & VIRTUAL FOLDERS ====================
                log.AppendLine("## 1. Folder & Namespace Navigation Checks");

                ExplorerTabItem? tab1 = window.SelectedTab;
                if (tab1 != null)
                {
                    await WaitForDirectUiAsync(tab1.BrowserHost, 2500);
                    List<string> thisPcItems = tab1.BrowserHost.GetDisplayedItemNames(30);
                    log.AppendLine($"- [此电脑] Title='{tab1.Title}', ItemCount={thisPcItems.Count}, Items=[{string.Join(", ", thisPcItems)}]");

                    await ProbeThisPcGesturesAsync(tab1.BrowserHost, log);
                }

                bool projNavOk = await TestNavigateFolderAsync(window, projectDir, $"当前项目目录 {projectDir}", log);
                if (projNavOk && window.SelectedTab != null)
                {
                    await ProbeDirectoryGesturesAsync(window.SelectedTab.BrowserHost, FOLDERVIEWMODE.FVM_DETAILS, "详细信息视图 (FVM_DETAILS)", log);
                    await ProbeDirectoryGesturesAsync(window.SelectedTab.BrowserHost, FOLDERVIEWMODE.FVM_ICON, "图标视图 (FVM_ICON)", log);
                    window.SelectedTab.BrowserHost.TrySetViewStyle(ExplorerViewStyle.Details);
                }

                // ==================== 2. STAGE 2 FEATURE VERIFICATION ====================
                log.AppendLine();
                log.AppendLine("## 2. Stage 2 Feature Checks (Breadcrumbs, History Stack, Async Search, Reopen Tab, config.json)");

                if (window.SelectedTab != null)
                {
                    var crumbs = PidlManager.BuildBreadcrumbChain(window.SelectedTab.CurrentPidl);
                    string crumbText = string.Join(" > ", crumbs.Select(c => c.Title));
                    log.AppendLine($"- BreadcrumbChain: [{crumbText}] (Count={crumbs.Count})");

                    bool canBackBefore = window.SelectedTab.CanNavigateBack;
                    window.SelectedTab.NavigateHistoryStep(-1);
                    await Task.Delay(350);
                    bool canFwdAfterBack = window.SelectedTab.CanNavigateForward;
                    window.SelectedTab.NavigateHistoryStep(+1);
                    await Task.Delay(350);
                    log.AppendLine($"- HistoryStack Back/Forward: CanBackBefore={canBackBefore}, CanFwdAfterBack={canFwdAfterBack}, RestoredTitle='{window.SelectedTab.Title}'");

                    // Async directory search into IResultsFolder
                    var searchSw = Stopwatch.StartNew();
                    int foundCs = await window.SelectedTab.BrowserHost.StartSearchAsync(
                        "*.cs",
                        window.SelectedTab.CurrentPidl,
                        window.SelectedTab.FileSystemPath);
                    searchSw.Stop();
                    bool inSearch = window.SelectedTab.BrowserHost.IsShowingSearchResults;
                    List<string> searchSample = window.SelectedTab.BrowserHost.GetDisplayedItemNames(5);
                    window.SelectedTab.BrowserHost.ExitSearchMode();
                    await Task.Delay(150);
                    log.AppendLine($"- AsyncSearch '*.cs': MatchedCount={foundCs}, Elapsed={searchSw.ElapsedMilliseconds}ms, EnteredSearchMode={inSearch}, ExitedCleanly={!window.SelectedTab.BrowserHost.IsShowingSearchResults}, Sample=[{string.Join(", ", searchSample)}]");

                    // Reopen closed tab (Ctrl+Shift+T)
                    (IntPtr dlPidl, _) = await PidlManager.TryParseAddressAsync("下载", CancellationToken.None);
                    try
                    {
                        ExplorerTabItem tempDlTab = window.AddNewTab(dlPidl, activate: true);
                        await Task.Delay(250);
                        string closedTitle = tempDlTab.Title;
                        window.CloseTab(tempDlTab);
                        bool reopened = window.ReopenLastClosedTab();
                        await Task.Delay(250);
                        log.AppendLine($"- ReopenClosedTab (Ctrl+Shift+T): Closed='{closedTitle}', Reopened={reopened}, ActiveTitle='{window.SelectedTab?.Title}'");
                    }
                    finally
                    {
                        PidlManager.Free(ref dlPidl);
                    }
                }

                // config.json round-trip serialization check
                string tempCfgPath = Path.Combine(Path.GetTempPath(), $"leaftab_selftest_{Guid.NewGuid():N}.json");
                try
                {
                    var sampleCfg = new AppConfig
                    {
                        ShowBookmarkBar = true,
                        ShowPreviewPane = false,
                        SelectedTabIndex = 0
                    };
                    sampleCfg.Bookmarks.Add(new BookmarkConfigItem { Title = "LeafTab", TargetPath = projectDir });
                    sampleCfg.OpenTabs.Add(new SavedTabConfigItem
                    {
                        CurrentPath = projectDir,
                        HistoryPaths = new List<string> { "此电脑", projectDir },
                        HistoryIndex = 1
                    });

                    bool savedOk = AppConfigService.Save(sampleCfg, tempCfgPath);
                    AppConfig loadedCfg = AppConfigService.Load(tempCfgPath);
                    log.AppendLine($"- ConfigJsonRoundTrip: Saved={savedOk}, LoadedBookmarks={loadedCfg.Bookmarks.Count}, LoadedTabs={loadedCfg.OpenTabs.Count}, ShowBookmarkBar={loadedCfg.ShowBookmarkBar}");
                }
                finally
                {
                    if (File.Exists(tempCfgPath)) File.Delete(tempCfgPath);
                }

                await TestNavigateFolderAsync(window, "回收站", "虚拟目录：回收站", log);
                await TestNavigateFolderAsync(window, @"\\wsl.localhost", "WSL 命名空间 (\\\\wsl.localhost)", log);

                // ==================== 3. UNREACHABLE UNC ASYNC PRE-CHECK ====================
                log.AppendLine();
                log.AppendLine("## 3. Unreachable Network Path Async Pre-Check");
                int uiHeartbeats = 0;
                var heartbeatTimer = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                heartbeatTimer.Tick += (_, _) => uiHeartbeats++;
                heartbeatTimer.Start();

                var uncSw = Stopwatch.StartNew();
                bool uncNavResult = await window.NavigateAddressInputAsync(@"\\192.0.2.1\nonexistent_share");
                uncSw.Stop();
                heartbeatTimer.Stop();

                log.AppendLine($"- UNC \\\\192.0.2.1\\nonexistent_share: NavSucceeded={uncNavResult}, ElapsedMs={uncSw.ElapsedMilliseconds}ms, UiDispatcherHeartbeatsDuringWait={uiHeartbeats}");

                // ==================== 4. 1 / 10 / 20 TABS RESOURCE BENCHMARK ====================
                log.AppendLine();
                log.AppendLine("## 4. Multi-Tab Resource Benchmark (1 / 10 / 20 Tabs)");

                while (window.Tabs.Count > 1)
                {
                    window.CloseTab(window.Tabs[window.Tabs.Count - 1]);
                }
                await window.NavigateAddressInputAsync("此电脑");
                await Task.Delay(500);
                ForceGcAndPump();

                var snapshots = new List<ResourceSnapshot>
                {
                    CaptureSnapshot("1 个标签 (此电脑)", window.Tabs.Count)
                };

                var candidateFolders = new List<string>
                {
                    @"C:\",
                    projectDir,
                    @"C:\Windows",
                    @"C:\Windows\System32",
                    @"C:\Program Files",
                    @"C:\Users",
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                if (Directory.Exists(@"D:\")) candidateFolders.Insert(1, @"D:\");

                string[] sampleFolders = candidateFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                int folderIdx = 0;
                while (window.Tabs.Count < 10)
                {
                    string path = sampleFolders[folderIdx % sampleFolders.Length];
                    folderIdx++;
                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(path, CancellationToken.None);
                    try
                    {
                        ExplorerTabItem t = window.AddNewTab(pidl, activate: true);
                        await WaitForDirectUiAsync(t.BrowserHost, 1500);
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
                await Task.Delay(500);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("10 个标签 (多磁盘与系统目录混合)", window.Tabs.Count));

                while (window.Tabs.Count < 20)
                {
                    string path = sampleFolders[folderIdx % sampleFolders.Length];
                    folderIdx++;
                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(path, CancellationToken.None);
                    try
                    {
                        ExplorerTabItem t = window.AddNewTab(pidl, activate: true);
                        await WaitForDirectUiAsync(t.BrowserHost, 1500);
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
                await Task.Delay(600);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("20 个标签 (满载保活状态)", window.Tabs.Count));

                while (window.Tabs.Count > 2)
                {
                    window.CloseTab(window.Tabs[window.Tabs.Count - 1]);
                }
                await Task.Delay(400);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("从 20 标签关闭回落至 2 标签 (压测起点)", window.Tabs.Count));

                // ==================== 5. 50x OPEN / CLOSE STABILITY CURVE ====================
                for (int i = 1; i <= 50; i++)
                {
                    string path = sampleFolders[i % sampleFolders.Length];
                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(path, CancellationToken.None);
                    ExplorerTabItem tempTab;
                    try
                    {
                        tempTab = window.AddNewTab(pidl, activate: true);
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }

                    await WaitForDirectUiAsync(tempTab.BrowserHost, 1200);
                    await Task.Delay(40);
                    window.CloseTab(tempTab);
                    await Task.Delay(25);

                    if (i % 10 == 0)
                    {
                        ForceGcAndPump();
                        snapshots.Add(CaptureSnapshot($"反复开关第 {i}/50 次后 (保持 2 标签)", window.Tabs.Count));
                    }
                }

                await Task.Delay(600);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("50 次开关静置收敛后 (保持 2 标签)", window.Tabs.Count));

                foreach (ResourceSnapshot s in snapshots)
                {
                    log.AppendLine($"- [{s.Label}] Tabs={s.TabCount}, WorkingSet={s.WorkingSetMb:F1} MB, PrivateBytes={s.PrivateBytesMb:F1} MB, GDI={s.GdiObjects}, USER={s.UserObjects}");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine();
                log.AppendLine("## ERROR");
                log.AppendLine(ex.ToString());
            }

            string fullOut = Path.GetFullPath(outputPath);
            string? outDir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            File.WriteAllText(fullOut, log.ToString(), Encoding.UTF8);
            window.Close();
        }

        private static string ResolveProjectDirectory()
        {
            string cur = Environment.CurrentDirectory;
            if (Directory.Exists(cur))
                return cur;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo? dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LeafTab.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return baseDir;
        }

        private static async Task<bool> TestNavigateFolderAsync(MainWindow window, string address, string description, StringBuilder log)
        {
            var sw = Stopwatch.StartNew();
            bool ok = await window.NavigateAddressInputAsync(address);
            if (ok && window.SelectedTab != null)
            {
                await WaitForDirectUiAsync(window.SelectedTab.BrowserHost, 2000);
                await Task.Delay(250);
                sw.Stop();
                List<string> items = window.SelectedTab.BrowserHost.GetDisplayedItemNames(15);
                log.AppendLine($"- [{description}] Input='{address}' -> OK ({sw.ElapsedMilliseconds}ms), Title='{window.SelectedTab.Title}', Count={items.Count}, Sample=[{string.Join(", ", items)}]");
                return true;
            }

            sw.Stop();
            log.AppendLine($"- [{description}] Input='{address}' -> FAILED/UNAVAILABLE ({sw.ElapsedMilliseconds}ms)");
            return false;
        }

        private static async Task ProbeDirectoryGesturesAsync(
            ExplorerBrowserHost host,
            FOLDERVIEWMODE mode,
            string modeLabel,
            StringBuilder log)
        {
            host.TrySetViewMode(mode);
            await Task.Delay(350);
            host.AttachDirectUiSubclassIfNeeded();

            IntPtr directUi = host.DirectUiHwnd;
            if (directUi == IntPtr.Zero)
            {
                log.AppendLine($"  - GestureProbe [{modeLabel}]: DirectUIHWND not found");
                return;
            }

            var elements = await Task.Run(() => InspectDirectUiElementsFromMta(directUi));
            log.AppendLine($"  - GestureProbe [{modeLabel}]: DirectUIHWND=0x{directUi.ToInt64():X}, DiscoveredElements={elements.Count}");

            int checkedCount = 0;
            foreach (var el in elements)
            {
                if (string.Equals(el.ClassName, "UIItem", StringComparison.Ordinal) &&
                    el.Bounds.Width > 8 && el.Bounds.Height > 8 &&
                    checkedCount < 4)
                {
                    checkedCount++;
                    POINT center = new POINT((el.Bounds.Left + el.Bounds.Right) / 2, (el.Bounds.Top + el.Bounds.Bottom) / 2);

                    UiaHitResult midHit = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.MiddleClickFolder, directUi, center));

                    IntPtr resolvedPidl = host.TryResolveUnambiguousFolderPidl(midHit);
                    string resolvedInfo = "REJECTED (Non-folder or ambiguous)";
                    if (resolvedPidl != IntPtr.Zero)
                    {
                        FolderDisplayInfo info = PidlManager.GetDisplayInfo(resolvedPidl);
                        resolvedInfo = $"FOLDER_RESOLVED='{info.AddressText}'";
                        PidlManager.Free(ref resolvedPidl);
                    }

                    UiaHitResult dblHitOnItem = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.DoubleClickBlank, directUi, center));

                    log.AppendLine($"    * Item '{el.Name}' (AutoId='{el.AutoId}'): MiddleClick -> {midHit.Outcome} ({midHit.ElapsedMs:F2}ms, {resolvedInfo}); DoubleClickOnItem -> {dblHitOnItem.Outcome} (Leaf='{dblHitOnItem.RawLeafClassName}')");
                }
            }

            if (NativeMethods.GetWindowRect(directUi, out RECT viewRect) && viewRect.Width > 120 && viewRect.Height > 120)
            {
                POINT blankPt = new POINT(viewRect.Left + 160, viewRect.Bottom - 30);
                UiaHitResult blankHit = await Task.Run(() =>
                    UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.DoubleClickBlank, directUi, blankPt));
                log.AppendLine($"    * BlankArea ({blankPt.X},{blankPt.Y}): DoubleClick -> {blankHit.Outcome} ({blankHit.ElapsedMs:F2}ms, Leaf='{blankHit.RawLeafClassName}')");
            }
        }

        private static async Task ProbeThisPcGesturesAsync(ExplorerBrowserHost host, StringBuilder log)
        {
            IntPtr directUi = host.DirectUiHwnd;
            if (directUi == IntPtr.Zero) return;

            var elements = await Task.Run(() => InspectDirectUiElementsFromMta(directUi));
            foreach (var el in elements)
            {
                if (string.Equals(el.ClassName, "UIGroupItem", StringComparison.Ordinal))
                {
                    POINT headerPt = new POINT(el.Bounds.Left + 45, el.Bounds.Top + 12);
                    UiaHitResult groupHdrDbl = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.DoubleClickBlank, directUi, headerPt));
                    log.AppendLine($"  - [此电脑 GroupHeader '{el.Name}'] DoubleClick -> {groupHdrDbl.Outcome} (Leaf='{groupHdrDbl.RawLeafClassName}')");
                    break;
                }
            }

            foreach (var el in elements)
            {
                if (string.Equals(el.ClassName, "UIItem", StringComparison.Ordinal) && el.Bounds.Width > 10 && el.Bounds.Height > 10)
                {
                    POINT center = new POINT((el.Bounds.Left + el.Bounds.Right) / 2, (el.Bounds.Top + el.Bounds.Bottom) / 2);
                    UiaHitResult midHit = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.MiddleClickFolder, directUi, center));
                    IntPtr resolvedPidl = host.TryResolveUnambiguousFolderPidl(midHit);
                    bool resolved = resolvedPidl != IntPtr.Zero;
                    string targetAddr = resolved ? PidlManager.GetDisplayInfo(resolvedPidl).AddressText : "NONE";
                    PidlManager.Free(ref resolvedPidl);

                    log.AppendLine($"  - [此电脑 Item '{el.Name}'] MiddleClick -> {midHit.Outcome} ({midHit.ElapsedMs:F2}ms), Resolved={resolved} ('{targetAddr}')");
                }
            }
        }

        private static List<(string ClassName, string Name, string AutoId, RECT Bounds)> InspectDirectUiElementsFromMta(IntPtr directUiHwnd)
        {
            var results = new List<(string, string, string, RECT)>();
            Type? t8 = Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"), false);
            if (t8 == null || Activator.CreateInstance(t8) is not IUIAutomation uia)
                return results;

            try
            {
                if (uia.ElementFromHandle(directUiHwnd, out IUIAutomationElement? root) != ShellConstants.S_OK || root == null)
                    return results;

                try
                {
                    uia.get_RawViewWalker(out IUIAutomationTreeWalker? walker);
                    if (walker == null) return results;
                    try
                    {
                        WalkElements(walker, root, 0, results);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(walker);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(root);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(uia);
            }

            return results;
        }

        private static void WalkElements(
            IUIAutomationTreeWalker walker,
            IUIAutomationElement parent,
            int depth,
            List<(string, string, string, RECT)> sink)
        {
            if (depth > 5 || sink.Count > 50) return;

            if (walker.GetFirstChildElement(parent, out IUIAutomationElement? child) != ShellConstants.S_OK || child == null)
                return;

            IUIAutomationElement? current = child;
            while (current != null && sink.Count <= 40)
            {
                current.get_CurrentClassName(out string? cls);
                current.get_CurrentName(out string? name);
                current.get_CurrentAutomationId(out string? autoId);
                current.get_CurrentBoundingRectangle(out RECT bounds);

                if (string.Equals(cls, "UIItem", StringComparison.Ordinal) ||
                    string.Equals(cls, "UIGroupItem", StringComparison.Ordinal))
                {
                    sink.Add((cls ?? string.Empty, name ?? string.Empty, autoId ?? string.Empty, bounds));
                }

                WalkElements(walker, current, depth + 1, sink);

                int hr = walker.GetNextSiblingElement(current, out IUIAutomationElement? next);
                Marshal.ReleaseComObject(current);
                current = hr == ShellConstants.S_OK ? next : null;
            }
        }

        private static async Task WaitForDirectUiAsync(ExplorerBrowserHost host, int maxWaitMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                host.AttachDirectUiSubclassIfNeeded();
                if (host.DirectUiHwnd != IntPtr.Zero && host.GetItemCounts().TotalItems >= 0)
                {
                    await Task.Delay(120);
                    host.AttachDirectUiSubclassIfNeeded();
                    if (host.DirectUiHwnd != IntPtr.Zero)
                        return;
                }
                await Task.Delay(80);
            }
        }

        private static void ForceGcAndPump()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Application.Current?.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        private static ResourceSnapshot CaptureSnapshot(string label, int tabCount)
        {
            using Process proc = Process.GetCurrentProcess();
            proc.Refresh();
            uint gdi = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_GDIOBJECTS);
            uint user = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_USEROBJECTS);

            return new ResourceSnapshot
            {
                Label = label,
                TabCount = tabCount,
                WorkingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0),
                PrivateBytesMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0),
                GdiObjects = gdi,
                UserObjects = user
            };
        }
    }
}
