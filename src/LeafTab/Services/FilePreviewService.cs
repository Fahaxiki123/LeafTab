using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        public ImageSource? HeaderIcon { get; set; }
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
        public MarkdownParsedDocument? ParsedMarkdown { get; set; }
        public bool MarkdownAutoRawFallback => ParsedMarkdown?.ExceedsComplexityBudget == true;
        public string MarkdownComplexityNotice => ParsedMarkdown?.ComplexityNotice ?? string.Empty;
    }

    internal static partial class FilePreviewService
    {
        public const int MaxTextPreviewBytes = 96 * 1024;
        public const long MaxImagePreviewBytes = 32L * 1024 * 1024;
        public const int MaxDecodedImageDimension = 1600;
        public const long MaxImageTotalPixels = 64L * 1024 * 1024;
        private const int MaxWpfTextureDimension = 16384;
        private const long MaxSniffableUnknownFileBytes = 8L * 1024 * 1024;
        private const int MaxFolderChildProbeCount = 500;

        private enum ImageLoadOutcome
        {
            Loaded,
            ExceedsPixelLimit,
            Failed
        }

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

        public static Task<FilePreviewData> LoadPreviewAsync(
            string? fullPath,
            string? displayName,
            bool isFolderHint,
            string? typeName = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => LoadPreviewCore(fullPath, displayName, isFolderHint, typeName, cancellationToken), cancellationToken);
        }

        private static FilePreviewData LoadPreviewCore(
            string? fullPath,
            string? displayName,
            bool isFolderHint,
            string? typeName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string iconKey = !string.IsNullOrWhiteSpace(fullPath) ? fullPath! : (displayName ?? string.Empty);
            ImageSource? resolvedIcon = null;
            string shellTypeName = string.Empty;
            if (!string.IsNullOrWhiteSpace(iconKey))
            {
                (resolvedIcon, shellTypeName) = PidlManager.GetLargeIconAndTypeName(iconKey, isFolderHint);
            }

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
                string virtualType = !string.IsNullOrWhiteSpace(typeName)
                    ? typeName!.Trim()
                    : (!string.IsNullOrWhiteSpace(shellTypeName) ? shellTypeName : "系统命名空间项目");
                FilePreviewData virtualCard = BuildVirtualItemCard(fallbackTitle, virtualType);
                virtualCard.HeaderIcon = resolvedIcon;
                return virtualCard;
            }

            string normalizedPath = fullPath!;
            string resolvedType = !string.IsNullOrWhiteSpace(typeName)
                ? typeName!.Trim()
                : (!string.IsNullOrWhiteSpace(shellTypeName)
                    ? shellTypeName
                    : (isFolderHint ? "文件夹" : GetFallbackTypeName(normalizedPath)));

            FilePreviewData AttachIcon(FilePreviewData result)
            {
                result.HeaderIcon = resolvedIcon;
                return result;
            }

            try
            {
                if (isFolderHint || Directory.Exists(normalizedPath))
                {
                    return AttachIcon(BuildDirectoryCard(normalizedPath, fallbackTitle, resolvedType, cancellationToken));
                }

                if (!File.Exists(normalizedPath))
                {
                    return AttachIcon(new FilePreviewData
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
                    });
                }

                var fi = new FileInfo(normalizedPath);
                long fileLength = fi.Length;
                string ext = fi.Extension ?? string.Empty;
                string fileName = fi.Name ?? fallbackTitle;

                int oversizedPixelW = 0;
                int oversizedPixelH = 0;
                if (ImageExtensions.Contains(ext) && fileLength > 0 && fileLength <= MaxImagePreviewBytes)
                {
                    ImageLoadOutcome imgOutcome = TryLoadImagePreview(
                        normalizedPath,
                        fallbackTitle,
                        resolvedType,
                        fi,
                        out FilePreviewData? imageData,
                        out oversizedPixelW,
                        out oversizedPixelH);

                    if (imgOutcome == ImageLoadOutcome.Loaded && imageData != null)
                        return AttachIcon(imageData);
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
                        return AttachIcon(textData!);
                }

                return AttachIcon(BuildFileMetadataCard(normalizedPath, fallbackTitle, resolvedType, fi, oversizedPixelW, oversizedPixelH));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return AttachIcon(new FilePreviewData
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
                });
            }
            catch (Exception ex)
            {
                return AttachIcon(new FilePreviewData
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
                });
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

        private static FilePreviewData BuildFileMetadataCard(
            string filePath,
            string title,
            string typeName,
            FileInfo fi,
            int oversizedPixelW = 0,
            int oversizedPixelH = 0)
        {
            string sizeText = PidlManager.FormatByteSize((ulong)Math.Max(0, fi.Length));
            bool isImage = ImageExtensions.Contains(fi.Extension ?? string.Empty);

            string statusMsg;
            if (oversizedPixelW > 0 && oversizedPixelH > 0)
            {
                statusMsg = $"图片像素总量或尺寸过大（{oversizedPixelW} × {oversizedPixelH}），为避免占用过多内存已跳过缩略预览。";
            }
            else if (isImage && fi.Length > MaxImagePreviewBytes)
            {
                statusMsg = "图片体积超过 32 MB，为避免占用过多内存已跳过缩略预览。";
            }
            else
            {
                statusMsg = "此文件类型暂不支持文本或图像直出预览，可点击下方按钮使用系统默认程序打开。";
            }

            var data = new FilePreviewData
            {
                Kind = FilePreviewKind.MetadataCard,
                FilePath = filePath,
                Title = title,
                Subtitle = (oversizedPixelW > 0 && oversizedPixelH > 0)
                    ? $"{typeName} · {oversizedPixelW} × {oversizedPixelH} · {sizeText}"
                    : $"{typeName} · {sizeText}",
                TotalFileBytes = fi.Length,
                ImagePixelWidth = oversizedPixelW,
                ImagePixelHeight = oversizedPixelH,
                CanOpenWithDefaultApp = true,
                StatusMessage = statusMsg
            };

            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "类型", Value = typeName });
            if (oversizedPixelW > 0 && oversizedPixelH > 0)
            {
                data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "尺寸", Value = $"{oversizedPixelW} × {oversizedPixelH}" });
            }
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "大小", Value = $"{sizeText} ({fi.Length:N0} 字节)" });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "修改时间", Value = fi.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "创建时间", Value = fi.CreationTime.ToString("yyyy/MM/dd HH:mm:ss") });
            data.MetadataRows.Add(new FilePreviewMetadataRow { Label = "路径", Value = filePath });
            return data;
        }

        private static ImageLoadOutcome TryLoadImagePreview(
            string filePath,
            string title,
            string typeName,
            FileInfo fi,
            out FilePreviewData? data,
            out int rawPixelW,
            out int rawPixelH)
        {
            data = null;
            rawPixelW = 0;
            rawPixelH = 0;

            byte[] bytes;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length <= 0 || fs.Length > MaxImagePreviewBytes)
                    return ImageLoadOutcome.Failed;

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
                    return ImageLoadOutcome.Failed;

                if (readTotal != len)
                {
                    Array.Resize(ref bytes, readTotal);
                }
            }

            ImageLoadOutcome firstAttempt = TryDecodeBoundedImage(bytes, BitmapCreateOptions.None, out BitmapSource? bmp, out rawPixelW, out rawPixelH);
            if (firstAttempt == ImageLoadOutcome.ExceedsPixelLimit)
                return ImageLoadOutcome.ExceedsPixelLimit;

            if (firstAttempt == ImageLoadOutcome.Failed)
            {
                ImageLoadOutcome fallbackAttempt = TryDecodeBoundedImage(bytes, BitmapCreateOptions.IgnoreColorProfile, out bmp, out rawPixelW, out rawPixelH);
                if (fallbackAttempt != ImageLoadOutcome.Loaded || bmp == null)
                    return fallbackAttempt;
            }

            if (bmp == null)
                return ImageLoadOutcome.Failed;

            string sizeText = PidlManager.FormatByteSize((ulong)fi.Length);
            data = new FilePreviewData
            {
                Kind = FilePreviewKind.Image,
                FilePath = filePath,
                Title = title,
                Subtitle = $"{typeName} · {rawPixelW} × {rawPixelH} · {sizeText}",
                TotalFileBytes = fi.Length,
                Image = bmp,
                ImagePixelWidth = rawPixelW,
                ImagePixelHeight = rawPixelH,
                CanOpenWithDefaultApp = true
            };
            return ImageLoadOutcome.Loaded;
        }

        private static ImageLoadOutcome TryDecodeBoundedImage(
            byte[] imageBytes,
            BitmapCreateOptions createOptions,
            out BitmapSource? bitmapResult,
            out int pixelWidth,
            out int pixelHeight)
        {
            bitmapResult = null;
            pixelWidth = 0;
            pixelHeight = 0;
            int orientation = 1;

            try
            {
                using (var msDim = new MemoryStream(imageBytes, writable: false))
                {
                    // Inspect frame header metadata ONLY (BitmapCacheOption.None avoids decoding/caching full image buffer)
                    BitmapDecoder decoder = BitmapDecoder.Create(msDim, createOptions, BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0)
                        return ImageLoadOutcome.Failed;

                    BitmapFrame frame = decoder.Frames[0];
                    pixelWidth = frame.PixelWidth;
                    pixelHeight = frame.PixelHeight;
                    orientation = ReadExifOrientation(frame);
                    if (pixelWidth <= 0 || pixelHeight <= 0)
                        return ImageLoadOutcome.Failed;
                }

                long totalPixels = (long)pixelWidth * pixelHeight;
                if (totalPixels > MaxImageTotalPixels)
                {
                    return ImageLoadOutcome.ExceedsPixelLimit;
                }

                int majorAxis = Math.Max(pixelWidth, pixelHeight);
                int minorAxis = Math.Min(pixelWidth, pixelHeight);

                // Guard ultra-thin strips (>1600:1) exceeding WPF's 16384px texture bound where scaling majorAxis to 1600 would round minorAxis to 0
                if (majorAxis > MaxWpfTextureDimension && (long)minorAxis * MaxDecodedImageDimension < majorAxis)
                {
                    return ImageLoadOutcome.ExceedsPixelLimit;
                }

                using (var msDecode = new MemoryStream(imageBytes, writable: false))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = createOptions;

                    if (majorAxis > MaxDecodedImageDimension && (long)minorAxis * MaxDecodedImageDimension >= majorAxis)
                    {
                        if (pixelWidth >= pixelHeight)
                        {
                            bitmap.DecodePixelWidth = MaxDecodedImageDimension;
                        }
                        else
                        {
                            bitmap.DecodePixelHeight = MaxDecodedImageDimension;
                        }
                    }

                    bitmap.StreamSource = msDecode;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    bitmapResult = ApplyExifOrientation(bitmap, orientation);
                    if (orientation >= 5 && orientation <= 8)
                    {
                        int originalWidth = pixelWidth;
                        pixelWidth = pixelHeight;
                        pixelHeight = originalWidth;
                    }
                    return ImageLoadOutcome.Loaded;
                }
            }
            catch
            {
                bitmapResult = null;
                return ImageLoadOutcome.Failed;
            }
        }

        private static int ReadExifOrientation(BitmapFrame frame)
        {
            BitmapMetadata? metadata;
            try { metadata = frame.Metadata as BitmapMetadata; }
            catch (NotSupportedException) { return 1; }
            catch (System.Runtime.InteropServices.COMException) { return 1; }
            if (metadata == null) return 1;
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    object? value = metadata.GetQuery(query);
                    if (value == null) continue;
                    int orientation = Convert.ToInt32(value);
                    if (orientation >= 1 && orientation <= 8) return orientation;
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException ||
                    ex is InvalidOperationException || ex is FormatException || ex is OverflowException ||
                    ex is System.Runtime.InteropServices.COMException) { }
            }
            return 1;
        }

        private static BitmapSource ApplyExifOrientation(BitmapSource bitmap, int orientation)
        {
            Transform? transform = orientation switch
            {
                2 => new ScaleTransform(-1, 1),
                3 => new RotateTransform(180),
                4 => new ScaleTransform(1, -1),
                5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
                6 => new RotateTransform(90),
                7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
                8 => new RotateTransform(270),
                _ => null
            };
            if (transform == null) return bitmap;
            var corrected = new TransformedBitmap(bitmap, transform);
            corrected.Freeze();
            return corrected;
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

            MarkdownParsedDocument? parsedMd = null;
            if (isMarkdown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                parsedMd = ParseMarkdownStructure(decoded);
            }

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
                CanOpenWithDefaultApp = true,
                ParsedMarkdown = parsedMd
            };
            return true;
        }

        internal static bool LooksLikeBinary(byte[] buffer, int count)
        {
            if (count <= 0)
                return false;

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

            return suspiciousControlCount * 50 > inspectLen;
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
    }
}
