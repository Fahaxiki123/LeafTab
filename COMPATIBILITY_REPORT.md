# LeafTab 0.6.1 验收收尾验证（2026-11-14）

- 对照 Gemini 提出的 6 项问题核实并修复：安装接管遗漏便携实例、标签展开时新建按钮跳位、菜单勾选未区分程序路径、右键菜单异常阻断默认关联自愈、注销关联未退出后台实例、标签下拉图标及自检临时目录残留。
- 安装接管请求当前会话内各 LeafTab 副本正常退出，分别调用各副本自身的 `--quit`，兼容旧版命名方式；未勾选默认接管时仍只处理目标安装路径。未增加强杀。
- `--unregister-shell` 恢复成功后发送退出请求，先查询命名管道接收进程并核对完整路径，保留其他安装副本。
- `scripts/verify.ps1` 完整通过；.NET SDK Release/x64 构建 **0 警告、0 错误**，主程序和安装器均成功生成。
- **33 个独立回归用例全部通过**，新增 6 个用例覆盖路径归属和取消勾选、改动命令保护、自愈分支独立性、标签展开进度与窄宽往返布局、真实管道接收进程路径校验、隔离进程的安装接管退出。
- 原生自检全部断言通过：1,000 个文件全选，记录耗时 **380 ms**；本次临时目录已清理；30 个额外标签溢出时末尾激活标签完整可见，最小宽度 72 DIP。
- 隔离窗口界面检查通过：单标签隐藏下拉按钮、双标签显示矢量箭头、标签列表与当前选中项正常、关闭到单标签后再次隐藏。验证窗口已退出。
- 当前交付主程序 SHA-256：`D7A6FF3C3C4914968988F1AA46D030D1462A6EF0C692278E1D66C2848214CA30`。
- 当前安装包 SHA-256：`B7E29C157D4683AA5CD066881DF36293497DBA28AF07BAB0D9FCCB4E24BD897C`。
- 本轮日志位于 `artifacts/verification/review-fixes-20261114`；更新后的可分发文件位于 `artifacts/latest`，更新前交付文件保留在本轮证据目录的 `previous-package`。

本轮关联测试使用注册表沙盒；安装器退出测试使用独立模拟进程，没有执行真实默认关联的安装/升级/卸载或旧版 COM 接管实测。当前运行的安装版保留原样；本轮未重跑 Visual Studio MSBuild 和 Windows 10 实机测试。

---

# LeafTab 0.6.1 首轮修复验证（2026-11-14，收尾补丁前记录）

- 本机：Windows 11 x64，系统构建 10.0.28000.0，.NET Framework 4.8。
- `scripts/verify.ps1` 在项目外复制源码、构建并执行测试；源码目录没有构建输出。
- .NET SDK `dotnet build` 与 Visual Studio MSBuild 的 Release/x64 构建均成功。最终构建为 0 警告、0 错误。
- **27 个独立回归用例全部通过**：关联归属与旧副本保护、缺失主程序恢复、改动过的命令和 COM 服务保护、升级选项、迁移冲突和备份、多窗口书签同步、补全、标签最小宽度、IPC/PIDL/ACL/超时、安装辅助进程错误与超时、释放内容与本次构建哈希一致、替换失败保留旧 EXE。
- 原生 `--self-test` 全部断言通过，补齐导航、面包屑、历史、搜索、恢复标签断言，并增加了原生全选与窄窗口标签边界检查。
- 本次原生全选测试：1,000 个测试文件全部选中，记录耗时 **82 ms**；仅代表本机该次运行，不承诺其他设备相同耗时。
- 窄窗口原生检查：额外打开 30 个标签，激活末尾标签后仍完整位于滚动视口内，标签宽度 72 DIP，裁剪开启。
- 人工界面验收：隔离窗口 20 标签、窄窗列表切换与自动滚入视图、最大化后 Alt+Space 与标题栏右键菜单定位通过。验证窗口已关闭，原安装版保留运行。
- .NET SDK 与 Visual Studio 构建得到的主程序 SHA-256 一致：`0D2FD2847F98B8432C53107910BE153598A82B6090FD1AB33C4870490C924E6E`。
- 验证原始记录保存在本次交付的 `artifacts/verification`；可分发文件与校验值在 `artifacts/latest`。

尚未执行：Windows 10 实机、多台显示器不同 DPI 同时使用、真实默认文件夹关联的安装/升级/卸载、长时间 COM 资源压力测试。关联变更仅在注册表沙盒中验证；短时资源数据不作为“完全无泄漏”的结论。

---

以下保留 0.6.0 的历史验证记录，供对照。

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
