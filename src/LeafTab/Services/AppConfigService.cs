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
            try
            {
                if (File.Exists(path))
                {
                    byte[] raw = File.ReadAllBytes(path);
                    if (raw.Length > 0)
                    {
                        var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                        using var ms = new MemoryStream(raw);
                        if (serializer.ReadObject(ms) is AppConfig loaded)
                        {
                            loaded.Bookmarks ??= new List<BookmarkConfigItem>();
                            loaded.OpenTabs ??= new List<SavedTabConfigItem>();
                            EnsureDefaultBookmarksIfNeeded(loaded);
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
            }

            var fresh = new AppConfig();
            EnsureDefaultBookmarksIfNeeded(fresh);
            return fresh;
        }

        public static bool Save(AppConfig config, string? customPath = null)
        {
            string path = customPath ?? ConfigFilePath;
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using var ms = new MemoryStream();
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

                string tempPath = path + ".tmp";
                File.WriteAllBytes(tempPath, ms.ToArray());
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
                return true;
            }
            catch
            {
                return false;
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
