using System;
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
        public ImageSource? Icon { get; set; }
    }

    internal static class PidlManager
    {
        public static IntPtr CreateKnownFolderPidl(Guid knownFolderId)
        {
            Guid copy = knownFolderId;
            int hr = NativeMethods.SHGetKnownFolderIDList(ref copy, 0, IntPtr.Zero, out IntPtr pidl);
            return hr == ShellConstants.S_OK ? pidl : IntPtr.Zero;
        }

        public static IntPtr CreateThisPcPidl() =>
            CreateKnownFolderPidl(NativeMethods.FOLDERID_ComputerFolder);

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

                    info.AddressText = !string.IsNullOrWhiteSpace(fsPath)
                        ? fsPath!
                        : (!string.IsNullOrWhiteSpace(editPath) ? editPath! : (info.Title));

                    info.ParsingName = parsing ?? info.AddressText;
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            info.Icon = ExtractSmallIcon(pidl);
            return info;
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

            if (string.Equals(expanded, "此电脑", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "This PC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "计算机", StringComparison.OrdinalIgnoreCase))
            {
                return (CreateThisPcPidl(), null);
            }

            if (string.Equals(expanded, "回收站", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Recycle Bin", StringComparison.OrdinalIgnoreCase))
            {
                return (CreateKnownFolderPidl(NativeMethods.FOLDERID_RecycleBinFolder), null);
            }

            if (string.Equals(expanded, "下载", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Downloads", StringComparison.OrdinalIgnoreCase))
            {
                return (CreateKnownFolderPidl(NativeMethods.FOLDERID_Downloads), null);
            }

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
                    // If the background parse eventually returns a PIDL later, free it safely so nothing leaks.
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

            // Local paths, ::{GUID}, shell:*, \\wsl.localhost\*
            if (expanded.Length == 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
                expanded += @"\";

            int parseHr = NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr resultPidl, 0, out _);
            if (parseHr == ShellConstants.S_OK && resultPidl != IntPtr.Zero)
                return (resultPidl, null);

            if (Directory.Exists(expanded))
            {
                string full = Path.GetFullPath(expanded);
                if (NativeMethods.SHParseDisplayName(full, IntPtr.Zero, out resultPidl, 0, out _) == ShellConstants.S_OK)
                    return (resultPidl, null);
            }

            return (IntPtr.Zero, $"找不到路径：{expanded}");
        }
    }
}
