using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LeafTab.Interop;

namespace LeafTab.Services
{
    internal enum FilePreviewKind
    {
        Empty,
        MultiSelection,
        Markdown,
        Text,
        Image,
        MetadataCard
    }

    internal sealed class FilePreviewMetadataRow
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    internal sealed class FilePreviewData
    {
        public FilePreviewKind Kind { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public string TextContent { get; set; } = string.Empty;
        public string EncodingName { get; set; } = string.Empty;
        public bool IsTruncated { get; set; }
        public long TotalFileBytes { get; set; }
        public BitmapSource? Image { get; set; }
        public int ImagePixelWidth { get; set; }
        public int ImagePixelHeight { get; set; }
        public List<FilePreviewMetadataRow> MetadataRows { get; } = new List<FilePreviewMetadataRow>();
        public string StatusMessage { get; set; } = string.Empty;
        public bool CanOpenWithDefaultApp { get; set; }
    }

    internal static class FilePreviewService
    {
        public const int MaxTextPreviewBytes = 96 * 1024;
        private const long MaxImagePreviewBytes = 32L * 1024 * 1024;
        private const long MaxSniffableUnknownFileBytes = 8L * 1024 * 1024;
        private const int MaxDecodedImageWidth = 1600;
        private const int MaxFolderChildProbeCount = 500;

        private static readonly HashSet<string> MarkdownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".mdx", ".mkd"
        };

        private static readonly HashSet<string> KnownTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".json", ".jsonc", ".jsonl",
            ".xml", ".xaml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".config", ".manifest",
            ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".properties", ".reg", ".inf",
            ".csv", ".tsv", ".sql",
            ".cs", ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".java", ".kt", ".kts",
            ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".svelte",
            ".html", ".htm", ".css", ".scss", ".less", ".svg",
            ".py", ".pyw", ".rb", ".php", ".go", ".rs", ".swift", ".lua", ".pl", ".r",
            ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".psd1", ".bat", ".cmd",
            ".dockerfile", ".gitignore", ".gitattributes", ".editorconfig",
            ".shader", ".hlsl", ".glsl", ".cginc", ".meta", ".asmdef", ".asmref", ".uxml", ".uss",
            ".nfo", ".srt", ".vtt", ".ass", ".lrc", ".tex", ".bib", ".rst", ".adoc", ".org", ".patch", ".diff"
        };

        private static readonly HashSet<string> KnownTextFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LICENSE", "LICENSE.txt", "README", "Makefile", "Dockerfile", "CHANGELOG", "AUTHORS", "NOTICE", ".gitignore", ".gitattributes", ".editorconfig", ".env"
        };

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".ico", ".tif", ".tiff", ".wdp", ".jxr", ".webp"
        };

        private static readonly HashSet<string> KnownBinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".sys", ".pdb", ".obj", ".lib",
            ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".nupkg", ".unitypackage",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".mp3", ".wav", ".flac", ".ogg", ".aac",
            ".mp4", ".mkv", ".avi", ".mov", ".webm",
            ".iso", ".msi", ".cab", ".ttf", ".otf", ".woff", ".woff2",
            ".psd", ".ai", ".blend", ".fbx", ".db", ".sqlite", ".sqlite3", ".lnk"
        };

        // Cached frozen brushes & fonts for fast FlowDocument construction on UI thread
        private static readonly FontFamily BodyFont = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        private static readonly SolidColorBrush BodyBrush = CreateFrozenBrush(0x1E, 0x29, 0x3B);
        private static readonly SolidColorBrush HeadingBrush = CreateFrozenBrush(0x0F, 0x17, 0x2A);
        private static readonly SolidColorBrush MutedBrush = CreateFrozenBrush(0x64, 0x74, 0x8B);
        private static readonly SolidColorBrush BorderBrush = CreateFrozenBrush(0xE2, 0xE8, 0xF0);
        private static readonly SolidColorBrush SurfaceBrush = CreateFrozenBrush(0xF8, 0xFA, 0xFC);
        private static readonly SolidColorBrush CodeInlineBgBrush = CreateFrozenBrush(0xF1, 0xF5, 0xF9);
        private static readonly SolidColorBrush QuoteBorderBrush = CreateFrozenBrush(0x3B, 0x82, 0xF6);
        private static readonly SolidColorBrush LinkBrush = CreateFrozenBrush(0x00, 0x62, 0xCC);
        private static readonly SolidColorBrush TaskDoneBrush = CreateFrozenBrush(0x00, 0x7A, 0xFF);
        private static readonly SolidColorBrush TableHeaderBorderBrush = CreateFrozenBrush(0xCB, 0xD5, 0xE1);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static Task<FilePreviewData> LoadPreviewAsync(
            string? fullPath,
            string? displayName,
            bool isFolderHint,
            string typeName,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => LoadPreviewCore(fullPath, displayName, isFolderHint, typeName, cancellationToken), cancellationToken);
        }

        private static FilePreviewData LoadPreviewCore(
            string? fullPath,
            string? displayName,
            bool isFolderHint,
            string typeName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? leafName = string.IsNullOrWhiteSpace(fullPath)
                ? null
                : Path.GetFileName(fullPath!.TrimEnd('\\', '/'));

            string fallbackTitle;
            if (!isFolderHint && !string.IsNullOrWhiteSpace(leafName))
            {
                fallbackTitle = leafName!;
            }
            else if (!string.IsNullOrWhiteSpace(displayName))
            {
                fallbackTitle = displayName!;
            }
            else
            {
                fallbackTitle = string.IsNullOrWhiteSpace(leafName) ? "项目" : leafName!;
            }
            if (string.IsNullOrWhiteSpace(fallbackTitle) && !string.IsNullOrWhiteSpace(fullPath))
                fallbackTitle = fullPath!;

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return BuildVirtualItemCard(fallbackTitle, typeName);
            }

            string normalizedPath = fullPath!;
            string resolvedType = string.IsNullOrWhiteSpace(typeName)
                ? (isFolderHint ? "文件夹" : GetFallbackTypeName(normalizedPath))
                : typeName.Trim();

            try
            {
                if (isFolderHint || Directory.Exists(normalizedPath))
                {
                    return BuildDirectoryCard(normalizedPath, fallbackTitle, resolvedType, cancellationToken);
                }

                if (!File.Exists(normalizedPath))
                {
                    return new FilePreviewData
                    {
                        Kind = FilePreviewKind.MetadataCard,
                        FilePath = normalizedPath,
                        Title = fallbackTitle,
                        Subtitle = resolvedType,
                        StatusMessage = "找不到目标文件，可能已被移动或删除。",
                        MetadataRows =
                        {
                            new FilePreviewMetadataRow { Label = "类型", Value = resolvedType },
                            new FilePreviewMetadataRow { Label = "路径", Value = normalizedPath }
                        }
                    };
                }

                var fi = new FileInfo(normalizedPath);
                long fileLength = fi.Length;
                string ext = fi.Extension ?? string.Empty;
                string fileName = fi.Name ?? fallbackTitle;

                if (ImageExtensions.Contains(ext) && fileLength > 0 && fileLength <= MaxImagePreviewBytes)
                {
                    if (TryLoadImagePreview(normalizedPath, fallbackTitle, resolvedType, fi, out FilePreviewData? imageData))
                        return imageData!;
                }

                bool isMarkdown = MarkdownExtensions.Contains(ext);
                bool isTextExt = isMarkdown ||
                                 KnownTextExtensions.Contains(ext) ||
                                 KnownTextFileNames.Contains(fileName);
                bool canSniff = !isTextExt &&
                                !KnownBinaryExtensions.Contains(ext) &&
                                !ImageExtensions.Contains(ext) &&
                                fileLength <= MaxSniffableUnknownFileBytes;

                if (isTextExt || canSniff)
                {
                    if (TryLoadTextPreview(normalizedPath, fallbackTitle, resolvedType, fi, isMarkdown, cancellationToken, out FilePreviewData? textData))
                        return textData!;
                }

                return BuildFileMetadataCard(normalizedPath, fallbackTitle, resolvedType, fi);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return new FilePreviewData
                {
                    Kind = FilePreviewKind.MetadataCard,
                    FilePath = normalizedPath,
                    Title = fallbackTitle,
                    IsDirectory = isFolderHint,
                    Subtitle = resolvedType,
                    StatusMessage = "无访问权限，无法读取预览内容。",
                    MetadataRows =
                    {
                        new FilePreviewMetadataRow { Label = "类型", Value = resolvedType },
                        new FilePreviewMetadataRow { Label = "路径", Value = normalizedPath }
                    }
                };
            }
            catch (Exception ex)
            {
                return new FilePreviewData
                {
                    Kind = FilePreviewKind.MetadataCard,
                    FilePath = normalizedPath,
                    Title = fallbackTitle,
                    IsDirectory = isFolderHint,
                    Subtitle = resolvedType,
                    StatusMessage = $"无法预览该项：{ex.Message}",
                    MetadataRows =
                    {
                        new FilePreviewMetadataRow { Label = "类型", Value = resolvedType },
                        new FilePreviewMetadataRow { Label = "路径", Value = normalizedPath }
                    }
                };
            }
        }

        private static FilePreviewData BuildVirtualItemCard(string title, string typeName)
        {
            string resolvedType = string.IsNullOrWhiteSpace(typeName) ? "系统命名空间项目" : typeName;
            var data = new FilePreviewData
            {
                Kind = FilePreviewKind.MetadataCard,
                Title = title,
                Subtitle = resolvedType,
                StatusMessage = "此系统位置不支持内容预览。",
                CanOpenWithDefaultApp = false
            };
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "类型", Value = resolvedType });
            return data;
        }

        private static FilePreviewData BuildDirectoryCard(
            string folderPath,
            string title,
            string typeName,
            CancellationToken cancellationToken)
        {
            bool isDriveRoot = IsDriveRootPath(folderPath);
            var data = new FilePreviewData
            {
                Kind = FilePreviewKind.MetadataCard,
                FilePath = folderPath,
                Title = title,
                IsDirectory = true,
                CanOpenWithDefaultApp = false
            };

            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "类型", Value = typeName });

            if (isDriveRoot)
            {
                try
                {
                    var di = new DriveInfo(folderPath);
                    if (di.IsReady)
                    {
                        string total = PidlManager.FormatByteSize((ulong)Math.Max(0, di.TotalSize));
                        string free = PidlManager.FormatByteSize((ulong)Math.Max(0, di.AvailableFreeSpace));
                        string used = PidlManager.FormatByteSize((ulong)Math.Max(0, di.TotalSize - di.AvailableFreeSpace));
                        data.Subtitle = $"{typeName} · 可用 {free} / 共 {total}";
                        data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "文件系统", Value = di.DriveFormat });
                        data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "总容量", Value = total });
                        data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "已用空间", Value = used });
                        data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "可用空间", Value = free });
                    }
                    else
                    {
                        data.Subtitle = $"{typeName} · 未就绪";
                        data.StatusMessage = "驱动器未就绪。";
                    }
                }
                catch
                {
                    data.Subtitle = typeName;
                }

                data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "路径", Value = folderPath });
                return data;
            }

            var info = new DirectoryInfo(folderPath);
            int dirCount = 0;
            int fileCount = 0;
            bool truncated = false;
            bool accessDenied = false;

            try
            {
                int seen = 0;
                foreach (string entry in Directory.EnumerateFileSystemEntries(folderPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seen++;
                    if (seen > MaxFolderChildProbeCount)
                    {
                        truncated = true;
                        break;
                    }

                    try
                    {
                        if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                            dirCount++;
                        else
                            fileCount++;
                    }
                    catch
                    {
                        fileCount++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
            }
            catch
            {
                // Keep directory timestamps & path even if child enumeration fails
            }

            string containsText = accessDenied
                ? "无权限读取内容"
                : truncated
                    ? $"超过 {MaxFolderChildProbeCount} 个子项（已统计 {dirCount} 个文件夹、{fileCount} 个文件）"
                    : (dirCount == 0 && fileCount == 0
                        ? "空文件夹"
                        : $"{dirCount} 个文件夹，{fileCount} 个文件");

            data.Subtitle = accessDenied
                ? typeName
                : truncated
                    ? $"{typeName} · >{MaxFolderChildProbeCount} 项"
                    : $"{typeName} · {dirCount + fileCount} 个项目";

            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "包含", Value = containsText });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "修改时间", Value = info.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "创建时间", Value = info.CreationTime.ToString("yyyy/MM/dd HH:mm:ss") });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "路径", Value = folderPath });
            return data;
        }

        private static FilePreviewData BuildFileMetadataCard(string filePath, string title, string typeName, FileInfo fi)
        {
            string sizeText = PidlManager.FormatByteSize((ulong)Math.Max(0, fi.Length));
            var data = new FilePreviewData
            {
                Kind = FilePreviewKind.MetadataCard,
                FilePath = filePath,
                Title = title,
                Subtitle = $"{typeName} · {sizeText}",
                TotalFileBytes = fi.Length,
                CanOpenWithDefaultApp = true,
                StatusMessage = ImageExtensions.Contains(fi.Extension ?? string.Empty) && fi.Length > MaxImagePreviewBytes
                    ? "图片体积超过 32 MB，为避免占用过多内存已跳过缩略预览。"
                    : "此文件类型暂不支持文本或图像直出预览，可点击下方按钮使用系统默认程序打开。"
            };

            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "类型", Value = typeName });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "大小", Value = $"{sizeText} ({fi.Length:N0} 字节)" });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "修改时间", Value = fi.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "创建时间", Value = fi.CreationTime.ToString("yyyy/MM/dd HH:mm:ss") });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "路径", Value = filePath });
            return data;
        }

        private static bool TryLoadImagePreview(
            string filePath,
            string title,
            string typeName,
            FileInfo fi,
            out FilePreviewData? data)
        {
            data = null;
            byte[] bytes;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length <= 0 || fs.Length > MaxImagePreviewBytes)
                    return false;

                int len = (int)fs.Length;
                bytes = new byte[len];
                int readTotal = 0;
                while (readTotal < len)
                {
                    int n = fs.Read(bytes, readTotal, len - readTotal);
                    if (n <= 0)
                        break;
                    readTotal += n;
                }

                if (readTotal <= 0)
                    return false;

                if (readTotal != len)
                {
                    Array.Resize(ref bytes, readTotal);
                }
            }

            try
            {
                (BitmapSource? bmp, int pixelW, int pixelH) = DecodeFrozenBitmap(bytes, BitmapCreateOptions.None);
                if (bmp == null)
                    return false;

                string sizeText = PidlManager.FormatByteSize((ulong)fi.Length);
                data = new FilePreviewData
                {
                    Kind = FilePreviewKind.Image,
                    FilePath = filePath,
                    Title = title,
                    Subtitle = $"{typeName} · {pixelW} × {pixelH} · {sizeText}",
                    TotalFileBytes = fi.Length,
                    Image = bmp,
                    ImagePixelWidth = pixelW,
                    ImagePixelHeight = pixelH,
                    CanOpenWithDefaultApp = true
                };
                return true;
            }
            catch
            {
                try
                {
                    (BitmapSource? bmp, int pixelW, int pixelH) = DecodeFrozenBitmap(bytes, BitmapCreateOptions.IgnoreColorProfile);
                    if (bmp == null)
                        return false;

                    string sizeText = PidlManager.FormatByteSize((ulong)fi.Length);
                    data = new FilePreviewData
                    {
                        Kind = FilePreviewKind.Image,
                        FilePath = filePath,
                        Title = title,
                        Subtitle = $"{typeName} · {pixelW} × {pixelH} · {sizeText}",
                        TotalFileBytes = fi.Length,
                        Image = bmp,
                        ImagePixelWidth = pixelW,
                        ImagePixelHeight = pixelH,
                        CanOpenWithDefaultApp = true
                    };
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static (BitmapSource? Bitmap, int PixelWidth, int PixelHeight) DecodeFrozenBitmap(
            byte[] imageBytes,
            BitmapCreateOptions createOptions)
        {
            int pixelWidth;
            int pixelHeight;

            using (var msDim = new MemoryStream(imageBytes, writable: false))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(msDim, createOptions, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    return (null, 0, 0);

                BitmapFrame frame = decoder.Frames[0];
                pixelWidth = frame.PixelWidth;
                pixelHeight = frame.PixelHeight;
                if (pixelWidth <= 0 || pixelHeight <= 0)
                    return (null, 0, 0);
            }

            using (var msDecode = new MemoryStream(imageBytes, writable: false))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = createOptions;
                if (pixelWidth > MaxDecodedImageWidth)
                {
                    bitmap.DecodePixelWidth = MaxDecodedImageWidth;
                }
                bitmap.StreamSource = msDecode;
                bitmap.EndInit();
                bitmap.Freeze();
                return (bitmap, pixelWidth, pixelHeight);
            }
        }

        private static bool TryLoadTextPreview(
            string filePath,
            string title,
            string typeName,
            FileInfo fi,
            bool isMarkdown,
            CancellationToken cancellationToken,
            out FilePreviewData? data)
        {
            data = null;
            if (fi.Length == 0)
            {
                data = new FilePreviewData
                {
                    Kind = isMarkdown ? FilePreviewKind.Markdown : FilePreviewKind.Text,
                    FilePath = filePath,
                    Title = title,
                    Subtitle = $"{typeName} · 0 字节（空文件）",
                    TextContent = string.Empty,
                    EncodingName = "UTF-8",
                    TotalFileBytes = 0,
                    CanOpenWithDefaultApp = true
                };
                return true;
            }

            byte[] buffer = new byte[MaxTextPreviewBytes];
            int bytesRead = 0;
            bool isTruncated = fi.Length > MaxTextPreviewBytes;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384))
            {
                while (bytesRead < buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = fs.Read(buffer, bytesRead, buffer.Length - bytesRead);
                    if (n <= 0)
                        break;
                    bytesRead += n;
                }

                if (!isTruncated && fs.Position < fs.Length)
                    isTruncated = true;
            }

            if (LooksLikeBinary(buffer, bytesRead))
                return false;

            string decoded = DecodeTextBuffer(buffer, bytesRead, isTruncated, out string encodingLabel);
            decoded = decoded.Replace("\r\n", "\n").Replace('\r', '\n');

            string sizeText = PidlManager.FormatByteSize((ulong)fi.Length);
            data = new FilePreviewData
            {
                Kind = isMarkdown ? FilePreviewKind.Markdown : FilePreviewKind.Text,
                FilePath = filePath,
                Title = title,
                Subtitle = $"{typeName} · {sizeText} · {encodingLabel}",
                TextContent = decoded,
                EncodingName = encodingLabel,
                IsTruncated = isTruncated,
                TotalFileBytes = fi.Length,
                CanOpenWithDefaultApp = true
            };
            return true;
        }

        internal static bool LooksLikeBinary(byte[] buffer, int count)
        {
            if (count <= 0)
                return false;

            // Recognized Unicode BOMs are always text (even though UTF-16/UTF-32 contain 0x00 bytes)
            if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                return false;
            if (count >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
                return false;
            if (count >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
                return false;

            int inspectLen = Math.Min(count, 8192);
            int suspiciousControlCount = 0;

            for (int i = 0; i < inspectLen; i++)
            {
                byte b = buffer[i];
                if (b == 0x00)
                    return true;

                if (b < 0x20 && b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r' && b != 0x0C)
                {
                    suspiciousControlCount++;
                }
            }

            return suspiciousControlCount * 50 > inspectLen; // > 2% non-whitespace control bytes
        }

        internal static string DecodeTextBuffer(byte[] buffer, int count, bool isTruncated, out string encodingLabel)
        {
            if (count <= 0)
            {
                encodingLabel = "UTF-8";
                return string.Empty;
            }

            if (count >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
            {
                int payloadLen = ((count - 4) / 4) * 4;
                encodingLabel = "UTF-32 LE";
                return new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetString(buffer, 4, payloadLen);
            }

            if (count >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
            {
                int payloadLen = ((count - 4) / 4) * 4;
                encodingLabel = "UTF-32 BE";
                return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(buffer, 4, payloadLen);
            }

            if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            {
                int payloadLen = isTruncated ? TrimIncompleteTrailingUtf8Bytes(buffer, 3, count - 3) : (count - 3);
                encodingLabel = "UTF-8 BOM";
                try
                {
                    var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                    return strictUtf8.GetString(buffer, 3, payloadLen);
                }
                catch (DecoderFallbackException)
                {
                    return Encoding.UTF8.GetString(buffer, 3, payloadLen);
                }
            }

            if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            {
                int payloadLen = ((count - 2) / 2) * 2;
                encodingLabel = "UTF-16 LE";
                return Encoding.Unicode.GetString(buffer, 2, payloadLen);
            }

            if (count >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            {
                int payloadLen = ((count - 2) / 2) * 2;
                encodingLabel = "UTF-16 BE";
                return Encoding.BigEndianUnicode.GetString(buffer, 2, payloadLen);
            }

            int utf8Len = isTruncated ? TrimIncompleteTrailingUtf8Bytes(buffer, 0, count) : count;
            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                string utf8 = strictUtf8.GetString(buffer, 0, utf8Len);
                encodingLabel = "UTF-8";
                return utf8;
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    Encoding ansi = Encoding.Default;
                    encodingLabel = string.IsNullOrWhiteSpace(ansi.WebName)
                        ? $"CP{ansi.CodePage}"
                        : ansi.WebName.ToUpperInvariant();
                    return ansi.GetString(buffer, 0, count);
                }
                catch
                {
                    encodingLabel = "UTF-8";
                    return Encoding.UTF8.GetString(buffer, 0, utf8Len);
                }
            }
        }

        private static int TrimIncompleteTrailingUtf8Bytes(byte[] buffer, int offset, int length)
        {
            if (length <= 0)
                return 0;

            byte lastByte = buffer[offset + length - 1];
            if ((lastByte & 0x80) == 0)
                return length;

            int minIndex = Math.Max(offset, offset + length - 4);
            for (int i = offset + length - 1; i >= minIndex; i--)
            {
                byte b = buffer[i];
                if ((b & 0xC0) == 0x80)
                    continue;

                int expectedLength;
                if ((b & 0xE0) == 0xC0)
                    expectedLength = 2;
                else if ((b & 0xF0) == 0xE0)
                    expectedLength = 3;
                else if ((b & 0xF8) == 0xF0)
                    expectedLength = 4;
                else
                    return length;

                int presentLength = (offset + length) - i;
                return presentLength < expectedLength ? (i - offset) : length;
            }

            return length;
        }

        private static bool IsDriveRootPath(string path)
        {
            return path.Length >= 2 &&
                   path[1] == ':' &&
                   (path.Length == 2 || (path.Length == 3 && (path[2] == '\\' || path[2] == '/')));
        }

        private static string GetFallbackTypeName(string path)
        {
            string ext = string.Empty;
            try
            {
                ext = Path.GetExtension(path);
            }
            catch
            {
                ext = string.Empty;
            }

            return string.IsNullOrEmpty(ext)
                ? "文件"
                : $"{ext.TrimStart('.').ToUpperInvariant()} 文件";
        }

        // ==================== RICH LIGHTWEIGHT MARKDOWN RENDERER (UI THREAD ONLY) ====================

        public static FlowDocument BuildMarkdownDocument(string markdown, Action<Uri>? onHyperlinkNavigate = null)
        {
            var doc = new FlowDocument
            {
                ColumnWidth = double.PositiveInfinity,
                PagePadding = new Thickness(16, 14, 16, 20),
                FontFamily = BodyFont,
                FontSize = 13,
                LineHeight = 20.5,
                Foreground = BodyBrush,
                Background = Brushes.White
            };

            string normalized = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length == 0)
                return doc;

            string[] lines = normalized.Split('\n');
            RenderMarkdownLines(doc.Blocks, lines, 0, lines.Length, onHyperlinkNavigate, depth: 0);
            return doc;
        }

        private static void RenderMarkdownLines(
            BlockCollection targetBlocks,
            string[] lines,
            int startIndex,
            int endIndex,
            Action<Uri>? onHyperlinkNavigate,
            int depth)
        {
            int i = startIndex;

            // Render YAML frontmatter if present at top of document
            if (depth == 0 && i == 0 && i < endIndex && lines[i].Trim() == "---")
            {
                int closing = FindFrontmatterClose(lines, i + 1, Math.Min(endIndex, i + 45));
                if (closing > i)
                {
                    var fmSection = new Section
                    {
                        Background = SurfaceBrush,
                        BorderBrush = BorderBrush,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(11, 8, 11, 8),
                        Margin = new Thickness(0, 0, 0, 12)
                    };
                    string fmText = string.Join("\n", lines, i + 1, closing - (i + 1));
                    var fmPara = new Paragraph(new Run(fmText))
                    {
                        FontFamily = MonoFont,
                        FontSize = 11.5,
                        LineHeight = 17,
                        Foreground = MutedBrush,
                        Margin = new Thickness(0)
                    };
                    fmSection.Blocks.Add(fmPara);
                    targetBlocks.Add(fmSection);
                    i = closing + 1;
                }
            }

            while (i < endIndex)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                // 1. Fenced code block (``` or ~~~)
                if (TryParseFenceHeader(line, out char fenceChar, out int fenceLen, out string infoString))
                {
                    int closeIdx = FindClosingFence(lines, i + 1, endIndex, fenceChar, fenceLen);
                    int contentEnd = closeIdx >= 0 ? closeIdx : endIndex;
                    string codeText = contentEnd > i + 1
                        ? string.Join("\n", lines, i + 1, contentEnd - (i + 1))
                        : string.Empty;

                    var codeSection = new Section
                    {
                        Background = SurfaceBrush,
                        BorderBrush = BorderBrush,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(11, 8, 11, 9),
                        Margin = new Thickness(0, 4, 0, 10)
                    };

                    if (!string.IsNullOrEmpty(infoString))
                    {
                        codeSection.Blocks.Add(new Paragraph(new Run(infoString))
                        {
                            FontFamily = MonoFont,
                            FontSize = 10.5,
                            LineHeight = 14,
                            Foreground = MutedBrush,
                            Margin = new Thickness(0, 0, 0, 4)
                        });
                    }

                    codeSection.Blocks.Add(new Paragraph(new Run(codeText))
                    {
                        FontFamily = MonoFont,
                        FontSize = 12,
                        LineHeight = 18,
                        Foreground = HeadingBrush,
                        Margin = new Thickness(0)
                    });

                    targetBlocks.Add(codeSection);
                    i = closeIdx >= 0 ? closeIdx + 1 : endIndex;
                    continue;
                }

                // 2. ATX Heading (# .. ######)
                if (TryParseAtxHeading(line, out int headingLevel, out string headingText))
                {
                    var heading = CreateHeadingParagraph(headingLevel, headingText, onHyperlinkNavigate);
                    targetBlocks.Add(heading);
                    i++;
                    continue;
                }

                // 3. Horizontal Rule (---, ***, ___)
                if (IsHorizontalRule(line))
                {
                    targetBlocks.Add(new Paragraph
                    {
                        BorderBrush = BorderBrush,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        FontSize = 1,
                        LineHeight = 1,
                        Margin = new Thickness(0, 8, 0, 12),
                        Padding = new Thickness(0)
                    });
                    i++;
                    continue;
                }

                // 4. Markdown Pipe Table
                if (i + 1 < endIndex && LooksLikeTableRow(line) && IsTableSeparatorRow(lines[i + 1]))
                {
                    List<string> headers = SplitTableRow(line);
                    int rowStart = i + 2;
                    int rowEnd = rowStart;
                    while (rowEnd < endIndex &&
                           !string.IsNullOrWhiteSpace(lines[rowEnd]) &&
                           LooksLikeTableRow(lines[rowEnd]) &&
                           !TryParseFenceHeader(lines[rowEnd], out _, out _, out _) &&
                           !TryParseAtxHeading(lines[rowEnd], out _, out _))
                    {
                        rowEnd++;
                    }

                    targetBlocks.Add(BuildMarkdownTable(headers, lines, rowStart, rowEnd, onHyperlinkNavigate));
                    i = rowEnd;
                    continue;
                }

                // 5. Blockquote (> ...)
                if (IsBlockquoteLine(line))
                {
                    var quotedLines = new List<string>();
                    while (i < endIndex && IsBlockquoteLine(lines[i]))
                    {
                        quotedLines.Add(StripBlockquotePrefix(lines[i]));
                        i++;
                    }

                    var quoteSection = new Section
                    {
                        Background = SurfaceBrush,
                        BorderBrush = QuoteBorderBrush,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(12, 6, 10, 6),
                        Margin = new Thickness(0, 4, 0, 10)
                    };

                    if (depth < 3)
                    {
                        string[] innerLines = quotedLines.ToArray();
                        RenderMarkdownLines(quoteSection.Blocks, innerLines, 0, innerLines.Length, onHyperlinkNavigate, depth + 1);
                    }
                    else
                    {
                        var fallbackPara = new Paragraph { Margin = new Thickness(0) };
                        AppendInlines(fallbackPara.Inlines, string.Join(" ", quotedLines), onHyperlinkNavigate);
                        quoteSection.Blocks.Add(fallbackPara);
                    }

                    targetBlocks.Add(quoteSection);
                    continue;
                }

                // 6. List (- / * / + or 1.) including GitHub task items (- [ ] / - [x])
                if (TryParseListItem(line, out bool isOrdered, out string firstItemBody, out bool isTask, out bool taskDone))
                {
                    var items = new List<(string Body, bool IsTask, bool TaskDone)>();
                    bool anyTask = isTask;
                    items.Add((firstItemBody, isTask, taskDone));
                    i++;

                    while (i < endIndex)
                    {
                        string nextLine = lines[i];
                        if (string.IsNullOrWhiteSpace(nextLine))
                        {
                            // Keep consuming if the very next non-empty line continues the same list type
                            if (i + 1 < endIndex &&
                                TryParseListItem(lines[i + 1], out bool nextIsOrd, out _, out _, out _) &&
                                nextIsOrd == isOrdered)
                            {
                                i++;
                                continue;
                            }
                            break;
                        }

                        if (TryParseListItem(nextLine, out bool itemOrd, out string itemBody, out bool itemTask, out bool itemDone))
                        {
                            if (itemOrd != isOrdered)
                                break;
                            anyTask |= itemTask;
                            items.Add((itemBody, itemTask, itemDone));
                            i++;
                            continue;
                        }

                        // Indented continuation line inside previous list item
                        if ((nextLine.StartsWith("  ", StringComparison.Ordinal) || nextLine.StartsWith("\t", StringComparison.Ordinal)) &&
                            !TryParseFenceHeader(nextLine, out _, out _, out _) &&
                            !IsBlockquoteLine(nextLine))
                        {
                            int lastIdx = items.Count - 1;
                            var prev = items[lastIdx];
                            items[lastIdx] = (prev.Body + " " + nextLine.Trim(), prev.IsTask, prev.TaskDone);
                            i++;
                            continue;
                        }

                        break;
                    }

                    var list = new List
                    {
                        MarkerStyle = anyTask
                            ? TextMarkerStyle.None
                            : (isOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc),
                        Padding = new Thickness(anyTask ? 8 : 20, 0, 0, 0),
                        Margin = new Thickness(0, 2, 0, 10)
                    };

                    foreach (var entry in items)
                    {
                        var para = new Paragraph
                        {
                            Margin = new Thickness(0, 2, 0, 3),
                            LineHeight = 20
                        };

                        if (entry.IsTask)
                        {
                            para.Inlines.Add(new Run(entry.TaskDone ? "☑ " : "☐ ")
                            {
                                FontWeight = FontWeights.SemiBold,
                                Foreground = entry.TaskDone ? TaskDoneBrush : MutedBrush
                            });
                        }

                        AppendInlines(para.Inlines, entry.Body, onHyperlinkNavigate);
                        list.ListItems.Add(new ListItem(para));
                    }

                    targetBlocks.Add(list);
                    continue;
                }

                // 7. Plain paragraph
                var paraLines = new List<string>();
                while (i < endIndex)
                {
                    string pLine = lines[i];
                    if (string.IsNullOrWhiteSpace(pLine))
                        break;
                    if (paraLines.Count > 0 &&
                        (TryParseFenceHeader(pLine, out _, out _, out _) ||
                         TryParseAtxHeading(pLine, out _, out _) ||
                         IsHorizontalRule(pLine) ||
                         IsBlockquoteLine(pLine) ||
                         TryParseListItem(pLine, out _, out _, out _, out _) ||
                         (i + 1 < endIndex && LooksLikeTableRow(pLine) && IsTableSeparatorRow(lines[i + 1]))))
                    {
                        break;
                    }

                    paraLines.Add(pLine);
                    i++;
                }

                targetBlocks.Add(BuildParagraphFromLines(paraLines, onHyperlinkNavigate));
            }
        }

        private static int FindFrontmatterClose(string[] lines, int startIndex, int limitIndex)
        {
            for (int j = startIndex; j < limitIndex; j++)
            {
                string trimmed = lines[j].Trim();
                if (trimmed == "---" || trimmed == "...")
                    return j;
            }
            return -1;
        }

        private static bool TryParseFenceHeader(string line, out char fenceChar, out int fenceLen, out string infoString)
        {
            fenceChar = '\0';
            fenceLen = 0;
            infoString = string.Empty;

            int idx = SkipLeadingSpaces(line, 3);
            if (idx >= line.Length)
                return false;

            char c = line[idx];
            if (c != '`' && c != '~')
                return false;

            int len = 0;
            while (idx + len < line.Length && line[idx + len] == c)
                len++;

            if (len < 3)
                return false;

            string rest = line.Substring(idx + len).Trim();
            if (c == '`' && rest.IndexOf('`') >= 0)
                return false;

            fenceChar = c;
            fenceLen = len;
            infoString = rest;
            return true;
        }

        private static int FindClosingFence(string[] lines, int startIndex, int endIndex, char fenceChar, int minFenceLen)
        {
            for (int j = startIndex; j < endIndex; j++)
            {
                string line = lines[j];
                int idx = SkipLeadingSpaces(line, 3);
                int count = 0;
                while (idx + count < line.Length && line[idx + count] == fenceChar)
                    count++;

                if (count >= minFenceLen && string.IsNullOrWhiteSpace(line.Substring(idx + count)))
                    return j;
            }
            return -1;
        }

        private static bool TryParseAtxHeading(string line, out int level, out string text)
        {
            level = 0;
            text = string.Empty;

            int idx = SkipLeadingSpaces(line, 3);
            if (idx >= line.Length || line[idx] != '#')
                return false;

            int hashes = 0;
            while (idx + hashes < line.Length && line[idx + hashes] == '#')
                hashes++;

            if (hashes < 1 || hashes > 6)
                return false;

            if (idx + hashes < line.Length && !char.IsWhiteSpace(line[idx + hashes]))
                return false;

            string body = line.Substring(idx + hashes).Trim();
            // Strip optional closing ATX hashes preceded by whitespace
            int end = body.Length;
            while (end > 0 && body[end - 1] == '#')
                end--;
            if (end < body.Length && (end == 0 || char.IsWhiteSpace(body[end - 1])))
                body = body.Substring(0, end).TrimEnd();

            level = hashes;
            text = body;
            return true;
        }

        private static Paragraph CreateHeadingParagraph(int level, string text, Action<Uri>? onHyperlinkNavigate)
        {
            double fontSize = level switch
            {
                1 => 20,
                2 => 16.5,
                3 => 14.5,
                4 => 13.5,
                5 => 13,
                _ => 12.5
            };

            var para = new Paragraph
            {
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = level == 6 ? MutedBrush : HeadingBrush,
                LineHeight = fontSize + 7,
                Margin = new Thickness(0, level == 1 ? 4 : 10, 0, level <= 2 ? 8 : 5)
            };

            if (level <= 2)
            {
                para.BorderBrush = BorderBrush;
                para.BorderThickness = new Thickness(0, 0, 0, 1);
                para.Padding = new Thickness(0, 0, 0, 4);
            }

            AppendInlines(para.Inlines, text, onHyperlinkNavigate);
            return para;
        }

        private static bool IsHorizontalRule(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 3)
                return false;

            char marker = '\0';
            int count = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == ' ' || c == '\t')
                    continue;
                if (c != '-' && c != '*' && c != '_')
                    return false;
                if (marker == '\0')
                    marker = c;
                else if (c != marker)
                    return false;
                count++;
            }

            return count >= 3;
        }

        private static bool LooksLikeTableRow(string line) => line.IndexOf('|') >= 0;

        private static bool IsTableSeparatorRow(string line)
        {
            List<string> cells = SplitTableRow(line);
            if (cells.Count == 0)
                return false;

            foreach (string rawCell in cells)
            {
                string cell = rawCell.Trim();
                if (cell.Length == 0)
                    return false;

                int start = cell[0] == ':' ? 1 : 0;
                int end = cell[cell.Length - 1] == ':' ? cell.Length - 1 : cell.Length;
                if (end - start < 1)
                    return false;

                for (int k = start; k < end; k++)
                {
                    if (cell[k] != '-')
                        return false;
                }
            }

            return true;
        }

        private static List<string> SplitTableRow(string line)
        {
            var cells = new List<string>();
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                return cells;

            int startIdx = trimmed[0] == '|' ? 1 : 0;
            int endIdx = trimmed.Length;
            if (endIdx > startIdx && trimmed[endIdx - 1] == '|' && (endIdx < 2 || trimmed[endIdx - 2] != '\\'))
                endIdx--;

            var sb = new StringBuilder();
            for (int i = startIdx; i < endIdx; i++)
            {
                char c = trimmed[i];
                if (c == '\\' && i + 1 < endIdx && trimmed[i + 1] == '|')
                {
                    sb.Append('|');
                    i++;
                }
                else if (c == '|')
                {
                    cells.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            cells.Add(sb.ToString().Trim());
            return cells;
        }

        private static Table BuildMarkdownTable(
            List<string> headers,
            string[] lines,
            int rowStart,
            int rowEnd,
            Action<Uri>? onHyperlinkNavigate)
        {
            int colCount = Math.Max(1, headers.Count);
            var table = new Table
            {
                CellSpacing = 0,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 12),
                FontSize = 12.5
            };

            for (int c = 0; c < colCount; c++)
            {
                table.Columns.Add(new TableColumn());
            }

            var rowGroup = new TableRowGroup();

            // Header row
            var headerRow = new TableRow { Background = CodeInlineBgBrush };
            for (int c = 0; c < colCount; c++)
            {
                string cellText = c < headers.Count ? headers[c] : string.Empty;
                var para = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = HeadingBrush,
                    LineHeight = 18
                };
                AppendInlines(para.Inlines, cellText, onHyperlinkNavigate);

                headerRow.Cells.Add(new TableCell(para)
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderBrush = TableHeaderBorderBrush,
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1)
                });
            }
            rowGroup.Rows.Add(headerRow);

            // Body rows
            for (int r = rowStart; r < rowEnd; r++)
            {
                List<string> rowCells = SplitTableRow(lines[r]);
                int bodyRowIdx = r - rowStart;
                var row = new TableRow
                {
                    Background = (bodyRowIdx % 2 == 1) ? SurfaceBrush : Brushes.White
                };

                for (int c = 0; c < colCount; c++)
                {
                    string cellText = c < rowCells.Count ? rowCells[c] : string.Empty;
                    var para = new Paragraph
                    {
                        Margin = new Thickness(0),
                        LineHeight = 18
                    };
                    AppendInlines(para.Inlines, cellText, onHyperlinkNavigate);

                    row.Cells.Add(new TableCell(para)
                    {
                        Padding = new Thickness(8, 5, 8, 5),
                        BorderBrush = BorderBrush,
                        BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, r < rowEnd - 1 ? 1 : 0)
                    });
                }

                rowGroup.Rows.Add(row);
            }

            table.RowGroups.Add(rowGroup);
            return table;
        }

        private static bool IsBlockquoteLine(string line)
        {
            int idx = SkipLeadingSpaces(line, 3);
            return idx < line.Length && line[idx] == '>';
        }

        private static string StripBlockquotePrefix(string line)
        {
            int idx = SkipLeadingSpaces(line, 3);
            if (idx < line.Length && line[idx] == '>')
            {
                idx++;
                if (idx < line.Length && line[idx] == ' ')
                    idx++;
                return line.Substring(idx);
            }
            return line;
        }

        private static bool TryParseListItem(
            string line,
            out bool isOrdered,
            out string body,
            out bool isTask,
            out bool taskDone)
        {
            isOrdered = false;
            body = string.Empty;
            isTask = false;
            taskDone = false;

            int idx = SkipLeadingSpaces(line, 5);
            if (idx >= line.Length)
                return false;

            char c = line[idx];
            if ((c == '-' || c == '*' || c == '+') &&
                idx + 1 < line.Length &&
                (line[idx + 1] == ' ' || line[idx + 1] == '\t'))
            {
                body = line.Substring(idx + 2).TrimStart();
                ExtractTaskPrefix(ref body, out isTask, out taskDone);
                return true;
            }

            if (char.IsDigit(c))
            {
                int digitEnd = idx;
                while (digitEnd < line.Length && char.IsDigit(line[digitEnd]))
                    digitEnd++;

                if (digitEnd < line.Length &&
                    digitEnd - idx <= 6 &&
                    (line[digitEnd] == '.' || line[digitEnd] == ')') &&
                    digitEnd + 1 < line.Length &&
                    (line[digitEnd + 1] == ' ' || line[digitEnd + 1] == '\t'))
                {
                    isOrdered = true;
                    body = line.Substring(digitEnd + 2).TrimStart();
                    ExtractTaskPrefix(ref body, out isTask, out taskDone);
                    return true;
                }
            }

            return false;
        }

        private static void ExtractTaskPrefix(ref string body, out bool isTask, out bool taskDone)
        {
            isTask = false;
            taskDone = false;

            if (body.Length >= 4 &&
                body[0] == '[' &&
                (body[1] == ' ' || body[1] == 'x' || body[1] == 'X') &&
                body[2] == ']' &&
                char.IsWhiteSpace(body[3]))
            {
                isTask = true;
                taskDone = body[1] != ' ';
                body = body.Substring(4).TrimStart();
            }
        }

        private static Paragraph BuildParagraphFromLines(List<string> lines, Action<Uri>? onHyperlinkNavigate)
        {
            var para = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 9),
                LineHeight = 20.5
            };

            for (int i = 0; i < lines.Count; i++)
            {
                string raw = lines[i];
                bool hardBreak = raw.EndsWith("  ", StringComparison.Ordinal);
                string content = raw.Trim();
                AppendInlines(para.Inlines, content, onHyperlinkNavigate);

                if (i < lines.Count - 1)
                {
                    if (hardBreak)
                        para.Inlines.Add(new LineBreak());
                    else
                        para.Inlines.Add(new Run(" "));
                }
            }

            return para;
        }

        private static int SkipLeadingSpaces(string line, int maxSpaces)
        {
            int idx = 0;
            while (idx < line.Length && idx < maxSpaces && line[idx] == ' ')
                idx++;
            return idx;
        }

        private static void AppendInlines(InlineCollection target, string text, Action<Uri>? onHyperlinkNavigate)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var plainBuffer = new StringBuilder();
            int i = 0;

            void FlushPlain()
            {
                if (plainBuffer.Length > 0)
                {
                    target.Add(new Run(plainBuffer.ToString()));
                    plainBuffer.Clear();
                }
            }

            while (i < text.Length)
            {
                char ch = text[i];

                // Backslash escape for ASCII punctuation
                if (ch == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
                {
                    plainBuffer.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                // Inline code (`code`)
                if (ch == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close > i)
                    {
                        FlushPlain();
                        string code = text.Substring(i + 1, close - (i + 1));
                        var codeSpan = new Span(new Run(code))
                        {
                            FontFamily = MonoFont,
                            FontSize = 12,
                            Background = CodeInlineBgBrush,
                            Foreground = HeadingBrush
                        };
                        target.Add(codeSpan);
                        i = close + 1;
                        continue;
                    }
                }

                // Markdown image fallback: ![alt](url) -> crisp badge text [图片: alt]
                if (ch == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                    TryParseBracketUrl(text, i + 1, out string altText, out _, out int afterImg))
                {
                    FlushPlain();
                    string label = string.IsNullOrWhiteSpace(altText) ? "[图片]" : $"[图片: {altText}]";
                    target.Add(new Run(label) { Foreground = MutedBrush });
                    i = afterImg;
                    continue;
                }

                // Markdown link [label](url)
                if (ch == '[' && TryParseBracketUrl(text, i, out string linkLabel, out string linkUrl, out int afterLink))
                {
                    FlushPlain();
                    if (Uri.TryCreate(linkUrl.Trim(), UriKind.Absolute, out Uri? uri) &&
                        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                    {
                        var hyperlink = new Hyperlink
                        {
                            Foreground = LinkBrush,
                            ToolTip = uri.AbsoluteUri
                        };
                        AppendInlines(hyperlink.Inlines, linkLabel, null);
                        if (onHyperlinkNavigate != null)
                        {
                            hyperlink.Click += (_, _) => onHyperlinkNavigate(uri);
                        }
                        target.Add(hyperlink);
                    }
                    else
                    {
                        target.Add(new Run(linkLabel) { Foreground = LinkBrush });
                    }
                    i = afterLink;
                    continue;
                }

                // ***bold italic*** or ___bold italic___
                if ((text.IndexOf("***", i, StringComparison.Ordinal) == i || text.IndexOf("___", i, StringComparison.Ordinal) == i))
                {
                    string marker = text.Substring(i, 3);
                    int close = text.IndexOf(marker, i + 3, StringComparison.Ordinal);
                    if (close > i + 3)
                    {
                        FlushPlain();
                        var span = new Span
                        {
                            FontWeight = FontWeights.SemiBold,
                            FontStyle = FontStyles.Italic
                        };
                        AppendInlines(span.Inlines, text.Substring(i + 3, close - (i + 3)), onHyperlinkNavigate);
                        target.Add(span);
                        i = close + 3;
                        continue;
                    }
                }

                // **bold** or __bold__
                if ((text.IndexOf("**", i, StringComparison.Ordinal) == i || text.IndexOf("__", i, StringComparison.Ordinal) == i))
                {
                    string marker = text.Substring(i, 2);
                    int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        FlushPlain();
                        var span = new Span
                        {
                            FontWeight = FontWeights.SemiBold,
                            Foreground = HeadingBrush
                        };
                        AppendInlines(span.Inlines, text.Substring(i + 2, close - (i + 2)), onHyperlinkNavigate);
                        target.Add(span);
                        i = close + 2;
                        continue;
                    }
                }

                // ~~strikethrough~~
                if (text.IndexOf("~~", i, StringComparison.Ordinal) == i)
                {
                    int close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        FlushPlain();
                        var span = new Span
                        {
                            TextDecorations = TextDecorations.Strikethrough,
                            Foreground = MutedBrush
                        };
                        AppendInlines(span.Inlines, text.Substring(i + 2, close - (i + 2)), onHyperlinkNavigate);
                        target.Add(span);
                        i = close + 2;
                        continue;
                    }
                }

                // *italic* (only match '_' when flanked cleanly to avoid breaking snake_case_identifiers)
                if (ch == '*' || (ch == '_' && IsFlankingUnderscoreStart(text, i)))
                {
                    int close = FindClosingEmphasis(text, i + 1, ch);
                    if (close > i + 1)
                    {
                        FlushPlain();
                        var span = new Span { FontStyle = FontStyles.Italic };
                        AppendInlines(span.Inlines, text.Substring(i + 1, close - (i + 1)), onHyperlinkNavigate);
                        target.Add(span);
                        i = close + 1;
                        continue;
                    }
                }

                plainBuffer.Append(ch);
                i++;
            }

            FlushPlain();
        }

        private static bool TryParseBracketUrl(string text, int openBracketIdx, out string label, out string url, out int afterIndex)
        {
            label = string.Empty;
            url = string.Empty;
            afterIndex = openBracketIdx;

            if (openBracketIdx < 0 || openBracketIdx >= text.Length || text[openBracketIdx] != '[')
                return false;

            int closeBracket = -1;
            int depth = 1;
            for (int j = openBracketIdx + 1; j < text.Length; j++)
            {
                if (text[j] == '\\' && j + 1 < text.Length)
                {
                    j++;
                    continue;
                }
                if (text[j] == '[')
                    depth++;
                else if (text[j] == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBracket = j;
                        break;
                    }
                }
            }

            if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
                return false;

            int closeParen = -1;
            int parenDepth = 1;
            for (int k = closeBracket + 2; k < text.Length; k++)
            {
                if (text[k] == '\\' && k + 1 < text.Length)
                {
                    k++;
                    continue;
                }
                if (text[k] == '(')
                    parenDepth++;
                else if (text[k] == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        closeParen = k;
                        break;
                    }
                }
            }

            if (closeParen < 0)
                return false;

            label = text.Substring(openBracketIdx + 1, closeBracket - (openBracketIdx + 1));
            url = text.Substring(closeBracket + 2, closeParen - (closeBracket + 2));
            afterIndex = closeParen + 1;
            return true;
        }

        private static bool IsFlankingUnderscoreStart(string text, int idx)
        {
            if (idx > 0 && char.IsLetterOrDigit(text[idx - 1]))
                return false;
            return idx + 1 < text.Length && !char.IsWhiteSpace(text[idx + 1]);
        }

        private static int FindClosingEmphasis(string text, int searchStart, char marker)
        {
            for (int j = searchStart; j < text.Length; j++)
            {
                if (text[j] == '\\' && j + 1 < text.Length)
                {
                    j++;
                    continue;
                }

                if (text[j] == marker)
                {
                    if (j > searchStart && !char.IsWhiteSpace(text[j - 1]))
                    {
                        if (marker == '_' && j + 1 < text.Length && char.IsLetterOrDigit(text[j + 1]))
                            continue;
                        return j;
                    }
                }
            }
            return -1;
        }

        private static bool IsAsciiPunctuation(char c) =>
            c == '\\' || c == '`' || c == '*' || c == '_' || c == '{' || c == '}' ||
            c == '[' || c == ']' || c == '(' || c == ')' || c == '#' || c == '+' ||
            c == '-' || c == '.' || c == '!' || c == '|' || c == '~' || c == '>';
    }
}
