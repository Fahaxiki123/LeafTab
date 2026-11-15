# LeafTab 0.6.5 资源管理器快捷入口回归修复验证（2026-11-15）

- 根因：0.6.4 将 `SetNoShowUI(true)` 与 `SW_HIDE` 一并视为丢弃打开命令的条件。真实任务栏 `File Explorer.lnk` 调用已进入 `Execute`，却记录 `shell-open-suppressed-silent` 后返回成功，导致窗口未打开。
- 修复：`SetNoShowUI` 继续保存并传给原生系统组件，但不再阻断正常文件夹打开；仅保留显式 `SW_HIDE` 的隐藏处理。后台 COM 启动隔离、空闲退出守卫、焦点保护、IPC 权限与系统关联配置保持现有逻辑。
- 回归验证：新增与修正的两项用例先在旧实现上失败；修复后 **71 项全部通过**。覆盖普通/新窗口打开、提示开关、显式隐藏、资源管理器启动 CLSID，以及已有的 COM 生命周期与焦点保护。
- 隔离 `Release|x64` 构建 **0 警告、0 错误**，原生自检 `RESULT: PASS`；最小化/后台输入焦点保持和主动打开恢复窗口断言均通过。
- 本机安装器升级退出码为 0，安装后主程序版本为 `0.6.5.0` 且 SHA-256 与验证构建一致。通过 Windows Shell 调用任务栏使用的原始 `File Explorer.lnk`，成功打开「此电脑」并记录 `shell-open-request-no-error-ui`；普通文件夹 `.lnk` 同样打开正确目录。用户随后实际点击任务栏按钮确认恢复正常。COM 探测保持隐藏并空闲退出，主窗口继续运行；书签、驻留和会话恢复偏好核对未变。
- 安装包 `artifacts/latest/LeafTab-v0.6.5-Setup-x64.exe`：338,432 字节，SHA-256 `400E5C42793291F46695D16AC86C1D94184425BFD99508552A41F128092D3F42`。主程序 SHA-256 `B04CE4D1B6DB435B63BC4E5F0094A3191BBEB14C0E941C0FF6657B647F76E520`。
- 构建、失败复现、回归、原生自检与安装后快捷方式日志位于 `artifacts/verification/v0.6.5-20261115`；旧发布包与本机安装/配置备份一并保留在该目录。
- 本轮未专项复测 Windows 10、混合 DPI 多屏及完整 UAC 权限组合；未通过自动化按键实测 Win+E。

---

# LeafTab 0.6.4 后台激活与焦点修复验证（2026-11-15）

- 修复前隔离复现：空目标 `--com-server` 经 IPC 解码后变为普通启动请求，最小化窗口恢复并新增标签；`SetNoShowUI(true)` 仍路由前台打开；延迟 `FocusNativeView` 会清除另一个 WPF 文本框的输入焦点。运行中安装版的命令行为 `--com-server -Embedding`，父进程为 `DcomLaunch`。
- 修复与复核收尾：
  1. **基础设施启动静默隔离与冷启动空闲退出保护**（`App.xaml.cs` / `SingleInstanceService.cs`）：`--com-server` / `-Embedding` / `--background` 保持隐藏启动，不再编码为 `ACTIVATE_NEW_TAB`；存在主实例时注册隐藏 COM 转发服务，仅转发实际 `Execute` 请求；补充 `_hasHandledExternalOpen` 与 `ShouldExitOnComIdle` 守卫，并将主实例空闲计时器置于 `EnsureHandle` 早期缓冲请求消费之后启动，确保任何存在可见窗口或已处理过实际打开请求（含后续进入后台热驻留）的主实例绝不被 30 秒空闲计时器误关。
  2. **跨完整性命名管道转发与本地回退**（`NativeMethods.cs` / `SingleInstanceService.cs` / `App.xaml.cs`）：命名管道服务端在仅授权当前用户 SID 的 DACL 基础上附加 `S:(ML;;NW;;;ME)` Medium 完整性标签，进程路径校验改用 `PROCESS_QUERY_LIMITED_INFORMATION`（`QueryFullProcessImageNameW`），并在隐藏 COM 转发超时/失败时自动降级本地打开窗口，避免标准 UAC 下普通权限 `explorer.exe` 向提权主实例转发失败导致打开文件夹无响应。
  3. **静默 Shell 请求拦截、原生焦点守卫与最小前台授权**（`ExplorerVerbComServer.cs` / `ExplorerBrowserHost.cs` / `MainWindow.xaml.cs` / `MainWindow.AddressAndSearch.cs`）：尊重 `SetNoShowUI(true)` 与 `SW_HIDE`；原生焦点操作统一核对可见性、非最小化、窗口激活与全局前台 HWND；IPC 仅对实际接收进程 PID 授予前台唤醒权限。
- `scripts/verify.ps1` 项目外副本隔离 `Release|x64` 构建 **0 警告、0 错误**，**70 项回归测试全部通过**（`RESULT: 70 passed, 0 failed`，含冷启动/热驻留 COM 空闲退出守卫、Medium 完整性标签命名管道真实连接与进程路径校验）。
- 原生 `--self-test` 全部断言通过（`## RESULT: PASS`）：
  - `[冷启动首屏资源基准]`（1 标签 `此电脑`、零前序操作）：`WorkingSet=158.9 MB`，`PrivateBytes=100.4 MB`，`GDI=137`，`USER=132`。
  - 后台焦点保护断言：`MinimizedWindowPreservesTypingFocus`（`TypingReady=True, Preserved=True, NoAddedTabs=True`）、`InactiveWindowPreservesTypingFocus`（`Preserved=True`）、`ExplicitOpenStillRestoresWindow`（`Restored=True`）。
  - `NativeSelectAll1000`：1,000 个测试文件全选通过（本次记录耗时 **426 ms**，测试目录已清理）；`NarrowWindowTabOverflow` 通过（72.0 DIP）。
- 当前交付主程序 `LeafTab.exe`（525,824 字节 / 513.5 KB）SHA-256：`D94A29D02E0086B3FC61B14C8A9FA0ED71A158622E5FED881F8B36A52CCEA626`。
- 当前安装包 `LeafTab-v0.6.4-Setup-x64.exe`（338,432 字节 / 330.5 KB）SHA-256：`73720B8DF7E9A2CDF218EBA3E46108EE95DD1DFABCAC68142340904C19BF153B`，已同步至 `artifacts/latest`，原始验证日志归档于 `artifacts/verification/v0.6.4-20261115`。
- 已静默更新本机 `%LOCALAPPDATA%\Programs\LeafTab` 至最终 `0.6.4` 构建并后台启动，保留用户原有配置、系统接管与开始菜单快捷方式，未新增桌面快捷方式；审查临时目录 `LeafTab-Review` 已彻底清理。

尚未执行：Windows 10 实机、混合 DPI 多屏及断网共享/移动盘热插拔专项实测。

---

# LeafTab 0.6.3 搜索完善、预览复杂度限制与复审边界收尾验证（2026-11-15）

- 围绕四轮审查建议与高 ROI 体验优化收敛至 **`v0.6.3`**，含复审 5 项组合边界修复，全部改动已落地并完成项目外隔离构建与回归验证：
  1. **内存搜索缓存文件级新鲜度复核与隐藏子目录可见性跟踪**（`FastFileSearchService.cs`）：在目录修改时间快照与 30 秒绝对 TTL 基础上，对缓存命中的候选条目在交付前通过 `GetFileAttributesExW` 实时复核文件属性、字节大小、修改时间及作用域内上级目录隐藏状态；同时记录首次扫描跳过的隐藏子目录快照（`WasHidden=true`），当隐藏文件夹被取消隐藏但父目录时间戳未变时立即触发缓存失效重新扫描（仍保持隐藏的子目录内部写入如 `.git` 不触发误失效）。
  2. **原生搜索筛选语法、子句级 `|` 或运算与 Everything 正则路径语义**（`FastFileSearchService.QueryParser.cs` / `FastFileSearchService.cs`）：原生扫描与缓存完整支持 `ext:`、`size:`、`dm:`/`date:`、`type:`/`kind:`、`folder:`/`dir:`、`file:`、`path:`、`regex:`/`path:regex:` 及完整子句级 `|` 或运算（如 `size:>4kb | ext:txt`、`ext:txt | size:>4kb`、`folder:sub | size:>4kb`，兼容紧凑与空格分隔写法并保护正则内部 `|` 分支）；修复带反斜杠转义正则（如 `regex:^report_\d+\.txt$`）在递归子目录误按相对路径匹配导致 `^` 锚点失效的问题，默认匹配文件名并支持显式 `path:regex:`；未运行 Everything 输入 `content:` 等仅索引语法时输出明确中文提示。
  3. **搜索结果视图闭环能力**（`ShellComInterfaces.cs` / `ExplorerBrowserHost.cs` / `ExplorerBrowserHost.Search.cs` / `MainWindow.AddressAndSearch.cs`）：搜索详细信息视图通过 `IColumnManager` 自动注入「所在文件夹」（`System.ItemFolderPathDisplay`）列并开放排序菜单项；状态栏新增「继续加载更多」渐进扩容按钮（`400 -> 1,200 -> 3,600 -> 10,000`）；命令栏、`Ctrl + Enter` 与 `⋯` 菜单新增「打开所在位置」，在新标签页（或复用已有标签页）打开父目录并等待原生视图枚举完成后精确高亮选中目标文件（严格校验 `S_OK` 与 `SVGIO_SELECTION > 0`）。
  4. **Markdown 预览严格内联节点预算与后台预解析**（`FilePreviewService.cs` / `FilePreviewService.Markdown.cs` / `MainWindow.Preview.cs`）：将 Markdown 语法块解析移至后台线程，限制单文档块节点数（`320`）、表格总行数（`120`）、表格单元格总数（`800`）、单段行数（`1,200`）与总内联节点数（`2,400`，严格计入 `LineBreak` 硬换行、文本与嵌套内联节点，合并普通软换行）；命中 3,500 行超密表格或 20,000 行带换行标记文本时自动切换为零卡顿源码预览，且用户手动点击「渲染」时在 AST 与 WPF 物化双阶段严格截断于 `2,400` 内联节点上限内，杜绝 UI 布局卡顿。
  5. **多窗口统一会话恢复、偏好记忆、无障碍与后台静默诊断**（`AppConfig.cs` / `AppConfigStore.cs` / `MainWindow.xaml` / `MainWindow.xaml.cs` / `App.xaml.cs`）：持久化保存预览侧栏宽度 `PreviewPaneWidthDip` 及可选「启动时恢复上次打开的标签页」（`RestorePreviousTabs`，默认关闭）；在「彻底退出」与系统注销关闭全部窗口前通过 `SaveUnifiedSessionBeforeExit` 一次性聚合保存所有打开窗口的完整标签列表并跳过逐窗口关闭覆写，解决多窗口退出仅恢复最后一个窗口标签的问题；为工具栏纯图标按钮补齐 `AutomationProperties.Name`；统一挂载全局未处理异常写入 `DiagnosticLog`，在无可见窗口的后台热驻留态静默记录异常并重置预热宿主。
- `scripts/verify.ps1` 项目外副本隔离构建与测试完整通过（源码目录无 `bin/obj`）：.NET SDK `Release|x64` 构建 **0 警告、0 错误**。
- **64 个独立回归用例全部通过**（`RESULT: 64 passed, 0 failed`），在 59 项基础上新增 5 组复审组合边界回归：多窗口「彻底退出」统一保存全部窗口标签会话、子句级 `|` 任意顺序组合 `size:`/`ext:`/`folder:`/`regex:`（冷扫与缓存一致）、带 `\d`/`\.` 转义的 `regex:` 递归子目录匹配与 `path:regex:` 语义、隐藏子目录取消隐藏后缓存自动失效、80 KB 两万行硬换行 Markdown 手动点击「渲染」严格截断内联节点数（`<= 2402`）与隔离控件布局耗时保护。
- 原生 `--self-test` 全部断言通过（`## RESULT: PASS`）：
  - `[冷启动首屏资源基准]`（1 标签 `此电脑`、零前序操作）：`WorkingSet=158.8 MB`，`PrivateBytes=100.6 MB`，`GDI=143`，`USER=139`。
  - `AsyncSearch '*.cs'`：`MatchedCount=55`，`Backend=NativeFastScan`，`BackendMs=186ms`，`FolderPathColVisible=True`，`ExitedCleanly=True`。
  - `SearchLoadMoreAndOpenLocation`：`LoadMoreOk=True`（超 400 项渐进加载扩容），`OpenItemLocationOk=True`（定位父目录并精确选中目标文件），`ToolbarA11yOk=True`。
  - `ThisPcAsyncSearch 'C:'`：`MatchedCount=1`，`EnteredSearchMode=True`，`Exited=True`；`NativeSelectAll1000`：1,000 个测试文件全选通过（本次记录耗时 **411 ms**，测试目录已清理）。
- 当前交付主程序 `LeafTab.exe`（516,608 字节 / 504.5 KB）SHA-256：`7FC7416C49FAF37D713B7CED2CC8E2F20A99725076AC6B8E5679BCD603508540`。
- 当前安装包 `LeafTab-v0.6.3-Setup-x64.exe`（334,336 字节 / 326.5 KB）SHA-256：`3755C62FEA6AE1BECB7B421798A9A6FA456CBF5D270D2233CA5E8EE22B15733C`，已同步至 `artifacts/latest`，原始验证日志归档于 `artifacts/verification/v0.6.3-20261115`。

尚未执行：Windows 10 实机、多显示器混合 DPI 实测、非沙盒环境下的真实用户默认关联安装/升级/卸载、断网共享/移动盘热插拔实测、代码签名及 GitHub Actions 云端运行记录。

---

# LeafTab 0.6.2 审查修复与边界回归收尾验证（2026-11-14）

- 两轮代码审查发现的 **11 项问题（首轮 6 项 + 二轮补充复现 2 项 P1、3 项 P2）** 及模块职责拆分、CI 配置均已落地并完成隔离验证：
  1. **搜索完整性与缓存一致性**（`FastFileSearchService.cs` / `EverythingIpcClient.cs`）：修复高密度目录同轮多批次覆盖丢结果问题；补齐 Everything IPC `offset` 连续翻页与截断提示；改用单次 `GetFileAttributesExW` 捕获前置目录时间戳，将缓存目录跟踪上限扩展至 `2048` 个子目录，并在超限或元数据不可读时自动放弃写入缓存（杜绝部分跟踪导致的新增文件漏报），同时以 `CreatedUtc` 严格限制 30 秒绝对有效期；协调 `EverythingIpcClient` 首次并发查询的惰性后台窗口/消息泵初始化，确保高并发冷启动仅创建唯一 `HWND_MESSAGE` 窗口且 `Dispose` 后零句柄/线程残留。
  2. **预览侧栏线程边界、大图保护与刷新同步**（`FilePreviewService.cs` / `PidlManager.cs` / `MainWindow.AddressAndSearch.cs`）：图标与类型名称统一移至后台线程提取，UNC 远程路径直接使用文件属性提取避免网络阻塞；图片预览前置采用 `BitmapCacheOption.None` 探测尺寸，限制单图总像素上限 `67.1 MP`（超限回退信息卡）及超长图解码高度 `1600 px`；工具栏刷新按钮与 `F5` 共用 `RefreshActiveTab`，同步清空 `_lastPreviewSelectionKey` 并重拉当前选中文件的预览内容。
  3. **安装器事务级回滚保护**（`SetupEngine.cs` / `ShellAssociationRecovery.cs`）：安装前完整快照受管关联子树、容器键默认值与备份槽位，解决跨目录安装（`A -> B`）中途失败丢失原安装目录 `A` 系统关联与备份的问题；将临时释放文件与 `.previous` 备份严格绑定当前事务 ID，杜绝尚未替换主程序即失败时误用历史遗留 `.previous` 覆盖当前程序。
  4. **核心大文件拆分、冷启动基准与 CI**：按职责拆分 `MainWindow`（窗口生命周期 / 地址与搜索 / 预览侧栏）、`ExplorerBrowserHost`（原生宿主 / 搜索控制器）与 `FilePreviewService`（加载服务 / Markdown 渲染器）；`--self-test` 新增零操作前 1 标签冷启动纯净资源基准；新增 `.github/workflows/ci.yml` 自动化验证工作流。
- `scripts/verify.ps1` 项目外副本隔离构建与测试完整通过（源码树无 `bin/obj`）：.NET SDK `Release|x64` 构建 **0 警告、0 错误**。
- **54 个独立回归用例全部通过**（`RESULT: 54 passed, 0 failed`），新增覆盖：高密度目录全批次交付、Everything 过滤后连续翻页、超 64 子目录（100 子目录）新增文件自动失效与超限放弃缓存、12 路并发冷启动 Everything IPC 唯一窗口与零泄漏销毁、后台图标与 UNC 防阻塞、超长图限高与亿级像素炸弹拦截、安装事务故障注入回滚、跨目录 `A -> B` 失败保留 `A` 关联与备份、遗留 `.previous` 防误覆写、工具栏刷新与 `F5` 预览缓存同步。
- 原生 `--self-test` 全部断言通过（`## RESULT: PASS`）：
  - `[冷启动首屏资源基准]`（1 标签 `此电脑`、零前序操作）：`WorkingSet=158.9 MB`，`PrivateBytes=100.8 MB`，`GDI=143`，`USER=139`。
  - `ToolbarRefreshUpdatesPreviewPane`：选中文件外部修改后点击工具栏刷新按钮，`InitialLoaded=True`，`UpdatedAfterToolbarClick=True`。
  - `PreviewPaneToggleSameFolder`：`Kind='Markdown'`，`SHELLDLL_DefView` 宽度 `1250 -> 819 -> 1250` 像素，`KeptSelection=True`。
  - `NativeSelectAll1000`：1,000 个测试文件全选通过（本次记录耗时 **314 ms**，测试目录已清理）；`NarrowWindowTabOverflow` 通过（72.0 DIP）。
- 当前交付主程序 `LeafTab.exe`（452,608 字节 / 442.0 KB）SHA-256：`BFDAA8C9E8F0443C941E211929F463F7659780BAB02339706F3D87FE2A6EDBD0`。
- 当前安装包 `LeafTab-v0.6.2-Setup-x64.exe`（312,832 字节 / 305.5 KB）SHA-256：`FE46BA82EA140A47F38DDEA0BE255F2C3FB2517CE8827CB2D2020378E3569F7E`，已同步至 `artifacts/latest`，原始验证日志归档于 `artifacts/verification/audit-fixes-20261114`。

尚未执行：Windows 10 实机、多显示器混合 DPI 实测、非沙盒环境下的真实用户默认关联安装/升级/卸载、代码签名及 GitHub Actions 云端运行记录。

---

# LeafTab 0.6.2 交互细节、内置预览侧栏与安装包精简验证（2026-11-14）

- 四项改进及内置预览方案全部落地并验证：
  1. 顶部 `预览` 按钮改为控制 LeafTab 内置轻量 WPF 预览侧栏（支持可拖动分割线与状态持久化）：自动渲染 `.md`/`.markdown` 富文档并支持 `渲染`/`源码` 切换；以无锁方式读取 `UTF-8`/`ANSI(GBK)` 文本、代码与配置（上限 96 KB）；安全解码常见图片展示缩略图与分辨率（上限 32 MB）；其余文件夹、驱动器与二进制文件展示图标、类型与大小卡片及 `用默认程序打开` 入口；同时明确关闭底部无第三方的 Shell 原生 `EP_PreviewPane`。
  2. 点击面包屑地址栏空白区由 `BreadcrumbContainerGrid` 统一截获（`BreadcrumbScrollViewer` 设为 `Focusable="False"` 并过滤 `ButtonBase` 子控件），立即进入编辑态、同步 Win32/WPF 焦点并全选完整路径；点击文件区时自动退出地址编辑态。
  3. 主窗口与任务栏标题对齐 Windows 原生资源管理器（仅显示当前文件夹名称，移除末尾 ` - LeafTab` 后缀）。
  4. 精简分发形态为单一图形化一键安装包 `LeafTab-v0.6.2-Setup-x64.exe`（移除独立单文件绿色便携版发布物），并更新默认资源管理器提示语，明确卸载时自动恢复系统默认。
- `scripts/verify.ps1` 与安装包构建完整通过（于临时目录 `LeafTab-v062-preview-*` 隔离编译运行后已清理，源码目录无 `bin/obj`）：.NET SDK Release/x64 构建 **0 警告、0 错误**。
- **40 个独立回归用例全部通过**（`RESULT: 40 passed, 0 failed`），包括默认接管提示语、版本一致性、面包屑空白点击命中过滤，以及 4 项内置文件预览侧栏测试（Markdown 无锁富文本渲染、96 KB `UTF-8`/`ANSI(GBK)` 截断解码、PNG 尺寸无锁读取、二进制信息卡回退）。
- 原生 `--self-test` 全部断言通过（`## RESULT: PASS`）：
  - `[此电脑]`：`WindowTitle='此电脑'`。
  - `WindowTitleMatchesFolderOnly`：窗口标题与标签标题完全一致且无 ` - LeafTab` 后缀。
  - `AddressBarBlankClickSelectsAll`：`Handled=True`，`AddressTextBox.SelectedText` 与当前完整路径一致。
  - `PreviewPaneToggleSameFolder`：在当前目录选中 `README.md` 打开预览后，`Kind='Markdown'`，`SHELLDLL_DefView` 宽度由 `1250` 收缩至 `819` 像素，关预览后恢复 `1250` 像素，且保持选中状态（`KeptSelection=True`）。
  - `NativeSelectAll1000`：1,000 个测试文件全选通过（记录耗时 **255 ms**，仅代表本次本机运行）；`NarrowWindowTabOverflow` 通过（72.0 DIP，裁剪开启）。
- 当前安装包 `LeafTab-v0.6.2-Setup-x64.exe`（274,432 字节 / 268.0 KB）SHA-256：`10BE21468E32DAF5E90516DBBB77DC69823AC422C1B6E909B3630D2564773554`，位于 `artifacts/latest`。

---

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
