using System;
using System.IO;
using Microsoft.Win32;

namespace LeafTab.Shared
{
    // Shared by the application and the uninstaller, including when LeafTab.exe is missing.
    internal static class ShellAssociationRecovery
    {
        internal const string ClassesRoot = @"Software\Classes";
        internal const string BackupRoot = @"Software\LeafTab\RegistryBackup";
        internal const string ExecuteClsid = "{8E4C7A29-3D51-4F82-9B14-6A5E20C91D73}";
        internal const string NewWindowClsid = "{8E4C7A29-3D51-4F82-9B14-6A5E20C91D74}";
        private const string ExplorerClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

        internal static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal static string? CommandExecutable(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string value = command!.Trim();
            if (value[0] != '"') return null;
            int end = value.IndexOf('"', 1);
            return end > 1 ? value.Substring(1, end - 1) : null;
        }

        internal static bool IsExpectedCommand(string? command, string exe, params string[] arguments)
        {
            string? actualExe = CommandExecutable(command);
            if (!PathsEqual(actualExe, exe)) return false;
            string suffix = command!.Trim().Substring(actualExe!.Length + 2).Trim();
            foreach (string argument in arguments)
                if (string.Equals(suffix, argument, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool OwnsCommand(string path, string exe, string? clsid, params string[] arguments)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key == null || !IsExpectedCommand(key.GetValue("") as string, exe, arguments)) return false;
            string? actual = key.GetValue("DelegateExecute") as string;
            return clsid == null ? string.IsNullOrEmpty(actual) : string.Equals(actual, clsid, StringComparison.OrdinalIgnoreCase);
        }

        internal static void RestoreContextMenu(string exe, string classes = ClassesRoot)
        {
            foreach (string parent in new[] { @"Directory\shell", @"Directory\Background\shell", @"Drive\shell" })
            {
                string verb = classes + "\\" + parent + @"\OpenInLeafTab";
                if (!OwnsCommand(verb + @"\command", exe, null, "\"%V \"")) continue;
                ClearOwnedCommand(verb + @"\command");
                RemoveManagedVerbValues(verb);
                DeleteIfEmpty(verb);
            }
        }

        internal static bool CanRepairDefault(string classes, string exe, string? movedExe = null)
        {
            foreach (var item in new[]
            {
                (@"Directory\shell\LeafTabOpen\command", ExecuteClsid, "\"%V \""),
                (@"Drive\shell\LeafTabOpen\command", ExecuteClsid, "\"%V \""),
                (@"Folder\shell\LeafTabOpen\command", ExecuteClsid, "\"%V \""),
                (@"Directory\shell\open\command", ExecuteClsid, "\"%V \""),
                (@"Drive\shell\open\command", ExecuteClsid, "\"%V \""),
                (@"Folder\shell\open\command", ExecuteClsid, "\"%V \""),
                (@"Folder\shell\explore\command", ExecuteClsid, "\"%V \""),
                (@"Folder\shell\opennewwindow\command", NewWindowClsid, "--new-window \"%V \""),
                (@"CLSID\" + ExplorerClsid + @"\shell\open\command", ExecuteClsid, "\"此电脑\""),
                (@"CLSID\" + ExplorerClsid + @"\shell\opennewwindow\command", NewWindowClsid, "--new-window \"此电脑\"")
            })
            {
                string path = classes + "\\" + item.Item1;
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key != null && !OwnsCommand(path, exe, item.Item2, item.Item3) &&
                    (movedExe == null || !OwnsCommand(path, movedExe, item.Item2, item.Item3))) return false;
            }
            foreach (string clsid in new[] { ExecuteClsid, NewWindowClsid })
            {
                string path = classes + @"\CLSID\" + clsid + @"\LocalServer32";
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue("") != null && !OwnsCommand(path, exe, null, "--com-server") &&
                    (movedExe == null || !OwnsCommand(path, movedExe, null, "--com-server"))) return false;
            }
            return true;
        }

        internal static bool HasReferencesToExecutable(string exe, string classes = ClassesRoot)
        {
            foreach (string parent in new[] { @"Directory\shell", @"Drive\shell", @"Folder\shell", @"Directory\Background\shell", @"CLSID\" + ExplorerClsid + @"\shell" })
                foreach (string verb in new[] { "open", "explore", "opennewwindow", "LeafTabOpen", "OpenInLeafTab" })
                {
                    using var key = Registry.CurrentUser.OpenSubKey(classes + "\\" + parent + "\\" + verb + @"\command");
                    if (PathsEqual(CommandExecutable(key?.GetValue("") as string), exe)) return true;
                }
            foreach (string clsid in new[] { ExecuteClsid, NewWindowClsid })
            {
                using var key = Registry.CurrentUser.OpenSubKey(classes + @"\CLSID\" + clsid + @"\LocalServer32");
                if (PathsEqual(CommandExecutable(key?.GetValue("") as string), exe)) return true;
            }
            return false;
        }

        private static bool HasDelegateReferences(string exe, string classes, string clsid)
        {
            foreach (string parent in new[] { @"Directory\shell", @"Drive\shell", @"Folder\shell", @"CLSID\" + ExplorerClsid + @"\shell" })
                foreach (string verb in new[] { "open", "explore", "opennewwindow", "LeafTabOpen" })
                {
                    using var key = Registry.CurrentUser.OpenSubKey(classes + "\\" + parent + "\\" + verb + @"\command");
                    if (string.Equals(key?.GetValue("DelegateExecute") as string, clsid, StringComparison.OrdinalIgnoreCase) &&
                        PathsEqual(CommandExecutable(key?.GetValue("") as string), exe)) return true;
                }
            return false;
        }

        internal static void RestoreDefault(string exe, string classes = ClassesRoot, string backup = BackupRoot)
        {
            RestoreVerb(classes + @"\Directory\shell", "open", backup + @"\DirectoryOpenCmd", exe, ExecuteClsid, "\"%V \"");
            RestoreVerb(classes + @"\Drive\shell", "open", backup + @"\DriveOpenCmd", exe, ExecuteClsid, "\"%V \"");
            RestoreVerb(classes + @"\Folder\shell", "open", backup + @"\FolderOpenCmd", exe, ExecuteClsid, "\"%V \"");
            RestoreVerb(classes + @"\Folder\shell", "explore", backup + @"\FolderExploreCmd", exe, ExecuteClsid, "\"%V \"");
            RestoreVerb(classes + @"\Folder\shell", "opennewwindow", backup + @"\FolderNewWinCmd", exe, NewWindowClsid, "--new-window \"%V \"");

            RestoreDefaultVerb(classes + @"\Directory\shell", backup + @"\DirectoryShell", exe);
            RestoreDefaultVerb(classes + @"\Drive\shell", backup + @"\DriveShell", exe);
            RestoreDefaultVerb(classes + @"\Folder\shell", backup + @"\FolderShell", exe);

            string explorer = classes + @"\CLSID\" + ExplorerClsid;
            string explorerBackup = backup + @"\WinExplorerClsid";
            bool open = RestoreCommand(explorer + @"\shell\open\command", explorerBackup, "Open", exe, ExecuteClsid, "\"此电脑\"");
            bool newWindow = RestoreCommand(explorer + @"\shell\opennewwindow\command", explorerBackup, "NewWin", exe, NewWindowClsid, "--new-window \"此电脑\"");
            foreach (string verb in new[] { "open", "opennewwindow" }) DeleteIfEmpty(explorer + @"\shell\" + verb);
            DeleteIfEmpty(explorer + @"\shell");
            if (open && newWindow)
            {
                using (var key = Registry.CurrentUser.OpenSubKey(explorer, true)) key?.DeleteValue("LeafTabManaged", false);
                // v0.6.0 records this description on a newly-created override.
                using (var bak = Registry.CurrentUser.OpenSubKey(explorerBackup))
                using (var key = Registry.CurrentUser.OpenSubKey(explorer, true))
                {
                    if (bak != null && Convert.ToInt32(bak.GetValue("KeyExisted", 0)) == 0 && key != null)
                    {
                        string? value = key.GetValue("") as string;
                        if (value != null && value.StartsWith("LeafTab", StringComparison.Ordinal)) key.DeleteValue("", false);
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(explorerBackup, false);
                DeleteIfEmpty(explorer);
            }

            foreach (string clsid in new[] { ExecuteClsid, NewWindowClsid })
            {
                string clsidPath = classes + @"\CLSID\" + clsid;
                string server = clsidPath + @"\LocalServer32";
                if (!OwnsCommand(server, exe, null, "--com-server")) continue;
                // Keep the server alive when an edited verb still references this installation.
                if (HasDelegateReferences(exe, classes, clsid)) continue;
                ClearOwnedCommand(server);
                using (var key = Registry.CurrentUser.OpenSubKey(clsidPath, true))
                {
                    key?.DeleteValue("LeafTabManaged", false);
                    string? description = key?.GetValue("") as string;
                    if (description == "LeafTab Shell Execute Command" || description == "LeafTab Open New Window Execute Command") key!.DeleteValue("", false);
                }
                DeleteIfEmpty(clsidPath);
            }

            if (string.Equals(classes, ClassesRoot, StringComparison.OrdinalIgnoreCase))
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (IsExpectedCommand(run?.GetValue("LeafTab") as string, exe, "--background")) run!.DeleteValue("LeafTab", false);
            }
            DeleteIfEmpty(backup);
        }

        private static void RestoreVerb(string shell, string verb, string backup, string exe, string clsid, string arguments)
        {
            if (!RestoreCommand(shell + "\\" + verb + @"\command", backup, "Cmd", exe, clsid, arguments)) return;
            DeleteIfEmpty(shell + "\\" + verb);
            Registry.CurrentUser.DeleteSubKeyTree(backup, false);
            DeleteIfEmpty(shell);
        }

        private static bool RestoreCommand(string path, string backup, string prefix, string exe, string clsid, string arguments)
        {
            using (var current = Registry.CurrentUser.OpenSubKey(path))
                if (current == null) return false; // Absence does not establish ownership of a backup.
            if (!OwnsCommand(path, exe, clsid, arguments)) return false;
            using var bak = Registry.CurrentUser.OpenSubKey(backup);
            using (var current = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (current == null) return true;
                if (bak != null && Convert.ToInt32(bak.GetValue("Captured", 0)) == 1 &&
                    Convert.ToInt32(bak.GetValue(prefix + "_Existed", 0)) == 1)
                {
                    RestoreValue(current, bak, "", prefix + "_HadDef", prefix + "_Def");
                    RestoreValue(current, bak, "DelegateExecute", prefix + "_HadDel", prefix + "_Del");
                }
                else
                {
                    current.DeleteValue("", false);
                    current.DeleteValue("DelegateExecute", false);
                }
                current.DeleteValue("LeafTabManaged", false);
            }
            DeleteIfEmpty(path);
            return true;
        }

        private static void RestoreValue(RegistryKey current, RegistryKey backup, string name, string hadName, string savedName)
        {
            if (Convert.ToInt32(backup.GetValue(hadName, 0)) == 1)
            {
                var kind = (RegistryValueKind)Convert.ToInt32(backup.GetValue(savedName + "Kind", (int)RegistryValueKind.String));
                current.SetValue(name, backup.GetValue(savedName, "", RegistryValueOptions.DoNotExpandEnvironmentNames), kind);
            }
            else current.DeleteValue(name, false);
        }

        private static void RestoreDefaultVerb(string shell, string backup, string exe)
        {
            string verb = shell + @"\LeafTabOpen";
            if (!OwnsCommand(verb + @"\command", exe, ExecuteClsid, "\"%V \"")) return;
            bool restored = false;
            using (var key = Registry.CurrentUser.OpenSubKey(shell, true))
            using (var bak = Registry.CurrentUser.OpenSubKey(backup))
            {
                if (string.Equals(key?.GetValue("") as string, "LeafTabOpen", StringComparison.Ordinal))
                {
                    if (bak != null && Convert.ToInt32(bak.GetValue("HadDefault", 0)) == 1)
                        key!.SetValue("", bak.GetValue("PriorDefault", ""), (RegistryValueKind)Convert.ToInt32(bak.GetValue("PriorDefaultKind", (int)RegistryValueKind.String)));
                    else key!.DeleteValue("", false);
                    restored = true;
                }
                key?.DeleteValue("LeafTabDefaultOwner", false);
            }
            ClearOwnedCommand(verb + @"\command");
            RemoveManagedVerbValues(verb);
            DeleteIfEmpty(verb);
            DeleteIfEmpty(shell);
            if (restored) Registry.CurrentUser.DeleteSubKeyTree(backup, false);
        }

        private static void ClearOwnedCommand(string path)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                key?.DeleteValue("", false);
                key?.DeleteValue("DelegateExecute", false);
                key?.DeleteValue("LeafTabManaged", false);
            }
            DeleteIfEmpty(path);
        }

        private static void RemoveManagedVerbValues(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, true);
            if (key == null) return;
            foreach (string name in new[] { "", "MUIVerb", "Icon", "MultiSelectModel", "LeafTabManaged" }) key.DeleteValue(name, false);
        }

        private static void DeleteIfEmpty(string path)
        {
            bool empty;
            using (var key = Registry.CurrentUser.OpenSubKey(path)) empty = key != null && key.ValueCount == 0 && key.SubKeyCount == 0;
            if (empty) Registry.CurrentUser.DeleteSubKey(path, false);
        }
    }
}
