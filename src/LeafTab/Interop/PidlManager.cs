using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LeafTab.Interop
{
    internal sealed class FolderDisplayInfo
    {
        public string Title { get; set; } = "文件夹";
        public string AddressText { get; set; } = string.Empty;
        public string ParsingName { get; set; } = string.Empty;
        public string? FileSystemPath { get; set; }
        public ImageSource? Icon { get; set; }
    }

    internal sealed class BreadcrumbNode
    {
        public string Title { get; set; } = string.Empty;
        public string ParsingName { get; set; } = string.Empty;
        public byte[] PidlBytes { get; set; } = Array.Empty<byte>();
        public ImageSource? Icon { get; set; }
    }

    internal static class PidlManager
    {
        private static readonly Guid BHID_SFObject = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");
        private static readonly object s_iconCacheLock = new object();
        private static readonly Dictionary<string, ImageSource> s_smallIconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (ImageSource? Icon, string TypeName)> s_largeTypeCache =
            new Dictionary<string, (ImageSource? Icon, string TypeName)>(StringComparer.OrdinalIgnoreCase);

        public static IntPtr CreateKnownFolderPidl(Guid knownFolderId)
        {
            Guid copy = knownFolderId;
            int hr = NativeMethods.SHGetKnownFolderIDList(ref copy, 0, IntPtr.Zero, out IntPtr pidl);
            return hr == ShellConstants.S_OK ? pidl : IntPtr.Zero;
        }

        public static IntPtr CreateThisPcPidl() =>
            CreateKnownFolderPidl(NativeMethods.FOLDERID_ComputerFolder);

        public static IntPtr CreateDesktopPidl() =>
            CreateKnownFolderPidl(NativeMethods.FOLDERID_Desktop);

        public static IntPtr Clone(IntPtr pidl) =>
            pidl == IntPtr.Zero ? IntPtr.Zero : NativeMethods.ILClone(pidl);

        public static IntPtr Combine(IntPtr parentPidl, IntPtr childPidl)
        {
            if (parentPidl == IntPtr.Zero || childPidl == IntPtr.Zero)
                return IntPtr.Zero;
            return NativeMethods.ILCombine(parentPidl, childPidl);
        }

        public static void Free(ref IntPtr pidl)
        {
            if (pidl != IntPtr.Zero)
            {
                NativeMethods.ILFree(pidl);
                pidl = IntPtr.Zero;
            }
        }

        public static bool AreEqual(IntPtr a, IntPtr b)
        {
            if (a == b) return true;
            if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
            return NativeMethods.ILIsEqual(a, b);
        }

        /// <summary>
        /// Copies an unmanaged PIDL into a GC-managed byte[] so UI models (breadcrumbs, history, closed tabs)
        /// never hold unmanaged CoTaskMem pointers.
        /// </summary>
        public static byte[] ToBytes(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return Array.Empty<byte>();

            int offset = 0;
            const int MaxSafePidlBytes = 65536;
            while (offset < MaxSafePidlBytes)
            {
                ushort cb = (ushort)Marshal.ReadInt16(pidl, offset);
                if (cb == 0)
                {
                    int total = offset + 2;
                    byte[] data = new byte[total];
                    Marshal.Copy(pidl, data, 0, total);
                    return data;
                }
                if (cb < 2)
                {
                    return Array.Empty<byte>();
                }
                offset += cb;
            }

            return Array.Empty<byte>();
        }

        public static IntPtr FromBytes(byte[]? data)
        {
            if (!IsValidPidlBytes(data))
                return IntPtr.Zero;

            IntPtr temp = Marshal.AllocHGlobal(data!.Length);
            try
            {
                Marshal.Copy(data, 0, temp, data.Length);
                return NativeMethods.ILClone(temp);
            }
            finally
            {
                Marshal.FreeHGlobal(temp);
            }
        }

        internal static bool IsValidPidlBytes(byte[]? data)
        {
            if (data == null || data.Length < 2 || data.Length > 65536) return false;
            int offset = 0;
            while (offset + 2 <= data.Length)
            {
                int size = data[offset] | (data[offset + 1] << 8);
                if (size == 0) return offset + 2 == data.Length;
                if (size < 2 || size > data.Length - offset - 2) return false;
                offset += size;
            }
            return false;
        }

        public static bool CanNavigateUp(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return false;

            // Empty root IDList (Desktop namespace root) has cb == 0 at offset 0
            ushort firstCb = (ushort)Marshal.ReadInt16(pidl, 0);
            return firstCb >= 2;
        }

        public static string? GetShellItemString(IShellItem item, SIGDN sigdn)
        {
            int hr = item.GetDisplayName(sigdn, out IntPtr ptr);
            if (hr != ShellConstants.S_OK || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }

        public static FolderDisplayInfo GetDisplayInfo(IntPtr pidl)
        {
            var info = new FolderDisplayInfo();
            if (pidl == IntPtr.Zero)
                return info;

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(pidl, ref iidShellItem, out IShellItem item) == ShellConstants.S_OK && item != null)
            {
                try
                {
                    string? title = GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                    string? fsPath = GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                    string? editPath = GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEEDITING);
                    string? parsing = GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);

                    if (!string.IsNullOrWhiteSpace(title))
                        info.Title = title!;

                    info.FileSystemPath = !string.IsNullOrWhiteSpace(fsPath) ? fsPath : null;

                    info.AddressText = !string.IsNullOrWhiteSpace(fsPath)
                        ? fsPath!
                        : (!string.IsNullOrWhiteSpace(editPath) ? editPath! : info.Title);

                    info.ParsingName = !string.IsNullOrWhiteSpace(parsing) ? parsing! : info.AddressText;
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            if (!string.IsNullOrWhiteSpace(info.ParsingName))
            {
                lock (s_iconCacheLock)
                {
                    if (s_smallIconCache.TryGetValue(info.ParsingName, out ImageSource? cached))
                    {
                        info.Icon = cached;
                        return info;
                    }
                }
            }

            info.Icon = ExtractSmallIcon(pidl);
            if (info.Icon != null && !string.IsNullOrWhiteSpace(info.ParsingName))
            {
                CacheIcon(info.ParsingName, info.Icon);
            }
            return info;
        }

        private static void CacheIcon(string key, ImageSource icon)
        {
            lock (s_iconCacheLock)
            {
                if (s_smallIconCache.Count >= 256)
                {
                    s_smallIconCache.Clear();
                }
                s_smallIconCache[key] = icon;
            }
        }

        private static ImageSource? GetOrExtractSmallIcon(IntPtr pidl, string? cacheKey)
        {
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                lock (s_iconCacheLock)
                {
                    if (s_smallIconCache.TryGetValue(cacheKey!, out ImageSource? cached))
                        return cached;
                }
            }

            ImageSource? extracted = ExtractSmallIcon(pidl);
            if (extracted != null && !string.IsNullOrWhiteSpace(cacheKey))
            {
                CacheIcon(cacheKey!, extracted);
            }
            return extracted;
        }

        public static ImageSource? ExtractTypeSmallIcon(string dummyNameOrExt, bool isDirectory)
        {
            string cacheKey = isDirectory ? "__DIR_ICON__" : $"__EXT_{dummyNameOrExt}";
            lock (s_iconCacheLock)
            {
                if (s_smallIconCache.TryGetValue(cacheKey, out ImageSource? cached))
                    return cached;
            }

            var shfi = new SHFILEINFOW();
            uint attrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
            IntPtr res = NativeMethods.SHGetFileInfoByNameW(
                isDirectory ? "folder" : dummyNameOrExt,
                attrs,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                NativeMethods.SHGFI_USEFILEATTRIBUTES | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON);

            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                CacheIcon(cacheKey, bmp);
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                NativeMethods.User32DestroyIcon(shfi.hIcon);
            }
        }

        public static (ImageSource? Icon, string TypeName) GetLargeIconAndTypeName(string fullPathOrName, bool isDirectory)
        {
            string input = fullPathOrName ?? string.Empty;
            string ext = string.Empty;
            if (!isDirectory && !string.IsNullOrWhiteSpace(input))
            {
                try
                {
                    ext = Path.GetExtension(input);
                }
                catch
                {
                    ext = string.Empty;
                }
            }

            bool isDriveRoot = isDirectory &&
                               input.Length >= 2 &&
                               input[1] == ':' &&
                               (input.Length == 2 || (input.Length == 3 && (input[2] == '\\' || input[2] == '/')));

            bool perInstanceIcon = !isDirectory &&
                                   (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".cur", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".ani", StringComparison.OrdinalIgnoreCase)) &&
                                   File.Exists(input);

            string cacheKey;
            if (isDriveRoot)
            {
                string driveNorm = input.Length == 2 ? input + "\\" : input.Substring(0, 2) + "\\";
                cacheKey = "__DRV_LARGE_" + driveNorm.ToUpperInvariant();
                input = driveNorm;
            }
            else if (perInstanceIcon)
            {
                cacheKey = "__INST_LARGE_" + input;
            }
            else if (isDirectory)
            {
                cacheKey = "__DIR_LARGE__";
            }
            else
            {
                cacheKey = string.IsNullOrEmpty(ext)
                    ? "__EXT_LARGE_NONE__"
                    : "__EXT_LARGE_" + ext.ToUpperInvariant();
            }

            lock (s_iconCacheLock)
            {
                if (s_largeTypeCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            var shfi = new SHFILEINFOW();
            uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_TYPENAME;
            string queryPath;
            uint attrs = 0;

            if (isDriveRoot || perInstanceIcon)
            {
                queryPath = input;
            }
            else
            {
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
                attrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
                queryPath = isDirectory
                    ? "folder"
                    : (string.IsNullOrEmpty(ext) ? "file" : "file" + ext);
            }

            IntPtr res = NativeMethods.SHGetFileInfoByNameW(
                queryPath,
                attrs,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                flags);

            ImageSource? icon = null;
            try
            {
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    icon = bmp;
                }
            }
            catch
            {
                icon = null;
            }
            finally
            {
                if (shfi.hIcon != IntPtr.Zero)
                {
                    NativeMethods.User32DestroyIcon(shfi.hIcon);
                }
            }

            string fallbackType = isDirectory
                ? "文件夹"
                : (string.IsNullOrEmpty(ext)
                    ? "文件"
                    : $"{ext.TrimStart('.').ToUpperInvariant()} 文件");

            string typeName = (res != IntPtr.Zero && !string.IsNullOrWhiteSpace(shfi.szTypeName))
                ? shfi.szTypeName.Trim()
                : fallbackType;

            var entry = (icon, typeName);
            lock (s_iconCacheLock)
            {
                if (s_largeTypeCache.Count >= 128)
                {
                    s_largeTypeCache.Clear();
                }
                s_largeTypeCache[cacheKey] = entry;
            }

            return entry;
        }

        /// <summary>
        /// Builds the root-to-leaf breadcrumb node chain matching Windows 11 File Explorer
        /// (e.g. [此电脑] > [新加卷 (D:)] > [project] > [trunk] > [Client]).
        /// </summary>
        public static List<BreadcrumbNode> BuildBreadcrumbChain(IntPtr folderPidl)
        {
            var reversed = new List<(BreadcrumbNode Node, IntPtr RawPidl)>();
            if (folderPidl == IntPtr.Zero)
                return new List<BreadcrumbNode>();

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(folderPidl, ref iidShellItem, out IShellItem? current) != ShellConstants.S_OK || current == null)
                return new List<BreadcrumbNode>();

            try
            {
                for (int depth = 0; depth < 32 && current != null; depth++)
                {
                    if (NativeMethods.SHGetIDListFromObject(current, out IntPtr itemPidl) == ShellConstants.S_OK && itemPidl != IntPtr.Zero)
                    {
                        string title = GetShellItemString(current, SIGDN.SIGDN_NORMALDISPLAY) ?? "文件夹";
                        string parsing = GetShellItemString(current, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING) ?? title;

                        reversed.Add((new BreadcrumbNode
                        {
                            Title = title,
                            ParsingName = parsing,
                            PidlBytes = ToBytes(itemPidl),
                            Icon = null
                        }, itemPidl));
                    }

                    int hrParent = current.GetParent(out IShellItem? parent);
                    Marshal.ReleaseComObject(current);
                    current = (hrParent == ShellConstants.S_OK) ? parent : null;
                }
                current = null;

                reversed.Reverse();

                // Match Win11 Explorer: omit top-level "桌面" when browsing under "此电脑" / "回收站" / "网络" / "Linux"
                int startIndex = 0;
                if (reversed.Count >= 2)
                {
                    ushort rootCb = (ushort)Marshal.ReadInt16(reversed[0].RawPidl, 0);
                    if (rootCb == 0)
                    {
                        startIndex = 1;
                    }
                }

                var result = new List<BreadcrumbNode>(reversed.Count - startIndex);
                for (int i = startIndex; i < reversed.Count; i++)
                {
                    if (i == startIndex && reversed[i].Node.Icon == null)
                    {
                        reversed[i].Node.Icon = GetOrExtractSmallIcon(reversed[i].RawPidl, reversed[i].Node.ParsingName);
                    }
                    result.Add(reversed[i].Node);
                }

                return result;
            }
            finally
            {
                if (current != null)
                    Marshal.ReleaseComObject(current);

                for (int i = 0; i < reversed.Count; i++)
                {
                    IntPtr p = reversed[i].RawPidl;
                    Free(ref p);
                }
            }
        }

        /// <summary>
        /// Enumerates immediate child folders for a breadcrumb chevron '>' dropdown popup.
        /// </summary>
        public static List<BreadcrumbNode> EnumerateChildFolders(IntPtr parentPidl, int maxCount = 80)
        {
            var children = new List<BreadcrumbNode>();
            if (parentPidl == IntPtr.Zero)
                return children;

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(parentPidl, ref iidShellItem, out IShellItem? parentItem) != ShellConstants.S_OK || parentItem == null)
                return children;

            IntPtr pFolder = IntPtr.Zero;
            IShellFolder? shellFolder = null;
            IntPtr pEnum = IntPtr.Zero;
            IEnumIDList? enumIdList = null;

            try
            {
                Guid bhid = BHID_SFObject;
                Guid iidFolder = ShellConstants.IID_IShellFolder;
                if (parentItem.BindToHandler(IntPtr.Zero, ref bhid, ref iidFolder, out pFolder) != ShellConstants.S_OK || pFolder == IntPtr.Zero)
                    return children;

                shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
                (bool _, bool showHidden) = GetShellVisibilityFlags();
                uint flags = ShellConstants.SHCONTF_FOLDERS | (showHidden ? ShellConstants.SHCONTF_INCLUDEHIDDEN : 0u);

                if (shellFolder.EnumObjects(IntPtr.Zero, flags, out pEnum) != ShellConstants.S_OK || pEnum == IntPtr.Zero)
                    return children;

                enumIdList = (IEnumIDList)Marshal.GetObjectForIUnknown(pEnum);
                while (children.Count < maxCount && enumIdList.Next(1, out IntPtr childRelPidl, out uint fetched) == ShellConstants.S_OK && fetched == 1 && childRelPidl != IntPtr.Zero)
                {
                    IntPtr fullChildPidl = IntPtr.Zero;
                    try
                    {
                        if (NativeMethods.SHCreateItemWithParent(parentPidl, shellFolder, childRelPidl, ref iidShellItem, out IShellItem? childItem) == ShellConstants.S_OK && childItem != null)
                        {
                            try
                            {
                                uint attrMask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
                                if (childItem.GetAttributes(attrMask, out uint attrs) >= 0 &&
                                    (attrs & ShellConstants.SFGAO_FOLDER) != 0 &&
                                    (attrs & ShellConstants.SFGAO_STREAM) == 0)
                                {
                                    fullChildPidl = Combine(parentPidl, childRelPidl);
                                    if (fullChildPidl != IntPtr.Zero)
                                    {
                                        string title = GetShellItemString(childItem, SIGDN.SIGDN_NORMALDISPLAY) ?? "文件夹";
                                        string parsing = GetShellItemString(childItem, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING) ?? title;
                                        children.Add(new BreadcrumbNode
                                        {
                                            Title = title,
                                            ParsingName = parsing,
                                            PidlBytes = ToBytes(fullChildPidl),
                                            Icon = GetOrExtractSmallIcon(fullChildPidl, parsing)
                                        });
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(childItem);
                            }
                        }
                    }
                    finally
                    {
                        Free(ref fullChildPidl);
                        Free(ref childRelPidl);
                    }
                }

                children.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
                return children;
            }
            catch
            {
                return children;
            }
            finally
            {
                if (enumIdList != null) Marshal.ReleaseComObject(enumIdList);
                if (pEnum != IntPtr.Zero) Marshal.Release(pEnum);
                if (shellFolder != null) Marshal.ReleaseComObject(shellFolder);
                if (pFolder != IntPtr.Zero) Marshal.Release(pFolder);
                Marshal.ReleaseComObject(parentItem);
            }
        }

        public static (bool ShowExtensions, bool ShowHidden) GetShellVisibilityFlags()
        {
            var state = new SHELLSTATE();
            NativeMethods.SHGetSetSettings(
                ref state,
                NativeMethods.SSF_SHOWEXTENSIONS | NativeMethods.SSF_SHOWALLOBJECTS,
                false);
            return (state.ShowExtensions, state.ShowAllObjects);
        }

        public static void SetShellVisibilityFlags(bool? showExtensions, bool? showHidden)
        {
            var state = new SHELLSTATE();
            uint mask = 0;

            if (showExtensions.HasValue)
            {
                state.ShowExtensions = showExtensions.Value;
                mask |= NativeMethods.SSF_SHOWEXTENSIONS;
            }

            if (showHidden.HasValue)
            {
                state.ShowAllObjects = showHidden.Value;
                mask |= NativeMethods.SSF_SHOWALLOBJECTS;
            }

            if (mask != 0)
            {
                NativeMethods.SHGetSetSettings(ref state, mask, true);
                // This changes ShellState, not file associations or the global icon cache.
                _ = Task.Run(() => NativeMethods.SendMessageTimeoutW(new IntPtr(0xffff), 0x001A,
                    IntPtr.Zero, "ShellState", 0x0002, 100, out _));
            }
        }

        public static string FormatByteSize(ulong bytes)
        {
            if (bytes < 1024UL)
                return $"{bytes} 字节";
            double kb = bytes / 1024.0;
            if (kb < 1024.0)
                return kb >= 100 ? $"{kb:F0} KB" : $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024.0)
                return mb >= 100 ? $"{mb:F0} MB" : $"{mb:F2} MB";
            double gb = mb / 1024.0;
            if (gb < 1024.0)
                return $"{gb:F2} GB";
            double tb = gb / 1024.0;
            return $"{tb:F2} TB";
        }

        public static ImageSource? ExtractSmallIcon(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return null;

            var shfi = new SHFILEINFOW();
            IntPtr res = NativeMethods.SHGetFileInfoW(
                pidl,
                0,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON);

            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                NativeMethods.User32DestroyIcon(shfi.hIcon);
            }
        }

        public static bool IsExplorerLauncherTarget(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string s = raw!.Trim().Trim('"').Trim();
            if (s.Length == 0)
                return false;

            // Windows 10/11 File Explorer launcher stub CLSID (invoked by Taskbar/Start Menu File Explorer shortcut & Win+E)
            if (s.IndexOf("52205fd8-5dfb-447d-801a-d0b52f2e83e1", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (string.Equals(s, "文件资源管理器", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "File Explorer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Windows 资源管理器", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Windows Explorer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (s.EndsWith(@"\File Explorer.lnk", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(@"\文件资源管理器.lnk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "File Explorer.lnk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "文件资源管理器.lnk", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public static IntPtr NormalizeToBrowsablePidl(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return IntPtr.Zero;

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(pidl, ref iidShellItem, out IShellItem? item) == ShellConstants.S_OK && item != null)
            {
                try
                {
                    string? parsing = GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);
                    string? title = GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                    if (IsExplorerLauncherTarget(parsing) || IsExplorerLauncherTarget(title))
                    {
                        Free(ref pidl);
                        return CreateThisPcPidl();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            return pidl;
        }

        /// <summary>
        /// Synchronous local/shell parsing helper used during startup session restoration for known local/virtual paths.
        /// Skips blocking UNC network paths unless allowUnc is true.
        /// </summary>
        public static IntPtr TryParseLocalOrShellFast(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return IntPtr.Zero;

            string expanded = Environment.ExpandEnvironmentVariables(rawPath!.Trim().Trim('"'));

            IntPtr known = TryParseKnownAlias(expanded);
            if (known != IntPtr.Zero)
                return known;

            bool isUnc = expanded.StartsWith(@"\\", StringComparison.Ordinal) &&
                         !expanded.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);
            if (isUnc)
                return IntPtr.Zero;

            if (expanded.Length == 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
                expanded += @"\";

            if (NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr pidl, 0, out _) == ShellConstants.S_OK && pidl != IntPtr.Zero)
                return NormalizeToBrowsablePidl(pidl);

            return IntPtr.Zero;
        }

        /// <summary>
        /// Parses user address bar input (local path, environment variables, friendly aliases, shell: commands, or UNC paths).
        /// For UNC paths (\\server\share), performs a bounded async pre-check on a ThreadPool worker so dead network hosts
        /// time out cleanly instead of freezing the UI thread for 20-30s during synchronous SHParseDisplayName.
        /// </summary>
        public static async Task<(IntPtr Pidl, string? ErrorMessage)> TryParseAddressAsync(
            string rawInput,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
                return (IntPtr.Zero, "请输入有效路径。");

            string expanded = Environment.ExpandEnvironmentVariables(rawInput.Trim().Trim('"'));

            IntPtr fastKnown = TryParseKnownAlias(expanded);
            if (fastKnown != IntPtr.Zero)
                return (fastKnown, null);

            bool isUnc = expanded.StartsWith(@"\\", StringComparison.Ordinal) &&
                         !expanded.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);

            if (isUnc)
            {
                Task<IntPtr> uncParseTask = Task.Run(() =>
                {
                    int hr = NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr parsedPidl, 0, out _);
                    return hr == ShellConstants.S_OK ? parsedPidl : IntPtr.Zero;
                });

                Task completed = await Task.WhenAny(uncParseTask, Task.Delay(2500, cancellationToken)).ConfigureAwait(true);
                if (completed != uncParseTask)
                {
                    _ = uncParseTask.ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != IntPtr.Zero)
                        {
                            IntPtr late = t.Result;
                            Free(ref late);
                        }
                    }, TaskScheduler.Default);
                    return (IntPtr.Zero, $"网络路径响应超时（>2.5秒）：{expanded}");
                }

                IntPtr uncPidl = await uncParseTask.ConfigureAwait(true);
                return uncPidl != IntPtr.Zero
                    ? (uncPidl, null)
                    : (IntPtr.Zero, $"无法访问网络路径：{expanded}");
            }

            if (expanded.Length == 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
                expanded += @"\";

            int parseHr = NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr resultPidl, 0, out _);
            if (parseHr == ShellConstants.S_OK && resultPidl != IntPtr.Zero)
                return (NormalizeToBrowsablePidl(resultPidl), null);

            if (Directory.Exists(expanded))
            {
                string full = Path.GetFullPath(expanded);
                if (NativeMethods.SHParseDisplayName(full, IntPtr.Zero, out resultPidl, 0, out _) == ShellConstants.S_OK)
                    return (NormalizeToBrowsablePidl(resultPidl), null);
            }

            return (IntPtr.Zero, $"找不到路径：{expanded}");
        }

        private static IntPtr TryParseKnownAlias(string expanded)
        {
            if (IsExplorerLauncherTarget(expanded) ||
                string.Equals(expanded, "此电脑", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "This PC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "计算机", StringComparison.OrdinalIgnoreCase))
            {
                return CreateThisPcPidl();
            }

            if (string.Equals(expanded, "主文件夹", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "快速访问", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Quick access", StringComparison.OrdinalIgnoreCase))
            {
                if (NativeMethods.SHParseDisplayName("shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", IntPtr.Zero, out IntPtr homePidl, 0, out _) == ShellConstants.S_OK && homePidl != IntPtr.Zero)
                    return homePidl;
                return CreateThisPcPidl();
            }

            if (string.Equals(expanded, "桌面", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return CreateDesktopPidl();
            }

            if (string.Equals(expanded, "回收站", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Recycle Bin", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_RecycleBinFolder);
            }

            if (string.Equals(expanded, "下载", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Downloads", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_Downloads);
            }

            if (string.Equals(expanded, "文档", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Documents", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_Documents);
            }

            if (string.Equals(expanded, "图片", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Pictures", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_Pictures);
            }

            return IntPtr.Zero;
        }
    }
}
