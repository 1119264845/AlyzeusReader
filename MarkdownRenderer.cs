using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace JianRead;

public static partial class MarkdownRenderer
{
    private static bool _isDark = true;

    private static Brush PrimaryText => BrushFrom(_isDark ? "#EAE6E1" : "#282522");
    private static Brush BodyText => BrushFrom(_isDark ? "#D2CDC6" : "#403B36");
    private static Brush MutedText => BrushFrom(_isDark ? "#9F9890" : "#766F68");
    private static Brush Accent => BrushFrom(_isDark ? "#C7AE98" : "#765B46");
    private static Brush CodeBackground => BrushFrom(_isDark ? "#302E2B" : "#EFECE7");
    private static Brush TableHeaderBackground => BrushFrom(_isDark ? "#34383C" : "#ECE9E4");
    private static Brush TableRowBackground => BrushFrom(_isDark ? "#2E3236" : "#FAF9F7");
    private static Brush TableAlternateBackground => BrushFrom(_isDark ? "#303438" : "#F4F1ED");
    private static Brush TableBorder => BrushFrom(_isDark ? "#485058" : "#D1CBC3");

    public static void SetTheme(bool isDark) => _isDark = isDark;

    public static FlowDocument Render(string content, bool markdown, double fontSize)
    {
        var document = CreateDocument(fontSize);
        if (!markdown)
        {
            RenderPlainText(document, content);
            return document;
        }

        RenderMarkdown(document, content);
        return document;
    }

    public static FlowDocument Welcome(double fontSize)
    {
        const string text = "# 欢迎使用阿利宙斯阅读\n\n选择左侧的“阅读文件夹”，应用会按照原有层级生成目录树。\n\n## 本地优先\n\n- 支持 `.md`、`.markdown` 和 `.txt`\n- 自动记录最近阅读，并按阅读节点分组\n- 不修改原文件，也不上传任何内容\n\n> 一个文件夹，就是一个清晰的阅读节点。";
        return Render(text, true, fontSize);
    }

    private static FlowDocument CreateDocument(double fontSize) => new()
    {
        FontFamily = new FontFamily("Microsoft YaHei UI"),
        FontSize = fontSize,
        Foreground = BodyText,
        Background = Brushes.Transparent,
        PagePadding = new Thickness(66, 48, 66, 72),
        ColumnWidth = 760,
        LineHeight = fontSize * 1.85
    };

    private static void RenderPlainText(FlowDocument document, string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        foreach (var part in BlankLineRegex().Split(normalized))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var paragraph = BodyParagraph();
            var lines = part.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                paragraph.Inlines.Add(new Run(lines[i]));
                if (i < lines.Length - 1) paragraph.Inlines.Add(new LineBreak());
            }
            document.Blocks.Add(paragraph);
        }
    }

    private static void RenderMarkdown(FlowDocument document, string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var paragraphBuffer = new List<string>();
        var codeBuffer = new List<string>();
        var inCode = false;
        List? activeList = null;

        void FlushParagraph()
        {
            if (paragraphBuffer.Count == 0) return;
            var paragraph = BodyParagraph();
            AddInlineMarkdown(paragraph, string.Join(" ", paragraphBuffer).Trim());
            document.Blocks.Add(paragraph);
            paragraphBuffer.Clear();
        }

        void CloseList() => activeList = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                if (inCode)
                {
                    var code = new Paragraph(new Run(string.Join(Environment.NewLine, codeBuffer)))
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = document.FontSize * 0.9,
                        Foreground = BodyText,
                        Background = CodeBackground,
                        Padding = new Thickness(14, 10, 14, 10),
                        Margin = new Thickness(0, 8, 0, 18),
                        LineHeight = document.FontSize * 1.55
                    };
                    document.Blocks.Add(code);
                    codeBuffer.Clear();
                }
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                codeBuffer.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            if (index + 1 < lines.Length && IsTableDelimiter(lines[index + 1]) && trimmed.Contains('|'))
            {
                FlushParagraph();
                CloseList();
                var headers = ParseTableRow(trimmed);
                var alignments = ParseTableRow(lines[index + 1])
                    .Select(ParseAlignment)
                    .ToList();

                if (headers.Count > 0)
                {
                    var rows = new List<List<string>>();
                    var bodyIndex = index + 2;
                    while (bodyIndex < lines.Length)
                    {
                        var candidate = lines[bodyIndex].Trim();
                        if (string.IsNullOrWhiteSpace(candidate) || !candidate.Contains('|')) break;
                        rows.Add(ParseTableRow(candidate));
                        bodyIndex++;
                    }

                    document.Blocks.Add(CreateTable(headers, alignments, rows));
                    index = bodyIndex - 1;
                    continue;
                }
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                CloseList();
                var level = heading.Groups[1].Value.Length;
                var paragraph = new Paragraph
                {
                    Foreground = PrimaryText,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = document.FontSize * (level == 1 ? 1.9 : level == 2 ? 1.35 : 1.12),
                    LineHeight = double.NaN,
                    Margin = level == 1
                        ? new Thickness(0, 0, 0, 24)
                        : new Thickness(0, 24, 0, 10)
                };
                AddInlineMarkdown(paragraph, heading.Groups[2].Value);
                document.Blocks.Add(paragraph);
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                CloseList();
                document.Blocks.Add(new Paragraph(new Run(" "))
                {
                    FontSize = 1,
                    BorderBrush = BrushFrom("#46423E"),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 18, 0, 18)
                });
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                var quote = new Paragraph
                {
                    Foreground = MutedText,
                    BorderBrush = Accent,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(16, 2, 0, 2),
                    Margin = new Thickness(0, 10, 0, 20)
                };
                AddInlineMarkdown(quote, trimmed.TrimStart('>', ' '));
                document.Blocks.Add(quote);
                continue;
            }

            var listMatch = ListRegex().Match(trimmed);
            if (listMatch.Success)
            {
                FlushParagraph();
                if (activeList is null)
                {
                    activeList = new List
                    {
                        MarkerStyle = TextMarkerStyle.Disc,
                        Margin = new Thickness(18, 2, 0, 18),
                        Padding = new Thickness(6, 0, 0, 0)
                    };
                    document.Blocks.Add(activeList);
                }

                var itemParagraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AddInlineMarkdown(itemParagraph, listMatch.Groups[1].Value);
                activeList.ListItems.Add(new ListItem(itemParagraph));
                continue;
            }

            CloseList();
            paragraphBuffer.Add(trimmed);
        }

        if (codeBuffer.Count > 0)
        {
            var code = new Paragraph(new Run(string.Join(Environment.NewLine, codeBuffer)))
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Background = CodeBackground,
                Padding = new Thickness(14, 10, 14, 10)
            };
            document.Blocks.Add(code);
        }
        FlushParagraph();
    }

    private static Paragraph BodyParagraph() => new()
    {
        Foreground = BodyText,
        Margin = new Thickness(0, 0, 0, 17)
    };

    private static Table CreateTable(IReadOnlyList<string> headers, IReadOnlyList<TextAlignment> alignments, IReadOnlyList<List<string>> rows)
    {
        var columnCount = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count));
        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = TableBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 10, 0, 24)
        };

        for (var column = 0; column < columnCount; column++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        group.Rows.Add(CreateTableRow(headers, alignments, header: true, rowIndex: 0, columnCount));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            group.Rows.Add(CreateTableRow(rows[rowIndex], alignments, header: false, rowIndex, columnCount));

        return table;
    }

    private static TableRow CreateTableRow(IReadOnlyList<string> values, IReadOnlyList<TextAlignment> alignments, bool header, int rowIndex, int columnCount)
    {
        var row = new TableRow
        {
            Background = header
                ? TableHeaderBackground
                : rowIndex % 2 == 0 ? TableRowBackground : TableAlternateBackground
        };

        for (var column = 0; column < columnCount; column++)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = double.NaN,
                Foreground = header ? PrimaryText : BodyText,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = column < alignments.Count ? alignments[column] : TextAlignment.Left
            };
            AddInlineMarkdown(paragraph, column < values.Count ? values[column] : string.Empty);

            row.Cells.Add(new TableCell(paragraph)
            {
                BorderBrush = TableBorder,
                BorderThickness = new Thickness(0, 0, column == columnCount - 1 ? 0 : 1, 1),
                Padding = new Thickness(11, 8, 11, 8)
            });
        }

        return row;
    }

    private static List<string> ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];

        var cells = new List<string>();
        var buffer = new System.Text.StringBuilder();
        var inCode = false;
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character == '`') inCode = !inCode;
            if (character == '|' && !inCode && (index == 0 || trimmed[index - 1] != '\\'))
            {
                cells.Add(buffer.ToString().Trim().Replace("\\|", "|", StringComparison.Ordinal));
                buffer.Clear();
                continue;
            }
            buffer.Append(character);
        }
        cells.Add(buffer.ToString().Trim().Replace("\\|", "|", StringComparison.Ordinal));
        return cells;
    }

    private static TextAlignment ParseAlignment(string delimiter)
    {
        var value = delimiter.Trim();
        var left = value.StartsWith(':');
        var right = value.EndsWith(':');
        if (left && right) return TextAlignment.Center;
        if (right) return TextAlignment.Right;
        return TextAlignment.Left;
    }

    private static bool IsTableDelimiter(string line) => TableDelimiterRegex().IsMatch(line.Trim());

    private static void AddInlineMarkdown(Paragraph paragraph, string text)
    {
        var last = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index > last)
                paragraph.Inlines.Add(new Run(text[last..match.Index]));

            if (match.Groups[1].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups[1].Value) { FontWeight = FontWeights.Bold, Foreground = PrimaryText });
            }
            else if (match.Groups[2].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups[2].Value) { FontStyle = FontStyles.Italic });
            }
            else if (match.Groups[3].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups[3].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = paragraph.FontSize > 0 ? paragraph.FontSize * 0.9 : 14,
                    Background = CodeBackground,
                    Foreground = PrimaryText
                });
            }
            else if (match.Groups[4].Success && Uri.TryCreate(match.Groups[5].Value, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink(new Run(match.Groups[4].Value))
                {
                    NavigateUri = uri,
                    Foreground = Accent,
                    TextDecorations = null
                };
                link.RequestNavigate += OpenLink;
                paragraph.Inlines.Add(link);
            }

            last = match.Index + match.Length;
        }

        if (last < text.Length)
            paragraph.Inlines.Add(new Run(text[last..]));
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch
        {
            // A malformed or blocked URL should not affect reading the document.
        }
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(?:[-*+]\s+)(.+)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*|(?<!\*)\*([^*]+?)\*|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$")]
    private static partial Regex TableDelimiterRegex();
}
