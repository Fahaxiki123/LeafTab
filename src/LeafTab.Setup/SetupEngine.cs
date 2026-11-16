using System;
using System.Collections.Generic;
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

    internal sealed class InstallEnvironment
    {
        public string ClassesRoot { get; set; } = ShellAssociationRecovery.ClassesRoot;
        public string BackupRoot { get; set; } = ShellAssociationRecovery.BackupRoot;
        public string UninstallRegistryKeyPath { get; set; } = SetupEngine.UninstallRegistryKeyPath;
        public string ProgramsShortcutDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        public string DesktopShortcutDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public string? SelfExecutablePath { get; set; }
        public bool SkipProcessStop { get; set; }
        public Action<string>? PayloadExtractorOverride { get; set; }
        public Action<string, bool, bool, string, string>? RegisterAssociationsOverride { get; set; }
        public Action<string>? FaultAfterStep { get; set; }
    }

    internal static class SetupEngine
    {
        public const string AppVersion = "0.6.6";
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

        public static Task<(bool Ok, string Message, string InstalledExePath)> ExecuteInstallAsync(
            SetupOptions opts,
            Action<int, string>? progress = null) =>
            ExecuteInstallCoreAsync(opts, new InstallEnvironment(), progress);

        internal static async Task<(bool Ok, string Message, string InstalledExePath)> ExecuteInstallCoreAsync(
            SetupOptions opts,
            InstallEnvironment env,
            Action<int, string>? progress = null)
        {
            string targetExePath = string.Empty;
            string uninstallExePath = string.Empty;
            string targetTempPath = string.Empty;
            string targetBackupPath = string.Empty;
            string uninstallBackupPath = string.Empty;
            var createdDirs = new List<string>();
            bool hadPreviousTargetExe = false;
            bool hadPreviousUninstallExe = false;
            bool createdTargetExe = false;
            bool createdUninstallExe = false;
            bool associationsTouched = false;
            bool shortcutsTouched = false;
            bool uninstallRegistryTouched = false;
            ShortcutSnapshot[]? shortcutSnapshots = null;
            UninstallRegistrySnapshot? uninstallSnapshot = null;
            ShellAssociationSnapshot? associationSnapshot = null;

            try
            {
                if (string.IsNullOrWhiteSpace(opts.InstallDir))
                    return (false, "安装路径不能为空。", string.Empty);

                string installDir = Path.GetFullPath(opts.InstallDir.Trim());
                string txId = Guid.NewGuid().ToString("N");
                targetExePath = Path.Combine(installDir, "LeafTab.exe");
                uninstallExePath = Path.Combine(installDir, UninstallerExeName);
                targetTempPath = $"{targetExePath}.{txId}.installing.tmp";
                targetBackupPath = $"{targetExePath}.{txId}.previous";
                uninstallBackupPath = $"{uninstallExePath}.{txId}.previous";

                createdDirs = CaptureMissingDirectoryChain(installDir);
                hadPreviousTargetExe = File.Exists(targetExePath);
                hadPreviousUninstallExe = File.Exists(uninstallExePath);
                associationSnapshot = CaptureShellAssociationSnapshot(env.ClassesRoot, env.BackupRoot);

                string programsShortcutPath = Path.Combine(env.ProgramsShortcutDirectory, ShortcutFileName);
                string desktopShortcutPath = Path.Combine(env.DesktopShortcutDirectory, ShortcutFileName);
                shortcutSnapshots = new[]
                {
                    CaptureShortcutSnapshot(programsShortcutPath),
                    CaptureShortcutSnapshot(desktopShortcutPath)
                };
                uninstallSnapshot = CaptureUninstallRegistrySnapshot(env.UninstallRegistryKeyPath);

                progress?.Invoke(12, "正在检测并关闭运行中的旧实例…");
                if (!env.SkipProcessStop)
                {
                    await Task.Run(() => StopRunningLeafTabProcesses(targetExePath, stopAllIfTakingOver: opts.SetDefaultExplorer));
                }
                env.FaultAfterStep?.Invoke("stop-processes");

                progress?.Invoke(35, "正在创建目录并释放 LeafTab 主程序…");
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(installDir);
                    if (env.PayloadExtractorOverride != null)
                    {
                        if (hadPreviousTargetExe && File.Exists(targetExePath))
                        {
                            File.Copy(targetExePath, targetBackupPath, overwrite: true);
                        }

                        env.PayloadExtractorOverride(targetExePath);
                    }
                    else
                    {
                        ExtractEmbeddedPayloadToTarget(targetExePath, targetTempPath, targetBackupPath);
                    }

                    createdTargetExe = !hadPreviousTargetExe && File.Exists(targetExePath);

                    string selfExe = env.SelfExecutablePath ?? Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrWhiteSpace(selfExe) &&
                        File.Exists(selfExe) &&
                        !PathsEqual(selfExe, uninstallExePath))
                    {
                        if (hadPreviousUninstallExe && File.Exists(uninstallExePath))
                        {
                            File.Copy(uninstallExePath, uninstallBackupPath, overwrite: true);
                        }

                        File.Copy(selfExe, uninstallExePath, overwrite: true);
                        createdUninstallExe = !hadPreviousUninstallExe && File.Exists(uninstallExePath);
                    }
                });
                env.FaultAfterStep?.Invoke("extract-files");

                progress?.Invoke(68, "正在配置系统关联与右键菜单…");
                associationsTouched = true;
                await Task.Run(() =>
                {
                    // Apply each checkbox independently, including silent upgrades.
                    RemoveDisabledAssociations(targetExePath, opts.SetDefaultExplorer, opts.AddContextMenu, env.ClassesRoot, env.BackupRoot);

                    if (opts.SetDefaultExplorer || opts.AddContextMenu)
                    {
                        RegisterAssociationsForInstall(targetExePath, opts.SetDefaultExplorer, opts.AddContextMenu, env);
                    }
                });
                env.FaultAfterStep?.Invoke("associations");

                progress?.Invoke(86, "正在创建快捷方式与卸载信息…");
                if (opts.CreateShortcuts)
                {
                    shortcutsTouched = true;
                    await Task.Run(() =>
                    {
                        CreateShortcutAt(programsShortcutPath, targetExePath);
                        CreateShortcutAt(desktopShortcutPath, targetExePath);
                    });
                }
                env.FaultAfterStep?.Invoke("shortcuts");

                if (opts.RegisterUninstaller)
                {
                    uninstallRegistryTouched = true;
                    await Task.Run(() =>
                    {
                        WriteUninstallRegistry(installDir, targetExePath, uninstallExePath, env.UninstallRegistryKeyPath);
                    });
                }
                env.FaultAfterStep?.Invoke("uninstall-registry");

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
                else if (opts.SetDefaultExplorer &&
                         string.Equals(env.ClassesRoot, ShellAssociationRecovery.ClassesRoot, StringComparison.OrdinalIgnoreCase))
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

                TryDeleteFile(targetTempPath);
                TryDeleteFile(targetBackupPath);
                TryDeleteFile(uninstallBackupPath);
                TryDeleteFile(targetExePath + ".previous");
                TryDeleteFile(uninstallExePath + ".previous");
                return (true, "安装成功", targetExePath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("install-failed", ex);
                RollbackFailedInstall(
                    targetExePath,
                    uninstallExePath,
                    targetTempPath,
                    targetBackupPath,
                    uninstallBackupPath,
                    createdDirs,
                    hadPreviousTargetExe,
                    hadPreviousUninstallExe,
                    createdTargetExe,
                    createdUninstallExe,
                    associationsTouched,
                    shortcutsTouched,
                    uninstallRegistryTouched,
                    associationSnapshot,
                    shortcutSnapshots,
                    uninstallSnapshot,
                    env);
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

        private static void ExtractEmbeddedPayload(string destinationExePath) =>
            ExtractEmbeddedPayloadToTarget(
                destinationExePath,
                destinationExePath + ".installing.tmp",
                destinationExePath + ".previous");

        private static void ExtractEmbeddedPayloadToTarget(
            string destinationExePath,
            string tempPath,
            string backupPath)
        {
            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
            if (resStream == null)
                throw new InvalidOperationException("安装包内部缺少 LeafTab.Payload.gz 组件资源。");

            try
            {
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
                    File.Replace(tempPath, destinationExePath, backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, destinationExePath);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
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

        private const string WinExplorerClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
        private const string AutoRunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoRunValueName = "LeafTab";

        private sealed class ShortcutSnapshot
        {
            public string Path { get; }
            public bool ExistedBefore { get; }
            public byte[]? PriorBytes { get; }

            public ShortcutSnapshot(string path, bool existedBefore, byte[]? priorBytes)
            {
                Path = path;
                ExistedBefore = existedBefore;
                PriorBytes = priorBytes;
            }
        }

        private sealed class UninstallRegistrySnapshot
        {
            public bool Existed { get; }
            public List<(string Name, object Value, RegistryValueKind Kind)> Values { get; }

            public UninstallRegistrySnapshot(bool existed, List<(string Name, object Value, RegistryValueKind Kind)> values)
            {
                Existed = existed;
                Values = values;
            }
        }

        private sealed class RegistryTreeNodeSnapshot
        {
            public bool Existed { get; }
            public List<(string Name, object Value, RegistryValueKind Kind)> Values { get; }
            public Dictionary<string, RegistryTreeNodeSnapshot> Children { get; }

            public RegistryTreeNodeSnapshot(
                bool existed,
                List<(string Name, object Value, RegistryValueKind Kind)> values,
                Dictionary<string, RegistryTreeNodeSnapshot> children)
            {
                Existed = existed;
                Values = values;
                Children = children;
            }
        }

        private sealed class ShallowRegistryKeySnapshot
        {
            public string KeyPath { get; }
            public bool Existed { get; }
            public List<(string Name, object Value, RegistryValueKind Kind)> Values { get; }

            public ShallowRegistryKeySnapshot(
                string keyPath,
                bool existed,
                List<(string Name, object Value, RegistryValueKind Kind)> values)
            {
                KeyPath = keyPath;
                Existed = existed;
                Values = values;
            }
        }

        private sealed class ShellAssociationSnapshot
        {
            public bool IsRealClassesRoot { get; }
            public List<(string KeyPath, RegistryTreeNodeSnapshot Node)> ManagedSubtrees { get; }
            public List<ShallowRegistryKeySnapshot> ShallowContainersDeepestFirst { get; }
            public bool AutoRunHadValue { get; }
            public object? AutoRunPriorValue { get; }
            public RegistryValueKind AutoRunPriorKind { get; }

            public ShellAssociationSnapshot(
                bool isRealClassesRoot,
                List<(string KeyPath, RegistryTreeNodeSnapshot Node)> managedSubtrees,
                List<ShallowRegistryKeySnapshot> shallowContainersDeepestFirst,
                bool autoRunHadValue,
                object? autoRunPriorValue,
                RegistryValueKind autoRunPriorKind)
            {
                IsRealClassesRoot = isRealClassesRoot;
                ManagedSubtrees = managedSubtrees;
                ShallowContainersDeepestFirst = shallowContainersDeepestFirst;
                AutoRunHadValue = autoRunHadValue;
                AutoRunPriorValue = autoRunPriorValue;
                AutoRunPriorKind = autoRunPriorKind;
            }
        }

        private static ShellAssociationSnapshot CaptureShellAssociationSnapshot(string classesRoot, string backupRoot)
        {
            bool isRealClasses = string.Equals(classesRoot, ShellAssociationRecovery.ClassesRoot, StringComparison.OrdinalIgnoreCase);

            string[] subtreePaths =
            {
                classesRoot + @"\Directory\shell\open",
                classesRoot + @"\Directory\shell\LeafTabOpen",
                classesRoot + @"\Directory\shell\OpenInLeafTab",
                classesRoot + @"\Directory\Background\shell\OpenInLeafTab",
                classesRoot + @"\Drive\shell\open",
                classesRoot + @"\Drive\shell\LeafTabOpen",
                classesRoot + @"\Drive\shell\OpenInLeafTab",
                classesRoot + @"\Folder\shell\open",
                classesRoot + @"\Folder\shell\explore",
                classesRoot + @"\Folder\shell\opennewwindow",
                classesRoot + @"\Folder\shell\LeafTabOpen",
                classesRoot + @"\CLSID\" + ShellAssociationRecovery.ExecuteClsid,
                classesRoot + @"\CLSID\" + ShellAssociationRecovery.NewWindowClsid,
                classesRoot + @"\CLSID\" + WinExplorerClsid,
                backupRoot
            };

            var managedSubtrees = new List<(string KeyPath, RegistryTreeNodeSnapshot Node)>(subtreePaths.Length);
            var seenSubtrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in subtreePaths)
            {
                if (!string.IsNullOrWhiteSpace(path) && seenSubtrees.Add(path.TrimEnd('\\')))
                {
                    managedSubtrees.Add((path, CaptureRegistryTreeNode(path)));
                }
            }

            var containerPaths = new List<string>
            {
                classesRoot + @"\Directory\Background\shell",
                classesRoot + @"\Directory\Background",
                classesRoot + @"\Directory\shell",
                classesRoot + @"\Directory",
                classesRoot + @"\Drive\shell",
                classesRoot + @"\Drive",
                classesRoot + @"\Folder\shell",
                classesRoot + @"\Folder",
                classesRoot + @"\CLSID"
            };

            int lastSep = backupRoot.TrimEnd('\\').LastIndexOf('\\');
            if (lastSep > 0)
            {
                string backupParent = backupRoot.Substring(0, lastSep);
                if (!string.Equals(backupParent, "Software", StringComparison.OrdinalIgnoreCase))
                {
                    containerPaths.Add(backupParent);
                }
            }

            var shallowContainers = new List<ShallowRegistryKeySnapshot>(containerPaths.Count);
            var seenContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string containerPath in containerPaths)
            {
                if (!string.IsNullOrWhiteSpace(containerPath) && seenContainers.Add(containerPath.TrimEnd('\\')))
                {
                    shallowContainers.Add(CaptureShallowRegistryKey(containerPath));
                }
            }

            bool autoRunHadVal = false;
            object? autoRunVal = null;
            RegistryValueKind autoRunKind = RegistryValueKind.String;
            if (isRealClasses)
            {
                try
                {
                    using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(AutoRunRegistryKeyPath);
                    if (runKey != null)
                    {
                        autoRunVal = runKey.GetValue(AutoRunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (autoRunVal != null)
                        {
                            autoRunHadVal = true;
                            autoRunKind = runKey.GetValueKind(AutoRunValueName);
                        }
                    }
                }
                catch
                {
                }
            }

            return new ShellAssociationSnapshot(
                isRealClasses,
                managedSubtrees,
                shallowContainers,
                autoRunHadVal,
                autoRunVal,
                autoRunKind);
        }

        private static RegistryTreeNodeSnapshot CaptureRegistryTreeNode(string keyPath)
        {
            var values = new List<(string Name, object Value, RegistryValueKind Kind)>();
            var children = new Dictionary<string, RegistryTreeNodeSnapshot>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key == null)
                    return new RegistryTreeNodeSnapshot(existed: false, values, children);

                foreach (string name in key.GetValueNames())
                {
                    object? val = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (val != null)
                    {
                        values.Add((name, val, key.GetValueKind(name)));
                    }
                }

                foreach (string subName in key.GetSubKeyNames())
                {
                    children[subName] = CaptureRegistryTreeNode(keyPath + "\\" + subName);
                }

                return new RegistryTreeNodeSnapshot(existed: true, values, children);
            }
            catch
            {
                return new RegistryTreeNodeSnapshot(existed: false, values, children);
            }
        }

        private static ShallowRegistryKeySnapshot CaptureShallowRegistryKey(string keyPath)
        {
            var values = new List<(string Name, object Value, RegistryValueKind Kind)>();
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key == null)
                    return new ShallowRegistryKeySnapshot(keyPath, existed: false, values);

                foreach (string name in key.GetValueNames())
                {
                    object? val = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (val != null)
                    {
                        values.Add((name, val, key.GetValueKind(name)));
                    }
                }

                return new ShallowRegistryKeySnapshot(keyPath, existed: true, values);
            }
            catch
            {
                return new ShallowRegistryKeySnapshot(keyPath, existed: false, values);
            }
        }

        private static void RestoreShellAssociationSnapshot(ShellAssociationSnapshot snapshot)
        {
            foreach (var (keyPath, node) in snapshot.ManagedSubtrees)
            {
                RestoreRegistryTreeNode(keyPath, node);
            }

            foreach (ShallowRegistryKeySnapshot container in snapshot.ShallowContainersDeepestFirst)
            {
                RestoreShallowRegistryKey(container);
            }

            if (snapshot.IsRealClassesRoot)
            {
                try
                {
                    using RegistryKey? runKey = Registry.CurrentUser.CreateSubKey(AutoRunRegistryKeyPath);
                    if (runKey != null)
                    {
                        if (snapshot.AutoRunHadValue && snapshot.AutoRunPriorValue != null)
                            runKey.SetValue(AutoRunValueName, snapshot.AutoRunPriorValue, snapshot.AutoRunPriorKind);
                        else
                            runKey.DeleteValue(AutoRunValueName, throwOnMissingValue: false);
                    }
                }
                catch
                {
                }
            }
        }

        private static void RestoreRegistryTreeNode(string keyPath, RegistryTreeNodeSnapshot node)
        {
            if (!node.Existed)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                return;
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
            var expectedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in node.Values)
            {
                expectedValues.Add(item.Name);
                key.SetValue(item.Name, item.Value, item.Kind);
            }

            foreach (string existingVal in key.GetValueNames())
            {
                if (!expectedValues.Contains(existingVal))
                {
                    key.DeleteValue(existingVal, throwOnMissingValue: false);
                }
            }

            foreach (string existingSub in key.GetSubKeyNames())
            {
                if (!node.Children.ContainsKey(existingSub))
                {
                    key.DeleteSubKeyTree(existingSub, throwOnMissingSubKey: false);
                }
            }

            foreach (KeyValuePair<string, RegistryTreeNodeSnapshot> kv in node.Children)
            {
                RestoreRegistryTreeNode(keyPath + "\\" + kv.Key, kv.Value);
            }
        }

        private static void RestoreShallowRegistryKey(ShallowRegistryKeySnapshot container)
        {
            if (container.Existed)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(container.KeyPath);
                var expectedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in container.Values)
                {
                    expectedValues.Add(item.Name);
                    key.SetValue(item.Name, item.Value, item.Kind);
                }

                foreach (string existingVal in key.GetValueNames())
                {
                    if (!expectedValues.Contains(existingVal))
                    {
                        key.DeleteValue(existingVal, throwOnMissingValue: false);
                    }
                }
                return;
            }

            bool isEmptyAfterClear = false;
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(container.KeyPath, writable: true))
            {
                if (key == null)
                    return;

                foreach (string existingVal in key.GetValueNames())
                {
                    key.DeleteValue(existingVal, throwOnMissingValue: false);
                }

                isEmptyAfterClear = key.ValueCount == 0 && key.SubKeyCount == 0;
            }

            if (isEmptyAfterClear)
            {
                try { Registry.CurrentUser.DeleteSubKey(container.KeyPath, throwOnMissingSubKey: false); }
                catch { }
            }
        }

        private static List<string> CaptureMissingDirectoryChain(string installDir)
        {
            var missing = new List<string>();
            try
            {
                for (var dir = new DirectoryInfo(installDir); dir != null && !dir.Exists; dir = dir.Parent)
                {
                    if (dir.Parent == null)
                        break;
                    missing.Add(dir.FullName);
                }
            }
            catch
            {
            }
            return missing;
        }

        private static ShortcutSnapshot CaptureShortcutSnapshot(string shortcutPath)
        {
            try
            {
                if (File.Exists(shortcutPath))
                {
                    return new ShortcutSnapshot(shortcutPath, existedBefore: true, File.ReadAllBytes(shortcutPath));
                }
            }
            catch
            {
            }
            return new ShortcutSnapshot(shortcutPath, existedBefore: false, priorBytes: null);
        }

        private static UninstallRegistrySnapshot CaptureUninstallRegistrySnapshot(string registryKeyPath)
        {
            var values = new List<(string Name, object Value, RegistryValueKind Kind)>();
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(registryKeyPath);
                if (key == null)
                    return new UninstallRegistrySnapshot(existed: false, values);

                foreach (string name in key.GetValueNames())
                {
                    object? val = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (val != null)
                    {
                        values.Add((name, val, key.GetValueKind(name)));
                    }
                }
                return new UninstallRegistrySnapshot(existed: true, values);
            }
            catch
            {
                return new UninstallRegistrySnapshot(existed: false, values);
            }
        }

        private static void RegisterAssociationsForInstall(
            string targetExePath,
            bool enableDefault,
            bool enableContextMenu,
            InstallEnvironment env)
        {
            if (env.RegisterAssociationsOverride != null)
            {
                env.RegisterAssociationsOverride(targetExePath, enableDefault, enableContextMenu, env.ClassesRoot, env.BackupRoot);
                return;
            }

            var argList = new StringBuilder();
            if (enableDefault)
                argList.Append("--register-default ");
            if (enableContextMenu)
                argList.Append("--register-context-menu");

            RunProcessHiddenAndWait(targetExePath, argList.ToString().Trim(), timeoutMs: 8000);
        }

        private static bool FilesHaveIdenticalBytes(string pathA, string pathB)
        {
            try
            {
                using var fsA = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var fsB = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fsA.Length != fsB.Length)
                    return false;

                byte[] bufA = new byte[8192];
                byte[] bufB = new byte[8192];
                while (true)
                {
                    int readA = fsA.Read(bufA, 0, bufA.Length);
                    if (readA == 0)
                        return true;

                    int offsetB = 0;
                    while (offsetB < readA)
                    {
                        int n = fsB.Read(bufB, offsetB, readA - offsetB);
                        if (n <= 0)
                            return false;
                        offsetB += n;
                    }

                    for (int i = 0; i < readA; i++)
                    {
                        if (bufA[i] != bufB[i])
                            return false;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RestoreTransactionBinary(
            string targetPath,
            string transactionTempPath,
            string transactionBackupPath,
            bool hadPrevious,
            bool createdDuringInstall)
        {
            if (!string.IsNullOrWhiteSpace(transactionTempPath))
                TryDeleteFile(transactionTempPath);
            TryDeleteFile(targetPath + ".installing.tmp");

            if (!string.IsNullOrWhiteSpace(transactionBackupPath) && File.Exists(transactionBackupPath))
            {
                if (File.Exists(targetPath))
                {
                    if (FilesHaveIdenticalBytes(targetPath, transactionBackupPath))
                        TryDeleteFile(transactionBackupPath);
                    else
                        File.Replace(transactionBackupPath, targetPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(transactionBackupPath, targetPath);
                }
            }
            else if (!hadPrevious && createdDuringInstall)
            {
                TryDeleteFile(targetPath);
            }
        }

        private static void RollbackFailedInstall(
            string targetExePath,
            string uninstallExePath,
            string targetTempPath,
            string targetBackupPath,
            string uninstallBackupPath,
            List<string> createdDirs,
            bool hadPreviousTargetExe,
            bool hadPreviousUninstallExe,
            bool createdTargetExe,
            bool createdUninstallExe,
            bool associationsTouched,
            bool shortcutsTouched,
            bool uninstallRegistryTouched,
            ShellAssociationSnapshot? associationSnapshot,
            ShortcutSnapshot[]? shortcutSnapshots,
            UninstallRegistrySnapshot? uninstallSnapshot,
            InstallEnvironment env)
        {
            // 1. Roll back Uninstall registry entry
            if (uninstallRegistryTouched && uninstallSnapshot != null)
            {
                try
                {
                    if (!uninstallSnapshot.Existed)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(env.UninstallRegistryKeyPath, throwOnMissingSubKey: false);
                    }
                    else
                    {
                        using RegistryKey key = Registry.CurrentUser.CreateSubKey(env.UninstallRegistryKeyPath);
                        var priorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in uninstallSnapshot.Values)
                        {
                            priorNames.Add(item.Name);
                            key.SetValue(item.Name, item.Value, item.Kind);
                        }
                        foreach (string existing in key.GetValueNames())
                        {
                            if (!priorNames.Contains(existing))
                            {
                                key.DeleteValue(existing, throwOnMissingValue: false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("install-rollback-uninstall-registry-failed", ex);
                }
            }

            // 2. Roll back Start Menu & Desktop shortcuts
            if (shortcutsTouched && shortcutSnapshots != null && !string.IsNullOrWhiteSpace(targetExePath))
            {
                foreach (ShortcutSnapshot snap in shortcutSnapshots)
                {
                    try
                    {
                        if (snap.ExistedBefore && snap.PriorBytes != null)
                        {
                            string? dir = Path.GetDirectoryName(snap.Path);
                            if (!string.IsNullOrWhiteSpace(dir))
                                Directory.CreateDirectory(dir!);
                            File.WriteAllBytes(snap.Path, snap.PriorBytes);
                        }
                        else if (!snap.ExistedBefore)
                        {
                            DeleteOwnedShortcut(snap.Path, targetExePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write("install-rollback-shortcut-failed", ex);
                    }
                }
            }

            // 3. Restore transaction-bound binaries only (never read stale .previous files from prior runs)
            if (!string.IsNullOrWhiteSpace(targetExePath))
            {
                try
                {
                    RestoreTransactionBinary(targetExePath, targetTempPath, targetBackupPath, hadPreviousTargetExe, createdTargetExe);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("install-rollback-exe-failed", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(uninstallExePath))
            {
                try
                {
                    RestoreTransactionBinary(uninstallExePath, string.Empty, uninstallBackupPath, hadPreviousUninstallExe, createdUninstallExe);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("install-rollback-uninstaller-failed", ex);
                }
            }

            // 4. Roll back Shell associations to the exact pre-install snapshot (preserving directory A on A -> B failure)
            if (associationsTouched && associationSnapshot != null)
            {
                try
                {
                    RestoreShellAssociationSnapshot(associationSnapshot);

                    if (associationSnapshot.IsRealClassesRoot)
                    {
                        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("install-rollback-associations-failed", ex);
                }
            }

            // 5. Remove newly created empty installation directories (deepest first)
            foreach (string dir in createdDirs)
            {
                TryRemoveDirectoryIfEmpty(dir);
            }
        }

        private static void WriteUninstallRegistry(
            string installDir,
            string targetExePath,
            string uninstallExePath,
            string registryKeyPath = UninstallRegistryKeyPath)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(registryKeyPath);
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
