using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Threading;
using System.Security.Cryptography;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Shared;

namespace LeafTab.Services
{
    internal static class AppConfigService
    {
        private static string? _resolvedConfigPath;

        public static string ConfigFilePath => _resolvedConfigPath ??= ResolveConfigFilePath();

        private static string ResolveConfigFilePath()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string userDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafTab");
            try { return ResolveConfigFilePath(directory, userDirectory); }
            catch (Exception ex)
            {
                DiagnosticLog.Write("config-migration-deferred", ex);
                string legacy = Path.Combine(directory, "config.json");
                return TryDeserializeLegacyConfig(legacy) != null || TryDeserializeLegacyConfig(legacy + ".bak") != null
                    ? legacy : Path.Combine(userDirectory, "config.json");
            }
        }

        internal static string ResolveConfigFilePath(string baseDir, string userDirectory)
        {
            string portablePath = Path.Combine(baseDir, "config.json");
            if (File.Exists(Path.Combine(baseDir, ".portable"))) return portablePath;
            string destination = Path.Combine(userDirectory, "config.json");
            if (ShellAssociationRecovery.PathsEqual(portablePath, destination)) return destination;
            using var gate = AcquireConfigGate(destination);
            if (TryDeserializeFile(destination) != null || TryDeserializeFile(destination + ".bak") != null) return destination;
            var legacy = TryDeserializeLegacyConfig(portablePath) ?? TryDeserializeLegacyConfig(portablePath + ".bak");
            if (legacy == null) return destination;
            // A valid destination always wins, including intentionally empty bookmarks.
            // Keep the source intact so an older binary and failed migrations remain recoverable.
            if (Save(legacy, destination) && TryDeserializeFile(destination) != null)
            {
                DiagnosticLog.Write("legacy-config-migrated");
                return destination;
            }
            DiagnosticLog.Write("legacy-config-migration-failed-retaining-source");
            return portablePath;
        }

        private static AppConfig? TryDeserializeLegacyConfig(string path) =>
            LegacyConfigFile.HasLeafTabFormat(path) ? TryDeserializeFile(path) : null;

        internal static bool PrepareInstalledConfig()
        {
            try { return PrepareInstalledConfigCore(); }
            catch (Exception ex) { DiagnosticLog.Write("config-handoff-failed", ex); return false; }
        }

        private static bool PrepareInstalledConfigCore()
        {
            string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafTab", "config.json");
            if (ShellAssociationRecovery.PathsEqual(ConfigFilePath, destination)) return true;
            using var gate = AcquireConfigGate(destination);
            if (TryDeserializeFile(destination) != null || TryDeserializeFile(destination + ".bak") != null) return true;
            return Save(Load(ConfigFilePath), destination) && TryDeserializeFile(destination) != null;
        }

        private static IDisposable AcquireConfigGate(string path)
        {
            using var hash = SHA256.Create();
            string name = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
            var mutex = new Mutex(false, "Local\\LeafTab.Config." + name);
            try
            {
                bool acquired;
                try { acquired = mutex.WaitOne(3000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("配置正在由另一个进程保存，请稍后重试。");
                return new ConfigLease(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        private sealed class ConfigLease : IDisposable
        {
            private readonly Mutex _mutex;
            internal ConfigLease(Mutex mutex) => _mutex = mutex;
            public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
        }

        public static AppConfig Load(string? customPath = null)
        {
            string path = customPath ?? ConfigFilePath;
            string bakPath = path + ".bak";

            IDisposable? gate = null;
            try
            {
                gate = AcquireConfigGate(path);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("config-read-gate-bypassed", ex);
            }

            using (gate)
            {
                AppConfig? primary = TryDeserializeFile(path);
                if (primary != null)
                {
                    EnsureDefaultBookmarksIfNeeded(primary);
                    return primary;
                }

                AppConfig? backup = TryDeserializeFile(bakPath);
                if (backup != null)
                {
                    EnsureDefaultBookmarksIfNeeded(backup);
                    return backup;
                }
            }

            var fresh = new AppConfig();
            EnsureDefaultBookmarksIfNeeded(fresh);
            return fresh;
        }

        private static AppConfig? TryDeserializeFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                byte[] raw = File.ReadAllBytes(filePath);
                if (raw.Length == 0)
                    return null;

                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using var ms = new MemoryStream(raw);
                if (serializer.ReadObject(ms) is AppConfig loaded)
                {
                    loaded.Bookmarks ??= new List<BookmarkConfigItem>();
                    loaded.OpenTabs ??= new List<SavedTabConfigItem>();
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("config-read-failed", ex);
            }

            return null;
        }

        public static bool Save(AppConfig config, string? customPath = null)
        {
            string path = customPath ?? ConfigFilePath;
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string bakPath = path + ".bak";

            try
            {
                using var gate = AcquireConfigGate(path);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                byte[] payload;
                using (var ms = new MemoryStream())
                {
                    using (XmlDictionaryWriter writer = JsonReaderWriterFactory.CreateJsonWriter(
                        ms,
                        Encoding.UTF8,
                        ownsStream: false,
                        indent: true,
                        indentChars: "  "))
                    {
                        serializer.WriteObject(writer, config);
                        writer.Flush();
                    }
                    payload = ms.ToArray();
                }

                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(payload, 0, payload.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    // A recovered backup must survive the first write after a damaged primary.
                    string? replacementBackup = TryDeserializeFile(path) != null ? bakPath : null;
                    try
                    {
                        File.Replace(tempPath, path, replacementBackup, ignoreMetadataErrors: true);
                        return true;
                    }
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 1 || (ex.HResult & 0xffff) == 50)
                    {
                        // Fallback for filesystems that do not support ReplaceFileW (e.g. FAT32/exFAT USB drives)
                        if (replacementBackup != null) File.Copy(path, replacementBackup, overwrite: true);
                        File.Copy(tempPath, path, overwrite: true);
                        File.Delete(tempPath);
                        return true;
                    }
                }

                File.Move(tempPath, path);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("config-write-failed", ex);
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
                return false;
            }
        }

        public static BookmarkItemViewModel CreateBookmarkViewModelFast(string? title, string? targetPath)
        {
            string safePath = targetPath ?? string.Empty;
            string displayTitle = title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayTitle))
            {
                string trimmed = safePath.TrimEnd('\\', '/');
                string leaf = Path.GetFileName(trimmed);
                displayTitle = !string.IsNullOrWhiteSpace(leaf) ? leaf : (string.IsNullOrWhiteSpace(safePath) ? "文件夹" : safePath);
            }

            return new BookmarkItemViewModel
            {
                Title = displayTitle,
                TargetPath = safePath,
                Icon = null
            };
        }

        public static void HydrateBookmarkIcon(BookmarkItemViewModel vm)
        {
            if (vm == null || vm.Icon != null || string.IsNullOrWhiteSpace(vm.TargetPath))
                return;

            IntPtr pidl = PidlManager.TryParseLocalOrShellFast(vm.TargetPath);
            if (pidl == IntPtr.Zero)
                return;

            try
            {
                FolderDisplayInfo info = PidlManager.GetDisplayInfo(pidl);
                if (info.Icon != null)
                {
                    vm.Icon = info.Icon;
                }
            }
            finally
            {
                PidlManager.Free(ref pidl);
            }
        }

        public static BookmarkItemViewModel CreateBookmarkViewModel(string title, string targetPath)
        {
            IntPtr pidl = PidlManager.TryParseLocalOrShellFast(targetPath);
            try
            {
                var info = pidl != IntPtr.Zero ? PidlManager.GetDisplayInfo(pidl) : null;
                return new BookmarkItemViewModel
                {
                    Title = !string.IsNullOrWhiteSpace(title) ? title : (info?.Title ?? "文件夹"),
                    TargetPath = targetPath,
                    Icon = info?.Icon
                };
            }
            finally
            {
                PidlManager.Free(ref pidl);
            }
        }

        private static void EnsureDefaultBookmarksIfNeeded(AppConfig config)
        {
            if (config.BookmarksInitialized)
                return;

            config.BookmarksInitialized = true;
            if (config.Bookmarks.Count > 0)
                return;

            TryAddKnownBookmark(config.Bookmarks, "桌面", NativeMethods.FOLDERID_Desktop);
            TryAddKnownBookmark(config.Bookmarks, "下载", NativeMethods.FOLDERID_Downloads);
            TryAddKnownBookmark(config.Bookmarks, "文档", NativeMethods.FOLDERID_Documents);
            config.Bookmarks.Add(new BookmarkConfigItem { Title = "此电脑", TargetPath = "此电脑" });
        }

        private static void TryAddKnownBookmark(List<BookmarkConfigItem> list, string fallbackTitle, Guid folderId)
        {
            IntPtr pidl = PidlManager.CreateKnownFolderPidl(folderId);
            if (pidl == IntPtr.Zero)
                return;

            try
            {
                FolderDisplayInfo info = PidlManager.GetDisplayInfo(pidl);
                string target = !string.IsNullOrWhiteSpace(info.FileSystemPath)
                    ? info.FileSystemPath!
                    : info.ParsingName;

                if (!string.IsNullOrWhiteSpace(target))
                {
                    list.Add(new BookmarkConfigItem
                    {
                        Title = !string.IsNullOrWhiteSpace(info.Title) ? info.Title : fallbackTitle,
                        TargetPath = target
                    });
                }
            }
            finally
            {
                PidlManager.Free(ref pidl);
            }
        }
    }
}
