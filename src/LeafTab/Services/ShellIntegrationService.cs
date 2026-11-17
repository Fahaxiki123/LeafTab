using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LeafTab.Interop;
using Microsoft.Win32;
using LeafTab.Shared;

namespace LeafTab.Services
{
    internal static class ShellIntegrationService
    {
        internal const string DefaultClassesRoot = @"Software\Classes";
        internal const string DefaultBackupRoot = @"Software\LeafTab\RegistryBackup";

        internal const string LeafTabExecuteClsidBare = "8E4C7A29-3D51-4F82-9B14-6A5E20C91D73";
        internal const string LeafTabExecuteClsidString = "{" + LeafTabExecuteClsidBare + "}";
        internal static readonly Guid LeafTabExecuteClsid = new Guid(LeafTabExecuteClsidBare);

        internal const string LeafTabNewWindowExecuteClsidBare = "8E4C7A29-3D51-4F82-9B14-6A5E20C91D74";
        internal const string LeafTabNewWindowExecuteClsidString = "{" + LeafTabNewWindowExecuteClsidBare + "}";
        internal static readonly Guid LeafTabNewWindowExecuteClsid = new Guid(LeafTabNewWindowExecuteClsidBare);

        internal static bool IsLeafTabDelegateClsid(string? clsidStr) =>
            string.Equals(clsidStr, LeafTabExecuteClsidString, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clsidStr, LeafTabNewWindowExecuteClsidString, StringComparison.OrdinalIgnoreCase);

        private const string WinExplorerClsidKeyName = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
        private const string AutoRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoRunValueName = "LeafTab";
        internal const string ContextMenuVerbKeyName = "OpenInLeafTab";
        internal const string DefaultVerbKeyName = "LeafTabOpen";
        internal static event EventHandler? DefaultExplorerChanged;

        private static readonly string[] s_contextMenuTargets =
        {
            @"Directory\shell",
            @"Directory\Background\shell",
            @"Drive\shell"
        };

        public static string GetCurrentExecutablePath() =>
            Assembly.GetExecutingAssembly().Location;

        public static bool IsContextMenuEnabled() =>
            IsContextMenuEnabledCore(DefaultClassesRoot, GetCurrentExecutablePath());

        public static (bool Ok, string Message) SetContextMenuEnabled(bool enable) =>
            SetContextMenuEnabledCore(enable, GetCurrentExecutablePath(), DefaultClassesRoot, notifyShell: true);

        public static bool IsDefaultExplorerEnabled() =>
            IsDefaultExplorerEnabledCore(DefaultClassesRoot, GetCurrentExecutablePath());

        public static (bool Ok, string Message) SetDefaultExplorerEnabled(bool enable) =>
            SetDefaultExplorerEnabledCore(enable, GetCurrentExecutablePath(), DefaultClassesRoot, DefaultBackupRoot, notifyShell: true);

        public static (bool Ok, string Message) SetDefaultExplorerEnabledForExe(bool enable, string targetExePath) =>
            SetDefaultExplorerEnabledCore(enable, targetExePath, DefaultClassesRoot, DefaultBackupRoot, notifyShell: true);

        public static (bool Ok, string Message) SetContextMenuEnabledForExe(bool enable, string targetExePath) =>
            SetContextMenuEnabledCore(enable, targetExePath, DefaultClassesRoot, notifyShell: true);

        public static bool UnregisterAllOwned()
        {
            var defaults = SetDefaultExplorerEnabledCore(false, GetCurrentExecutablePath(), DefaultClassesRoot, DefaultBackupRoot, notifyShell: false);
            var context = SetContextMenuEnabledCore(false, GetCurrentExecutablePath(), DefaultClassesRoot, notifyShell: true);
            return defaults.Ok && context.Ok && !ShellAssociationRecovery.HasReferencesToExecutable(GetCurrentExecutablePath());
        }

        public static void RepairRegisteredPathsIfMoved()
        {
            try
            {
                string exePath = GetCurrentExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    RepairRegisteredPathsIfMovedCore(exePath, DefaultClassesRoot, DefaultBackupRoot, notifyShell: true);
                }
            }
            catch
            {
            }
        }

        // ==================== CORE REGISTRY OPERATIONS (HKCU ONLY, ZERO ADMIN) ====================

        // Without an expected path, detect repair candidates (including moved executables).
        // UI queries supply the current path so another copy is never shown as owned.
        internal static bool IsContextMenuEnabledCore(string classesRoot, string? expectedExePath = null)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Directory\shell\{ContextMenuVerbKeyName}");
                if (key == null)
                    return false;

                if (!string.Equals(key.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal))
                    return false;

                using RegistryKey? cmdKey = key.OpenSubKey("command");
                string? cmd = cmdKey?.GetValue(null) as string;
                return !string.IsNullOrWhiteSpace(cmd) &&
                       (expectedExePath == null ||
                        (ShellAssociationRecovery.IsExpectedCommand(cmd, expectedExePath, "\"%V \"") &&
                         string.IsNullOrEmpty(cmdKey?.GetValue("DelegateExecute") as string)));
            }
            catch
            {
                return false;
            }
        }

        internal static (bool Ok, string Message) SetContextMenuEnabledCore(
            bool enable,
            string exePath,
            string classesRoot,
            bool notifyShell)
        {
            try
            {
                if (enable)
                {
                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                        return (false, "未找到当前可执行文件路径，无法写入右键菜单。");

                    string iconVal = $"\"{exePath}\",0";
                    string cmdVal = $"\"{exePath}\" \"%V \"";

                    foreach (string parentRel in s_contextMenuTargets)
                    {
                        using RegistryKey verbKey = Registry.CurrentUser.CreateSubKey($@"{classesRoot}\{parentRel}\{ContextMenuVerbKeyName}");
                        verbKey.SetValue(null, "在 LeafTab 中打开", RegistryValueKind.String);
                        verbKey.SetValue("Icon", iconVal, RegistryValueKind.String);
                        verbKey.SetValue("LeafTabManaged", "1", RegistryValueKind.String);

                        using RegistryKey cmdKey = verbKey.CreateSubKey("command");
                        cmdKey.SetValue(null, cmdVal, RegistryValueKind.String);
                    }

                    if (notifyShell)
                        NotifyShellAssociationsChanged();

                    return (true, "已添加文件夹与磁盘右键菜单「在 LeafTab 中打开」");
                }
                else
                {
                    ShellAssociationRecovery.RestoreContextMenu(exePath, classesRoot);

                    if (notifyShell)
                        NotifyShellAssociationsChanged();

                    return (true, "已移除右键菜单「在 LeafTab 中打开」");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("shell-registration-failed", ex);
                return (false, $"更新右键菜单失败：{ex.Message}");
            }
        }

        internal static bool IsDefaultExplorerEnabledCore(string classesRoot, string? expectedExePath = null)
        {
            try
            {
                using RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Directory\shell");
                if (dirShell == null)
                    return false;

                string? defVerb = dirShell.GetValue(null) as string;
                if (!string.Equals(defVerb, DefaultVerbKeyName, StringComparison.OrdinalIgnoreCase))
                    return false;

                using RegistryKey? verbKey = dirShell.OpenSubKey(DefaultVerbKeyName);
                using RegistryKey? command = verbKey?.OpenSubKey("command");
                bool ownsDefault = verbKey != null &&
                       string.Equals(verbKey.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal) &&
                       (expectedExePath == null ||
                        (ShellAssociationRecovery.IsExpectedCommand(command?.GetValue(null) as string, expectedExePath, "\"%V \"") &&
                         string.Equals(command?.GetValue("DelegateExecute") as string, LeafTabExecuteClsidString, StringComparison.OrdinalIgnoreCase)));
                // The path-free probe is intentionally only an ownership/discovery probe
                // for conservative repair. UI and interception require the full contract.
                if (!ownsDefault || expectedExePath == null) return ownsDefault;
                foreach (string parent in new[] { "Directory", "Drive", "Folder" })
                {
                    using RegistryKey? shell = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\{parent}\shell");
                    if (!string.Equals(shell?.GetValue(null) as string, DefaultVerbKeyName, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!HasExpectedCommand(classesRoot, $@"{parent}\shell\{DefaultVerbKeyName}\command", expectedExePath, "\"%V \"", LeafTabExecuteClsidString)) return false;
                    if (!HasExpectedCommand(classesRoot, $@"{parent}\shell\open\command", expectedExePath, "\"%V \"", LeafTabExecuteClsidString)) return false;
                }
                return HasExpectedCommand(classesRoot, @"Folder\shell\explore\command", expectedExePath, "\"%V \"", LeafTabExecuteClsidString) &&
                       HasExpectedCommand(classesRoot, @"Folder\shell\opennewwindow\command", expectedExePath, "--new-window \"%V \"", LeafTabNewWindowExecuteClsidString) &&
                       HasExpectedCommand(classesRoot, $@"CLSID\{WinExplorerClsidKeyName}\shell\open\command", expectedExePath, "\"此电脑\"", LeafTabExecuteClsidString) &&
                       HasExpectedCommand(classesRoot, $@"CLSID\{WinExplorerClsidKeyName}\shell\opennewwindow\command", expectedExePath, "--new-window \"此电脑\"", LeafTabNewWindowExecuteClsidString) &&
                       HasExpectedCommand(classesRoot, $@"CLSID\{LeafTabExecuteClsidString}\LocalServer32", expectedExePath, "--com-server", null) &&
                       HasExpectedCommand(classesRoot, $@"CLSID\{LeafTabNewWindowExecuteClsidString}\LocalServer32", expectedExePath, "--com-server", null);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasExpectedCommand(string classesRoot, string path, string exe, string arguments, string? clsid)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(classesRoot + "\\" + path);
            return key != null && ShellAssociationRecovery.IsExpectedCommand(key.GetValue(null) as string, exe, arguments) &&
                   (clsid == null ? string.IsNullOrEmpty(key.GetValue("DelegateExecute") as string) :
                    string.Equals(key.GetValue("DelegateExecute") as string, clsid, StringComparison.OrdinalIgnoreCase));
        }

        private static void NotifyDefaultExplorerChanged(string classesRoot)
        {
            if (!string.Equals(classesRoot, DefaultClassesRoot, StringComparison.OrdinalIgnoreCase)) return;
            try { DefaultExplorerChanged?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { DiagnosticLog.Write("shell-integration-refresh-failed", ex); }
        }

        internal static (bool Ok, string Message) SetDefaultExplorerEnabledCore(
            bool enable,
            string exePath,
            string classesRoot,
            string backupRoot,
            bool notifyShell)
        {
            try
            {
                if (enable)
                {
                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                        return (false, "未找到当前可执行文件路径，无法注册默认资源管理器。");

                    string iconVal = $"\"{exePath}\",0";
                    string folderCmdVal = $"\"{exePath}\" \"%V \"";
                    string folderNewWinCmdVal = $"\"{exePath}\" --new-window \"%V \"";
                    string thisPcCmdVal = $"\"{exePath}\" \"此电脑\"";
                    string thisPcNewWinCmdVal = $"\"{exePath}\" --new-window \"此电脑\"";
                    string comServerCmdVal = $"\"{exePath}\" --com-server";

                    // 1. Register per-user COM LocalServer32 for DelegateExecute (both standard & opennewwindow)
                    foreach ((string clsidStr, string desc) in new[]
                    {
                        (LeafTabExecuteClsidString, "LeafTab Shell Execute Command"),
                        (LeafTabNewWindowExecuteClsidString, "LeafTab Open New Window Execute Command")
                    })
                    {
                        using RegistryKey clsidKey = Registry.CurrentUser.CreateSubKey($@"{classesRoot}\CLSID\{clsidStr}");
                        clsidKey.SetValue(null, desc, RegistryValueKind.String);
                        clsidKey.SetValue("LeafTabManaged", "1", RegistryValueKind.String);

                        using RegistryKey localSrv = clsidKey.CreateSubKey("LocalServer32");
                        localSrv.SetValue(null, comServerCmdVal, RegistryValueKind.String);
                    }

                    // 2. Backup & apply Directory\shell, Drive\shell, and Folder\shell
                    //    (Control Panel / System Applets are detected via IShellItem in LeafTabExecuteCommand and forwarded to native CLSID_ExecuteFolder)
                    ApplyShellDefaultVerbWithBackup(
                        $@"{classesRoot}\Directory\shell",
                        $@"{backupRoot}\DirectoryShell",
                        iconVal,
                        folderCmdVal);
                    ApplyExplicitVerbCommandWithBackup(
                        $@"{classesRoot}\Directory\shell",
                        "open",
                        $@"{backupRoot}\DirectoryOpenCmd",
                        folderCmdVal);

                    ApplyShellDefaultVerbWithBackup(
                        $@"{classesRoot}\Drive\shell",
                        $@"{backupRoot}\DriveShell",
                        iconVal,
                        folderCmdVal);
                    ApplyExplicitVerbCommandWithBackup(
                        $@"{classesRoot}\Drive\shell",
                        "open",
                        $@"{backupRoot}\DriveOpenCmd",
                        folderCmdVal);

                    ApplyShellDefaultVerbWithBackup(
                        $@"{classesRoot}\Folder\shell",
                        $@"{backupRoot}\FolderShell",
                        iconVal,
                        folderCmdVal);
                    ApplyExplicitVerbCommandWithBackup(
                        $@"{classesRoot}\Folder\shell",
                        "open",
                        $@"{backupRoot}\FolderOpenCmd",
                        folderCmdVal);
                    ApplyExplicitVerbCommandWithBackup(
                        $@"{classesRoot}\Folder\shell",
                        "explore",
                        $@"{backupRoot}\FolderExploreCmd",
                        folderCmdVal);
                    ApplyExplicitVerbCommandWithBackup(
                        $@"{classesRoot}\Folder\shell",
                        "opennewwindow",
                        $@"{backupRoot}\FolderNewWinCmd",
                        folderNewWinCmdVal,
                        LeafTabNewWindowExecuteClsidString);

                    // 3. Backup & apply Win+E / New File Explorer Window CLSID
                    ApplyWinExplorerClsidWithBackup(
                        $@"{classesRoot}\CLSID\{WinExplorerClsidKeyName}",
                        $@"{backupRoot}\WinExplorerClsid",
                        thisPcCmdVal,
                        thisPcNewWinCmdVal);

                    // 4. Register login background warm-standby only when operating on real HKCU Classes (never in self-test sandbox)
                    if (string.Equals(classesRoot, DefaultClassesRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        SetAutoRunBackgroundWarmup(enable: true, exePath);
                    }

                    if (notifyShell)
                        NotifyShellAssociationsChanged();

                    NotifyDefaultExplorerChanged(classesRoot);
                    return (true, "已设为默认资源管理器（已接管文件夹、此电脑、任务栏入口与 Win+E；卸载软件时将自动恢复系统默认）");
                }
                else
                {
                    ShellAssociationRecovery.RestoreDefault(exePath, classesRoot, backupRoot);
                    if (notifyShell) NotifyShellAssociationsChanged();
                    NotifyDefaultExplorerChanged(classesRoot);
                    return (true, "已恢复仍属于此安装位置的资源管理器关联；其他程序的修改已保留");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("shell-registration-failed", ex);
                return (false, $"切换默认资源管理器失败：{ex.Message}");
            }
        }

        internal static bool RepairRegisteredPathsIfMovedCore(
            string currentExePath,
            string classesRoot,
            string backupRoot,
            bool notifyShell)
        {
            bool repaired = false;
            bool isRealUserClasses = string.Equals(classesRoot, DefaultClassesRoot, StringComparison.OrdinalIgnoreCase);

            if (IsContextMenuEnabledCore(classesRoot))
            {
                using RegistryKey? cmdKey = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Directory\shell\{ContextMenuVerbKeyName}\command");
                string? curCmd = cmdKey?.GetValue(null) as string;
                string? contextOwner = ShellAssociationRecovery.CommandExecutable(curCmd);
                if (contextOwner != null && string.Equals(Path.GetFileName(contextOwner), Path.GetFileName(currentExePath), StringComparison.OrdinalIgnoreCase) &&
                    ShellAssociationRecovery.IsExpectedCommand(curCmd, contextOwner, "\"%V \"") &&
                    string.IsNullOrEmpty(cmdKey?.GetValue("DelegateExecute") as string))
                {
                    string effectiveExe = ResolveEffectiveExeForRepair(curCmd, currentExePath);
                    string expectedFolderCmd = $"\"{effectiveExe}\" \"%V \"";
                    if (!string.Equals(curCmd, expectedFolderCmd, StringComparison.OrdinalIgnoreCase))
                    {
                        var res = SetContextMenuEnabledCore(true, effectiveExe, classesRoot, notifyShell: false);
                        if (res.Ok)
                            repaired = true;
                    }
                }
            }

            if (IsDefaultExplorerEnabledCore(classesRoot))
            {
                using RegistryKey? srvKey = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\CLSID\{LeafTabExecuteClsidString}\LocalServer32");
                using RegistryKey? newWinSrvKey = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\CLSID\{LeafTabNewWindowExecuteClsidString}\LocalServer32");
                using RegistryKey? verbCmdKey = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Directory\shell\{DefaultVerbKeyName}\command");
                using RegistryKey? folderOpenCmdKey = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Folder\shell\open\command");
                using RegistryKey? folderNewWinCmdKey = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\Folder\shell\opennewwindow\command");

                string? curSrv = srvKey?.GetValue(null) as string;
                string? curNewWinSrv = newWinSrvKey?.GetValue(null) as string;
                string? curVerbCmd = verbCmdKey?.GetValue(null) as string;
                string? curFolderDel = folderOpenCmdKey?.GetValue("DelegateExecute") as string;
                string? curNewWinDel = folderNewWinCmdKey?.GetValue("DelegateExecute") as string;

                string? registeredExe = ShellAssociationRecovery.CommandExecutable(curVerbCmd);
                bool otherDefaultOwner = false;
                foreach (string parent in new[] { "Drive", "Folder" })
                {
                    using RegistryKey? shell = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\{parent}\shell");
                    string? verb = shell?.GetValue(null) as string;
                    otherDefaultOwner |= !string.IsNullOrEmpty(verb) && !string.Equals(verb, DefaultVerbKeyName, StringComparison.OrdinalIgnoreCase);
                }
                if (registeredExe == null || otherDefaultOwner || !string.Equals(Path.GetFileName(registeredExe), Path.GetFileName(currentExePath), StringComparison.OrdinalIgnoreCase) ||
                    !ShellAssociationRecovery.CanRepairDefault(classesRoot, registeredExe, currentExePath))
                {
                    DiagnosticLog.Write("shell-repair-preserved-modified-association");
                    return repaired;
                }
                string effectiveExe = ResolveEffectiveExeForRepair(curVerbCmd, currentExePath);
                string expectedFolderCmd = $"\"{effectiveExe}\" \"%V \"";
                string expectedComCmd = $"\"{effectiveExe}\" --com-server";

                if (!IsDefaultExplorerEnabledCore(classesRoot, effectiveExe) ||
                    !string.Equals(curSrv, expectedComCmd, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(curNewWinSrv, expectedComCmd, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(curVerbCmd, expectedFolderCmd, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(curFolderDel, LeafTabExecuteClsidString, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(curNewWinDel, LeafTabNewWindowExecuteClsidString, StringComparison.OrdinalIgnoreCase))
                {
                    var res = SetDefaultExplorerEnabledCore(true, effectiveExe, classesRoot, backupRoot, notifyShell: false);
                    if (res.Ok)
                        repaired = true;
                }
                else if (isRealUserClasses)
                {
                    string expectedRunCmd = $"\"{effectiveExe}\" --background";
                    using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(AutoRunKeyPath);
                    string? curRunCmd = runKey?.GetValue(AutoRunValueName) as string;
                    if (!string.Equals(curRunCmd, expectedRunCmd, StringComparison.OrdinalIgnoreCase))
                    {
                        SetAutoRunBackgroundWarmup(enable: true, effectiveExe);
                    }
                }
            }

            if (repaired && notifyShell)
            {
                NotifyShellAssociationsChanged();
            }

            return repaired;
        }

        private static string ResolveEffectiveExeForRepair(string? existingCmdLine, string currentExePath)
        {
            string? registeredExe = ExtractLeadingQuotedExe(existingCmdLine);
            if (!string.IsNullOrWhiteSpace(registeredExe) &&
                !string.Equals(registeredExe, currentExePath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(registeredExe))
            {
                // Preserve existing fixed installation path if user temporarily ran a downloaded copy from Downloads/Temp
                return registeredExe!;
            }

            return currentExePath;
        }

        private static string? ExtractLeadingQuotedExe(string? cmdLine)
        {
            if (string.IsNullOrWhiteSpace(cmdLine))
                return null;

            string s = cmdLine!.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = s.IndexOf('"', 1);
                if (endQuote > 1)
                    return s.Substring(1, endQuote - 1);
            }
            return null;
        }

        internal static void SetAutoRunBackgroundWarmup(bool enable, string exePath)
        {
            try
            {
                using RegistryKey? runKey = Registry.CurrentUser.CreateSubKey(AutoRunKeyPath);
                if (runKey == null)
                    return;

                if (enable && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    runKey.SetValue(AutoRunValueName, $"\"{exePath}\" --background", RegistryValueKind.String);
                }
                else if (ShellAssociationRecovery.IsExpectedCommand(runKey.GetValue(AutoRunValueName) as string, exePath, "--background"))
                {
                    runKey.DeleteValue(AutoRunValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
            }
        }

        private static void ApplyExplicitVerbCommandWithBackup(
            string shellKeyPath,
            string verbName,
            string backupSlotPath,
            string cmdVal,
            string? delegateClsid = null)
        {
            string targetClsid = delegateClsid ?? LeafTabExecuteClsidString;
            string verbKeyPath = $@"{shellKeyPath}\{verbName}";
            string cmdKeyPath = $@"{verbKeyPath}\command";

            bool alreadyOwned = false;
            bool verbExisted = false;
            bool cmdExisted = false;

            using (RegistryKey? existingVerb = Registry.CurrentUser.OpenSubKey(verbKeyPath))
            {
                if (existingVerb != null)
                {
                    verbExisted = true;
                    using RegistryKey? existingCmd = existingVerb.OpenSubKey("command");
                    if (existingCmd != null)
                    {
                        cmdExisted = true;
                        string? curDel = existingCmd.GetValue("DelegateExecute") as string;
                        if (IsLeafTabDelegateClsid(curDel) &&
                            ShellAssociationRecovery.CommandExecutable(existingCmd.GetValue("") as string) != null)
                        {
                            alreadyOwned = true;
                        }
                    }
                }
            }

            if (!alreadyOwned)
            {
                using RegistryKey bak = Registry.CurrentUser.CreateSubKey(backupSlotPath);
                bak.SetValue("Captured", 1, RegistryValueKind.DWord);
                bak.SetValue("VerbExisted", verbExisted ? 1 : 0, RegistryValueKind.DWord);
                CaptureCommandSubKeyBackup(cmdKeyPath, bak, "Cmd");
                if (!cmdExisted)
                {
                    bak.SetValue("Cmd_Existed", 0, RegistryValueKind.DWord);
                }
            }

            using (RegistryKey cmd = Registry.CurrentUser.CreateSubKey(cmdKeyPath))
            {
                cmd.SetValue(null, cmdVal, RegistryValueKind.String);
                cmd.SetValue("DelegateExecute", targetClsid, RegistryValueKind.String);
                cmd.SetValue("LeafTabManaged", "1", RegistryValueKind.String);
            }
        }

        private static void ApplyShellDefaultVerbWithBackup(
            string shellKeyPath,
            string backupSlotPath,
            string iconVal,
            string folderCmdVal)
        {
            bool alreadyLeafTabOwned = false;
            bool keyExisted = false;
            bool hadPriorDefault = false;
            object priorDefault = string.Empty;
            RegistryValueKind priorDefaultKind = RegistryValueKind.String;

            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(shellKeyPath))
            {
                if (existing != null)
                {
                    keyExisted = true;
                    object? rawDef = existing.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string? defStr = rawDef as string;
                    if (string.Equals(defStr, DefaultVerbKeyName, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyLeafTabOwned = true;
                    }
                    else if (rawDef != null)
                    {
                        hadPriorDefault = true;
                        priorDefault = rawDef;
                        priorDefaultKind = existing.GetValueKind(string.Empty);
                    }
                }
            }

            if (!alreadyLeafTabOwned)
            {
                using RegistryKey bak = Registry.CurrentUser.CreateSubKey(backupSlotPath);
                bak.SetValue("Captured", 1, RegistryValueKind.DWord);
                bak.SetValue("KeyExisted", keyExisted ? 1 : 0, RegistryValueKind.DWord);
                bak.SetValue("HadDefault", hadPriorDefault ? 1 : 0, RegistryValueKind.DWord);
                bak.SetValue("PriorDefault", priorDefault, priorDefaultKind);
                bak.SetValue("PriorDefaultKind", (int)priorDefaultKind, RegistryValueKind.DWord);
            }

            using (RegistryKey shellKey = Registry.CurrentUser.CreateSubKey(shellKeyPath))
            {
                shellKey.SetValue(null, DefaultVerbKeyName, RegistryValueKind.String);
                shellKey.SetValue("LeafTabDefaultOwner", "1", RegistryValueKind.String);

                using RegistryKey verbKey = shellKey.CreateSubKey(DefaultVerbKeyName);
                verbKey.SetValue(null, "在 LeafTab 中打开", RegistryValueKind.String);
                verbKey.SetValue("MUIVerb", "打开(&O)", RegistryValueKind.String);
                verbKey.SetValue("Icon", iconVal, RegistryValueKind.String);
                verbKey.SetValue("MultiSelectModel", "Document", RegistryValueKind.String);
                verbKey.SetValue("LeafTabManaged", "1", RegistryValueKind.String);

                using RegistryKey cmdKey = verbKey.CreateSubKey("command");
                cmdKey.SetValue(null, folderCmdVal, RegistryValueKind.String);
                cmdKey.SetValue("DelegateExecute", LeafTabExecuteClsidString, RegistryValueKind.String);
            }
        }

        private static void ApplyWinExplorerClsidWithBackup(
            string clsidKeyPath,
            string backupSlotPath,
            string thisPcCmdVal,
            string thisPcNewWinCmdVal)
        {
            bool alreadyOwned = false;
            bool keyExisted = false;

            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(clsidKeyPath))
            {
                if (existing != null)
                {
                    keyExisted = true;
                    if (string.Equals(existing.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal))
                    {
                        alreadyOwned = true;
                    }
                }
            }

            if (!alreadyOwned)
            {
                using RegistryKey bak = Registry.CurrentUser.CreateSubKey(backupSlotPath);
                bak.SetValue("Captured", 1, RegistryValueKind.DWord);
                bak.SetValue("KeyExisted", keyExisted ? 1 : 0, RegistryValueKind.DWord);

                CaptureCommandSubKeyBackup(clsidKeyPath + @"\shell\open\command", bak, "Open");
                CaptureCommandSubKeyBackup(clsidKeyPath + @"\shell\opennewwindow\command", bak, "NewWin");
            }

            using (RegistryKey root = Registry.CurrentUser.CreateSubKey(clsidKeyPath))
            {
                root.SetValue("LeafTabManaged", "1", RegistryValueKind.String);

                using (RegistryKey openCmd = root.CreateSubKey(@"shell\open\command"))
                {
                    openCmd.SetValue(null, thisPcCmdVal, RegistryValueKind.String);
                    openCmd.SetValue("DelegateExecute", LeafTabExecuteClsidString, RegistryValueKind.String);
                    openCmd.SetValue("LeafTabManaged", "1", RegistryValueKind.String);
                }

                using (RegistryKey newWinCmd = root.CreateSubKey(@"shell\opennewwindow\command"))
                {
                    newWinCmd.SetValue(null, thisPcNewWinCmdVal, RegistryValueKind.String);
                    newWinCmd.SetValue("DelegateExecute", LeafTabNewWindowExecuteClsidString, RegistryValueKind.String);
                    newWinCmd.SetValue("LeafTabManaged", "1", RegistryValueKind.String);
                }
            }
        }

        private static void CaptureCommandSubKeyBackup(string cmdKeyPath, RegistryKey bak, string prefix)
        {
            using RegistryKey? cmd = Registry.CurrentUser.OpenSubKey(cmdKeyPath);
            if (cmd == null)
            {
                bak.SetValue(prefix + "_Existed", 0, RegistryValueKind.DWord);
                bak.SetValue(prefix + "_HadDef", 0, RegistryValueKind.DWord);
                bak.SetValue(prefix + "_HadDel", 0, RegistryValueKind.DWord);
                return;
            }

            bak.SetValue(prefix + "_Existed", 1, RegistryValueKind.DWord);
            object? rawDef = cmd.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bak.SetValue(prefix + "_HadDef", rawDef != null ? 1 : 0, RegistryValueKind.DWord);
            bak.SetValue(prefix + "_Def", (rawDef as string) ?? string.Empty, RegistryValueKind.String);
            if (rawDef != null) bak.SetValue(prefix + "_DefKind", (int)cmd.GetValueKind(""));

            object? rawDel = cmd.GetValue("DelegateExecute", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bak.SetValue(prefix + "_HadDel", rawDel != null ? 1 : 0, RegistryValueKind.DWord);
            bak.SetValue(prefix + "_Del", (rawDel as string) ?? string.Empty, RegistryValueKind.String);
            if (rawDel != null) bak.SetValue(prefix + "_DelKind", (int)cmd.GetValueKind("DelegateExecute"));
        }

        private static void TryDeleteKeyIfEmpty(string subKeyPath)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath);
                if (key != null && key.ValueCount == 0 && key.SubKeyCount == 0)
                {
                    key.Close();
                    Registry.CurrentUser.DeleteSubKey(subKeyPath, throwOnMissingSubKey: false);
                }
            }
            catch
            {
            }
        }

        private static void NotifyShellAssociationsChanged()
        {
            try
            {
                NativeMethods.SHChangeNotify(
                    NativeMethods.SHCNE_ASSOCCHANGED,
                    NativeMethods.SHCNF_IDLIST,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            catch
            {
            }
        }
    }

    // ==================== PER-USER COM DELEGATEEXECUTE SERVER (ZERO-LATENCY SHELL INTEGRATION) ====================

    internal sealed class ComShellExecuteServer : IDisposable
    {
        private readonly Action<LaunchActionRequest> _onLaunchRequest;
        private readonly Action? _onActivity;
        private LeafTabExecuteClassFactory? _factory;
        private LeafTabExecuteClassFactory? _newWindowFactory;
        private uint _cookie;
        private uint _newWindowCookie;
        private bool _registered;
        private bool _newWindowRegistered;
        private bool _disposed;

        public ComShellExecuteServer(Action<LaunchActionRequest> onLaunchRequest, Action? onActivity = null)
        {
            _onLaunchRequest = onLaunchRequest;
            _onActivity = onActivity;
        }

        public bool Start()
        {
            if ((_registered && _newWindowRegistered) || _disposed)
                return _registered && _newWindowRegistered;

            try
            {
                _factory = new LeafTabExecuteClassFactory(_onLaunchRequest, forceNewWindow: false, _onActivity);
                Guid clsid = ShellIntegrationService.LeafTabExecuteClsid;
                int hr = NativeMethods.CoRegisterClassObject(
                    ref clsid,
                    _factory,
                    NativeMethods.CLSCTX_LOCAL_SERVER,
                    NativeMethods.REGCLS_MULTIPLEUSE,
                    out _cookie);

                _registered = hr >= 0;

                _newWindowFactory = new LeafTabExecuteClassFactory(_onLaunchRequest, forceNewWindow: true, _onActivity);
                Guid newWinClsid = ShellIntegrationService.LeafTabNewWindowExecuteClsid;
                int hr2 = NativeMethods.CoRegisterClassObject(
                    ref newWinClsid,
                    _newWindowFactory,
                    NativeMethods.CLSCTX_LOCAL_SERVER,
                    NativeMethods.REGCLS_MULTIPLEUSE,
                    out _newWindowCookie);

                _newWindowRegistered = hr2 >= 0;
                if (_registered && _newWindowRegistered) return true;
                DiagnosticLog.Write("shell-com-registration-failed", new COMException("Shell command registration failed.", hr < 0 ? hr : hr2));
                RevokeRegistrations();
                return false;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("shell-com-registration-failed", ex);
                RevokeRegistrations();
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            RevokeRegistrations();
        }

        private void RevokeRegistrations()
        {
            if (_registered)
            {
                try
                {
                    NativeMethods.CoRevokeClassObject(_cookie);
                }
                catch { }
                _registered = false;
            }

            if (_newWindowRegistered)
            {
                try
                {
                    NativeMethods.CoRevokeClassObject(_newWindowCookie);
                }
                catch { }
                _newWindowRegistered = false;
            }

            _factory = null;
            _newWindowFactory = null;
        }
    }

    [ComVisible(true)]
    internal sealed class LeafTabExecuteClassFactory : IClassFactory
    {
        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
        private readonly Action<LaunchActionRequest> _onLaunchRequest;
        private readonly bool _forceNewWindow;
        private readonly Action? _onActivity;

        public LeafTabExecuteClassFactory(Action<LaunchActionRequest> onLaunchRequest, bool forceNewWindow, Action? onActivity = null)
        {
            _onLaunchRequest = onLaunchRequest;
            _forceNewWindow = forceNewWindow;
            _onActivity = onActivity;
        }

        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
                return CLASS_E_NOAGGREGATION;

            _onActivity?.Invoke();
            var cmd = new LeafTabExecuteCommand(_onLaunchRequest, _forceNewWindow, _onActivity);
            IntPtr punk = Marshal.GetIUnknownForObject(cmd);
            try
            {
                return Marshal.QueryInterface(punk, ref riid, out ppvObject);
            }
            finally
            {
                Marshal.Release(punk);
            }
        }

        public int LockServer(bool fLock) => ShellConstants.S_OK;
    }

    [ComVisible(true)]
    [Guid(ShellIntegrationService.LeafTabExecuteClsidBare)]
    internal sealed class LeafTabExecuteCommand : IExecuteCommand, IObjectWithSelection, IObjectWithSite
    {
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private static readonly uint s_currentPid = (uint)Process.GetCurrentProcess().Id;

        private readonly Action<LaunchActionRequest> _onLaunchRequest;
        private readonly bool _forceNewWindow;
        private readonly Action? _onActivity;
        private object? _site;
        private IShellItemArray? _selection;
        private string? _parameters;
        private string? _directory;

        public LeafTabExecuteCommand(Action<LaunchActionRequest> onLaunchRequest, bool forceNewWindow, Action? onActivity = null)
        {
            _onLaunchRequest = onLaunchRequest;
            _forceNewWindow = forceNewWindow;
            _onActivity = onActivity;
        }

        public int SetSite(object? pUnkSite)
        {
            _onActivity?.Invoke();
            _site = pUnkSite;
            return ShellConstants.S_OK;
        }

        public int GetSite(ref Guid riid, out IntPtr ppvSite)
        {
            ppvSite = IntPtr.Zero;
            if (_site == null)
                return E_NOINTERFACE;

            IntPtr punk = Marshal.GetIUnknownForObject(_site);
            try
            {
                return Marshal.QueryInterface(punk, ref riid, out ppvSite);
            }
            finally
            {
                Marshal.Release(punk);
            }
        }

        public int SetSelection(IShellItemArray? psia)
        {
            _onActivity?.Invoke();
            _selection = psia;
            return ShellConstants.S_OK;
        }

        public int GetSelection(ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (_selection == null)
                return E_NOINTERFACE;

            IntPtr punk = Marshal.GetIUnknownForObject(_selection);
            try
            {
                return Marshal.QueryInterface(punk, ref riid, out ppv);
            }
            finally
            {
                Marshal.Release(punk);
            }
        }

        private static readonly Guid s_clsidExecuteFolder = new Guid("11dbb47c-a525-400b-9e80-a54615a090c0");
        private static readonly Guid s_iidExecuteCommand = new Guid("7F9185B0-CB92-43c5-80A9-92277A4F7B54");
        private static uint s_cachedExplorerPid;
        private static bool s_cachedIsExplorer;

        private uint _keyState;
        private POINT _position;
        private int _nShow = NativeMethods.SW_SHOWNORMAL;
        private bool _noShowUi;

        public int SetKeyState(uint grfKeyState) { _keyState = grfKeyState; return ShellConstants.S_OK; }
        public int SetParameters(string? pszParameters) { _parameters = pszParameters; return ShellConstants.S_OK; }
        public int SetPosition(POINT pt) { _position = pt; return ShellConstants.S_OK; }
        public int SetShowWindow(int nShow) { _nShow = nShow; return ShellConstants.S_OK; }
        public int SetNoShowUI(bool fNoShowUI) { _noShowUi = fNoShowUI; return ShellConstants.S_OK; }
        public int SetDirectory(string? pszDirectory) { _directory = pszDirectory; return ShellConstants.S_OK; }

        public int Execute()
        {
            _onActivity?.Invoke();
            try
            {
                // 1. Control Panel / System Applets / Settings: forward directly to Windows native CLSID_ExecuteFolder
                if (IsSystemAppletInvocation(_selection, _parameters))
                {
                    return ForwardToNativeExecuteFolder();
                }

                // 2. Unless explicitly requesting 'opennewwindow', navigate IN-PLACE in 0ms inside LeafTab or File Dialogs (#32770)
                if (!_forceNewWindow && TryBrowseInPlaceIfEmbeddedHost(_site, _selection))
                {
                    return ShellConstants.S_OK;
                }

                // SetNoShowUI suppresses handler/error prompts; Shell shortcuts can set it
                // even for an explicit visible open. Only SW_HIDE requests a hidden launch.
                // Passive COM startup is filtered separately, before Execute is invoked.
                if (_nShow == NativeMethods.SW_HIDE)
                {
                    DiagnosticLog.Write("shell-open-suppressed-hidden");
                    return ShellConstants.S_OK;
                }

                // Route verbs that reach DelegateExecute. Direct Explorer launches are
                // handled separately; API selection completes via ShellViewRegistration.
                LaunchActionRequest req = BuildLaunchRequestFromShellInvocation();
                if (_forceNewWindow)
                {
                    req.OpenInNewWindow = true;
                }
                DiagnosticLog.Write(_noShowUi ? "shell-open-request-no-error-ui" : "shell-open-request");
                _onLaunchRequest(req);

                return ShellConstants.S_OK;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("shell-open-failed", ex);
                return Marshal.GetHRForException(ex);
            }
            finally
            {
                _site = null;
                _selection = null;
            }
        }

        private static void ReleaseLocalCom(object value)
        {
            if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private static bool IsSystemAppletInvocation(IShellItemArray? selection, string? parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                string p = parameters!.Trim();
                if (p.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (selection == null || selection.GetCount(out uint count) != ShellConstants.S_OK || count == 0)
                return false;

            if (selection.GetItemAt(0, out IShellItem? item) != ShellConstants.S_OK || item == null)
                return false;

            try
            {
                string? parsing = PidlManager.GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);
                if (string.IsNullOrWhiteSpace(parsing))
                    return false;

                string s = parsing!;
                return s.IndexOf("26EE0668-A00A-44D7-9371-BEB064C98683", StringComparison.OrdinalIgnoreCase) >= 0 || // Control Panel Category
                       s.IndexOf("21EC2020-3AEA-1069-A2DD-08002B30309D", StringComparison.OrdinalIgnoreCase) >= 0 || // Control Panel All Items
                       s.IndexOf("5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0", StringComparison.OrdinalIgnoreCase) >= 0 || // Control Panel
                       s.IndexOf("7007ACC7-3202-11D1-AAD2-00805FC1270E", StringComparison.OrdinalIgnoreCase) >= 0 || // Network Connections
                       s.IndexOf("992CFFA0-F557-101A-88EC-00DD010CCC48", StringComparison.OrdinalIgnoreCase) >= 0 || // Network Connections
                       s.IndexOf("A8A91A66-3A7D-4424-8D24-04E180695C7A", StringComparison.OrdinalIgnoreCase) >= 0 || // Devices and Printers
                       s.IndexOf("2227A280-3AEA-1069-A2DE-08002B30309D", StringComparison.OrdinalIgnoreCase) >= 0 || // Printers
                       s.IndexOf("D20EA4E1-3957-11D2-A40B-0C5020524153", StringComparison.OrdinalIgnoreCase) >= 0 || // Administrative Tools
                       s.IndexOf("ED7BA470-8E54-465E-825C-99712043E01C", StringComparison.OrdinalIgnoreCase) >= 0 || // All Tasks (GodMode)
                       s.IndexOf("BD84B380-8CA2-1069-AB1D-08000948F534", StringComparison.OrdinalIgnoreCase) >= 0 || // Fonts
                       s.IndexOf("0DF44EAA-FF21-4412-828E-260A8728E7F1", StringComparison.OrdinalIgnoreCase) >= 0 || // Taskbar Settings
                       s.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);
            }
            finally { ReleaseLocalCom(item); }
        }

        private int ForwardToNativeExecuteFolder()
        {
            Guid clsid = s_clsidExecuteFolder;
            Guid iid = s_iidExecuteCommand;
            int createResult = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, ref iid, out IntPtr pCmd);
            if (createResult != ShellConstants.S_OK ||
                pCmd == IntPtr.Zero)
            {
                return createResult < 0 ? createResult : unchecked((int)0x80004005);
            }

            object nativeObj = Marshal.GetObjectForIUnknown(pCmd);
            try
            {
                if (_site != null && nativeObj is IObjectWithSite withSite)
                    withSite.SetSite(_site);

                if (_selection != null && nativeObj is IObjectWithSelection withSel)
                    withSel.SetSelection(_selection);

                if (nativeObj is IExecuteCommand exec)
                {
                    exec.SetKeyState(_keyState);
                    if (_parameters != null) exec.SetParameters(_parameters);
                    exec.SetPosition(_position);
                    exec.SetShowWindow(_nShow);
                    exec.SetNoShowUI(_noShowUi);
                    if (_directory != null) exec.SetDirectory(_directory);
                    return exec.Execute();
                }

                return ShellConstants.S_OK;
            }
            finally
            {
                if (nativeObj is IObjectWithSite cleanupSite)
                    cleanupSite.SetSite(null);
                Marshal.ReleaseComObject(nativeObj);
                Marshal.Release(pCmd);
            }
        }

        private static bool TryBrowseInPlaceIfEmbeddedHost(object? site, IShellItemArray? selection)
        {
            if (site is not LeafTab.Interop.IServiceProvider sp || selection == null)
                return false;

            Guid sidTop = ShellConstants.SID_STopLevelBrowser;
            Guid iidSb = ShellConstants.IID_IShellBrowser;
            if (sp.QueryService(ref sidTop, ref iidSb, out IntPtr psbPtr) != ShellConstants.S_OK || psbPtr == IntPtr.Zero)
                return false;

            var sb = (IShellBrowser)Marshal.GetObjectForIUnknown(psbPtr);
            try
            {
                if (sb.GetWindow(out IntPtr browserHwnd) != ShellConstants.S_OK || browserHwnd == IntPtr.Zero)
                    return false;

                if (ShouldInterceptIntoLeafTab(browserHwnd))
                    return false;

                if (selection.GetCount(out uint count) != ShellConstants.S_OK || count == 0) return false;
                NativeMethods.GetWindowThreadProcessId(browserHwnd, out uint browserPid);
                bool leafTabHost = browserPid == s_currentPid ||
                    ShellAssociationRecovery.PathsEqual(NativeMethods.TryGetProcessImagePath(browserPid),
                        ShellIntegrationService.GetCurrentExecutablePath());
                // Route the complete selection through the normal tab-opening
                // path. Consuming only index zero silently discarded the rest.
                // Common file dialogs retain their own in-place navigation.
                if (ShouldRouteEmbeddedSelectionAsTabs(leafTabHost, count)) return false;

                if (
                    selection.GetItemAt(0, out IShellItem? firstItem) == ShellConstants.S_OK && firstItem != null)
                {
                    IntPtr itemPidl = IntPtr.Zero;
                    try
                    {
                        if (NativeMethods.SHGetIDListFromObject(firstItem, out itemPidl) == ShellConstants.S_OK && itemPidl != IntPtr.Zero)
                        {
                            itemPidl = PidlManager.NormalizeToBrowsablePidl(itemPidl);
                            return sb.BrowseObject(itemPidl, ShellConstants.SBSP_SAMEBROWSER | ShellConstants.SBSP_ABSOLUTE) >= 0;
                        }
                    }
                    finally
                    {
                        PidlManager.Free(ref itemPidl);
                        ReleaseLocalCom(firstItem);
                    }
                }

                return false;
            }
            finally
            {
                ReleaseLocalCom(sb);
                Marshal.Release(psbPtr);
            }
        }

        internal static bool ShouldRouteEmbeddedSelectionAsTabs(bool leafTabHost, uint selectionCount) =>
            leafTabHost && selectionCount > 1;

        private static bool ShouldInterceptIntoLeafTab(IntPtr browserHwnd)
        {
            NativeMethods.GetWindowThreadProcessId(browserHwnd, out uint browserPid);

            // Never hijack double-clicks originating inside our own LeafTab IExplorerBrowser!
            if (browserPid == s_currentPid)
                return false;

            IntPtr rootHwnd = NativeMethods.GetAncestor(browserHwnd, NativeMethods.GA_ROOT);
            if (rootHwnd == IntPtr.Zero)
                rootHwnd = browserHwnd;

            var clsSb = new StringBuilder(64);
            if (NativeMethods.GetClassNameW(rootHwnd, clsSb, clsSb.Capacity) > 0)
            {
                string cls = clsSb.ToString();
                // Never hijack Common File Open/Save dialogs (#32770)!
                if (string.Equals(cls, "#32770", StringComparison.Ordinal))
                    return false;
            }

            // Only intercept Windows Shell surfaces owned by explorer.exe (Desktop Progman/WorkerW, Taskbar, standalone Explorer);
            // leave embedded shell browsers inside third-party applications untouched.
            return IsWindowsExplorerProcess(browserPid);
        }

        private static bool IsWindowsExplorerProcess(uint pid)
        {
            if (pid == 0)
                return false;
            if (pid == s_cachedExplorerPid)
                return s_cachedIsExplorer;

            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                bool isExp = string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
                s_cachedExplorerPid = pid;
                s_cachedIsExplorer = isExp;
                return isExp;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("shell-process-identification-failed", ex);
                return false;
            }
        }

        private LaunchActionRequest BuildLaunchRequestFromShellInvocation()
        {
            var req = new LaunchActionRequest();

            if (_selection != null && _selection.GetCount(out uint count) == ShellConstants.S_OK && count > 0)
            {
                uint limit = Math.Min(count, 25u);
                for (uint i = 0; i < limit; i++)
                {
                    if (_selection.GetItemAt(i, out IShellItem? item) == ShellConstants.S_OK && item != null)
                    {
                        try
                        {
                            string? parsing = PidlManager.GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);
                            string? fsPath = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);

                            if (!string.IsNullOrWhiteSpace(fsPath) && File.Exists(fsPath))
                            {
                                req.Targets.AddRange(LaunchRequestParser.Parse(new[] { "/select," + fsPath }).Targets);
                                continue;
                            }

                            // Redirect Windows File Explorer launcher CLSID {52205fd8-5dfb-447d-801a-d0b52f2e83e1} cleanly to "此电脑"
                            if (string.IsNullOrWhiteSpace(fsPath) && PidlManager.IsExplorerLauncherTarget(parsing))
                            {
                                req.Targets.Add(new LaunchTargetItem("此电脑"));
                                continue;
                            }

                            IntPtr pidl = IntPtr.Zero;
                            try
                            {
                                if (NativeMethods.SHGetIDListFromObject(item, out pidl) == ShellConstants.S_OK && pidl != IntPtr.Zero)
                                {
                                    byte[] pidlBytes = PidlManager.ToBytes(pidl);
                                    string targetStr = !string.IsNullOrWhiteSpace(fsPath)
                                        ? fsPath!
                                        : (!string.IsNullOrWhiteSpace(parsing) ? parsing! : "此电脑");

                                    req.Targets.Add(new LaunchTargetItem(targetStr, selectItemName: null, pidlBytes: pidlBytes));
                                }
                            }
                            finally
                            {
                                PidlManager.Free(ref pidl);
                            }
                        }
                        finally { ReleaseLocalCom(item); }
                    }
                }
            }

            if (req.Targets.Count == 0 && !string.IsNullOrWhiteSpace(_parameters))
            {
                LaunchActionRequest parsed = ParseShellParameters(_parameters!);
                req.OpenInNewWindow = parsed.OpenInNewWindow;
                req.Targets.AddRange(parsed.Targets);
            }

            if (req.Targets.Count == 0 && !string.IsNullOrWhiteSpace(_directory) && Directory.Exists(_directory))
            {
                req.Targets.Add(new LaunchTargetItem(_directory!));
            }

            // E.g. Win + E has no selection and no folder parameter -> open "此电脑"
            if (req.Targets.Count == 0)
            {
                req.Targets.Add(new LaunchTargetItem("此电脑"));
            }

            return req;
        }

        internal static LaunchActionRequest ParseShellParameters(string parameters)
        {
            // Some Shell callers supply one unquoted literal path with spaces.
            if (Directory.Exists(parameters) || File.Exists(parameters))
                return LaunchRequestParser.Parse(new[] { parameters });

            IntPtr argv = CommandLineToArgvW("LeafTab.exe " + parameters, out int count);
            if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var args = new string[Math.Max(0, count - 1)];
                for (int i = 1; i < count; i++)
                    args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
                return LaunchRequestParser.Parse(args);
            }
            finally { FreeCommandLineArguments(argv); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
        [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
        private static extern IntPtr FreeCommandLineArguments(IntPtr memory);
    }
}
