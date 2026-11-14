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
using LeafTab.Shared;
using System.Windows.Controls;
using LeafTab.Views;
using Microsoft.Win32;

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
            DiagnosticLog.DirectoryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "self-test-logs");
            var log = new StringBuilder();
            var failures = new List<string>();

            void AssertCheck(bool condition, string name)
            {
                if (!condition)
                    failures.Add(name);
            }

            try
            {
                await Task.Delay(900);

                string exePath = Assembly.GetExecutingAssembly().Location;
                long exeBytes = File.Exists(exePath) ? new FileInfo(exePath).Length : 0;
                string projectDir = ResolveProjectDirectory();

                log.AppendLine("# LeafTab v0.6.1 Native Regression Diagnostics");
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
                AssertCheck(projNavOk && window.SelectedTab != null, "ProjectNavigation");
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
                    AssertCheck(crumbs.Count > 0, "BreadcrumbChain");

                    bool canBackBefore = window.SelectedTab.CanNavigateBack;
                    window.SelectedTab.NavigateHistoryStep(-1);
                    await Task.Delay(350);
                    bool canFwdAfterBack = window.SelectedTab.CanNavigateForward;
                    window.SelectedTab.NavigateHistoryStep(+1);
                    await Task.Delay(350);
                    log.AppendLine($"- HistoryStack Back/Forward: CanBackBefore={canBackBefore}, CanFwdAfterBack={canFwdAfterBack}, RestoredTitle='{window.SelectedTab.Title}'");
                    AssertCheck(canBackBefore && canFwdAfterBack, "HistoryBackForward");

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
                    AssertCheck(inSearch && !window.SelectedTab.BrowserHost.IsShowingSearchResults && (!Directory.Exists(Path.Combine(projectDir, "src", "LeafTab")) || foundCs > 0), "AsyncSearchResults");

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
                        AssertCheck(reopened && window.SelectedTab?.Title == closedTitle, "ReopenClosedTab");
                    }
                    finally
                    {
                        PidlManager.Free(ref dlPidl);
                    }

                    // Pre-warmed background '此电脑' tab pool instant switch + reopen history retention check
                    window.EnsurePrewarmedThisPcHostReady();
                    await Task.Delay(350);
                    bool poolArmed = window.HasPrewarmedHost;
                    var prewarmSw = Stopwatch.StartNew();
                    ExplorerTabItem instantTab = window.OpenNewThisPcTab();
                    prewarmSw.Stop();
                    bool directUiImmediate = instantTab.BrowserHost.DirectUiHwnd != IntPtr.Zero;
                    log.AppendLine($"- PrewarmedThisPcTabPool: Armed={poolArmed}, SwitchLatency={prewarmSw.Elapsed.TotalMilliseconds:F2}ms, DirectUiImmediate={directUiImmediate}, ConsumedCleanly={!window.HasPrewarmedHost}");
                    AssertCheck(poolArmed && directUiImmediate && !window.HasPrewarmedHost, "PrewarmedThisPcTabPool");

                    window.EnsurePrewarmedThisPcHostReady();
                    await Task.Delay(300);
                    window.CloseTab(instantTab);
                    bool reopenedThisPc = window.ReopenLastClosedTab();
                    await Task.Delay(200);
                    bool navFromReopened = await window.NavigateAddressInputAsync(projectDir);
                    await Task.Delay(350);
                    bool canBackAfterPrewarmReopen = window.SelectedTab?.CanNavigateBack ?? false;
                    log.AppendLine($"- PrewarmedReopenHistoryRetention: Reopened={reopenedThisPc}, NavigatedProject={navFromReopened}, CanNavigateBack={canBackAfterPrewarmReopen}");
                    AssertCheck(reopenedThisPc && navFromReopened && canBackAfterPrewarmReopen, "PrewarmedReopenHistoryRetention");
                    if (window.SelectedTab != null && window.Tabs.Count > 1)
                    {
                        window.CloseTab(window.SelectedTab);
                    }
                    window.DiscardPrewarmedHost();
                }

                // config.json atomic replace + .bak corruption recovery check
                string tempCfgPath = Path.Combine(Path.GetTempPath(), $"leaftab_selftest_{Guid.NewGuid():N}.json");
                try
                {
                    var v1Cfg = new AppConfig
                    {
                        ShowBookmarkBar = false,
                        ShowPreviewPane = false,
                        SelectedTabIndex = 0
                    };
                    v1Cfg.Bookmarks.Add(new BookmarkConfigItem { Title = "LeafTab_V1", TargetPath = projectDir });
                    v1Cfg.OpenTabs.Add(new SavedTabConfigItem
                    {
                        CurrentPath = projectDir,
                        HistoryPaths = new List<string> { "此电脑", projectDir },
                        HistoryIndex = 1
                    });
                    bool savedV1 = AppConfigService.Save(v1Cfg, tempCfgPath);

                    var v2Cfg = new AppConfig
                    {
                        ShowBookmarkBar = true,
                        ShowPreviewPane = true,
                        SelectedTabIndex = 0
                    };
                    v2Cfg.Bookmarks.Add(new BookmarkConfigItem { Title = "LeafTab_V2", TargetPath = projectDir });
                    bool savedV2 = AppConfigService.Save(v2Cfg, tempCfgPath);
                    AppConfig loadedV2 = AppConfigService.Load(tempCfgPath);

                    // Corrupt primary config.json and verify automatic fallback to config.json.bak (which holds v1Cfg)
                    File.WriteAllText(tempCfgPath, "{ corrupted_json_payload", Encoding.UTF8);
                    AppConfig recoveredFromBak = AppConfigService.Load(tempCfgPath);
                    bool bakRecoveredOk = recoveredFromBak.Bookmarks.Count == 1 &&
                                          recoveredFromBak.Bookmarks[0].Title == "LeafTab_V1" &&
                                          recoveredFromBak.OpenTabs.Count == 1;

                    log.AppendLine($"- ConfigJsonAtomicAndBackupRecovery: SavedV1={savedV1}, SavedV2={savedV2}, LoadedV2Bar={loadedV2.ShowBookmarkBar}, BakRecovered={bakRecoveredOk}");
                    AssertCheck(savedV1 && savedV2 && loadedV2.ShowBookmarkBar && bakRecoveredOk, "ConfigJsonAtomicAndBackupRecovery");
                }
                finally
                {
                    if (File.Exists(tempCfgPath)) File.Delete(tempCfgPath);
                    if (File.Exists(tempCfgPath + ".bak")) File.Delete(tempCfgPath + ".bak");
                    if (File.Exists(tempCfgPath + ".tmp")) File.Delete(tempCfgPath + ".tmp");
                }

                // ==================== 2B. STAGE 3 + v0.6.0 SYSTEM INTEGRATION & UX CHECKS ====================
                log.AppendLine();
                log.AppendLine("## 2B. Stage 3 & v0.6.0 Checks (CLI Parser, Multi-Window, Side Buttons, Smart Reuse, Address Autocomplete, Volatile Guard, HKCU Sandbox)");

                LaunchActionRequest pQuote = LaunchRequestParser.Parse(new[] { "D:\"", "C:\\ " });
                LaunchActionRequest pSelect = LaunchRequestParser.Parse(new[] { "/n,/select,D:\\Work, 2026\\report.pdf" });
                LaunchActionRequest pExplorerClsid = LaunchRequestParser.Parse(new[] { "::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", @"C:\Users\Public\File Explorer.lnk" });
                LaunchActionRequest pBgQuit = LaunchRequestParser.Parse(new[] { "--background", "--quit", "--new-window", "--handoff" });
                string slnFile = Path.Combine(projectDir, "LeafTab.sln");
                string csprojFile = Path.Combine(projectDir, "src", "LeafTab", "LeafTab.csproj");
                LaunchActionRequest pDirectFile = File.Exists(slnFile)
                    ? LaunchRequestParser.Parse(new[] { slnFile })
                    : LaunchRequestParser.Parse(new[] { $"/select,{slnFile}" });

                bool parserOk = pQuote.Targets.Count == 2 &&
                                pQuote.Targets[0].FolderOrAlias == "D:\\" &&
                                pQuote.Targets[1].FolderOrAlias == "C:\\" &&
                                pSelect.Targets.Count == 1 &&
                                pSelect.Targets[0].FolderOrAlias == "D:\\Work, 2026" &&
                                pSelect.Targets[0].SelectItemName == "report.pdf" &&
                                pDirectFile.Targets.Count == 1 &&
                                pDirectFile.Targets[0].SelectItemName == "LeafTab.sln" &&
                                pExplorerClsid.Targets.Count == 2 &&
                                pExplorerClsid.Targets[0].FolderOrAlias == "此电脑" &&
                                pExplorerClsid.Targets[1].FolderOrAlias == "此电脑" &&
                                pBgQuit.IsBackgroundWarmup &&
                                pBgQuit.IsQuitProcess &&
                                pBgQuit.OpenInNewWindow &&
                                pBgQuit.WaitMutexHandoff;
                log.AppendLine($"- LaunchRequestParserChecks: Passed={parserOk} (QuoteFix='{pQuote.Targets[0].FolderOrAlias}', CommaSelect='{pSelect.Targets[0].FolderOrAlias}' -> '{pSelect.Targets[0].SelectItemName}', ExplorerLauncherRedirect='{pExplorerClsid.Targets[0].FolderOrAlias}', Flags={pBgQuit.IsBackgroundWarmup && pBgQuit.IsQuitProcess && pBgQuit.OpenInNewWindow && pBgQuit.WaitMutexHandoff})");
                AssertCheck(parserOk, "LaunchRequestParserChecks");

                // Verify File Explorer launcher stub CLSID & display alias normalize to This PC PIDL
                IntPtr expectedThisPc = PidlManager.CreateThisPcPidl();
                (IntPtr parsedClsidPidl, _) = await PidlManager.TryParseAddressAsync("::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", CancellationToken.None);
                (IntPtr parsedLabelPidl, _) = await PidlManager.TryParseAddressAsync("文件资源管理器", CancellationToken.None);
                try
                {
                    bool launcherRedirectOk = PidlManager.AreEqual(parsedClsidPidl, expectedThisPc) &&
                                              PidlManager.AreEqual(parsedLabelPidl, expectedThisPc);
                    log.AppendLine($"- ExplorerLauncherClsidRedirect: Passed={launcherRedirectOk}");
                    AssertCheck(launcherRedirectOk, "ExplorerLauncherClsidRedirect");
                }
                finally
                {
                    PidlManager.Free(ref expectedThisPc);
                    PidlManager.Free(ref parsedClsidPidl);
                    PidlManager.Free(ref parsedLabelPidl);
                }

                // Verify DirectUIHWND UISF_HIDEFOCUS + focus-parking on _hostHwnd when 0 items are selected (eliminates 1px black item box)
                if (window.SelectedTab != null)
                {
                    await WaitForDirectUiAsync(window.SelectedTab.BrowserHost, 1500);
                    window.SelectedTab.BrowserHost.FocusNativeView();
                    IntPtr dui = window.SelectedTab.BrowserHost.DirectUiHwnd;
                    long uiState = dui != IntPtr.Zero
                        ? NativeMethods.SendMessageW(dui, NativeMethods.WM_QUERYUISTATE, IntPtr.Zero, IntPtr.Zero).ToInt64()
                        : 0;
                    bool hideFocusSet = (uiState & NativeMethods.UISF_HIDEFOCUS) != 0;
                    int selCount = window.SelectedTab.BrowserHost.GetItemCounts().SelectedItems;
                    IntPtr curFocus = NativeMethods.GetFocus();
                    bool parkedAwayFromDirectUi = selCount > 0 || (curFocus != dui);
                    log.AppendLine($"- DirectUiFocusSuppression: DirectUIHWND=0x{dui.ToInt64():X}, FocusHwnd=0x{curFocus.ToInt64():X}, Selected={selCount}, UIState=0x{uiState:X}, HideFocusActive={hideFocusSet}, ParkedAwayFromDirectUI={parkedAwayFromDirectUi}");
                    AssertCheck(hideFocusSet && parkedAwayFromDirectUi, "DirectUiFocusSuppression");
                }

                // Activate Tab 0 (projectDir, which navigated from '此电脑' -> projectDir) to test Mouse Side Buttons & History-Preserving Tear-Out
                if (window.Tabs.Count > 0)
                {
                    window.ActivateTab(window.Tabs[0], focusNativeView: true);
                    await Task.Delay(150);
                }

                // Verify Mouse Side Buttons (XBUTTON1 = Back, XBUTTON2 = Forward)
                if (window.SelectedTab != null)
                {
                    string beforeSideBtnTitle = window.SelectedTab.Title;
                    bool sideBackOk = window.HandleMouseSideButton(NativeMethods.XBUTTON1);
                    await Task.Delay(300);
                    bool sideFwdOk = window.HandleMouseSideButton(NativeMethods.XBUTTON2);
                    await Task.Delay(300);
                    bool restoredAfterSideBtn = string.Equals(window.SelectedTab.Title, beforeSideBtnTitle, StringComparison.Ordinal);
                    log.AppendLine($"- MouseSideButtons (XBUTTON1/XBUTTON2): Back={sideBackOk}, Forward={sideFwdOk}, RestoredTitle='{window.SelectedTab.Title}' (Matched={restoredAfterSideBtn})");
                    AssertCheck(sideBackOk && sideFwdOk && restoredAfterSideBtn, "MouseSideButtons");
                }

                // Verify Tab Duplication + Tear-Out to Independent Secondary Window + Secondary Window Clean Close
                if (window.SelectedTab != null && App.CurrentApp != null)
                {
                    ExplorerTabItem? dupTab = window.DuplicateTab(window.SelectedTab);
                    await Task.Delay(200);
                    bool dupOk = dupTab != null && window.Tabs.Count >= 2;
                    MainWindow? secondWin = dupTab != null
                        ? await window.MoveTabToNewWindowAsync(dupTab)
                        : null;
                    await Task.Delay(250);
                    bool multiWinOpened = secondWin != null &&
                                          App.CurrentApp.ManagedWindows.Count == 2 &&
                                          secondWin.SelectedTab != null &&
                                          secondWin.SelectedTab.CanNavigateBack;
                    secondWin?.Close();
                    await Task.Delay(150);
                    bool secondaryDisposedCleanly = App.CurrentApp.ManagedWindows.Count == 1 && !window.IsClosingOrClosed;
                    window.BringWindowToFront();
                    log.AppendLine($"- MultiWindowSplitAndClose: Duplicated={dupOk}, SecondWindowOpenedWithHistory={multiWinOpened}, SecondaryClosedCleanly={secondaryDisposedCleanly}");
                    AssertCheck(dupOk && multiWinOpened && secondaryDisposedCleanly, "MultiWindowSplitAndClose");
                }

                // Verify External /select Launch + Smart Duplicate Folder Reuse
                if (File.Exists(csprojFile) && File.Exists(slnFile))
                {
                    int tabsBeforeExt = window.Tabs.Count;
                    // 1) Subfolder src\LeafTab is NOT currently open -> should open a new tab and select LeafTab.csproj
                    var extNewFolderReq = LaunchRequestParser.Parse(new[] { $"/select,{csprojFile}" });
                    await window.HandleExternalLaunchRequestAsync(extNewFolderReq, isInitialLaunch: false);
                    await Task.Delay(280);
                    List<string> selectedCsproj = window.SelectedTab?.BrowserHost.GetSelectedPaths() ?? new List<string>();
                    bool openedNewForUnopenFolder = window.Tabs.Count == tabsBeforeExt + 1 &&
                                                    selectedCsproj.Any(p => p.EndsWith("LeafTab.csproj", StringComparison.OrdinalIgnoreCase));

                    // 2) Re-launching the ALREADY-OPEN src\LeafTab folder -> should REUSE that tab without piling up duplicates!
                    int tabsBeforeReuse = window.Tabs.Count;
                    var extDuplicateFolderReq = LaunchRequestParser.Parse(new[] { Path.GetDirectoryName(csprojFile)! });
                    await window.HandleExternalLaunchRequestAsync(extDuplicateFolderReq, isInitialLaunch: false);
                    await Task.Delay(180);
                    bool reusedExistingTab = window.Tabs.Count == tabsBeforeReuse;

                    // 3) /select on LeafTab.sln whose parent (projectDir) is ALSO already open in tab 0 -> switches back & selects LeafTab.sln!
                    var extSelectExistingTabReq = LaunchRequestParser.Parse(new[] { $"/select,{slnFile}" });
                    await window.HandleExternalLaunchRequestAsync(extSelectExistingTabReq, isInitialLaunch: false);
                    await Task.Delay(250);
                    List<string> selectedSln = window.SelectedTab?.BrowserHost.GetSelectedPaths() ?? new List<string>();
                    bool reusedAndSelectedSln = window.Tabs.Count == tabsBeforeReuse &&
                                                selectedSln.Any(p => p.EndsWith("LeafTab.sln", StringComparison.OrdinalIgnoreCase));

                    log.AppendLine($"- ExternalLaunchAndSmartReuse: NewTabForUnopen={openedNewForUnopenFolder}, ReusedDuplicateFolder={reusedExistingTab}, ReusedAndSelectedSln={reusedAndSelectedSln}");
                    AssertCheck(openedNewForUnopenFolder && reusedExistingTab && reusedAndSelectedSln, "ExternalLaunchAndSmartReuse");

                    while (window.Tabs.Count > tabsBeforeExt)
                    {
                        window.CloseTab(window.Tabs[window.Tabs.Count - 1]);
                    }
                }

                // Verify Address Bar Tool Commands, Subdirectory Autocomplete & Volatile Folder Guard
                bool toolCmdWt = QuickLaunchAndInstallService.IsAddressBarToolCommand("wt", out string parsedTool, out _) && parsedTool == "wt";
                bool toolCmdCode = QuickLaunchAndInstallService.IsAddressBarToolCommand("code .", out string parsedCode, out _) && parsedCode == "code";
                bool toolCmdRejectPath = !QuickLaunchAndInstallService.IsAddressBarToolCommand(@"C:\wt", out _, out _);

                string acQuery = Path.Combine(projectDir, "sr");
                List<AddressSuggestionItem> acItems = QuickLaunchAndInstallService.BuildAddressSuggestions(
                    acQuery,
                    showDefaultHistory: false,
                    window.RecentFolders,
                    Array.Empty<string>());
                bool acMatchedSrc = acItems.Any(s => s.InsertText.EndsWith(@"\src", StringComparison.OrdinalIgnoreCase));

                string volatileTempExe = Path.Combine(Path.GetTempPath(), "LeafTab.exe");
                string fixedInstallExe = QuickLaunchAndInstallService.GetRecommendedInstallExePath();
                bool volatileGuardOk = QuickLaunchAndInstallService.IsVolatileLaunchLocation(volatileTempExe) &&
                                       !QuickLaunchAndInstallService.IsVolatileLaunchLocation(fixedInstallExe);

                log.AppendLine($"- AddressAutocompleteAndVolatileGuard: ToolCmds={toolCmdWt && toolCmdCode && toolCmdRejectPath}, AutocompleteMatchedSrc={acMatchedSrc}, VolatileGuard={volatileGuardOk}");
                AssertCheck(toolCmdWt && toolCmdCode && toolCmdRejectPath && acMatchedSrc && volatileGuardOk, "AddressAutocompleteAndVolatileGuard");

                // Verify Status Bar Timer Throttling when Minimized
                WindowState prevWinState = window.WindowState;
                window.WindowState = WindowState.Minimized;
                await Task.Delay(80);
                bool pausedWhenMinimized = !window.IsStatusPollingActive;
                window.WindowState = prevWinState == WindowState.Minimized ? WindowState.Normal : prevWinState;
                window.BringWindowToFront();
                await Task.Delay(80);
                log.AppendLine($"- MinimizedStatusPollingThrottle: PausedWhenMinimized={pausedWhenMinimized}");
                AssertCheck(pausedWhenMinimized, "MinimizedStatusPollingThrottle");

                // Isolated HKCU sandbox test (strictly NEVER touches real Software\Classes!)
                string sandboxBase = $@"Software\LeafTab\SelfTestSandbox_{Guid.NewGuid():N}";
                string sandboxClasses = $@"{sandboxBase}\Classes";
                string sandboxBackup = $@"{sandboxBase}\Backup";
                try
                {
                    using (RegistryKey priorKey = Registry.CurrentUser.CreateSubKey($@"{sandboxClasses}\Directory\shell"))
                    {
                        priorKey.SetValue(null, "CustomPriorVerb", RegistryValueKind.String);
                    }

                    var ctxOn = ShellIntegrationService.SetContextMenuEnabledCore(true, exePath, sandboxClasses, notifyShell: false);
                    var defOn = ShellIntegrationService.SetDefaultExplorerEnabledCore(true, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    bool enabledBoth = ctxOn.Ok && defOn.Ok &&
                                       ShellIntegrationService.IsContextMenuEnabledCore(sandboxClasses) &&
                                       ShellIntegrationService.IsDefaultExplorerEnabledCore(sandboxClasses);

                    // Simulate missing Folder\shell\open\command (schema upgrade) + moved exe and verify startup self-healing
                    Registry.CurrentUser.DeleteSubKeyTree($@"{sandboxClasses}\Folder", throwOnMissingSubKey: false);
                    using (RegistryKey? verbCmd = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell\{ShellIntegrationService.DefaultVerbKeyName}\command", writable: true))
                    {
                        verbCmd?.SetValue(null, "\"C:\\OldMovedFolder\\LeafTab.exe\" \"%V \"", RegistryValueKind.String);
                    }
                    bool selfHealed = ShellIntegrationService.RepairRegisteredPathsIfMovedCore(exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    bool folderOpenUpgraded;
                    using (RegistryKey? folderOpenCmd = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Folder\shell\open\command"))
                    using (RegistryKey? folderNewWinCmd = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Folder\shell\opennewwindow\command"))
                    {
                        folderOpenUpgraded =
                            string.Equals(
                                folderOpenCmd?.GetValue("DelegateExecute") as string,
                                ShellIntegrationService.LeafTabExecuteClsidString,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                folderNewWinCmd?.GetValue("DelegateExecute") as string,
                                ShellIntegrationService.LeafTabNewWindowExecuteClsidString,
                                StringComparison.OrdinalIgnoreCase);
                    }

                    // Verify volatile Temp copy refuses to overwrite an existing valid non-volatile registration
                    bool volatileRefusedClobber = !ShellIntegrationService.RepairRegisteredPathsIfMovedCore(
                        volatileTempExe,
                        sandboxClasses,
                        sandboxBackup,
                        notifyShell: false);

                    // Disable while owned by LeafTab and verify clean rollback back to "CustomPriorVerb" AND complete cleanup of Folder\shell
                    ShellIntegrationService.SetDefaultExplorerEnabledCore(false, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    ShellIntegrationService.SetContextMenuEnabledCore(false, exePath, sandboxClasses, notifyShell: false);

                    string? restoredPrior;
                    using (RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell"))
                    {
                        restoredPrior = dirShell?.GetValue(null) as string;
                    }
                    bool folderCleanedUp;
                    using (RegistryKey? folderShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Folder\shell"))
                    {
                        folderCleanedUp = folderShell == null;
                    }
                    bool cleanRollback = string.Equals(restoredPrior, "CustomPriorVerb", StringComparison.Ordinal) &&
                                         folderCleanedUp &&
                                         !ShellIntegrationService.IsContextMenuEnabledCore(sandboxClasses) &&
                                         !ShellIntegrationService.IsDefaultExplorerEnabledCore(sandboxClasses);

                    // Re-enable, then simulate a third-party file manager taking over (Default); verify ownership protection refuses to clobber it
                    ShellIntegrationService.SetDefaultExplorerEnabledCore(true, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    using (RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell", writable: true))
                    {
                        dirShell?.SetValue(null, "OtherFileManagerVerb", RegistryValueKind.String);
                    }
                    ShellIntegrationService.SetDefaultExplorerEnabledCore(false, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    string? afterProtectedUnreg;
                    using (RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell"))
                    {
                        afterProtectedUnreg = dirShell?.GetValue(null) as string;
                    }
                    bool ownershipProtected = string.Equals(afterProtectedUnreg, "OtherFileManagerVerb", StringComparison.Ordinal);

                    log.AppendLine($"- ShellIntegrationSandbox: Enabled={enabledBoth}, SelfHealed={selfHealed}, FolderOpenAndNewWinUpgraded={folderOpenUpgraded}, VolatileRefusedClobber={volatileRefusedClobber}, OwnershipProtected={ownershipProtected}, RestoredPriorVerb='{restoredPrior}' (CleanRollback={cleanRollback})");
                    AssertCheck(enabledBoth && selfHealed && folderOpenUpgraded && volatileRefusedClobber && ownershipProtected && cleanRollback, "ShellIntegrationSandbox");
                }
                finally
                {
                    Registry.CurrentUser.DeleteSubKeyTree(sandboxBase, throwOnMissingSubKey: false);
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

                // Dedicated native selection fixture: no user documents are modified.
                string selectionDir = Path.Combine(Path.GetTempPath(), "LeafTab-selection-" + Guid.NewGuid().ToString("N"));
                ExplorerTabItem? selectionTab = null;
                try
                {
                    Directory.CreateDirectory(selectionDir);
                    for (int i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(selectionDir, $"item-{i:D4}.txt"), string.Empty);
                    selectionTab = window.AddNewTab(IntPtr.Zero, activate: true, animateOpen: false);
                    bool selectionNavigation = await TestNavigateFolderAsync(window, selectionDir, "全选测试目录", log);
                    if (selectionNavigation && window.SelectedTab != null)
                    {
                        var host = window.SelectedTab.BrowserHost;
                        for (int i = 0; i < 50 && host.GetItemCounts().TotalItems != 1000; i++) await Task.Delay(100);
                        var timer = Stopwatch.StartNew();
                        host.SelectAllItems();
                        for (int i = 0; i < 100 && host.GetItemCounts().SelectedItems != 1000; i++) await Task.Delay(50);
                        timer.Stop();
                        bool allSelected = host.GetItemCounts().SelectedItems == 1000;
                        log.AppendLine($"- NativeSelectAll1000: Selected={host.GetItemCounts().SelectedItems}, ElapsedMs={timer.ElapsedMilliseconds}");
                        AssertCheck(allSelected, "NativeSelectAll1000");
                    }
                    else AssertCheck(false, "NativeSelectAllNavigation");
                }
                finally
                {
                    // Release the native folder view before deleting its fixture, including on failure.
                    try { if (selectionTab != null) window.CloseTab(selectionTab); }
                    finally
                    {
                        for (int attempt = 0; Directory.Exists(selectionDir); attempt++)
                        {
                            try { Directory.Delete(selectionDir, recursive: true); }
                            catch (IOException) when (attempt < 5) { await Task.Delay(100); }
                        }
                        log.AppendLine("- NativeSelectAllFixtureCleaned: " + !Directory.Exists(selectionDir));
                    }
                }

                double oldWidth = window.Width;
                window.Width = 720;
                var overflowTabs = new List<ExplorerTabItem>();
                IntPtr overflowPidl = PidlManager.CreateThisPcPidl();
                try
                {
                    for (int i = 0; i < 30; i++) overflowTabs.Add(window.AddNewTab(overflowPidl, false, false, deferHostMount: true));
                    window.ActivateTab(overflowTabs[overflowTabs.Count - 1]);
                    await Task.Delay(200);
                    window.UpdateLayout();
                    var scroll = (ScrollViewer)window.FindName("TabsScrollViewer");
                    var items = (ItemsControl)window.FindName("TabsItemsControl");
                    var last = (FrameworkElement)items.ItemContainerGenerator.ContainerFromItem(overflowTabs[overflowTabs.Count - 1]);
                    Point position = last.TranslatePoint(new Point(0, 0), scroll);
                    bool inside = position.X >= -1 && position.X + last.ActualWidth <= scroll.ActualWidth + 1;
                    AssertCheck(scroll.ScrollableWidth > 0 && scroll.ClipToBounds && inside, "NarrowWindowTabOverflow");
                    log.AppendLine($"- NarrowWindowTabOverflow: Inside={inside}, Viewport={scroll.ActualWidth:F1}, LastTabX={position.X:F1}, Width={last.ActualWidth:F1}");
                }
                finally
                {
                    PidlManager.Free(ref overflowPidl);
                    foreach (var tab in overflowTabs) window.CloseTab(tab);
                    window.Width = oldWidth;
                }

                log.AppendLine();
                if (failures.Count == 0)
                {
                    log.AppendLine("## RESULT: PASS (All Automated Assertions Succeeded)");
                }
                else
                {
                    log.AppendLine($"## RESULT: FAIL ({failures.Count} Assertion(s) Failed: {string.Join(", ", failures)})");
                    Environment.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
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
