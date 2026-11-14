using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LeafTab.Shared;

namespace LeafTab.Setup
{
    internal sealed class SetupOptions
    {
        public bool IsUninstall { get; set; }
        public bool IsSilent { get; set; }
        public string InstallDir { get; set; } = string.Empty;
        public bool SetDefaultExplorer { get; set; } = true;
        public bool AddContextMenu { get; set; } = true;
        public bool CreateShortcuts { get; set; } = true;
        public bool RegisterUninstaller { get; set; } = true;
        public bool LaunchAfterInstall { get; set; } = true;
        public bool RemoveUserDataOnUninstall { get; set; }
    }

    internal static class SetupEngine
    {
        public const string AppVersion = "0.6.2";
        public const string PayloadResourceName = "LeafTab.Payload.gz";
        public const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LeafTab";
        public const string ShortcutFileName = "LeafTab（一叶）.lnk";
        public const string UninstallerExeName = "Uninstall-LeafTab.exe";

        private static readonly Guid s_clsidShellLink = new Guid("00021401-0000-0000-C000-000000000046");
        private static readonly Guid s_clsidFileOpenDialog = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

        public static string GetDefaultInstallDirectory() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "LeafTab");

        public static string? GetRegisteredInstallDirectory()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKeyPath);
                string? loc = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(loc))
                    return loc!.Trim();
            }
            catch
            {
            }
            return null;
        }

        public static SetupOptions ParseArguments(string[]? args)
        {
            var opts = new SetupOptions();
            string selfPath = Assembly.GetExecutingAssembly().Location;
            string selfName = Path.GetFileName(selfPath) ?? string.Empty;

            if (selfName.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
            {
                opts.IsUninstall = true;
            }

            bool explicitDir = false;
            bool explicitLaunch = false;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = (args[i] ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(a))
                        continue;

                    if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.IsUninstall = true;
                    }
                    else if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.IsSilent = true;
                    }
                    else if (string.Equals(a, "--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        opts.InstallDir = args[++i].Trim().Trim('"');
                        explicitDir = true;
                    }
                    else if (string.Equals(a, "--no-default", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.SetDefaultExplorer = false;
                    }
                    else if (string.Equals(a, "--no-context-menu", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.AddContextMenu = false;
                    }
                    else if (string.Equals(a, "--no-shortcuts", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.CreateShortcuts = false;
                    }
                    else if (string.Equals(a, "--no-register-uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.RegisterUninstaller = false;
                    }
                    else if (string.Equals(a, "--no-launch", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.LaunchAfterInstall = false;
                        explicitLaunch = true;
                    }
                    else if (string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.LaunchAfterInstall = true;
                        explicitLaunch = true;
                    }
                    else if (string.Equals(a, "--remove-data", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.RemoveUserDataOnUninstall = true;
                    }
                }
            }

            if (opts.IsSilent && !explicitLaunch)
            {
                opts.LaunchAfterInstall = false;
            }

            if (!explicitDir)
            {
                if (opts.IsUninstall)
                {
                    string? selfDir = Path.GetDirectoryName(selfPath);
                    if (!string.IsNullOrWhiteSpace(selfDir) &&
                        (File.Exists(Path.Combine(selfDir, "LeafTab.exe")) ||
                         string.Equals(selfName, UninstallerExeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        opts.InstallDir = selfDir!;
                    }
                    else
                    {
                        opts.InstallDir = GetRegisteredInstallDirectory() ?? GetDefaultInstallDirectory();
                    }
                }
                else
                {
                    opts.InstallDir = GetRegisteredInstallDirectory() ?? GetDefaultInstallDirectory();
                }
            }

            return opts;
        }

        public static async Task<(bool Ok, string Message, string InstalledExePath)> ExecuteInstallAsync(
            SetupOptions opts,
            Action<int, string>? progress = null)
        {
            string targetExePath = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(opts.InstallDir))
                    return (false, "安装路径不能为空。", string.Empty);

                string installDir = Path.GetFullPath(opts.InstallDir.Trim());
                targetExePath = Path.Combine(installDir, "LeafTab.exe");
                string uninstallExePath = Path.Combine(installDir, UninstallerExeName);

                progress?.Invoke(12, "正在检测并关闭运行中的旧实例…");
                await Task.Run(() => StopRunningLeafTabProcesses(targetExePath, stopAllIfTakingOver: opts.SetDefaultExplorer));

                progress?.Invoke(35, "正在创建目录并释放 LeafTab 主程序…");
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(installDir);
                    ExtractEmbeddedPayload(targetExePath);

                    string selfExe = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrWhiteSpace(selfExe) &&
                        File.Exists(selfExe) &&
                        !PathsEqual(selfExe, uninstallExePath))
                    {
                        File.Copy(selfExe, uninstallExePath, overwrite: true);
                    }
                });

                progress?.Invoke(68, "正在配置系统关联与右键菜单…");
                await Task.Run(() =>
                {
                    // Apply each checkbox independently, including silent upgrades.
                    RemoveDisabledAssociations(targetExePath, opts.SetDefaultExplorer, opts.AddContextMenu);

                    if (opts.SetDefaultExplorer || opts.AddContextMenu)
                    {
                        var argList = new StringBuilder();
                        if (opts.SetDefaultExplorer)
                            argList.Append("--register-default ");
                        if (opts.AddContextMenu)
                            argList.Append("--register-context-menu");

                        RunProcessHiddenAndWait(targetExePath, argList.ToString().Trim(), timeoutMs: 8000);
                    }
                });

                progress?.Invoke(86, "正在创建快捷方式与卸载信息…");
                await Task.Run(() =>
                {
                    if (opts.CreateShortcuts)
                    {
                        CreateShortcutAt(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFileName),
                            targetExePath);
                        CreateShortcutAt(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName),
                            targetExePath);
                    }

                    if (opts.RegisterUninstaller)
                    {
                        WriteUninstallRegistry(installDir, targetExePath, uninstallExePath);
                    }
                });

                progress?.Invoke(100, "LeafTab（一叶）v" + AppVersion + " 安装完成！");

                if (opts.LaunchAfterInstall)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = targetExePath,
                            WorkingDirectory = installDir,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                    }
                }
                else if (opts.SetDefaultExplorer)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = targetExePath,
                            Arguments = "--background",
                            WorkingDirectory = installDir,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    catch
                    {
                    }
                }

                TryDeleteFile(targetExePath + ".previous");
                return (true, "安装成功", targetExePath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("install-failed", ex);
                try
                {
                    if (!string.IsNullOrWhiteSpace(targetExePath) && File.Exists(targetExePath + ".previous"))
                        File.Replace(targetExePath + ".previous", targetExePath, null, ignoreMetadataErrors: true);
                }
                catch (Exception restoreError) { DiagnosticLog.Write("install-rollback-failed", restoreError); }
                return (false, ex.Message, targetExePath);
            }
        }

        public static async Task<(bool Ok, string Message)> ExecuteUninstallAsync(
            SetupOptions opts, Action<int, string>? progress = null)
        {
            try
            {
                string installDir = Path.GetFullPath(string.IsNullOrWhiteSpace(opts.InstallDir) ? GetDefaultInstallDirectory() : opts.InstallDir.Trim());
                string targetExePath = Path.Combine(installDir, "LeafTab.exe");
                string uninstallExePath = Path.Combine(installDir, UninstallerExeName);
                string selfExe = Assembly.GetExecutingAssembly().Location;
                string? registeredDirectory = GetRegisteredInstallDirectory();
                bool ownsSharedData = registeredDirectory == null || PathsEqual(registeredDirectory, installDir);

                progress?.Invoke(20, "正在退出此安装位置的程序并恢复关联…");
                await Task.Run(() =>
                {
                    StopRunningLeafTabProcesses(targetExePath, stopAllIfTakingOver: false);
                    FallbackCleanOrphanedShellRegistry(targetExePath);
                });
                progress?.Invoke(55, "正在移除程序文件…");
                await Task.Run(() =>
                {
                    for (int attempt = 0; attempt < 6 && File.Exists(targetExePath); attempt++)
                    {
                        try { File.Delete(targetExePath); }
                        catch (IOException) { Thread.Sleep(250); }
                    }
                    if (File.Exists(targetExePath)) throw new IOException("程序仍被占用，卸载未完成。已保留卸载入口，请关闭程序后重试。");
                });

                progress?.Invoke(80, "正在清理此安装位置的快捷方式和配置…");
                await Task.Run(() =>
                {
                    if (opts.CreateShortcuts)
                    {
                        DeleteOwnedShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFileName), targetExePath);
                        DeleteOwnedShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName), targetExePath);
                    }
                    if (opts.RemoveUserDataOnUninstall) RemoveKnownUserData(installDir, includeSharedData: ownsSharedData);
                    if (opts.RegisterUninstaller && PathsEqual(GetRegisteredInstallDirectory() ?? string.Empty, installDir))
                        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKeyPath, throwOnMissingSubKey: false);
                    TryDeleteFile(targetExePath + ".previous");
                    if (!PathsEqual(selfExe, uninstallExePath))
                    {
                        TryDeleteFile(uninstallExePath);
                        TryRemoveDirectoryIfEmpty(installDir);
                    }
                });
                progress?.Invoke(100, "此安装位置已卸载，其他程序的关联和配置已保留。");
                return (true, "卸载成功");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("uninstall-failed", ex);
                return (false, ex.Message);
            }
        }

        public static void ScheduleSelfDeleteIfRunningAsUninstaller(string installDir)
        {
            try
            {
                string selfExe = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrWhiteSpace(selfExe) || !File.Exists(selfExe))
                    return;

                string fullDir = Path.GetFullPath(installDir);
                string uninstallExePath = Path.Combine(fullDir, UninstallerExeName);
                if (!PathsEqual(selfExe, uninstallExePath))
                    return;

                string cmdArgs = $"/d /c ping 127.0.0.1 -n 2 > nul & del /f /q \"{selfExe}\" > nul 2>&1 & rmdir \"{fullDir}\" > nul 2>&1";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    WorkingDirectory = Path.GetTempPath(),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch
            {
            }
        }

        private static void ExtractEmbeddedPayload(string destinationExePath)
        {
            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
            if (resStream == null)
                throw new InvalidOperationException("安装包内部缺少 LeafTab.Payload.gz 组件资源。");

            string tempPath = destinationExePath + ".installing.tmp";
            using (var gz = new GZipStream(resStream, CompressionMode.Decompress))
            using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                gz.CopyTo(outFs);
                outFs.Flush(flushToDisk: true);
            }

            // Verify valid PE MZ signature
            byte[] header = new byte[2];
            using (var checkFs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (checkFs.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                {
                    TryDeleteFile(tempPath);
                    throw new InvalidDataException("释放的主程序校验失败（无效 PE 文件头）。");
                }
            }

            using (Stream? digestStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LeafTab.Payload.sha256"))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var payload = File.OpenRead(tempPath))
            {
                if (digestStream == null) throw new InvalidDataException("安装包缺少校验信息。");
                using var reader = new StreamReader(digestStream);
                string expected = reader.ReadToEnd().Trim();
                string actual = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException("主程序校验失败，已保留原版本。");
            }
            if (File.Exists(destinationExePath))
                File.Replace(tempPath, destinationExePath, destinationExePath + ".previous", ignoreMetadataErrors: true);
            else
                File.Move(tempPath, destinationExePath);
        }

        private static void StopRunningLeafTabProcesses(string targetExePath, bool stopAllIfTakingOver)
            => StopRunningLeafTabProcesses(targetExePath, stopAllIfTakingOver, Process.GetProcessesByName("LeafTab"));

        internal static void StopRunningLeafTabProcesses(string targetExePath, bool stopAllIfTakingOver, Process[] processes)
        {
            using var currentProcess = Process.GetCurrentProcess();
            int sessionId = currentProcess.SessionId;
            foreach (Process process in processes)
            {
                using (process)
                {
                    string? path;
                    try
                    {
                        if (process.HasExited || process.SessionId != sessionId) continue;
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) { DiagnosticLog.Write("process-inspection-skipped", ex); continue; }
                    // The session-wide primary can be an unregistered portable copy. Run each
                    // copy's own quit client so older mutex/pipe naming schemes also release COM.
                    if (!stopAllIfTakingOver && !ShellAssociationRecovery.PathsEqual(path, targetExePath)) continue;
                    if (process.HasExited) continue;
                    if (File.Exists(path)) RunProcessHiddenAndWait(path!, "--quit", 3000);
                    if (!process.WaitForExit(4000)) throw new IOException("LeafTab 仍在运行。请从菜单彻底退出后重试，安装器没有强制终止进程。");
                }
            }
        }

        private static void WriteUninstallRegistry(string installDir, string targetExePath, string uninstallExePath)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKeyPath);
            key.SetValue("DisplayName", "LeafTab（一叶）多标签资源管理器", RegistryValueKind.String);
            key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
            key.SetValue("Publisher", "LeafTab", RegistryValueKind.String);
            key.SetValue("DisplayIcon", $"\"{targetExePath}\",0", RegistryValueKind.String);
            key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallExePath}\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstallExePath}\" --uninstall --silent", RegistryValueKind.String);
            key.SetValue("URLInfoAbout", "https://github.com/Fahaxiki123/LeafTab", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            long totalBytes = 0;
            if (File.Exists(targetExePath)) totalBytes += new FileInfo(targetExePath).Length;
            if (File.Exists(uninstallExePath)) totalBytes += new FileInfo(uninstallExePath).Length;
            int estimatedKb = Math.Max(256, (int)(totalBytes / 1024));
            key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
        }

        internal static void RemoveDisabledAssociations(string exe, bool defaultExplorer, bool contextMenu,
            string classes = ShellAssociationRecovery.ClassesRoot, string backup = ShellAssociationRecovery.BackupRoot)
        {
            if (!defaultExplorer) ShellAssociationRecovery.RestoreDefault(exe, classes, backup);
            if (!contextMenu) ShellAssociationRecovery.RestoreContextMenu(exe, classes);
        }

        internal static void FallbackCleanOrphanedShellRegistry(string expectedExePath,
            string classesRoot = ShellAssociationRecovery.ClassesRoot, string backupRoot = ShellAssociationRecovery.BackupRoot)
        {
            ShellAssociationRecovery.RestoreDefault(expectedExePath, classesRoot, backupRoot);
            ShellAssociationRecovery.RestoreContextMenu(expectedExePath, classesRoot);
            if (ShellAssociationRecovery.HasReferencesToExecutable(expectedExePath, classesRoot))
                throw new InvalidOperationException("存在经用户修改、仍引用此程序的关联，已保留程序和配置。请先调整该关联再卸载。");
            if (string.Equals(classesRoot, ShellAssociationRecovery.ClassesRoot, StringComparison.OrdinalIgnoreCase))
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }

        internal static void RemoveKnownUserData(string installDir, string? userDirectory = null, bool includeSharedData = true)
        {
            string userDir = userDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafTab");
            foreach (string directory in includeSharedData ? new[] { installDir, userDir } : new[] { installDir })
            {
                string absolute = Path.GetFullPath(directory);
                if (string.Equals(absolute.TrimEnd('\\'), Path.GetPathRoot(absolute)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("拒绝清理磁盘根目录。");
                for (var parent = new DirectoryInfo(absolute); parent != null; parent = parent.Parent)
                    if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("配置目录包含链接，已保留数据。");
                foreach (string name in new[] { "config.json", "config.json.bak", "config.json.tmp", "config.json.migrated.bak" })
                {
                    string file = Path.Combine(absolute, name);
                    if (!File.Exists(file)) continue;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("配置文件是链接，已保留数据。");
                    File.Delete(file);
                }
                TryRemoveDirectoryIfEmpty(absolute);
            }
        }

        private static void DeleteOwnedShortcut(string path, string expectedExe)
        {
            if (!File.Exists(path)) return;
            object? instance = Activator.CreateInstance(Type.GetTypeFromCLSID(s_clsidShellLink)!);
            try
            {
                if (instance is IShellLinkW link && instance is IPersistFile file)
                {
                    file.Load(path, 0);
                    var target = new StringBuilder(32768);
                    link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                    if (PathsEqual(target.ToString(), expectedExe)) File.Delete(path);
                }
            }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
        }

        private static void CreateShortcutAt(string lnkPath, string targetExePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir!);

                Type? shellLinkType = Type.GetTypeFromCLSID(s_clsidShellLink, throwOnError: false);
                if (shellLinkType == null)
                    return;

                object? instance = Activator.CreateInstance(shellLinkType);
                if (instance is IShellLinkW link)
                {
                    try
                    {
                        link.SetPath(targetExePath);
                        link.SetWorkingDirectory(Path.GetDirectoryName(targetExePath) ?? string.Empty);
                        link.SetDescription("LeafTab（一叶）轻量级多标签资源管理器");
                        link.SetIconLocation(targetExePath, 0);

                        if (link is IPersistFile persistFile)
                        {
                            persistFile.Save(lnkPath, fRemember: true);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(link);
                    }
                }
            }
            catch
            {
            }
        }

        public static string? PickInstallFolderDialog(IntPtr ownerHwnd, string currentPath)
        {
            // Try modern Windows Vista/10/11 IFileOpenDialog with FOS_PICKFOLDERS first
            try
            {
                Type? dlgType = Type.GetTypeFromCLSID(s_clsidFileOpenDialog, throwOnError: false);
                if (dlgType != null && Activator.CreateInstance(dlgType) is IFileOpenDialog fileDlg)
                {
                    try
                    {
                        fileDlg.GetOptions(out uint options);
                        // FOS_PICKFOLDERS (0x20) | FOS_FORCEFILESYSTEM (0x40) | FOS_PATHMUSTEXIST (0x800)
                        fileDlg.SetOptions(options | 0x00000020u | 0x00000040u | 0x00000800u);
                        fileDlg.SetTitle("选择 LeafTab 安装位置");

                        string initialDir = currentPath;
                        if (!string.IsNullOrWhiteSpace(initialDir) && !Directory.Exists(initialDir))
                        {
                            initialDir = Path.GetDirectoryName(initialDir) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        }

                        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                        {
                            Guid iidShellItem = typeof(IShellItem).GUID;
                            if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, ref iidShellItem, out IShellItem folderItem) == 0 && folderItem != null)
                            {
                                fileDlg.SetFolder(folderItem);
                                Marshal.ReleaseComObject(folderItem);
                            }
                        }

                        int hr = fileDlg.Show(ownerHwnd);
                        if (hr == 0)
                        {
                            fileDlg.GetResult(out IShellItem resultItem);
                            if (resultItem != null)
                            {
                                try
                                {
                                    resultItem.GetDisplayName(0x80058000u, out string selectedPath); // SIGDN_FILESYSPATH
                                    return NormalizeChosenFolder(selectedPath);
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(resultItem);
                                }
                            }
                        }
                        return null;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(fileDlg);
                    }
                }
            }
            catch
            {
            }

            // Fallback to WinForms FolderBrowserDialog
            using var fallback = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 LeafTab 安装位置",
                SelectedPath = Directory.Exists(currentPath) ? currentPath : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ShowNewFolderButton = true
            };
            if (fallback.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fallback.SelectedPath))
            {
                return NormalizeChosenFolder(fallback.SelectedPath);
            }
            return null;
        }

        private static string NormalizeChosenFolder(string chosenPath)
        {
            string trimmed = chosenPath.Trim().TrimEnd('\\', '/');
            if (trimmed.EndsWith("\\LeafTab", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(trimmed, "LeafTab.exe")))
            {
                return trimmed;
            }
            return Path.Combine(trimmed, "LeafTab");
        }

        private static void RunProcessHiddenAndWait(string fileName, string arguments, int timeoutMs)
        {
            using Process? p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? string.Empty,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) throw new InvalidOperationException("无法启动安装辅助进程。");
            if (!p.WaitForExit(timeoutMs))
                throw new TimeoutException("安装辅助进程响应超时，未继续后续操作。请关闭相关窗口后重试。");
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"安装辅助进程失败（退出码 {p.ExitCode}）。");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void TryRemoveDirectoryIfEmpty(string dirPath)
        {
            try
            {
                if (Directory.Exists(dirPath) &&
                    Directory.GetFiles(dirPath).Length == 0 &&
                    Directory.GetDirectories(dirPath).Length == 0)
                {
                    Directory.Delete(dirPath, recursive: false);
                }
            }
            catch
            {
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            out IShellItem ppv);

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid([In] ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }
    }
}
