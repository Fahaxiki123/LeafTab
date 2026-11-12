# LeafTab（一叶）

一款面向 **Windows 10 x64（1903~22H2）与 Windows 11 x64** 的轻量级、无广告、零注入多标签资源管理器。

内部直接托管 Windows 官方公开的 `IExplorerBrowser`（`SHELLDLL_DefView`）原生视图控件，外层基于 `.NET Framework 4.8 + WPF` 构建现代一体化多标签顶栏与地址导航栏。编译产物为**不到 100 KB 的纯净单文件 `LeafTab.exe`**（系统自带运行时，零第三方 DLL 依赖）。

---

## 核心特性（v0.1.0 原型版）

- **原生文件浏览体验**：锁定 `x64` 进程托管 `IExplorerBrowser (EBO_SHOWFRAMES)`，保留系统原生左导航树、右键菜单（7-Zip / Git / SVN 等）、图标缩略图、此电脑磁盘容量条、回收站与 WSL 命名空间。
- **零注入独立进程**：不向 `explorer.exe` 注入任何 BHO / Inline Hook DLL，不影响系统桌面稳定性，不修改任何系统注册表项。
- **顺手的多标签与手势操作**：
  - **鼠标中键点击文件夹 / 磁盘分区**：在后台新建标签页打开（采用独立 MTA 后台线程调用 `CUIAutomation8` 命中，防 UI 线程重入死锁，非文件夹自动忽略）；
  - **鼠标左键双击文件区空白处**：返回上一级目录（精准排除文件项与分组标题栏）；
  - **跨标签拖拽文件**：拖起文件移至其他标签悬停约 `380ms` 自动切页，移入下方原生列表松手即可完成系统原生移动/复制；
  - **标签栏交互**：`Ctrl+T` 新建、双击标签 / 中键点击标签 / `Ctrl+W` 关闭、标签栏滚轮切换、`Ctrl+Tab` 循环切换。
- **键盘加速键与路径防卡保护**：
  - 完整转发 `IShellView::TranslateAccelerator`（支持 `F2` 重命名、`Delete` 删除、`Ctrl+C/V/X/Z/A`、`Alt+Enter` 属性等）；
  - 地址栏支持盘符路径、环境变量（如 `%appdata%`）、`此电脑` / `回收站` / `\\wsl.localhost`，并对 `\\server\share` 网络路径执行异步超时预检。

---

## 快捷键一览

| 快捷键 / 手势 | 功能 |
| :--- | :--- |
| `Ctrl + T` | 新建标签页 |
| `Ctrl + W` / 双击标签 / 中键点标签 | 关闭当前标签页 |
| `Ctrl + Tab` / `Ctrl + Shift + Tab` / 标签栏滚轮 | 切换标签页 |
| `Ctrl + L` / `Alt + D` | 聚焦并全选地址栏 |
| `Alt + ←` / `Alt + →` / `Alt + ↑` | 后退 / 前进 / 向上一级 |
| `F5` | 刷新当前视图 |
| 文件区**中键点击文件夹** | 后台新建标签打开该目录 |
| 文件区**左键双击纯空白处** | 返回上一级目录 |

---

## 编译与运行

### 环境要求
- Windows 10 x64（1903 及以上）或 Windows 11 x64（系统已内置 `.NET Framework 4.8`）
- 编译工具：Visual Studio 2022+（含 `.NET 桌面开发` 工作负载）或 MSBuild

### 命令行构建
```powershell
msbuild LeafTab.sln /t:Restore,Build /p:Configuration=Release /p:Platform=x64
```
生成的单文件可执行程序位于：
```text
src\LeafTab\bin\x64\Release\net48\LeafTab.exe
```

### 运行内置兼容性与资源自检
```powershell
.\src\LeafTab\bin\x64\Release\net48\LeafTab.exe --self-test COMPATIBILITY_REPORT.md
```

---

## 文档索引
- [架构设计与工程边界规范 (DESIGN.md)](./DESIGN.md)
- [阶段一兼容性与资源压测记录 (COMPATIBILITY_REPORT.md)](./COMPATIBILITY_REPORT.md)
