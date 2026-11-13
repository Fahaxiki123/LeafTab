# LeafTab Stage 1-3 + Liquid Glass (v0.6.0) Automated Diagnostic Telemetry
- Timestamp: 2026-11-13 18:30:22
- OSVersion: Microsoft Windows NT 10.0.26200.0 (64BitOS=True, 64BitProc=True)
- CLR Version: 4.0.30319.42000
- Executable: D:\LeafTab\src\LeafTab\bin\x64\Release\net48\LeafTab.exe (321.5 KB)
- ResolvedProjectDir: D:\LeafTab

## 1. Folder & Namespace Navigation Checks
- [此电脑] Title='此电脑', ItemCount=3, Items=[WPS云盘, 系统 (C:), 新加卷 (D:)]
  - [此电脑 GroupHeader '设备和驱动器'] DoubleClick -> IgnoredOrAmbiguous (Leaf='UIProperty')
  - [此电脑 Item 'WPS云盘'] MiddleClick -> HitItem (3.01ms), Resolved=True ('D:\WPS\WPS Cloud\WPSDrive\603841493\WPS云盘')
  - [此电脑 Item '系统 (C:)'] MiddleClick -> HitItem (3.92ms), Resolved=True ('C:\')
  - [此电脑 Item '新加卷 (D:)'] MiddleClick -> HitItem (2.68ms), Resolved=True ('D:\')
- [当前项目目录 D:\LeafTab] Input='D:\LeafTab' -> OK (411ms), Title='LeafTab', Count=6, Sample=[src, .gitignore, COMPATIBILITY_REPORT, DESIGN, LeafTab.sln, README]
  - GestureProbe [详细信息视图 (FVM_DETAILS)]: DirectUIHWND=0xC21246, DiscoveredElements=6
    * Item 'src' (AutoId='0'): MiddleClick -> HitItem (3.00ms, FOLDER_RESOLVED='D:\LeafTab\src'); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIProperty')
    * Item '.gitignore' (AutoId='1'): MiddleClick -> HitItem (2.29ms, REJECTED (Non-folder or ambiguous)); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIProperty')
    * Item 'COMPATIBILITY_REPORT' (AutoId='2'): MiddleClick -> HitItem (2.78ms, REJECTED (Non-folder or ambiguous)); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIProperty')
    * Item 'DESIGN' (AutoId='3'): MiddleClick -> HitItem (2.25ms, REJECTED (Non-folder or ambiguous)); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIProperty')
    * BlankArea (727,828): DoubleClick -> HitBlankView (1.97ms, Leaf='UIItemsView')
  - GestureProbe [图标视图 (FVM_ICON)]: DirectUIHWND=0xC21246, DiscoveredElements=6
    * Item 'src' (AutoId='0'): MiddleClick -> HitItem (3.67ms, FOLDER_RESOLVED='D:\LeafTab\src'); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIItem')
    * Item '.gitignore' (AutoId='1'): MiddleClick -> HitItem (2.17ms, REJECTED (Non-folder or ambiguous)); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIItem')
    * Item 'COMPATIBILITY_REPORT' (AutoId='2'): MiddleClick -> HitItem (2.66ms, REJECTED (Non-folder or ambiguous)); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIProperty')
    * Item 'DESIGN' (AutoId='3'): MiddleClick -> HitItem (2.93ms, REJECTED (Non-folder or ambiguous)); DoubleClickOnItem -> IgnoredOrAmbiguous (Leaf='UIItem')
    * BlankArea (727,828): DoubleClick -> HitBlankView (1.63ms, Leaf='UIItemsView')

## 2. Stage 2 Feature Checks (Breadcrumbs, History Stack, Async Search, Reopen Tab, config.json)
- BreadcrumbChain: [此电脑 > 新加卷 (D:) > LeafTab] (Count=3)
- HistoryStack Back/Forward: CanBackBefore=True, CanFwdAfterBack=True, RestoredTitle='LeafTab'
- AsyncSearch '*.cs': MatchedCount=46, Elapsed=230ms, EnteredSearchMode=True, ExitedCleanly=True, Sample=[App.xaml.cs, AdaptiveTabStripPanel.cs, ExplorerBrowserHost.cs, SelfTestRunner.cs, NativeMethods.cs]
- ReopenClosedTab (Ctrl+Shift+T): Closed='下载', Reopened=True, ActiveTitle='下载'
- PrewarmedThisPcTabPool: Armed=True, SwitchLatency=27.10ms, DirectUiImmediate=True, ConsumedCleanly=True
- PrewarmedReopenHistoryRetention: Reopened=True, NavigatedProject=True, CanNavigateBack=True
- ConfigJsonAtomicAndBackupRecovery: SavedV1=True, SavedV2=True, LoadedV2Bar=True, BakRecovered=True

## 2B. Stage 3 & v0.6.0 Checks (CLI Parser, Multi-Window, Side Buttons, Smart Reuse, Address Autocomplete, Volatile Guard, HKCU Sandbox)
- LaunchRequestParserChecks: Passed=True (QuoteFix='D:\', CommaSelect='D:\Work, 2026' -> 'report.pdf', ExplorerLauncherRedirect='此电脑', Flags=True)
- ExplorerLauncherClsidRedirect: Passed=True
- DirectUiFocusSuppression: DirectUIHWND=0x1C12DC, FocusHwnd=0x2710A0, Selected=0, UIState=0x3, HideFocusActive=True, ParkedAwayFromDirectUI=True
- MouseSideButtons (XBUTTON1/XBUTTON2): Back=True, Forward=True, RestoredTitle='LeafTab' (Matched=True)
- MultiWindowSplitAndClose: Duplicated=True, SecondWindowOpenedWithHistory=True, SecondaryClosedCleanly=True
- ExternalLaunchAndSmartReuse: NewTabForUnopen=True, ReusedDuplicateFolder=True, ReusedAndSelectedSln=True
- AddressAutocompleteAndVolatileGuard: ToolCmds=True, AutocompleteMatchedSrc=True, VolatileGuard=True
- MinimizedStatusPollingThrottle: PausedWhenMinimized=True
- ShellIntegrationSandbox: Enabled=True, SelfHealed=True, FolderOpenAndNewWinUpgraded=True, VolatileRefusedClobber=True, OwnershipProtected=True, RestoredPriorVerb='CustomPriorVerb' (CleanRollback=True)
- [虚拟目录：回收站] Input='回收站' -> OK (400ms), Title='回收站', Count=9, Sample=[D:\project\trunk\Client\assets\resources\ConfigForClient.zip.r44, D:\project\trunk\Config\sharedata\excel\D-单位属性表(UnitAttrData), D:\project\trunk\Client\assets\script\Config\Typings\HeroData.ts.mine, D:\project\trunk\Client\assets\script\Config\Typings\HeroData.ts.r44, C:\Users\Administrator\Desktop\Screen, D:\project\trunk\Client\assets\script\Config\Typings\SoldierData.ts.mine, D:\project\trunk\Client\assets\script\Config\Typings\SoldierData.ts.r44, D:\project\trunk\Config\sharedata\excel\Z-战斗属性表(FightAttrData), C:\Users\Administrator\Desktop\迅雷]
- [WSL 命名空间 (\\wsl.localhost)] Input='\\wsl.localhost' -> OK (393ms), Title='wsl.localhost', Count=0, Sample=[]

## 3. Unreachable Network Path Async Pre-Check
- UNC \\192.0.2.1\nonexistent_share: NavSucceeded=False, ElapsedMs=2521ms, UiDispatcherHeartbeatsDuringWait=22

## 4. Multi-Tab Resource Benchmark (1 / 10 / 20 Tabs)
- [1 个标签 (此电脑)] Tabs=1, WorkingSet=165.6 MB, PrivateBytes=111.9 MB, GDI=204, USER=204
- [10 个标签 (多磁盘与系统目录混合)] Tabs=10, WorkingSet=177.3 MB, PrivateBytes=124.6 MB, GDI=529, USER=596
- [20 个标签 (满载保活状态)] Tabs=20, WorkingSet=191.1 MB, PrivateBytes=139.7 MB, GDI=909, USER=1033
- [从 20 标签关闭回落至 2 标签 (压测起点)] Tabs=2, WorkingSet=186.4 MB, PrivateBytes=135.0 MB, GDI=305, USER=370
- [反复开关第 10/50 次后 (保持 2 标签)] Tabs=2, WorkingSet=188.8 MB, PrivateBytes=137.6 MB, GDI=243, USER=372
- [反复开关第 20/50 次后 (保持 2 标签)] Tabs=2, WorkingSet=189.2 MB, PrivateBytes=137.8 MB, GDI=243, USER=372
- [反复开关第 30/50 次后 (保持 2 标签)] Tabs=2, WorkingSet=189.4 MB, PrivateBytes=138.1 MB, GDI=243, USER=372
- [反复开关第 40/50 次后 (保持 2 标签)] Tabs=2, WorkingSet=189.7 MB, PrivateBytes=138.3 MB, GDI=243, USER=372
- [反复开关第 50/50 次后 (保持 2 标签)] Tabs=2, WorkingSet=189.8 MB, PrivateBytes=138.8 MB, GDI=243, USER=372
- [50 次开关静置收敛后 (保持 2 标签)] Tabs=2, WorkingSet=189.8 MB, PrivateBytes=138.8 MB, GDI=243, USER=370

## RESULT: PASS (All Automated Assertions Succeeded)
