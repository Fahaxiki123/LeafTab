using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using LeafTab.Interop;

namespace LeafTab.Services
{
    internal sealed class AddressSuggestionItem
    {
        public string InsertText { get; set; } = string.Empty;
        public string DisplayTitle { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public bool IsCommand { get; set; }
    }

    internal static class QuickLaunchAndInstallService
    {
        private static readonly Guid s_clsidShellLink = new Guid("00021401-0000-0000-C000-000000000046");

        // ==================== 1. TERMINAL & DEVELOPER QUICK LAUNCH ====================

        public static bool IsAddressBarToolCommand(string? rawInput, out string cmdName, out string extraArgs)
        {
            cmdName = string.Empty;
            extraArgs = string.Empty;

            if (string.IsNullOrWhiteSpace(rawInput))
                return false;

            string trimmed = rawInput!.Trim();

            // Never treat rooted paths or UNC paths as tool commands
            if (trimmed.IndexOf('\\') >= 0 || trimmed.IndexOf('/') >= 0 || trimmed.IndexOf(':') >= 0)
                return false;

            string head;
            string rest = string.Empty;
            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                head = trimmed.Substring(0, spaceIdx).Trim();
                rest = trimmed.Substring(spaceIdx + 1).Trim();
            }
            else
            {
                head = trimmed;
            }

            if (string.Equals(head, "cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(head, "cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                cmdName = "cmd";
                extraArgs = rest;
                return true;
            }

            if (string.Equals(head, "powershell", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(head, "powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                cmdName = "powershell";
                extraArgs = rest;
                return true;
            }

            if (string.Equals(head, "pwsh", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(head, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                cmdName = "pwsh";
                extraArgs = rest;
                return true;
            }

            if (string.Equals(head, "wt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(head, "wt.exe", StringComparison.OrdinalIgnoreCase))
            {
                cmdName = "wt";
                extraArgs = rest;
                return true;
            }

            if (string.Equals(head, "code", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(rest) || string.Equals(rest, ".", StringComparison.Ordinal)))
            {
                cmdName = "code";
                extraArgs = ".";
                return true;
            }

            return false;
        }

        public static bool TryExecuteAddressToolCommand(string? rawInput, string? currentFolderPath, out string statusMessage)
        {
            statusMessage = string.Empty;
            if (!IsAddressBarToolCommand(rawInput, out string cmdName, out string extraArgs))
                return false;

            string workDir = ResolveSafeWorkingDirectory(currentFolderPath);

            try
            {
                switch (cmdName)
                {
                    case "wt":
                        if (TryStartWindowsTerminal(workDir, extraArgs))
                        {
                            statusMessage = $"已在当前目录打开 Windows Terminal：{workDir}";
                            return true;
                        }
                        StartShellProcess("powershell.exe", extraArgs, workDir);
                        statusMessage = $"未检测到 wt.exe，已回退打开 PowerShell：{workDir}";
                        return true;

                    case "powershell":
                        StartShellProcess("powershell.exe", extraArgs, workDir);
                        statusMessage = $"已在当前目录打开 PowerShell：{workDir}";
                        return true;

                    case "pwsh":
                        try
                        {
                            StartShellProcess("pwsh.exe", extraArgs, workDir);
                            statusMessage = $"已在当前目录打开 PowerShell 7：{workDir}";
                        }
                        catch
                        {
                            StartShellProcess("powershell.exe", extraArgs, workDir);
                            statusMessage = $"未检测到 pwsh.exe，已打开 Windows PowerShell：{workDir}";
                        }
                        return true;

                    case "cmd":
                        StartShellProcess("cmd.exe", extraArgs, workDir);
                        statusMessage = $"已在当前目录打开命令提示符 (CMD)：{workDir}";
                        return true;

                    case "code":
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c code \"{workDir}\"",
                            WorkingDirectory = workDir,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        statusMessage = $"已请求在 VS Code 中打开：{workDir}";
                        return true;
                }
            }
            catch (Exception ex)
            {
                statusMessage = $"启动命令行工具失败：{ex.Message}";
                return true;
            }

            return false;
        }

        public static bool OpenPreferredTerminal(string? currentFolderPath, out string statusMessage)
        {
            string workDir = ResolveSafeWorkingDirectory(currentFolderPath);
            try
            {
                if (TryStartWindowsTerminal(workDir, string.Empty))
                {
                    statusMessage = $"已在当前目录打开 Windows Terminal：{workDir}";
                    return true;
                }

                StartShellProcess("powershell.exe", string.Empty, workDir);
                statusMessage = $"已在当前目录打开 PowerShell：{workDir}";
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = $"打开终端失败：{ex.Message}";
                return false;
            }
        }

        private static string ResolveSafeWorkingDirectory(string? candidatePath)
        {
            if (!string.IsNullOrWhiteSpace(candidatePath) && Directory.Exists(candidatePath))
                return candidatePath!;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(profile) ? profile : Environment.CurrentDirectory;
        }

        private static bool TryStartWindowsTerminal(string workDir, string extraArgs)
        {
            string wtAppAlias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");

            if (!File.Exists(wtAppAlias))
                return false;

            string safeDir = workDir.EndsWith("\\", StringComparison.Ordinal)
                ? workDir + "\\"
                : workDir;
            string args = string.IsNullOrWhiteSpace(extraArgs)
                ? $"-d \"{safeDir}\""
                : $"-d \"{safeDir}\" {extraArgs}";

            Process.Start(new ProcessStartInfo
            {
                FileName = wtAppAlias,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = true
            });
            return true;
        }

        private static void StartShellProcess(string exeName, string args, string workDir)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exeName,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = true
            });
        }

        // ==================== 2. ADDRESS BAR AUTOCOMPLETE & RECENT HISTORY ====================

        public static List<AddressSuggestionItem> BuildAddressSuggestions(
            string? rawInput,
            bool showDefaultHistory,
            IReadOnlyList<string> recentFolders,
            IReadOnlyList<string> bookmarkPaths,
            int maxResults = 12)
        {
            var results = new List<AddressSuggestionItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string input = (rawInput ?? string.Empty).Trim();

            if (showDefaultHistory || string.IsNullOrEmpty(input))
            {
                foreach (string recent in recentFolders)
                {
                    if (results.Count >= maxResults - 2)
                        break;
                    AddFolderSuggestionIfUnique(results, seen, recent, "最近访问");
                }

                foreach (string bm in bookmarkPaths)
                {
                    if (results.Count >= maxResults - 1)
                        break;
                    AddFolderSuggestionIfUnique(results, seen, bm, "收藏书签");
                }

                results.Add(new AddressSuggestionItem
                {
                    InsertText = "wt",
                    DisplayTitle = "wt / powershell / cmd",
                    Subtitle = "在地址栏直接输入并回车，可在当前目录打开终端",
                    IsCommand = true
                });
                return results;
            }

            // 1. Check terminal quick command prefix
            if (input.IndexOf('\\') < 0 && input.IndexOf('/') < 0 && input.IndexOf(':') < 0)
            {
                (string Cmd, string Desc)[] toolCmds =
                {
                    ("wt", "在当前目录打开 Windows Terminal"),
                    ("powershell", "在当前目录打开 PowerShell"),
                    ("cmd", "在当前目录打开命令提示符 (CMD)"),
                    ("code .", "在当前目录打开 VS Code")
                };

                foreach (var tc in toolCmds)
                {
                    if (tc.Cmd.StartsWith(input, StringComparison.OrdinalIgnoreCase) && seen.Add("CMD:" + tc.Cmd))
                    {
                        results.Add(new AddressSuggestionItem
                        {
                            InsertText = tc.Cmd,
                            DisplayTitle = tc.Cmd,
                            Subtitle = tc.Desc,
                            IsCommand = true
                        });
                    }
                }
            }

            // 2. Fast local filesystem subdirectory completion (never block on UNC \\ network paths while typing)
            string normalizedInput = input.Replace('/', '\\');
            if (normalizedInput.Length == 2 && char.IsLetter(normalizedInput[0]) && normalizedInput[1] == ':')
            {
                normalizedInput += "\\";
            }

            if (normalizedInput.Length >= 3 &&
                char.IsLetter(normalizedInput[0]) &&
                normalizedInput[1] == ':' &&
                normalizedInput[2] == '\\')
            {
                string parentDir;
                string leafPrefix;

                if (normalizedInput.EndsWith("\\", StringComparison.Ordinal))
                {
                    parentDir = normalizedInput;
                    leafPrefix = string.Empty;
                }
                else
                {
                    int lastSlash = normalizedInput.LastIndexOf('\\');
                    parentDir = normalizedInput.Substring(0, lastSlash + 1);
                    leafPrefix = normalizedInput.Substring(lastSlash + 1);
                }

                if (Directory.Exists(parentDir))
                {
                    var prefixMatches = new List<string>();
                    var containsMatches = new List<string>();
                    try
                    {
                        int scanned = 0;
                        foreach (string subDir in Directory.EnumerateDirectories(parentDir))
                        {
                            if (++scanned > 400)
                                break;

                            string folderName = Path.GetFileName(subDir);
                            if (string.IsNullOrEmpty(folderName))
                                continue;

                            // Hide dot/system folders unless user explicitly typed '.'
                            if (folderName[0] == '.' && (leafPrefix.Length == 0 || leafPrefix[0] != '.'))
                                continue;

                            try
                            {
                                FileAttributes attr = File.GetAttributes(subDir);
                                if ((attr & FileAttributes.System) != 0)
                                    continue;
                                if ((attr & FileAttributes.Hidden) != 0 && string.IsNullOrEmpty(leafPrefix))
                                    continue;
                            }
                            catch
                            {
                                continue;
                            }

                            if (string.IsNullOrEmpty(leafPrefix) ||
                                folderName.StartsWith(leafPrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                prefixMatches.Add(subDir);
                            }
                            else if (folderName.IndexOf(leafPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                containsMatches.Add(subDir);
                            }
                        }
                    }
                    catch
                    {
                    }

                    prefixMatches.Sort(StringComparer.OrdinalIgnoreCase);
                    containsMatches.Sort(StringComparer.OrdinalIgnoreCase);

                    foreach (string match in prefixMatches)
                    {
                        if (results.Count >= maxResults) break;
                        AddFolderSuggestionIfUnique(results, seen, match, "子文件夹");
                    }
                    foreach (string match in containsMatches)
                    {
                        if (results.Count >= maxResults) break;
                        AddFolderSuggestionIfUnique(results, seen, match, "子文件夹");
                    }
                }
            }

            // 3. Match against recent folders & bookmarks
            foreach (string recent in recentFolders)
            {
                if (results.Count >= maxResults) break;
                if (recent.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AddFolderSuggestionIfUnique(results, seen, recent, "最近访问");
                }
            }

            foreach (string bm in bookmarkPaths)
            {
                if (results.Count >= maxResults) break;
                if (bm.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AddFolderSuggestionIfUnique(results, seen, bm, "收藏书签");
                }
            }

            return results;
        }

        private static void AddFolderSuggestionIfUnique(
            List<AddressSuggestionItem> list,
            HashSet<string> seen,
            string path,
            string categoryLabel)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string trimmed = path.Trim();
            if (!seen.Add(trimmed))
                return;

            string leaf = Path.GetFileName(trimmed.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(leaf))
                leaf = trimmed;

            list.Add(new AddressSuggestionItem
            {
                InsertText = trimmed,
                DisplayTitle = leaf,
                Subtitle = $"{categoryLabel}  ·  {trimmed}",
                IsCommand = false
            });
        }

        // ==================== 3. VOLATILE FOLDER GUARD & SELF-INSTALLER ====================

        public static string GetRecommendedInstallDirectory() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "LeafTab");

        public static string GetRecommendedInstallExePath() =>
            Path.Combine(GetRecommendedInstallDirectory(), "LeafTab.exe");

        public static bool IsVolatileLaunchLocation(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(exePath!);
            }
            catch
            {
                return false;
            }

            // Never treat the recommended fixed install directory as volatile
            string fixedDir = EnsureTrailingSlash(GetRecommendedInstallDirectory());
            if (fullPath.StartsWith(fixedDir, StringComparison.OrdinalIgnoreCase))
                return false;

            var volatileRoots = new List<string>();

            TryAddDirectoryPrefix(volatileRoots, Path.GetTempPath());
            TryAddDirectoryPrefix(volatileRoots, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            TryAddDirectoryPrefix(volatileRoots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

            string? downloadsKnown = TryGetKnownDownloadsFolder();
            if (!string.IsNullOrWhiteSpace(downloadsKnown))
            {
                TryAddDirectoryPrefix(volatileRoots, downloadsKnown!);
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                TryAddDirectoryPrefix(volatileRoots, Path.Combine(userProfile, "Downloads"));
            }

            foreach (string root in volatileRoots)
            {
                if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Catch archive-manager temp extraction folders anywhere
            if (fullPath.IndexOf("\\Temp\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        public static bool InstallToFixedLocation(
            string sourceExePath,
            out string installedExePath,
            out string? errorMessage)
        {
            installedExePath = GetRecommendedInstallExePath();
            errorMessage = null;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceExePath) || !File.Exists(sourceExePath))
                {
                    errorMessage = "未找到当前 LeafTab.exe 源文件。";
                    return false;
                }

                string targetDir = GetRecommendedInstallDirectory();
                Directory.CreateDirectory(targetDir);

                if (!string.Equals(
                    Path.GetFullPath(sourceExePath),
                    Path.GetFullPath(installedExePath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceExePath, installedExePath, overwrite: true);
                }

                TryCreateStartMenuShortcut(installedExePath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static void TryCreateStartMenuShortcut(string targetExePath)
        {
            try
            {
                string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrWhiteSpace(programsDir))
                    return;

                Directory.CreateDirectory(programsDir);
                string lnkPath = Path.Combine(programsDir, "LeafTab（一叶）.lnk");

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

        private static string? TryGetKnownDownloadsFolder()
        {
            Guid rfid = NativeMethods.FOLDERID_Downloads;
            if (NativeMethods.SHGetKnownFolderPath(ref rfid, 0, IntPtr.Zero, out IntPtr pszPath) == 0 &&
                pszPath != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStringUni(pszPath);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pszPath);
                }
            }
            return null;
        }

        private static void TryAddDirectoryPrefix(List<string> list, string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            try
            {
                string full = EnsureTrailingSlash(Path.GetFullPath(dir!));
                list.Add(full);
            }
            catch
            {
            }
        }

        private static string EnsureTrailingSlash(string path) =>
            path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";
    }
}
