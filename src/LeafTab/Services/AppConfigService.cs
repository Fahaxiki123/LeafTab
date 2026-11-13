using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using LeafTab.Interop;
using LeafTab.Models;

namespace LeafTab.Services
{
    internal static class AppConfigService
    {
        private static string? _resolvedConfigPath;

        public static string ConfigFilePath => _resolvedConfigPath ??= ResolveConfigFilePath();

        private static string ResolveConfigFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string portablePath = Path.Combine(baseDir, "config.json");

            try
            {
                if (File.Exists(portablePath))
                    return portablePath;

                // Probe whether the exe directory is writable (portable mode)
                string probe = Path.Combine(baseDir, $".leaftab_write_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return portablePath;
            }
            catch
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LeafTab");
                Directory.CreateDirectory(appData);
                return Path.Combine(appData, "config.json");
            }
        }

        public static AppConfig Load(string? customPath = null)
        {
            string path = customPath ?? ConfigFilePath;
            string bakPath = path + ".bak";

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
            catch
            {
            }

            return null;
        }

        public static bool Save(AppConfig config, string? customPath = null)
        {
            string path = customPath ?? ConfigFilePath;
            string tempPath = path + ".tmp";
            string bakPath = path + ".bak";

            try
            {
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
                    try
                    {
                        File.Replace(tempPath, path, bakPath, ignoreMetadataErrors: true);
                        return true;
                    }
                    catch
                    {
                        // Fallback for filesystems that do not support ReplaceFileW (e.g. FAT32/exFAT USB drives)
                        File.Copy(path, bakPath, overwrite: true);
                        File.Copy(tempPath, path, overwrite: true);
                        File.Delete(tempPath);
                        return true;
                    }
                }

                File.Move(tempPath, path);
                return true;
            }
            catch
            {
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
