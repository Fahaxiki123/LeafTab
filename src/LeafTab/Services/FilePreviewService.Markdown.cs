using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LeafTab.Services
{
    internal enum MdInlineKind
    {
        Plain,
        InlineCode,
        ImageLabel,
        Link,
        BoldItalic,
        Bold,
        Strikethrough,
        Italic,
        LineBreak
    }

    internal sealed class MdInlineNode
    {
        public MdInlineKind Kind { get; }
        public string Text { get; }
        public string? Url { get; }
        public MdInlineNode[]? Children { get; }

        public MdInlineNode(MdInlineKind kind, string text, string? url = null, MdInlineNode[]? children = null)
        {
            Kind = kind;
            Text = text;
            Url = url;
            Children = children;
        }
    }

    internal abstract class MdBlockNode
    {
    }

    internal sealed class MdFrontmatterBlock : MdBlockNode
    {
        public string Content { get; }
        public MdFrontmatterBlock(string content) => Content = content;
    }

    internal sealed class MdCodeFenceBlock : MdBlockNode
    {
        public string InfoString { get; }
        public string CodeText { get; }
        public MdCodeFenceBlock(string infoString, string codeText)
        {
            InfoString = infoString;
            CodeText = codeText;
        }
    }

    internal sealed class MdHeadingBlock : MdBlockNode
    {
        public int Level { get; }
        public MdInlineNode[] Inlines { get; }
        public MdHeadingBlock(int level, MdInlineNode[] inlines)
        {
            Level = level;
            Inlines = inlines;
        }
    }

    internal sealed class MdHorizontalRuleBlock : MdBlockNode
    {
        public static readonly MdHorizontalRuleBlock Instance = new MdHorizontalRuleBlock();
    }

    internal sealed class MdTableBlock : MdBlockNode
    {
        public MdInlineNode[][] Headers { get; }
        public MdInlineNode[][][] Rows { get; }
        public int OmittedRowCount { get; }

        public MdTableBlock(MdInlineNode[][] headers, MdInlineNode[][][] rows, int omittedRowCount)
        {
            Headers = headers;
            Rows = rows;
            OmittedRowCount = omittedRowCount;
        }
    }

    internal sealed class MdBlockquoteBlock : MdBlockNode
    {
        public MdBlockNode[] InnerBlocks { get; }
        public MdBlockquoteBlock(MdBlockNode[] innerBlocks) => InnerBlocks = innerBlocks;
    }

    internal sealed class MdListItemData
    {
        public MdInlineNode[] Inlines { get; }
        public bool IsTask { get; }
        public bool TaskDone { get; }

        public MdListItemData(MdInlineNode[] inlines, bool isTask, bool taskDone)
        {
            Inlines = inlines;
            IsTask = isTask;
            TaskDone = taskDone;
        }
    }

    internal sealed class MdListBlock : MdBlockNode
    {
        public bool IsOrdered { get; }
        public bool AnyTask { get; }
        public MdListItemData[] Items { get; }

        public MdListBlock(bool isOrdered, bool anyTask, MdListItemData[] items)
        {
            IsOrdered = isOrdered;
            AnyTask = anyTask;
            Items = items;
        }
    }

    internal sealed class MdParagraphBlock : MdBlockNode
    {
        public MdInlineNode[] Inlines { get; }
        public MdParagraphBlock(MdInlineNode[] inlines) => Inlines = inlines;
    }

    internal sealed class MarkdownParsedDocument
    {
        public MdBlockNode[] Blocks { get; }
        public bool ExceedsComplexityBudget { get; }
        public string ComplexityNotice { get; }
        public int RenderedBlockCount { get; }
        public int RenderedInlineCount { get; }
        public int TotalTableRowsSeen { get; }
        public int RenderedTableRows { get; }

        public MarkdownParsedDocument(
            MdBlockNode[] blocks,
            bool exceedsComplexityBudget,
            string complexityNotice,
            int renderedBlockCount,
            int renderedInlineCount,
            int totalTableRowsSeen,
            int renderedTableRows)
        {
            Blocks = blocks;
            ExceedsComplexityBudget = exceedsComplexityBudget;
            ComplexityNotice = complexityNotice;
            RenderedBlockCount = renderedBlockCount;
            RenderedInlineCount = renderedInlineCount;
            TotalTableRowsSeen = totalTableRowsSeen;
            RenderedTableRows = renderedTableRows;
        }
    }

    internal static partial class FilePreviewService
    {
        // Complexity budgets preventing UI-thread WPF FlowDocument construction stalls on dense/synthetic Markdown
        public const int MaxMarkdownBlocks = 320;
        public const int MaxMarkdownTableRows = 120;
        public const int MaxMarkdownTableCells = 800;
        public const int MaxMarkdownInlineNodes = 2400;
        public const int MaxMarkdownParagraphLines = 1200;
        public const int MaxMarkdownCodeFenceLines = 800;

        private sealed class MarkdownParseBudget
        {
            public int BlockCount;
            public int TableRowsSeen;
            public int TableRowsKept;
            public int TableCellsKept;
            public int InlineNodes;
            public bool Exceeded;
            public string? Reason;

            public bool IsInlineBudgetExhausted => InlineNodes >= MaxMarkdownInlineNodes;

            public void FlagExceeded(string reason)
            {
                if (!Exceeded)
                {
                    Exceeded = true;
                    Reason = reason;
                }
            }

            public bool TryConsumeInlineNodes(int count = 1)
            {
                if (InlineNodes + count > MaxMarkdownInlineNodes)
                {
                    InlineNodes = MaxMarkdownInlineNodes;
                    FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    return false;
                }

                InlineNodes += count;
                return true;
            }

            public bool CanAddBlock()
            {
                if (InlineNodes >= MaxMarkdownInlineNodes)
                {
                    FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    return false;
                }

                if (BlockCount >= MaxMarkdownBlocks)
                {
                    FlagExceeded($"文档段落与块级结构超过 {MaxMarkdownBlocks} 项，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    return false;
                }

                BlockCount++;
                return true;
            }
        }

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

        // ==================== STAGE 1: BACKGROUND THREAD-SAFE MARKDOWN PARSER ====================

        internal static MarkdownParsedDocument ParseMarkdownStructure(string? markdown)
        {
            string normalized = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length == 0)
            {
                return new MarkdownParsedDocument(
                    Array.Empty<MdBlockNode>(),
                    exceedsComplexityBudget: false,
                    complexityNotice: string.Empty,
                    renderedBlockCount: 0,
                    renderedInlineCount: 0,
                    totalTableRowsSeen: 0,
                    renderedTableRows: 0);
            }

            string[] lines = normalized.Split('\n');
            var budget = new MarkdownParseBudget();
            MdBlockNode[] blocks = ParseMarkdownLinesAst(lines, 0, lines.Length, depth: 0, budget);

            return new MarkdownParsedDocument(
                blocks,
                budget.Exceeded,
                budget.Reason ?? string.Empty,
                budget.BlockCount,
                budget.InlineNodes,
                budget.TableRowsSeen,
                budget.TableRowsKept);
        }

        private static MdBlockNode[] ParseMarkdownLinesAst(
            string[] lines,
            int startIndex,
            int endIndex,
            int depth,
            MarkdownParseBudget budget)
        {
            var blocks = new List<MdBlockNode>(Math.Min(64, Math.Max(4, (endIndex - startIndex) / 2)));
            int i = startIndex;

            // YAML frontmatter at top of document
            if (depth == 0 && i == 0 && i < endIndex && lines[i].Trim() == "---")
            {
                int closing = FindFrontmatterClose(lines, i + 1, Math.Min(endIndex, i + 45));
                if (closing > i)
                {
                    if (!budget.CanAddBlock())
                        return blocks.ToArray();

                    string fmText = string.Join("\n", lines, i + 1, closing - (i + 1));
                    blocks.Add(new MdFrontmatterBlock(fmText));
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

                if (!budget.CanAddBlock())
                    break;

                // 1. Fenced code block (``` or ~~~)
                if (TryParseFenceHeader(line, out char fenceChar, out int fenceLen, out string infoString))
                {
                    int closeIdx = FindClosingFence(lines, i + 1, endIndex, fenceChar, fenceLen);
                    int contentEnd = closeIdx >= 0 ? closeIdx : endIndex;
                    int totalFenceLines = Math.Max(0, contentEnd - (i + 1));
                    int keptFenceLines = Math.Min(totalFenceLines, MaxMarkdownCodeFenceLines);
                    if (totalFenceLines > keptFenceLines)
                    {
                        budget.FlagExceeded($"代码块行数过多（共 {totalFenceLines} 行），已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    }

                    string codeText = keptFenceLines > 0
                        ? string.Join("\n", lines, i + 1, keptFenceLines) + (totalFenceLines > keptFenceLines ? "\n…" : string.Empty)
                        : string.Empty;

                    blocks.Add(new MdCodeFenceBlock(infoString, codeText));
                    i = closeIdx >= 0 ? closeIdx + 1 : endIndex;
                    continue;
                }

                // 2. ATX Heading (# .. ######)
                if (TryParseAtxHeading(line, out int headingLevel, out string headingText))
                {
                    blocks.Add(new MdHeadingBlock(headingLevel, ParseInlinesAst(headingText, budget, allowLinks: true)));
                    i++;
                    continue;
                }

                // 3. Horizontal Rule (---, ***, ___)
                if (IsHorizontalRule(line))
                {
                    blocks.Add(MdHorizontalRuleBlock.Instance);
                    i++;
                    continue;
                }

                // 4. Markdown Pipe Table (strictly bounded by row & cell complexity budgets)
                if (i + 1 < endIndex && LooksLikeTableRow(line) && IsTableSeparatorRow(lines[i + 1]))
                {
                    List<string> rawHeaders = SplitTableRow(line);
                    int colCount = Math.Max(1, Math.Min(rawHeaders.Count, 32));
                    if (rawHeaders.Count > 32)
                    {
                        budget.FlagExceeded($"表格列数过多（{rawHeaders.Count} 列），已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    }

                    var headerAst = new MdInlineNode[colCount][];
                    for (int c = 0; c < colCount; c++)
                    {
                        string hText = c < rawHeaders.Count ? rawHeaders[c] : string.Empty;
                        headerAst[c] = ParseInlinesAst(hText, budget, allowLinks: true);
                        budget.TableCellsKept++;
                    }

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

                    int totalTableDataRows = rowEnd - rowStart;
                    budget.TableRowsSeen += totalTableDataRows;

                    var keptRows = new List<MdInlineNode[][]>(Math.Min(totalTableDataRows, MaxMarkdownTableRows));
                    int omittedRows = 0;

                    for (int r = rowStart; r < rowEnd; r++)
                    {
                        if (budget.IsInlineBudgetExhausted ||
                            budget.TableRowsKept >= MaxMarkdownTableRows ||
                            budget.TableCellsKept + colCount > MaxMarkdownTableCells)
                        {
                            omittedRows = rowEnd - r;
                            budget.FlagExceeded(
                                $"Markdown 表格规模较大（共 {budget.TableRowsSeen} 行），已自动切换为源码极速预览（点击「渲染」可查看前 {budget.TableRowsKept} 行排版）");
                            break;
                        }

                        List<string> rowCells = SplitTableRow(lines[r]);
                        var rowAst = new MdInlineNode[colCount][];
                        for (int c = 0; c < colCount; c++)
                        {
                            string cellText = c < rowCells.Count ? rowCells[c] : string.Empty;
                            rowAst[c] = ParseInlinesAst(cellText, budget, allowLinks: true);
                        }

                        keptRows.Add(rowAst);
                        budget.TableRowsKept++;
                        budget.TableCellsKept += colCount;
                    }

                    blocks.Add(new MdTableBlock(headerAst, keptRows.ToArray(), omittedRows));
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

                    MdBlockNode[] innerBlocks;
                    if (depth < 3)
                    {
                        string[] innerArr = quotedLines.ToArray();
                        innerBlocks = ParseMarkdownLinesAst(innerArr, 0, innerArr.Length, depth + 1, budget);
                    }
                    else
                    {
                        innerBlocks = new MdBlockNode[]
                        {
                            new MdParagraphBlock(ParseInlinesAst(string.Join(" ", quotedLines), budget, allowLinks: true))
                        };
                    }

                    blocks.Add(new MdBlockquoteBlock(innerBlocks));
                    continue;
                }

                // 6. List (- / * / + or 1.) including GitHub task items (- [ ] / - [x])
                if (TryParseListItem(line, out bool isOrdered, out string firstItemBody, out bool isTask, out bool taskDone))
                {
                    var rawItems = new List<(string Body, bool IsTask, bool TaskDone)>();
                    bool anyTask = isTask;
                    rawItems.Add((firstItemBody, isTask, taskDone));
                    i++;

                    while (i < endIndex)
                    {
                        string nextLine = lines[i];
                        if (string.IsNullOrWhiteSpace(nextLine))
                        {
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
                            rawItems.Add((itemBody, itemTask, itemDone));
                            i++;
                            continue;
                        }

                        if ((nextLine.StartsWith("  ", StringComparison.Ordinal) || nextLine.StartsWith("\t", StringComparison.Ordinal)) &&
                            !TryParseFenceHeader(nextLine, out _, out _, out _) &&
                            !IsBlockquoteLine(nextLine))
                        {
                            int lastIdx = rawItems.Count - 1;
                            var prev = rawItems[lastIdx];
                            rawItems[lastIdx] = (prev.Body + " " + nextLine.Trim(), prev.IsTask, prev.TaskDone);
                            i++;
                            continue;
                        }

                        break;
                    }

                    int itemCap = Math.Min(rawItems.Count, MaxMarkdownBlocks);
                    if (rawItems.Count > itemCap)
                    {
                        budget.FlagExceeded($"列表项过多（共 {rawItems.Count} 项），已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    }

                    var parsedItems = new List<MdListItemData>(itemCap);
                    for (int li = 0; li < itemCap; li++)
                    {
                        if (li > 0 && !budget.CanAddBlock())
                            break;
                        var entry = rawItems[li];
                        parsedItems.Add(new MdListItemData(
                            ParseInlinesAst(entry.Body, budget, allowLinks: true),
                            entry.IsTask,
                            entry.TaskDone));
                    }

                    blocks.Add(new MdListBlock(isOrdered, anyTask, parsedItems.ToArray()));
                    continue;
                }

                // 7. Plain paragraph
                var paraLines = new List<string>();
                int totalParaLines = 0;
                while (i < endIndex)
                {
                    string pLine = lines[i];
                    if (string.IsNullOrWhiteSpace(pLine))
                        break;
                    if (totalParaLines > 0 &&
                        (TryParseFenceHeader(pLine, out _, out _, out _) ||
                         TryParseAtxHeading(pLine, out _, out _) ||
                         IsHorizontalRule(pLine) ||
                         IsBlockquoteLine(pLine) ||
                         TryParseListItem(pLine, out _, out _, out _, out _) ||
                         (i + 1 < endIndex && LooksLikeTableRow(pLine) && IsTableSeparatorRow(lines[i + 1]))))
                    {
                        break;
                    }

                    if (paraLines.Count < MaxMarkdownParagraphLines)
                    {
                        paraLines.Add(pLine);
                    }
                    totalParaLines++;
                    i++;
                }

                if (totalParaLines > MaxMarkdownParagraphLines)
                {
                    budget.FlagExceeded($"单段连续文本行数过多（共 {totalParaLines} 行），已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                }

                MdInlineNode[] paraInlines = ParseParagraphInlinesAst(paraLines, budget);
                if (paraInlines.Length > 0)
                {
                    blocks.Add(new MdParagraphBlock(paraInlines));
                }
            }

            return blocks.ToArray();
        }

        private static bool StripHardBreakMarker(string rawLine, out string trimmedLine)
        {
            if (string.IsNullOrEmpty(rawLine))
            {
                trimmedLine = string.Empty;
                return false;
            }

            if (rawLine.EndsWith("  ", StringComparison.Ordinal))
            {
                trimmedLine = rawLine.Trim();
                return true;
            }

            // CommonMark/GFM trailing single backslash hard line break (odd count of trailing '\')
            int trailingSlashes = 0;
            for (int k = rawLine.Length - 1; k >= 0 && rawLine[k] == '\\'; k--)
            {
                trailingSlashes++;
            }

            if ((trailingSlashes & 1) == 1)
            {
                trimmedLine = rawLine.Substring(0, rawLine.Length - 1).Trim();
                return true;
            }

            trimmedLine = rawLine.Trim();
            return false;
        }

        private static MdInlineNode[] ParseParagraphInlinesAst(List<string> lines, MarkdownParseBudget budget)
        {
            if (lines.Count == 0 || budget.IsInlineBudgetExhausted)
            {
                if (lines.Count > 0 && budget.IsInlineBudgetExhausted)
                {
                    budget.FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                }
                return Array.Empty<MdInlineNode>();
            }

            if (lines.Count == 1)
                return ParseInlinesAst(lines[0].Trim(), budget, allowLinks: true);

            var combined = new List<MdInlineNode>(Math.Min(lines.Count * 2, MaxMarkdownInlineNodes));
            for (int i = 0; i < lines.Count; i++)
            {
                if (budget.IsInlineBudgetExhausted)
                {
                    budget.FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    break;
                }

                string raw = lines[i];
                bool hardBreak = StripHardBreakMarker(raw, out string trimmed);
                MdInlineNode[] lineInlines = ParseInlinesAst(trimmed, budget, allowLinks: true);

                for (int k = 0; k < lineInlines.Length; k++)
                {
                    MdInlineNode item = lineInlines[k];
                    if (k == 0 &&
                        item.Kind == MdInlineKind.Plain &&
                        combined.Count > 0 &&
                        combined[combined.Count - 1].Kind == MdInlineKind.Plain &&
                        combined[combined.Count - 1].Text.Length + item.Text.Length < 4096)
                    {
                        // Coalesce adjacent soft-wrapped plain runs without allocating separate WPF Run objects
                        combined[combined.Count - 1] = new MdInlineNode(MdInlineKind.Plain, combined[combined.Count - 1].Text + item.Text);
                        if (budget.InlineNodes > 0)
                            budget.InlineNodes--;
                    }
                    else
                    {
                        combined.Add(item);
                    }
                }

                if (i < lines.Count - 1)
                {
                    if (budget.IsInlineBudgetExhausted)
                    {
                        budget.FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                        break;
                    }

                    if (hardBreak)
                    {
                        if (!budget.TryConsumeInlineNodes(1))
                            break;
                        combined.Add(new MdInlineNode(MdInlineKind.LineBreak, string.Empty));
                    }
                    else if (combined.Count > 0 && combined[combined.Count - 1].Kind == MdInlineKind.Plain)
                    {
                        combined[combined.Count - 1] = new MdInlineNode(MdInlineKind.Plain, combined[combined.Count - 1].Text + " ");
                    }
                    else
                    {
                        if (!budget.TryConsumeInlineNodes(1))
                            break;
                        combined.Add(new MdInlineNode(MdInlineKind.Plain, " "));
                    }
                }
            }

            return combined.ToArray();
        }

        private static MdInlineNode[] ParseInlinesAst(string text, MarkdownParseBudget budget, bool allowLinks, int inlineDepth = 0)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<MdInlineNode>();

            if (budget.IsInlineBudgetExhausted)
            {
                budget.FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                return Array.Empty<MdInlineNode>();
            }

            if (inlineDepth > 6)
            {
                if (!budget.TryConsumeInlineNodes(1))
                    return Array.Empty<MdInlineNode>();
                return new[] { new MdInlineNode(MdInlineKind.Plain, text) };
            }

            var nodes = new List<MdInlineNode>(4);
            var plainBuffer = new StringBuilder();
            int i = 0;

            bool FlushPlain()
            {
                if (plainBuffer.Length == 0)
                    return true;

                if (!budget.TryConsumeInlineNodes(1))
                {
                    plainBuffer.Clear();
                    return false;
                }

                nodes.Add(new MdInlineNode(MdInlineKind.Plain, plainBuffer.ToString()));
                plainBuffer.Clear();
                return true;
            }

            while (i < text.Length)
            {
                if (budget.IsInlineBudgetExhausted)
                {
                    budget.FlagExceeded($"文档富文本排版节点超过 {MaxMarkdownInlineNodes} 个，已自动切换为源码极速预览（点击「渲染」可预览前部排版）");
                    break;
                }

                char ch = text[i];

                if (ch == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
                {
                    plainBuffer.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close > i)
                    {
                        if (!FlushPlain() || !budget.TryConsumeInlineNodes(2))
                            break;
                        string code = text.Substring(i + 1, close - (i + 1));
                        nodes.Add(new MdInlineNode(MdInlineKind.InlineCode, code));
                        i = close + 1;
                        continue;
                    }
                }

                if (ch == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                    TryParseBracketUrl(text, i + 1, out string altText, out _, out int afterImg))
                {
                    if (!FlushPlain() || !budget.TryConsumeInlineNodes(1))
                        break;
                    string label = string.IsNullOrWhiteSpace(altText) ? "[图片]" : $"[图片: {altText}]";
                    nodes.Add(new MdInlineNode(MdInlineKind.ImageLabel, label));
                    i = afterImg;
                    continue;
                }

                if (allowLinks && ch == '[' && TryParseBracketUrl(text, i, out string linkLabel, out string linkUrl, out int afterLink))
                {
                    if (!FlushPlain() || !budget.TryConsumeInlineNodes(1))
                        break;
                    MdInlineNode[] labelChildren = ParseInlinesAst(linkLabel, budget, allowLinks: false, inlineDepth + 1);
                    nodes.Add(new MdInlineNode(MdInlineKind.Link, linkLabel, linkUrl.Trim(), labelChildren));
                    i = afterLink;
                    continue;
                }

                if (text.IndexOf("***", i, StringComparison.Ordinal) == i || text.IndexOf("___", i, StringComparison.Ordinal) == i)
                {
                    string marker = text.Substring(i, 3);
                    int close = text.IndexOf(marker, i + 3, StringComparison.Ordinal);
                    if (close > i + 3)
                    {
                        if (!FlushPlain() || !budget.TryConsumeInlineNodes(1))
                            break;
                        string inner = text.Substring(i + 3, close - (i + 3));
                        nodes.Add(new MdInlineNode(MdInlineKind.BoldItalic, inner, null, ParseInlinesAst(inner, budget, allowLinks, inlineDepth + 1)));
                        i = close + 3;
                        continue;
                    }
                }

                if (text.IndexOf("**", i, StringComparison.Ordinal) == i || text.IndexOf("__", i, StringComparison.Ordinal) == i)
                {
                    string marker = text.Substring(i, 2);
                    int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        if (!FlushPlain() || !budget.TryConsumeInlineNodes(1))
                            break;
                        string inner = text.Substring(i + 2, close - (i + 2));
                        nodes.Add(new MdInlineNode(MdInlineKind.Bold, inner, null, ParseInlinesAst(inner, budget, allowLinks, inlineDepth + 1)));
                        i = close + 2;
                        continue;
                    }
                }

                if (text.IndexOf("~~", i, StringComparison.Ordinal) == i)
                {
                    int close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        if (!FlushPlain() || !budget.TryConsumeInlineNodes(1))
                            break;
                        string inner = text.Substring(i + 2, close - (i + 2));
                        nodes.Add(new MdInlineNode(MdInlineKind.Strikethrough, inner, null, ParseInlinesAst(inner, budget, allowLinks, inlineDepth + 1)));
                        i = close + 2;
                        continue;
                    }
                }

                if (ch == '*' || (ch == '_' && IsFlankingUnderscoreStart(text, i)))
                {
                    int close = FindClosingEmphasis(text, i + 1, ch);
                    if (close > i + 1)
                    {
                        if (!FlushPlain() || !budget.TryConsumeInlineNodes(1))
                            break;
                        string inner = text.Substring(i + 1, close - (i + 1));
                        nodes.Add(new MdInlineNode(MdInlineKind.Italic, inner, null, ParseInlinesAst(inner, budget, allowLinks, inlineDepth + 1)));
                        i = close + 1;
                        continue;
                    }
                }

                plainBuffer.Append(ch);
                i++;
            }

            FlushPlain();
            return nodes.ToArray();
        }

        // ==================== STAGE 2: FAST UI-THREAD FLOWDOCUMENT MATERIALIZER ====================

        public static FlowDocument BuildMarkdownDocument(string markdown, Action<Uri>? onHyperlinkNavigate = null)
        {
            MarkdownParsedDocument parsed = ParseMarkdownStructure(markdown);
            return BuildMarkdownDocument(parsed, onHyperlinkNavigate);
        }

        internal static FlowDocument BuildMarkdownDocument(MarkdownParsedDocument parsed, Action<Uri>? onHyperlinkNavigate = null)
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

            if (parsed == null || parsed.Blocks.Length == 0)
                return doc;

            int remainingInlines = MaxMarkdownInlineNodes;
            MaterializeBlocks(doc.Blocks, parsed.Blocks, onHyperlinkNavigate, ref remainingInlines);

            if (parsed.ExceedsComplexityBudget || remainingInlines <= 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("… 文档结构较为庞大，已仅渲染前部内容；点击右上角「源码」可极速浏览完整原文。"))
                {
                    FontSize = 11.5,
                    FontStyle = FontStyles.Italic,
                    Foreground = MutedBrush,
                    Margin = new Thickness(0, 8, 0, 4)
                });
            }

            return doc;
        }

        private static void MaterializeBlocks(
            BlockCollection targetBlocks,
            MdBlockNode[] astBlocks,
            Action<Uri>? onHyperlinkNavigate,
            ref int remainingInlines)
        {
            for (int i = 0; i < astBlocks.Length; i++)
            {
                if (remainingInlines <= 0)
                    break;

                switch (astBlocks[i])
                {
                    case MdFrontmatterBlock fm:
                    {
                        if (remainingInlines <= 0)
                            break;
                        remainingInlines--;
                        var fmSection = new Section
                        {
                            Background = SurfaceBrush,
                            BorderBrush = BorderBrush,
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(11, 8, 11, 8),
                            Margin = new Thickness(0, 0, 0, 12)
                        };
                        fmSection.Blocks.Add(new Paragraph(new Run(fm.Content))
                        {
                            FontFamily = MonoFont,
                            FontSize = 11.5,
                            LineHeight = 17,
                            Foreground = MutedBrush,
                            Margin = new Thickness(0)
                        });
                        targetBlocks.Add(fmSection);
                        break;
                    }

                    case MdCodeFenceBlock code:
                    {
                        if (remainingInlines <= 0)
                            break;
                        remainingInlines--;
                        var codeSection = new Section
                        {
                            Background = SurfaceBrush,
                            BorderBrush = BorderBrush,
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(11, 8, 11, 9),
                            Margin = new Thickness(0, 4, 0, 10)
                        };

                        if (!string.IsNullOrEmpty(code.InfoString) && remainingInlines > 0)
                        {
                            remainingInlines--;
                            codeSection.Blocks.Add(new Paragraph(new Run(code.InfoString))
                            {
                                FontFamily = MonoFont,
                                FontSize = 10.5,
                                LineHeight = 14,
                                Foreground = MutedBrush,
                                Margin = new Thickness(0, 0, 0, 4)
                            });
                        }

                        codeSection.Blocks.Add(new Paragraph(new Run(code.CodeText))
                        {
                            FontFamily = MonoFont,
                            FontSize = 12,
                            LineHeight = 18,
                            Foreground = HeadingBrush,
                            Margin = new Thickness(0)
                        });

                        targetBlocks.Add(codeSection);
                        break;
                    }

                    case MdHeadingBlock heading:
                        targetBlocks.Add(CreateHeadingParagraph(heading.Level, heading.Inlines, onHyperlinkNavigate, ref remainingInlines));
                        break;

                    case MdHorizontalRuleBlock _:
                        targetBlocks.Add(new Paragraph
                        {
                            BorderBrush = BorderBrush,
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            FontSize = 1,
                            LineHeight = 1,
                            Margin = new Thickness(0, 8, 0, 12),
                            Padding = new Thickness(0)
                        });
                        break;

                    case MdTableBlock table:
                        targetBlocks.Add(MaterializeTable(table, onHyperlinkNavigate, ref remainingInlines));
                        break;

                    case MdBlockquoteBlock quote:
                    {
                        var quoteSection = new Section
                        {
                            Background = SurfaceBrush,
                            BorderBrush = QuoteBorderBrush,
                            BorderThickness = new Thickness(3, 0, 0, 0),
                            Padding = new Thickness(12, 6, 10, 6),
                            Margin = new Thickness(0, 4, 0, 10)
                        };
                        MaterializeBlocks(quoteSection.Blocks, quote.InnerBlocks, onHyperlinkNavigate, ref remainingInlines);
                        targetBlocks.Add(quoteSection);
                        break;
                    }

                    case MdListBlock listNode:
                    {
                        var list = new List
                        {
                            MarkerStyle = listNode.AnyTask
                                ? TextMarkerStyle.None
                                : (listNode.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc),
                            Padding = new Thickness(listNode.AnyTask ? 8 : 20, 0, 0, 0),
                            Margin = new Thickness(0, 2, 0, 10)
                        };

                        for (int li = 0; li < listNode.Items.Length; li++)
                        {
                            if (remainingInlines <= 0)
                                break;

                            MdListItemData entry = listNode.Items[li];
                            var para = new Paragraph
                            {
                                Margin = new Thickness(0, 2, 0, 3),
                                LineHeight = 20
                            };

                            if (entry.IsTask && remainingInlines > 0)
                            {
                                remainingInlines--;
                                para.Inlines.Add(new Run(entry.TaskDone ? "☑ " : "☐ ")
                                {
                                    FontWeight = FontWeights.SemiBold,
                                    Foreground = entry.TaskDone ? TaskDoneBrush : MutedBrush
                                });
                            }

                            MaterializeInlines(para.Inlines, entry.Inlines, onHyperlinkNavigate, ref remainingInlines);
                            list.ListItems.Add(new ListItem(para));
                        }

                        targetBlocks.Add(list);
                        break;
                    }

                    case MdParagraphBlock paraBlock:
                    {
                        var para = new Paragraph
                        {
                            Margin = new Thickness(0, 0, 0, 9),
                            LineHeight = 20.5
                        };
                        MaterializeInlines(para.Inlines, paraBlock.Inlines, onHyperlinkNavigate, ref remainingInlines);
                        if (para.Inlines.Count > 0)
                        {
                            targetBlocks.Add(para);
                        }
                        break;
                    }
                }
            }
        }

        private static Paragraph CreateHeadingParagraph(
            int level,
            MdInlineNode[] inlines,
            Action<Uri>? onHyperlinkNavigate,
            ref int remainingInlines)
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

            MaterializeInlines(para.Inlines, inlines, onHyperlinkNavigate, ref remainingInlines);
            return para;
        }

        private static Table MaterializeTable(
            MdTableBlock tableNode,
            Action<Uri>? onHyperlinkNavigate,
            ref int remainingInlines)
        {
            int colCount = Math.Max(1, tableNode.Headers.Length);
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

            var headerRow = new TableRow { Background = CodeInlineBgBrush };
            for (int c = 0; c < colCount; c++)
            {
                var para = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = HeadingBrush,
                    LineHeight = 18
                };
                MaterializeInlines(para.Inlines, tableNode.Headers[c], onHyperlinkNavigate, ref remainingInlines);

                headerRow.Cells.Add(new TableCell(para)
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderBrush = TableHeaderBorderBrush,
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1)
                });
            }
            rowGroup.Rows.Add(headerRow);

            bool hasFooterRow = tableNode.OmittedRowCount > 0;
            for (int r = 0; r < tableNode.Rows.Length; r++)
            {
                if (remainingInlines <= 0)
                    break;

                MdInlineNode[][] rowCells = tableNode.Rows[r];
                var row = new TableRow
                {
                    Background = (r % 2 == 1) ? SurfaceBrush : Brushes.White
                };

                bool hasBottomBorder = r < tableNode.Rows.Length - 1 || hasFooterRow;
                for (int c = 0; c < colCount; c++)
                {
                    var para = new Paragraph
                    {
                        Margin = new Thickness(0),
                        LineHeight = 18
                    };
                    if (c < rowCells.Length)
                    {
                        MaterializeInlines(para.Inlines, rowCells[c], onHyperlinkNavigate, ref remainingInlines);
                    }

                    row.Cells.Add(new TableCell(para)
                    {
                        Padding = new Thickness(8, 5, 8, 5),
                        BorderBrush = BorderBrush,
                        BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, hasBottomBorder ? 1 : 0)
                    });
                }

                rowGroup.Rows.Add(row);
            }

            if (hasFooterRow)
            {
                var omittedRow = new TableRow { Background = SurfaceBrush };
                var omittedPara = new Paragraph(new Run($"… 已省略其余 {tableNode.OmittedRowCount:N0} 行表格数据（切换「源码」可查看全部内容）"))
                {
                    Margin = new Thickness(0),
                    FontSize = 11.5,
                    FontStyle = FontStyles.Italic,
                    Foreground = MutedBrush,
                    LineHeight = 18
                };
                omittedRow.Cells.Add(new TableCell(omittedPara)
                {
                    ColumnSpan = colCount,
                    Padding = new Thickness(8, 6, 8, 6)
                });
                rowGroup.Rows.Add(omittedRow);
            }

            table.RowGroups.Add(rowGroup);
            return table;
        }

        private static void MaterializeInlines(
            InlineCollection target,
            MdInlineNode[]? nodes,
            Action<Uri>? onHyperlinkNavigate,
            ref int remainingInlines)
        {
            if (nodes == null || nodes.Length == 0 || remainingInlines <= 0)
                return;

            for (int i = 0; i < nodes.Length; i++)
            {
                if (remainingInlines <= 0)
                    break;

                MdInlineNode node = nodes[i];
                switch (node.Kind)
                {
                    case MdInlineKind.Plain:
                        remainingInlines--;
                        target.Add(new Run(node.Text));
                        break;

                    case MdInlineKind.LineBreak:
                        remainingInlines--;
                        target.Add(new LineBreak());
                        break;

                    case MdInlineKind.InlineCode:
                        if (remainingInlines < 2)
                        {
                            remainingInlines = 0;
                            break;
                        }
                        remainingInlines -= 2;
                        target.Add(new Span(new Run(node.Text))
                        {
                            FontFamily = MonoFont,
                            FontSize = 12,
                            Background = CodeInlineBgBrush,
                            Foreground = HeadingBrush
                        });
                        break;

                    case MdInlineKind.ImageLabel:
                        remainingInlines--;
                        target.Add(new Run(node.Text) { Foreground = MutedBrush });
                        break;

                    case MdInlineKind.Link:
                        remainingInlines--;
                        if (!string.IsNullOrEmpty(node.Url) &&
                            Uri.TryCreate(node.Url, UriKind.Absolute, out Uri? uri) &&
                            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                        {
                            var hyperlink = new Hyperlink
                            {
                                Foreground = LinkBrush,
                                ToolTip = uri.AbsoluteUri
                            };
                            if (node.Children != null && node.Children.Length > 0)
                            {
                                MaterializeInlines(hyperlink.Inlines, node.Children, null, ref remainingInlines);
                            }
                            else if (remainingInlines > 0)
                            {
                                remainingInlines--;
                                hyperlink.Inlines.Add(new Run(node.Text));
                            }

                            if (onHyperlinkNavigate != null)
                            {
                                Uri captured = uri;
                                hyperlink.Click += (_, _) => onHyperlinkNavigate(captured);
                            }
                            target.Add(hyperlink);
                        }
                        else
                        {
                            target.Add(new Run(node.Text) { Foreground = LinkBrush });
                        }
                        break;

                    case MdInlineKind.BoldItalic:
                    {
                        remainingInlines--;
                        var span = new Span
                        {
                            FontWeight = FontWeights.SemiBold,
                            FontStyle = FontStyles.Italic
                        };
                        MaterializeInlines(span.Inlines, node.Children, onHyperlinkNavigate, ref remainingInlines);
                        target.Add(span);
                        break;
                    }

                    case MdInlineKind.Bold:
                    {
                        remainingInlines--;
                        var span = new Span
                        {
                            FontWeight = FontWeights.SemiBold,
                            Foreground = HeadingBrush
                        };
                        MaterializeInlines(span.Inlines, node.Children, onHyperlinkNavigate, ref remainingInlines);
                        target.Add(span);
                        break;
                    }

                    case MdInlineKind.Strikethrough:
                    {
                        remainingInlines--;
                        var span = new Span
                        {
                            TextDecorations = TextDecorations.Strikethrough,
                            Foreground = MutedBrush
                        };
                        MaterializeInlines(span.Inlines, node.Children, onHyperlinkNavigate, ref remainingInlines);
                        target.Add(span);
                        break;
                    }

                    case MdInlineKind.Italic:
                    {
                        remainingInlines--;
                        var span = new Span { FontStyle = FontStyles.Italic };
                        MaterializeInlines(span.Inlines, node.Children, onHyperlinkNavigate, ref remainingInlines);
                        target.Add(span);
                        break;
                    }
                }
            }
        }

        // ==================== LEXICAL HELPERS ====================

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
            int end = body.Length;
            while (end > 0 && body[end - 1] == '#')
                end--;
            if (end < body.Length && (end == 0 || char.IsWhiteSpace(body[end - 1])))
                body = body.Substring(0, end).TrimEnd();

            level = hashes;
            text = body;
            return true;
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

        private static int SkipLeadingSpaces(string line, int maxSpaces)
        {
            int idx = 0;
            while (idx < line.Length && idx < maxSpaces && line[idx] == ' ')
                idx++;
            return idx;
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
