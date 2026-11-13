using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LeafTab.Interop;
using Microsoft.Win32;

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

        private static readonly string[] s_contextMenuTargets =
        {
            @"Directory\shell",
            @"Directory\Background\shell",
            @"Drive\shell"
        };

        public static string GetCurrentExecutablePath() =>
            Assembly.GetExecutingAssembly().Location;

        public static bool IsContextMenuEnabled() =>
            IsContextMenuEnabledCore(DefaultClassesRoot);

        public static (bool Ok, string Message) SetContextMenuEnabled(bool enable) =>
            SetContextMenuEnabledCore(enable, GetCurrentExecutablePath(), DefaultClassesRoot, notifyShell: true);

        public static bool IsDefaultExplorerEnabled() =>
            IsDefaultExplorerEnabledCore(DefaultClassesRoot);

        public static (bool Ok, string Message) SetDefaultExplorerEnabled(bool enable) =>
            SetDefaultExplorerEnabledCore(enable, GetCurrentExecutablePath(), DefaultClassesRoot, DefaultBackupRoot, notifyShell: true);

        public static (bool Ok, string Message) SetDefaultExplorerEnabledForExe(bool enable, string targetExePath) =>
            SetDefaultExplorerEnabledCore(enable, targetExePath, DefaultClassesRoot, DefaultBackupRoot, notifyShell: true);

        public static (bool Ok, string Message) SetContextMenuEnabledForExe(bool enable, string targetExePath) =>
            SetContextMenuEnabledCore(enable, targetExePath, DefaultClassesRoot, notifyShell: true);

        public static void UnregisterAllOwned()
        {
            SetDefaultExplorerEnabledCore(false, GetCurrentExecutablePath(), DefaultClassesRoot, DefaultBackupRoot, notifyShell: false);
            SetContextMenuEnabledCore(false, GetCurrentExecutablePath(), DefaultClassesRoot, notifyShell: true);
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

        internal static bool IsContextMenuEnabledCore(string classesRoot)
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
                return !string.IsNullOrWhiteSpace(cmd);
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
                    foreach (string parentRel in s_contextMenuTargets)
                    {
                        string verbSubPath = $@"{classesRoot}\{parentRel}\{ContextMenuVerbKeyName}";
                        if (IsSubKeyOwnedByLeafTab(verbSubPath))
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(verbSubPath, throwOnMissingSubKey: false);
                        }
                    }

                    if (notifyShell)
                        NotifyShellAssociationsChanged();

                    return (true, "已移除右键菜单「在 LeafTab 中打开」");
                }
            }
            catch (Exception ex)
            {
                return (false, $"更新右键菜单失败：{ex.Message}");
            }
        }

        internal static bool IsDefaultExplorerEnabledCore(string classesRoot)
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
                return verbKey != null &&
                       string.Equals(verbKey.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
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

                    return (true, "已设为默认资源管理器（全面接管文件夹、此电脑、任务栏入口与 Win+E；删除程序前请先在此取消勾选）");
                }
                else
                {
                    if (string.Equals(classesRoot, DefaultClassesRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        SetAutoRunBackgroundWarmup(enable: false, exePath);
                    }

                    RestoreExplicitVerbCommandFromBackup(
                        $@"{classesRoot}\Directory\shell",
                        "open",
                        $@"{backupRoot}\DirectoryOpenCmd");
                    RestoreShellDefaultVerbFromBackup(
                        $@"{classesRoot}\Directory\shell",
                        $@"{backupRoot}\DirectoryShell");

                    RestoreExplicitVerbCommandFromBackup(
                        $@"{classesRoot}\Drive\shell",
                        "open",
                        $@"{backupRoot}\DriveOpenCmd");
                    RestoreShellDefaultVerbFromBackup(
                        $@"{classesRoot}\Drive\shell",
                        $@"{backupRoot}\DriveShell");

                    RestoreExplicitVerbCommandFromBackup(
                        $@"{classesRoot}\Folder\shell",
                        "open",
                        $@"{backupRoot}\FolderOpenCmd");
                    RestoreExplicitVerbCommandFromBackup(
                        $@"{classesRoot}\Folder\shell",
                        "explore",
                        $@"{backupRoot}\FolderExploreCmd");
                    RestoreExplicitVerbCommandFromBackup(
                        $@"{classesRoot}\Folder\shell",
                        "opennewwindow",
                        $@"{backupRoot}\FolderNewWinCmd");
                    RestoreShellDefaultVerbFromBackup(
                        $@"{classesRoot}\Folder\shell",
                        $@"{backupRoot}\FolderShell");

                    RestoreWinExplorerClsidFromBackup(
                        $@"{classesRoot}\CLSID\{WinExplorerClsidKeyName}",
                        $@"{backupRoot}\WinExplorerClsid");

                    foreach (string ownClsidStr in new[] { LeafTabExecuteClsidString, LeafTabNewWindowExecuteClsidString })
                    {
                        string ownClsidPath = $@"{classesRoot}\CLSID\{ownClsidStr}";
                        if (IsSubKeyOwnedByLeafTab(ownClsidPath))
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(ownClsidPath, throwOnMissingSubKey: false);
                        }
                    }

                    TryDeleteKeyIfEmpty(backupRoot);

                    if (notifyShell)
                        NotifyShellAssociationsChanged();

                    return (true, "已恢复系统默认 Windows 资源管理器");
                }
            }
            catch (Exception ex)
            {
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
                string effectiveExe = ResolveEffectiveExeForRepair(curCmd, currentExePath);
                string expectedFolderCmd = $"\"{effectiveExe}\" \"%V \"";

                if (!string.Equals(curCmd, expectedFolderCmd, StringComparison.OrdinalIgnoreCase))
                {
                    var res = SetContextMenuEnabledCore(true, effectiveExe, classesRoot, notifyShell: false);
                    if (res.Ok)
                        repaired = true;
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

                string effectiveExe = ResolveEffectiveExeForRepair(curVerbCmd, currentExePath);
                string expectedFolderCmd = $"\"{effectiveExe}\" \"%V \"";
                string expectedComCmd = $"\"{effectiveExe}\" --com-server";

                if (!string.Equals(curSrv, expectedComCmd, StringComparison.OrdinalIgnoreCase) ||
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
                File.Exists(registeredExe) &&
                !QuickLaunchAndInstallService.IsVolatileLaunchLocation(registeredExe) &&
                QuickLaunchAndInstallService.IsVolatileLaunchLocation(currentExePath))
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
                else
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
                        if (IsLeafTabDelegateClsid(curDel) ||
                            string.Equals(existingCmd.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal))
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

        private static void RestoreExplicitVerbCommandFromBackup(
            string shellKeyPath,
            string verbName,
            string backupSlotPath)
        {
            string verbKeyPath = $@"{shellKeyPath}\{verbName}";
            string cmdKeyPath = $@"{verbKeyPath}\command";

            using (RegistryKey? cmd = Registry.CurrentUser.OpenSubKey(cmdKeyPath))
            {
                if (cmd == null)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(backupSlotPath, throwOnMissingSubKey: false);
                    return;
                }

                string? curDel = cmd.GetValue("DelegateExecute") as string;
                if (!IsLeafTabDelegateClsid(curDel) &&
                    !string.Equals(cmd.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal))
                {
                    // Another file manager modified this verb; respect ownership and leave untouched
                    return;
                }
            }

            bool verbExistedPrior = false;
            using (RegistryKey? bak = Registry.CurrentUser.OpenSubKey(backupSlotPath))
            {
                if (bak != null && Convert.ToInt32(bak.GetValue("Captured", 0)) == 1)
                {
                    verbExistedPrior = Convert.ToInt32(bak.GetValue("VerbExisted", 0)) == 1;
                    if (verbExistedPrior)
                    {
                        RestoreCommandSubKeyBackup(cmdKeyPath, bak, "Cmd");
                    }
                }
            }

            if (!verbExistedPrior)
            {
                Registry.CurrentUser.DeleteSubKeyTree(verbKeyPath, throwOnMissingSubKey: false);
            }
            else
            {
                TryDeleteKeyIfEmpty(verbKeyPath);
            }

            Registry.CurrentUser.DeleteSubKeyTree(backupSlotPath, throwOnMissingSubKey: false);
            TryDeleteKeyIfEmpty(shellKeyPath);
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
            string priorDefault = string.Empty;

            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(shellKeyPath))
            {
                if (existing != null)
                {
                    keyExisted = true;
                    object? rawDef = existing.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string? defStr = rawDef as string;
                    if (string.Equals(defStr, DefaultVerbKeyName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.GetValue("LeafTabDefaultOwner") as string, "1", StringComparison.Ordinal))
                    {
                        alreadyLeafTabOwned = true;
                    }
                    else if (rawDef != null)
                    {
                        hadPriorDefault = true;
                        priorDefault = defStr ?? rawDef.ToString() ?? string.Empty;
                    }
                }
            }

            if (!alreadyLeafTabOwned)
            {
                using RegistryKey bak = Registry.CurrentUser.CreateSubKey(backupSlotPath);
                bak.SetValue("Captured", 1, RegistryValueKind.DWord);
                bak.SetValue("KeyExisted", keyExisted ? 1 : 0, RegistryValueKind.DWord);
                bak.SetValue("HadDefault", hadPriorDefault ? 1 : 0, RegistryValueKind.DWord);
                bak.SetValue("PriorDefault", priorDefault, RegistryValueKind.String);
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

        private static void RestoreShellDefaultVerbFromBackup(string shellKeyPath, string backupSlotPath)
        {
            bool keyExistedPrior = true;
            bool hadPriorDefault = false;
            string priorDefault = string.Empty;
            bool hasBackup = false;

            using (RegistryKey? bak = Registry.CurrentUser.OpenSubKey(backupSlotPath))
            {
                if (bak != null && Convert.ToInt32(bak.GetValue("Captured", 0)) == 1)
                {
                    hasBackup = true;
                    keyExistedPrior = Convert.ToInt32(bak.GetValue("KeyExisted", 1)) == 1;
                    hadPriorDefault = Convert.ToInt32(bak.GetValue("HadDefault", 0)) == 1;
                    priorDefault = (bak.GetValue("PriorDefault") as string) ?? string.Empty;
                }
            }

            string verbSubKeyPath = $@"{shellKeyPath}\{DefaultVerbKeyName}";
            if (IsSubKeyOwnedByLeafTab(verbSubKeyPath))
            {
                Registry.CurrentUser.DeleteSubKeyTree(verbSubKeyPath, throwOnMissingSubKey: false);
            }

            bool restoredOwnership = false;
            using (RegistryKey? shellKey = Registry.CurrentUser.OpenSubKey(shellKeyPath, writable: true))
            {
                if (shellKey != null)
                {
                    string? curDef = shellKey.GetValue(null) as string;
                    // Ownership verification: NEVER overwrite if another file manager took over (Default) after LeafTab!
                    bool ownedByUs = string.Equals(curDef, DefaultVerbKeyName, StringComparison.OrdinalIgnoreCase);

                    if (ownedByUs)
                    {
                        if (hasBackup && hadPriorDefault)
                        {
                            shellKey.SetValue(null, priorDefault, RegistryValueKind.String);
                        }
                        else
                        {
                            shellKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                        }
                        restoredOwnership = true;
                    }

                    shellKey.DeleteValue("LeafTabDefaultOwner", throwOnMissingValue: false);
                }
            }

            if (hasBackup && restoredOwnership)
            {
                Registry.CurrentUser.DeleteSubKeyTree(backupSlotPath, throwOnMissingSubKey: false);
            }

            if (!keyExistedPrior)
            {
                TryDeleteKeyIfEmpty(shellKeyPath);
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
                return;
            }

            bak.SetValue(prefix + "_Existed", 1, RegistryValueKind.DWord);
            object? rawDef = cmd.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bak.SetValue(prefix + "_HadDef", rawDef != null ? 1 : 0, RegistryValueKind.DWord);
            bak.SetValue(prefix + "_Def", (rawDef as string) ?? string.Empty, RegistryValueKind.String);

            object? rawDel = cmd.GetValue("DelegateExecute", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            bak.SetValue(prefix + "_HadDel", rawDel != null ? 1 : 0, RegistryValueKind.DWord);
            bak.SetValue(prefix + "_Del", (rawDel as string) ?? string.Empty, RegistryValueKind.String);
        }

        private static void RestoreWinExplorerClsidFromBackup(string clsidKeyPath, string backupSlotPath)
        {
            using RegistryKey? clsidKey = Registry.CurrentUser.OpenSubKey(clsidKeyPath, writable: true);
            if (clsidKey == null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(backupSlotPath, throwOnMissingSubKey: false);
                return;
            }

            // Ownership check before touching CLSID\{52205fd8-...}
            bool rootOwned = string.Equals(clsidKey.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal);
            if (!rootOwned)
                return;

            bool keyExistedPrior = false;
            using (RegistryKey? bak = Registry.CurrentUser.OpenSubKey(backupSlotPath))
            {
                if (bak != null && Convert.ToInt32(bak.GetValue("Captured", 0)) == 1)
                {
                    keyExistedPrior = Convert.ToInt32(bak.GetValue("KeyExisted", 0)) == 1;
                    if (keyExistedPrior)
                    {
                        RestoreCommandSubKeyBackup(clsidKeyPath + @"\shell\open\command", bak, "Open");
                        RestoreCommandSubKeyBackup(clsidKeyPath + @"\shell\opennewwindow\command", bak, "NewWin");
                    }
                }
            }

            if (!keyExistedPrior)
            {
                clsidKey.Close();
                Registry.CurrentUser.DeleteSubKeyTree(clsidKeyPath, throwOnMissingSubKey: false);
            }
            else
            {
                clsidKey.DeleteValue("LeafTabManaged", throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree(backupSlotPath, throwOnMissingSubKey: false);
        }

        private static void RestoreCommandSubKeyBackup(string cmdKeyPath, RegistryKey bak, string prefix)
        {
            using RegistryKey? cmd = Registry.CurrentUser.OpenSubKey(cmdKeyPath, writable: true);
            if (cmd == null)
                return;

            string? curDel = cmd.GetValue("DelegateExecute") as string;
            if (!IsLeafTabDelegateClsid(curDel) &&
                !string.Equals(cmd.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal))
            {
                return;
            }

            bool existed = Convert.ToInt32(bak.GetValue(prefix + "_Existed", 0)) == 1;
            if (!existed)
            {
                cmd.Close();
                Registry.CurrentUser.DeleteSubKeyTree(cmdKeyPath, throwOnMissingSubKey: false);
                return;
            }

            if (Convert.ToInt32(bak.GetValue(prefix + "_HadDef", 0)) == 1)
                cmd.SetValue(null, (bak.GetValue(prefix + "_Def") as string) ?? string.Empty, RegistryValueKind.String);
            else
                cmd.DeleteValue(string.Empty, throwOnMissingValue: false);

            if (Convert.ToInt32(bak.GetValue(prefix + "_HadDel", 0)) == 1)
                cmd.SetValue("DelegateExecute", (bak.GetValue(prefix + "_Del") as string) ?? string.Empty, RegistryValueKind.String);
            else
                cmd.DeleteValue("DelegateExecute", throwOnMissingValue: false);

            cmd.DeleteValue("LeafTabManaged", throwOnMissingValue: false);
        }

        private static bool IsSubKeyOwnedByLeafTab(string subKeyPath)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath);
            if (key == null)
                return false;

            if (string.Equals(key.GetValue("LeafTabManaged") as string, "1", StringComparison.Ordinal))
                return true;

            using RegistryKey? cmd = key.OpenSubKey("command");
            string? cmdVal = cmd?.GetValue(null) as string;
            return cmdVal != null && cmdVal.IndexOf("LeafTab.exe", StringComparison.OrdinalIgnoreCase) >= 0;
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
        private LeafTabExecuteClassFactory? _factory;
        private LeafTabExecuteClassFactory? _newWindowFactory;
        private uint _cookie;
        private uint _newWindowCookie;
        private bool _registered;
        private bool _newWindowRegistered;
        private bool _disposed;

        public ComShellExecuteServer(Action<LaunchActionRequest> onLaunchRequest)
        {
            _onLaunchRequest = onLaunchRequest;
        }

        public bool Start()
        {
            if (_registered || _disposed)
                return _registered;

            try
            {
                _factory = new LeafTabExecuteClassFactory(_onLaunchRequest, forceNewWindow: false);
                Guid clsid = ShellIntegrationService.LeafTabExecuteClsid;
                int hr = NativeMethods.CoRegisterClassObject(
                    ref clsid,
                    _factory,
                    NativeMethods.CLSCTX_LOCAL_SERVER,
                    NativeMethods.REGCLS_MULTIPLEUSE,
                    out _cookie);

                _registered = hr >= 0;

                _newWindowFactory = new LeafTabExecuteClassFactory(_onLaunchRequest, forceNewWindow: true);
                Guid newWinClsid = ShellIntegrationService.LeafTabNewWindowExecuteClsid;
                int hr2 = NativeMethods.CoRegisterClassObject(
                    ref newWinClsid,
                    _newWindowFactory,
                    NativeMethods.CLSCTX_LOCAL_SERVER,
                    NativeMethods.REGCLS_MULTIPLEUSE,
                    out _newWindowCookie);

                _newWindowRegistered = hr2 >= 0;
                return _registered;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

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

        public LeafTabExecuteClassFactory(Action<LaunchActionRequest> onLaunchRequest, bool forceNewWindow)
        {
            _onLaunchRequest = onLaunchRequest;
            _forceNewWindow = forceNewWindow;
        }

        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
                return CLASS_E_NOAGGREGATION;

            var cmd = new LeafTabExecuteCommand(_onLaunchRequest, _forceNewWindow);
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
        private object? _site;
        private IShellItemArray? _selection;
        private string? _parameters;
        private string? _directory;

        public LeafTabExecuteCommand(Action<LaunchActionRequest> onLaunchRequest, bool forceNewWindow)
        {
            _onLaunchRequest = onLaunchRequest;
            _forceNewWindow = forceNewWindow;
        }

        public int SetSite(object? pUnkSite)
        {
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

        public int SetKeyState(uint grfKeyState) { _keyState = grfKeyState; return ShellConstants.S_OK; }
        public int SetParameters(string? pszParameters) { _parameters = pszParameters; return ShellConstants.S_OK; }
        public int SetPosition(POINT pt) { _position = pt; return ShellConstants.S_OK; }
        public int SetShowWindow(int nShow) { _nShow = nShow; return ShellConstants.S_OK; }
        public int SetNoShowUI(bool fNoShowUI) => ShellConstants.S_OK;
        public int SetDirectory(string? pszDirectory) { _directory = pszDirectory; return ShellConstants.S_OK; }

        public int Execute()
        {
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

                // 3. Otherwise (Desktop double-click, This PC, Recycle Bin, Win+E, Taskbar File Explorer,
                //    opennewwindow, Chrome/Edge/WeChat/VSCode ShellExecute / SHOpenFolderAndSelectItems, etc.),
                //    route into LeafTab.
                LaunchActionRequest req = BuildLaunchRequestFromShellInvocation();
                if (_forceNewWindow)
                {
                    req.OpenInNewWindow = true;
                }
                _onLaunchRequest(req);

                return ShellConstants.S_OK;
            }
            catch
            {
                return ShellConstants.S_OK;
            }
            finally
            {
                _site = null;
                _selection = null;
            }
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

        private int ForwardToNativeExecuteFolder()
        {
            Guid clsid = s_clsidExecuteFolder;
            Guid iid = s_iidExecuteCommand;
            if (NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, ref iid, out IntPtr pCmd) != ShellConstants.S_OK ||
                pCmd == IntPtr.Zero)
            {
                return ShellConstants.S_OK;
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

                if (selection.GetCount(out uint count) == ShellConstants.S_OK && count > 0 &&
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
                    }
                }

                return false;
            }
            finally
            {
                Marshal.Release(psbPtr);
            }
        }

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
            catch
            {
                return true;
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
                        string? parsing = PidlManager.GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);
                        string? display = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                        string? fsPath = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);

                        // Redirect Windows File Explorer launcher CLSID {52205fd8-5dfb-447d-801a-d0b52f2e83e1} cleanly to "此电脑"
                        if (PidlManager.IsExplorerLauncherTarget(parsing) || PidlManager.IsExplorerLauncherTarget(display))
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
                }
            }

            if (req.Targets.Count == 0 && !string.IsNullOrWhiteSpace(_parameters))
            {
                string norm = LaunchRequestParser.NormalizePathToken(_parameters!);
                if (!string.IsNullOrWhiteSpace(norm))
                {
                    req.Targets.Add(PidlManager.IsExplorerLauncherTarget(norm)
                        ? new LaunchTargetItem("此电脑")
                        : new LaunchTargetItem(norm));
                }
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
    }
}
