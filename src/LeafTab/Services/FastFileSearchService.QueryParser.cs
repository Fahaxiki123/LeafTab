using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LeafTab.Interop;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace LeafTab.Services
{
    internal sealed partial class FastFileSearchService
    {
        internal sealed class SearchClause
        {
            private readonly Func<string, string, bool>? _pathPredicate;
            private readonly Func<SearchResultEntry, bool>? _metadataPredicate;
            private readonly bool _requireDirectory;
            private readonly bool _requireFile;
            private readonly SearchClause[]? _orBranches;

            public SearchClause(
                Func<string, string, bool>? pathPredicate,
                Func<SearchResultEntry, bool>? metadataPredicate,
                bool requireDirectory,
                bool requireFile)
            {
                _pathPredicate = pathPredicate;
                _metadataPredicate = metadataPredicate;
                _requireDirectory = requireDirectory;
                _requireFile = requireFile;
            }

            public SearchClause(SearchClause[] orBranches)
            {
                _orBranches = orBranches;
            }

            internal bool RequiresMetadata
            {
                get
                {
                    if (_metadataPredicate != null) return true;
                    if (_orBranches != null)
                        foreach (SearchClause branch in _orBranches)
                            if (branch.RequiresMetadata) return true;
                    return false;
                }
            }

            public bool MayMatchPathAndKind(string name, string relativePath, bool isDirectory)
            {
                if (_orBranches != null)
                {
                    for (int i = 0; i < _orBranches.Length; i++)
                    {
                        if (_orBranches[i].MayMatchPathAndKind(name, relativePath, isDirectory))
                            return true;
                    }
                    return false;
                }

                if (_requireDirectory && !isDirectory)
                    return false;
                if (_requireFile && isDirectory)
                    return false;
                if (_requireDirectory && _requireFile)
                    return false;

                return _pathPredicate == null || _pathPredicate(name, relativePath);
            }

            public bool MatchesEntry(string name, string relativePath, in SearchResultEntry entry)
            {
                if (_orBranches != null)
                {
                    for (int i = 0; i < _orBranches.Length; i++)
                    {
                        if (_orBranches[i].MatchesEntry(name, relativePath, in entry))
                            return true;
                    }
                    return false;
                }

                bool isDir = (entry.FileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
                if (!MayMatchPathAndKind(name, relativePath, isDir))
                    return false;

                return _metadataPredicate == null || _metadataPredicate(entry);
            }
        }

        internal sealed class ParsedSearchQuery
        {
            private readonly SearchClause[] _clauses;

            public bool HasUnsupportedNativeSyntax { get; }
            public string? UnsupportedSyntaxNotice { get; }
            internal bool RequiresMetadata
            {
                get
                {
                    foreach (SearchClause clause in _clauses)
                        if (clause.RequiresMetadata) return true;
                    return false;
                }
            }

            public ParsedSearchQuery(
                SearchClause[] clauses,
                bool hasUnsupportedNativeSyntax,
                string? unsupportedSyntaxNotice)
            {
                _clauses = clauses;
                HasUnsupportedNativeSyntax = hasUnsupportedNativeSyntax;
                UnsupportedSyntaxNotice = unsupportedSyntaxNotice;
            }

            public bool MatchesPathAndKind(string name, string relativePath, bool isDirectory)
            {
                for (int i = 0; i < _clauses.Length; i++)
                {
                    if (!_clauses[i].MayMatchPathAndKind(name, relativePath, isDirectory))
                        return false;
                }

                return true;
            }

            public bool MatchesEntry(string name, string relativePath, in SearchResultEntry entry)
            {
                for (int i = 0; i < _clauses.Length; i++)
                {
                    if (!_clauses[i].MatchesEntry(name, relativePath, in entry))
                        return false;
                }

                return true;
            }
        }

        private static readonly Dictionary<string, string[]> KnownTypeExtensions =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["doc"] = new[] { "txt", "md", "markdown", "doc", "docx", "pdf", "xls", "xlsx", "ppt", "pptx", "csv", "rtf", "odt" },
                ["document"] = new[] { "txt", "md", "markdown", "doc", "docx", "pdf", "xls", "xlsx", "ppt", "pptx", "csv", "rtf", "odt" },
                ["pic"] = new[] { "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tif", "tiff", "heic", "avif" },
                ["image"] = new[] { "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tif", "tiff", "heic", "avif" },
                ["photo"] = new[] { "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico", "svg", "tif", "tiff", "heic", "avif" },
                ["audio"] = new[] { "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus" },
                ["music"] = new[] { "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus" },
                ["video"] = new[] { "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "ts" },
                ["archive"] = new[] { "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "iso", "cab" },
                ["zip"] = new[] { "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "iso", "cab" },
                ["code"] = new[] { "cs", "csproj", "sln", "xaml", "js", "ts", "tsx", "jsx", "py", "go", "rs", "c", "cpp", "h", "hpp", "java", "kt", "json", "xml", "yaml", "yml", "toml", "ps1", "sh", "cmd", "bat", "html", "css", "scss", "sql" },
                ["exe"] = new[] { "exe", "msi", "bat", "cmd", "ps1", "com", "msix" }
            };

        internal static ParsedSearchQuery ParseQuery(string query)
        {
            List<List<string>> andGroups = TokenizeConjunctionOfDisjunctions(query ?? string.Empty);
            if (andGroups.Count == 0)
            {
                return new ParsedSearchQuery(
                    Array.Empty<SearchClause>(),
                    hasUnsupportedNativeSyntax: false,
                    unsupportedSyntaxNotice: null);
            }

            var clauses = new List<SearchClause>(andGroups.Count);
            bool hasUnsupported = false;
            string? notice = null;

            for (int g = 0; g < andGroups.Count; g++)
            {
                List<string> orAtoms = andGroups[g];
                if (orAtoms.Count == 0)
                    continue;

                if (orAtoms.Count == 1)
                {
                    SearchClause? single = ParseSingleAtomClause(orAtoms[0], out bool atomUnsupported, out string? atomNotice);
                    if (atomUnsupported)
                    {
                        hasUnsupported = true;
                        notice ??= atomNotice;
                        break;
                    }
                    if (single != null)
                    {
                        clauses.Add(single);
                    }
                    continue;
                }

                var branchClauses = new List<SearchClause>(orAtoms.Count);
                for (int b = 0; b < orAtoms.Count; b++)
                {
                    SearchClause? branch = ParseSingleAtomClause(orAtoms[b], out bool branchUnsupported, out string? branchNotice);
                    if (branchUnsupported)
                    {
                        hasUnsupported = true;
                        notice ??= branchNotice;
                        break;
                    }
                    if (branch != null)
                    {
                        branchClauses.Add(branch);
                    }
                }

                if (hasUnsupported)
                    break;

                if (branchClauses.Count == 1)
                {
                    clauses.Add(branchClauses[0]);
                }
                else if (branchClauses.Count > 1)
                {
                    clauses.Add(new SearchClause(branchClauses.ToArray()));
                }
            }

            return new ParsedSearchQuery(
                clauses.ToArray(),
                hasUnsupported,
                notice);
        }

        // These filters are LeafTab's documented semantics, not Everything aliases:
        // type: is an extension category, path: is relative, and size: is in exact
        // bytes. Evaluate the complete query locally rather than prefiltering with
        // an incompatible IPC query and permanently losing possible matches.
        // Queries using Everything-only functions retain the existing IPC path.
        internal static bool RequiresNativeQuerySemantics(string query, ParsedSearchQuery parsedQuery)
        {
            if (parsedQuery.HasUnsupportedNativeSyntax)
                return false;

            foreach (List<string> group in TokenizeConjunctionOfDisjunctions(query))
            {
                if (group.Count > 1)
                    return true; // Preserve LeafTab's OR-before-AND regardless of Everything settings.
                foreach (string atom in group)
                {
                    int colon = atom.IndexOf(':');
                    if (colon >= 0)
                    {
                        string prefix = atom.Substring(0, colon);
                        string argument = atom.Substring(colon + 1);
                        if (string.Equals(prefix, "ext", StringComparison.OrdinalIgnoreCase) &&
                            IsSimpleEverythingExtensionList(argument))
                            continue;
                        if ((string.Equals(prefix, "file", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(prefix, "folder", StringComparison.OrdinalIgnoreCase)) &&
                            IsPlainEverythingNameTerm(argument))
                            continue;
                        return true;
                    }
                    if (!IsPlainEverythingNameTerm(atom)) return true;
                }
            }
            return false;
        }

        private static bool IsPlainEverythingNameTerm(string text) =>
            text.IndexOfAny(new[] { ':', '\\', '/', '<', '>', '|', '!' }) < 0;

        private static bool IsSimpleEverythingExtensionList(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string extension in text.Split(';'))
            {
                if (extension.Length == 0) return false;
                foreach (char c in extension)
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-'))
                        return false;
            }
            return true;
        }

        private static SearchClause? ParseSingleAtomClause(string token, out bool hasUnsupported, out string? notice)
        {
            hasUnsupported = false;
            notice = null;

            if (string.IsNullOrWhiteSpace(token))
                return null;

            // Chained Everything path+regex modifiers ("path:regex:<pattern>" or "regex:path:<pattern>")
            if (token.StartsWith("path:regex:", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("regex:path:", StringComparison.OrdinalIgnoreCase))
            {
                string rxPattern = token.Substring("path:regex:".Length);
                return BuildRegexClause(rxPattern, matchRelativePath: true, out hasUnsupported, out notice);
            }

            int colonIdx = token.IndexOf(':');
            // Treat single-letter Windows drive tokens (e.g. "C:", "D:\foo") as normal path/name terms, not filter operators
            bool isDriveSpec = colonIdx == 1 &&
                               ((token[0] >= 'A' && token[0] <= 'Z') || (token[0] >= 'a' && token[0] <= 'z'));
            if (colonIdx >= 0 && !isDriveSpec)
            {
                string prefix = token.Substring(0, colonIdx);
                string arg = token.Substring(colonIdx + 1);

                if (string.Equals(prefix, "ext", StringComparison.OrdinalIgnoreCase))
                {
                    return new SearchClause(BuildExtensionMatcher(arg), null, requireDirectory: false, requireFile: true);
                }

                if (string.Equals(prefix, "size", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseSizeFilter(arg, out ulong minBytes, out ulong maxBytes))
                    {
                        hasUnsupported = true;
                        notice = $"大小筛选条件“{token}”格式无效（示例：size:>1mb、size:10kb..5mb、size:empty）。";
                        return null;
                    }

                    ulong capturedMin = minBytes;
                    ulong capturedMax = maxBytes;
                    return new SearchClause(
                        pathPredicate: null,
                        metadataPredicate: entry => entry.HasFileSize && entry.FileSize >= capturedMin && entry.FileSize <= capturedMax,
                        requireDirectory: false,
                        requireFile: true);
                }

                if (string.Equals(prefix, "dm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefix, "datemodified", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefix, "date", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseDateModifiedFilter(arg, out long minFileTimeUtc, out long maxFileTimeUtc))
                    {
                        hasUnsupported = true;
                        notice = $"日期筛选条件“{token}”格式无效（示例：dm:today、dm:>2026-01-01、dm:2026-01-01..2026-12-31）。";
                        return null;
                    }

                    long capturedMinFt = minFileTimeUtc;
                    long capturedMaxFt = maxFileTimeUtc;
                    return new SearchClause(
                        pathPredicate: null,
                        metadataPredicate: entry =>
                        {
                            long ft = FileTimeToTicks(entry.LastWriteTime);
                            return entry.HasLastWriteTime && ft >= capturedMinFt && ft <= capturedMaxFt;
                        },
                        requireDirectory: false,
                        requireFile: false);
                }

                if (string.Equals(prefix, "folder", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefix, "dir", StringComparison.OrdinalIgnoreCase))
                {
                    Func<string, string, bool>? pred = string.IsNullOrWhiteSpace(arg)
                        ? null
                        : BuildSingleTokenMatcher(arg, forceRelativePath: false);
                    return new SearchClause(pred, null, requireDirectory: true, requireFile: false);
                }

                if (string.Equals(prefix, "file", StringComparison.OrdinalIgnoreCase))
                {
                    Func<string, string, bool>? pred = string.IsNullOrWhiteSpace(arg)
                        ? null
                        : BuildSingleTokenMatcher(arg, forceRelativePath: false);
                    return new SearchClause(pred, null, requireDirectory: false, requireFile: true);
                }

                if (string.Equals(prefix, "path", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(arg))
                        return null;

                    return new SearchClause(
                        BuildSingleTokenMatcher(arg, forceRelativePath: true),
                        metadataPredicate: null,
                        requireDirectory: false,
                        requireFile: false);
                }

                if (string.Equals(prefix, "type", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prefix, "kind", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(arg, "folder", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "dir", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "folders", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SearchClause(null, null, requireDirectory: true, requireFile: false);
                    }

                    if (string.Equals(arg, "file", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "files", StringComparison.OrdinalIgnoreCase))
                    {
                        return new SearchClause(null, null, requireDirectory: false, requireFile: true);
                    }

                    if (KnownTypeExtensions.TryGetValue(arg, out string[]? categoryExts))
                    {
                        var set = new HashSet<string>(categoryExts, StringComparer.OrdinalIgnoreCase);
                        return new SearchClause(
                            (name, _) => set.Contains(GetExtensionWithoutDot(name)),
                            metadataPredicate: null,
                            requireDirectory: false,
                            requireFile: true);
                    }

                    hasUnsupported = true;
                    notice = $"暂不支持类型筛选“{token}”（支持 folder / file / doc / image / audio / video / archive / code / exe）。";
                    return null;
                }

                if (string.Equals(prefix, "regex", StringComparison.OrdinalIgnoreCase))
                {
                    // Everything regex: matches filename by default so regex escapes (\d, \., \s) never break ^ anchors in subdirectories
                    return BuildRegexClause(arg, matchRelativePath: false, out hasUnsupported, out notice);
                }

                hasUnsupported = true;
                string label = string.IsNullOrEmpty(prefix) ? token : (prefix + ":");
                notice = $"当前未运行 Everything，扫描模式暂不支持“{label}”高级语法（已原生支持 ext: / size: / dm: / type: / file: / folder: / path: / regex:）。";
                return null;
            }

            if (token.IndexOf('<') >= 0 || token.IndexOf('>') >= 0)
            {
                hasUnsupported = true;
                notice = $"无法识别筛选词“{token}”（大小或日期筛选请使用 size:>1mb 或 dm:>2026-01-01）。";
                return null;
            }

            return new SearchClause(
                BuildSingleTokenMatcher(token, forceRelativePath: false),
                metadataPredicate: null,
                requireDirectory: false,
                requireFile: false);
        }

        private static SearchClause? BuildRegexClause(
            string pattern,
            bool matchRelativePath,
            out bool hasUnsupported,
            out string? notice)
        {
            hasUnsupported = false;
            notice = null;

            if (string.IsNullOrEmpty(pattern))
            {
                hasUnsupported = true;
                notice = "正则表达式筛选“regex:”缺少匹配模式。";
                return null;
            }

            try
            {
                var rx = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));

                return new SearchClause(
                    pathPredicate: (name, rel) =>
                    {
                        try
                        {
                            if (!matchRelativePath)
                                return rx.IsMatch(name);

                            if (rx.IsMatch(rel))
                                return true;

                            return rel.IndexOf('\\') >= 0 && rx.IsMatch(rel.Replace('\\', '/'));
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            return false;
                        }
                    },
                    metadataPredicate: null,
                    requireDirectory: false,
                    requireFile: false);
            }
            catch (ArgumentException)
            {
                hasUnsupported = true;
                notice = $"正则表达式“{pattern}”语法无效。";
                return null;
            }
        }

        internal static Func<string, string, bool> BuildEntryMatcher(string query)
        {
            ParsedSearchQuery parsed = ParseQuery(query);
            if (parsed.HasUnsupportedNativeSyntax)
                return (_, _) => false;

            return (name, rel) => parsed.MatchesPathAndKind(name, rel, isDirectory: false) ||
                                  parsed.MatchesPathAndKind(name, rel, isDirectory: true);
        }

        private static Func<string, string, bool> BuildExtensionMatcher(string rawExtList)
        {
            if (string.IsNullOrWhiteSpace(rawExtList))
            {
                return (name, _) => string.IsNullOrEmpty(GetExtensionWithoutDot(name));
            }

            string[] parts = rawExtList.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                    part = part.Substring(4).Trim();
                string cleaned = part.TrimStart('.');
                if (cleaned.Length > 0)
                    extSet.Add(cleaned);
            }

            if (extSet.Count == 0)
                return (name, _) => string.IsNullOrEmpty(GetExtensionWithoutDot(name));

            return (name, _) => extSet.Contains(GetExtensionWithoutDot(name));
        }

        private static string GetExtensionWithoutDot(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            int dot = fileName.LastIndexOf('.');
            if (dot < 0 || dot == fileName.Length - 1)
                return string.Empty;

            return fileName.Substring(dot + 1);
        }

        /// <summary>
        /// Splits a search query into AND-conjoined groups of OR-disjoined atoms:
        /// - Preserves quoted phrases ("my file.txt" | size:&gt;4kb)
        /// - Treats both spaced ("size:&gt;4kb | ext:txt") and unspaced ("size:&gt;4kb|ext:txt") '|' as OR separators
        /// - Keeps '|' inside a single regex: token ("regex:^(foo|bar)\.txt$") intact as regex alternation,
        ///   while still allowing spaced '|' between regex and other clauses ("regex:^a\.txt$ | size:&gt;4kb").
        /// </summary>
        private static List<List<string>> TokenizeConjunctionOfDisjunctions(string query)
        {
            var andGroups = new List<List<string>>(4);
            var currentOrGroup = new List<string>(2);
            var currentAtom = new StringBuilder();
            bool inQuotes = false;
            bool pendingOrContinuation = false;

            bool IsCurrentAtomRegex()
            {
                if (currentAtom.Length < 6)
                    return false;
                string cur = currentAtom.ToString();
                return cur.StartsWith("regex:", StringComparison.OrdinalIgnoreCase) ||
                       cur.StartsWith("path:regex:", StringComparison.OrdinalIgnoreCase) ||
                       cur.StartsWith("regex:path:", StringComparison.OrdinalIgnoreCase);
            }

            for (int i = 0; i < query.Length; i++)
            {
                char ch = query[i];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    if (currentAtom.Length > 0)
                    {
                        currentOrGroup.Add(currentAtom.ToString());
                        currentAtom.Clear();
                        pendingOrContinuation = false;
                    }

                    int nextNonWs = i + 1;
                    while (nextNonWs < query.Length && char.IsWhiteSpace(query[nextNonWs]))
                        nextNonWs++;

                    if (!pendingOrContinuation && currentOrGroup.Count > 0)
                    {
                        bool nextIsOrPipe = nextNonWs < query.Length && query[nextNonWs] == '|';
                        if (!nextIsOrPipe)
                        {
                            andGroups.Add(currentOrGroup);
                            currentOrGroup = new List<string>(2);
                        }
                    }

                    i = nextNonWs - 1;
                    continue;
                }

                if (!inQuotes && ch == '|' && !IsCurrentAtomRegex())
                {
                    if (currentAtom.Length > 0)
                    {
                        currentOrGroup.Add(currentAtom.ToString());
                        currentAtom.Clear();
                    }
                    pendingOrContinuation = true;
                    continue;
                }

                currentAtom.Append(ch);
            }

            if (currentAtom.Length > 0)
            {
                currentOrGroup.Add(currentAtom.ToString());
            }

            if (currentOrGroup.Count > 0)
            {
                andGroups.Add(currentOrGroup);
            }

            return andGroups;
        }

        private static Func<string, string, bool> BuildSingleTokenMatcher(string token, bool forceRelativePath)
        {
            string normalized = token.Replace('/', '\\');
            bool matchAgainstRelative = forceRelativePath || normalized.IndexOf('\\') >= 0;

            if (normalized.IndexOf('*') >= 0 || normalized.IndexOf('?') >= 0)
            {
                string escaped = Regex.Escape(normalized).Replace(@"\*", ".*").Replace(@"\?", ".");
                string pattern = matchAgainstRelative ? escaped : ("^" + escaped + "$");
                var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return matchAgainstRelative
                    ? new Func<string, string, bool>((_, rel) => rx.IsMatch(rel))
                    : ((name, _) => rx.IsMatch(name));
            }

            return matchAgainstRelative
                ? new Func<string, string, bool>((_, rel) => rel.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                : ((name, _) => name.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool TryParseSizeFilter(string expr, out ulong minBytes, out ulong maxBytes)
        {
            minBytes = 0;
            maxBytes = ulong.MaxValue;
            if (string.IsNullOrWhiteSpace(expr))
                return false;

            string s = expr.Trim();
            switch (s.ToLowerInvariant())
            {
                case "empty":
                    minBytes = 0;
                    maxBytes = 0;
                    return true;
                case "tiny":
                    minBytes = 1;
                    maxBytes = 10UL * 1024UL;
                    return true;
                case "small":
                    minBytes = 10UL * 1024UL + 1UL;
                    maxBytes = 100UL * 1024UL;
                    return true;
                case "medium":
                    minBytes = 100UL * 1024UL + 1UL;
                    maxBytes = 1024UL * 1024UL;
                    return true;
                case "large":
                    minBytes = 1024UL * 1024UL + 1UL;
                    maxBytes = 16UL * 1024UL * 1024UL;
                    return true;
                case "huge":
                    minBytes = 16UL * 1024UL * 1024UL + 1UL;
                    maxBytes = 128UL * 1024UL * 1024UL;
                    return true;
                case "gigantic":
                case "giant":
                    minBytes = 128UL * 1024UL * 1024UL + 1UL;
                    maxBytes = ulong.MaxValue;
                    return true;
            }

            int rangeDots = s.IndexOf("..", StringComparison.Ordinal);
            if (rangeDots >= 0)
            {
                string left = s.Substring(0, rangeDots).Trim();
                string right = s.Substring(rangeDots + 2).Trim();
                if (left.Length == 0 && right.Length == 0)
                    return false;

                if (left.Length > 0 && !TryParseByteSizeLiteral(left, out minBytes))
                    return false;
                if (right.Length > 0 && !TryParseByteSizeLiteral(right, out maxBytes))
                    return false;

                return minBytes <= maxBytes;
            }

            if (s.StartsWith(">=", StringComparison.Ordinal))
            {
                if (!TryParseByteSizeLiteral(s.Substring(2), out ulong v))
                    return false;
                minBytes = v;
                return true;
            }

            if (s.StartsWith(">", StringComparison.Ordinal))
            {
                if (!TryParseByteSizeLiteral(s.Substring(1), out ulong v))
                    return false;
                minBytes = v == ulong.MaxValue ? ulong.MaxValue : (v + 1UL);
                return true;
            }

            if (s.StartsWith("<=", StringComparison.Ordinal))
            {
                if (!TryParseByteSizeLiteral(s.Substring(2), out ulong v))
                    return false;
                maxBytes = v;
                return true;
            }

            if (s.StartsWith("<", StringComparison.Ordinal))
            {
                if (!TryParseByteSizeLiteral(s.Substring(1), out ulong v))
                    return false;
                if (v == 0)
                {
                    minBytes = 1;
                    maxBytes = 0;
                    return true;
                }
                maxBytes = v - 1UL;
                return true;
            }

            if (s.StartsWith("=", StringComparison.Ordinal))
                s = s.Substring(1).Trim();

            if (!TryParseByteSizeLiteral(s, out ulong exact))
                return false;

            minBytes = exact;
            maxBytes = exact;
            return true;
        }

        private static bool TryParseByteSizeLiteral(string text, out ulong bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            int unitIdx = 0;
            while (unitIdx < trimmed.Length && (char.IsDigit(trimmed[unitIdx]) || trimmed[unitIdx] == '.'))
            {
                unitIdx++;
            }

            if (unitIdx == 0)
                return false;

            string numPart = trimmed.Substring(0, unitIdx);
            string unitPart = trimmed.Substring(unitIdx).Trim().ToLowerInvariant();

            if (!double.TryParse(numPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double val) ||
                double.IsNaN(val) || double.IsInfinity(val) || val < 0)
            {
                return false;
            }

            ulong multiplier;
            switch (unitPart)
            {
                case "":
                case "b":
                case "byte":
                case "bytes":
                    multiplier = 1UL;
                    break;
                case "k":
                case "kb":
                case "kib":
                    multiplier = 1024UL;
                    break;
                case "m":
                case "mb":
                case "mib":
                    multiplier = 1024UL * 1024UL;
                    break;
                case "g":
                case "gb":
                case "gib":
                    multiplier = 1024UL * 1024UL * 1024UL;
                    break;
                case "t":
                case "tb":
                case "tib":
                    multiplier = 1024UL * 1024UL * 1024UL * 1024UL;
                    break;
                default:
                    return false;
            }

            double scaled = Math.Round(val * multiplier);
            if (scaled >= ulong.MaxValue)
            {
                bytes = ulong.MaxValue;
                return true;
            }

            bytes = (ulong)scaled;
            return true;
        }

        private static bool TryParseDateModifiedFilter(string expr, out long minFileTimeUtc, out long maxFileTimeUtc)
        {
            minFileTimeUtc = 0;
            maxFileTimeUtc = long.MaxValue;
            if (string.IsNullOrWhiteSpace(expr))
                return false;

            string s = expr.Trim();
            DateTime todayLocal = DateTime.Now.Date;
            DateTime tomorrowLocal = todayLocal.AddDays(1);

            switch (s.ToLowerInvariant())
            {
                case "today":
                    minFileTimeUtc = ToSafeFileTimeUtc(todayLocal);
                    maxFileTimeUtc = ToSafeFileTimeUtc(tomorrowLocal) - 1;
                    return true;
                case "yesterday":
                    minFileTimeUtc = ToSafeFileTimeUtc(todayLocal.AddDays(-1));
                    maxFileTimeUtc = ToSafeFileTimeUtc(todayLocal) - 1;
                    return true;
                case "thisweek":
                case "week":
                {
                    int delta = (7 + (int)todayLocal.DayOfWeek - (int)DayOfWeek.Monday) % 7;
                    minFileTimeUtc = ToSafeFileTimeUtc(todayLocal.AddDays(-delta));
                    maxFileTimeUtc = ToSafeFileTimeUtc(tomorrowLocal) - 1;
                    return true;
                }
                case "pastweek":
                case "last7days":
                    minFileTimeUtc = ToSafeFileTimeUtc(todayLocal.AddDays(-7));
                    maxFileTimeUtc = long.MaxValue;
                    return true;
                case "thismonth":
                case "month":
                    minFileTimeUtc = ToSafeFileTimeUtc(new DateTime(todayLocal.Year, todayLocal.Month, 1, 0, 0, 0, DateTimeKind.Local));
                    maxFileTimeUtc = ToSafeFileTimeUtc(tomorrowLocal) - 1;
                    return true;
                case "thisyear":
                case "year":
                    minFileTimeUtc = ToSafeFileTimeUtc(new DateTime(todayLocal.Year, 1, 1, 0, 0, 0, DateTimeKind.Local));
                    maxFileTimeUtc = ToSafeFileTimeUtc(tomorrowLocal) - 1;
                    return true;
            }

            int rangeDots = s.IndexOf("..", StringComparison.Ordinal);
            if (rangeDots >= 0)
            {
                string left = s.Substring(0, rangeDots).Trim();
                string right = s.Substring(rangeDots + 2).Trim();
                if (left.Length == 0 && right.Length == 0)
                    return false;

                if (left.Length > 0)
                {
                    if (!TryParseLocalDateBounds(left, out DateTime leftStart, out _))
                        return false;
                    minFileTimeUtc = ToSafeFileTimeUtc(leftStart);
                }

                if (right.Length > 0)
                {
                    if (!TryParseLocalDateBounds(right, out _, out DateTime rightEndExclusive))
                        return false;
                    maxFileTimeUtc = Math.Max(0, ToSafeFileTimeUtc(rightEndExclusive) - 1);
                }

                return minFileTimeUtc <= maxFileTimeUtc;
            }

            if (s.StartsWith(">=", StringComparison.Ordinal))
            {
                if (!TryParseLocalDateBounds(s.Substring(2), out DateTime start, out _))
                    return false;
                minFileTimeUtc = ToSafeFileTimeUtc(start);
                return true;
            }

            if (s.StartsWith(">", StringComparison.Ordinal))
            {
                if (!TryParseLocalDateBounds(s.Substring(1), out _, out DateTime endExclusive))
                    return false;
                minFileTimeUtc = ToSafeFileTimeUtc(endExclusive);
                return true;
            }

            if (s.StartsWith("<=", StringComparison.Ordinal))
            {
                if (!TryParseLocalDateBounds(s.Substring(2), out _, out DateTime endExclusive))
                    return false;
                maxFileTimeUtc = Math.Max(0, ToSafeFileTimeUtc(endExclusive) - 1);
                return true;
            }

            if (s.StartsWith("<", StringComparison.Ordinal))
            {
                if (!TryParseLocalDateBounds(s.Substring(1), out DateTime start, out _))
                    return false;
                maxFileTimeUtc = Math.Max(0, ToSafeFileTimeUtc(start) - 1);
                return true;
            }

            if (s.StartsWith("=", StringComparison.Ordinal))
                s = s.Substring(1).Trim();

            if (!TryParseLocalDateBounds(s, out DateTime exactStart, out DateTime exactEndExclusive))
                return false;

            minFileTimeUtc = ToSafeFileTimeUtc(exactStart);
            maxFileTimeUtc = Math.Max(0, ToSafeFileTimeUtc(exactEndExclusive) - 1);
            return true;
        }

        private static bool TryParseLocalDateBounds(string text, out DateTime startLocal, out DateTime endExclusiveLocal)
        {
            startLocal = default;
            endExclusiveLocal = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string s = text.Trim();

            // Year only: e.g. 2026
            if (s.Length == 4 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int yearOnly) &&
                yearOnly >= 1700 && yearOnly <= 9998)
            {
                startLocal = new DateTime(yearOnly, 1, 1, 0, 0, 0, DateTimeKind.Local);
                endExclusiveLocal = startLocal.AddYears(1);
                return true;
            }

            // Year-Month: e.g. 2026-11 or 2026/11
            if ((s.Length == 6 || s.Length == 7) && (s.IndexOf('-') == 4 || s.IndexOf('/') == 4))
            {
                string[] ym = s.Split('-', '/');
                if (ym.Length == 2 &&
                    int.TryParse(ym[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ymYear) &&
                    int.TryParse(ym[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ymMonth) &&
                    ymYear >= 1700 && ymYear <= 9998 && ymMonth >= 1 && ymMonth <= 12)
                {
                    startLocal = new DateTime(ymYear, ymMonth, 1, 0, 0, 0, DateTimeKind.Local);
                    endExclusiveLocal = startLocal.AddMonths(1);
                    return true;
                }
            }

            string[] dateFormats =
            {
                "yyyy-M-d",
                "yyyy/M/d",
                "yyyy.M.d",
                "yyyyMMdd"
            };

            if (DateTime.TryParseExact(s, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate) &&
                parsedDate.Year >= 1700 && parsedDate.Year <= 9998)
            {
                startLocal = DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Local);
                endExclusiveLocal = startLocal.AddDays(1);
                return true;
            }

            return false;
        }

        private static long ToSafeFileTimeUtc(DateTime localTime)
        {
            DateTime utc = localTime.Kind == DateTimeKind.Utc ? localTime : localTime.ToUniversalTime();
            if (utc.Year < 1602)
                return 0;
            return utc.ToFileTimeUtc();
        }

        private static long FileTimeToTicks(FILETIME ft) =>
            ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }
}
