using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;

namespace MDVieW;

/// <summary>Converts Markdown to editable WPF elements; unchanged blocks retain their exact source.</summary>
public sealed class MarkdownCodec
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UseAutoLinks().Build();
    private readonly ConditionalWeakTable<Block, Snapshot> _snapshots = new();
    private readonly ConditionalWeakTable<FlowDocument, DocumentOrigin> _origins = new();
    private string _source = "";
    private string? _baseDirectory;
    public event Action? InteractiveContentChanged;

    private sealed record Snapshot(string Original, string Canonical, string Suffix);
    private sealed record DocumentOrigin(string Prefix);
    private static readonly Brush Ink = BrushOf("#293344");
    private static readonly Brush Muted = BrushOf("#7D8597");
    private static readonly Brush CodeBackground = BrushOf("#F2F3F8");
    private static readonly Brush Accent = BrushOf("#5555D7");
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Yu Gothic UI");

    public FlowDocument Render(string source, string? baseDirectory = null)
    {
        _source = source;
        _baseDirectory = baseDirectory;
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Yu Gothic UI, Segoe UI"), FontSize = 15,
            Foreground = Ink, PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity,
            LineHeight = 26
        };
        var ast = Markdown.Parse(source, _pipeline).Where(block => block is not LinkReferenceDefinitionGroup).ToArray();
        var prefix = ast.Length == 0 ? source : source[..LineStart(ast[0].Span.Start)];
        _origins.Add(document, new DocumentOrigin(prefix));
        for (var i = 0; i < ast.Length; i++)
        {
            var node = ast[i];
            var start = LineStart(node.Span.Start);
            var end = i + 1 < ast.Length ? LineStart(ast[i + 1].Span.Start) : source.Length;
            end = Math.Max(start, end);
            var block = RenderBlock(node);
            document.Blocks.Add(block);
            var original = source[start..end];
            var suffixStart = Math.Clamp(node.Span.End + 1, start, end);
            var suffix = source[suffixStart..end];
            _snapshots.Add(block, new Snapshot(original, SerializeBlock(block), suffix));
        }
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    public string Serialize(FlowDocument document)
    {
        var output = new StringBuilder();
        if (_origins.TryGetValue(document, out var origin)) output.Append(origin.Prefix);
        var blocks = document.Blocks.Cast<Block>().ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            var canonical = SerializeBlock(block);
            if (_snapshots.TryGetValue(block, out var snapshot) && canonical == snapshot.Canonical)
            {
                output.Append(snapshot.Original);
                if (i < blocks.Length - 1 && !snapshot.Original.EndsWith('\n')) output.Append("\n\n");
            }
            else
            {
                output.Append(canonical);
                var suffix = snapshot?.Suffix ?? (i < blocks.Length - 1 ? "\n\n" : "");
                if (i < blocks.Length - 1 && string.IsNullOrWhiteSpace(suffix) && suffix.Count(c => c == '\n') < 2) suffix = "\n\n";
                output.Append(suffix);
            }
        }
        return output.ToString();
    }

    private int LineStart(int index)
    {
        index = Math.Clamp(index, 0, _source.Length);
        while (index > 0 && _source[index - 1] != '\n') index--;
        return index;
    }

    private string Raw(MarkdownObject node)
    {
        var start = Math.Clamp(node.Span.Start, 0, _source.Length);
        var end = Math.Clamp(node.Span.End + 1, start, _source.Length);
        return _source[start..end];
    }

    private Block RenderBlock(Markdig.Syntax.Block node)
    {
        switch (node)
        {
            case HeadingBlock heading:
                var hp = ParagraphOf(heading.Inline);
                SetHeading(hp, heading.Level);
                return hp;
            case ParagraphBlock paragraph:
                return ParagraphOf(paragraph.Inline);
            case FencedCodeBlock fenced:
                return CodeParagraph(fenced.Lines.ToString(), fenced.Info ?? "");
            case CodeBlock code:
                return CodeParagraph(code.Lines.ToString(), "");
            case QuoteBlock quote:
                var section = new Section { Tag = "quote", BorderBrush = BrushOf("#C4BEF1"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(17, 4, 8, 4), Margin = new Thickness(0, 8, 0, 18) };
                foreach (var child in quote) section.Blocks.Add(RenderBlock(child));
                return section;
            case ListBlock list:
                var wl = new WpfList { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(23, 0, 0, 0), Margin = new Thickness(0, 2, 0, 16) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) wl.StartIndex = Math.Max(1, start);
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var li = new ListItem { Margin = new Thickness(0, 2, 0, 2) };
                    foreach (var child in item) li.Blocks.Add(RenderBlock(child));
                    wl.ListItems.Add(li);
                }
                return wl;
            case ThematicBreakBlock:
                return RuleParagraph();
            case MdTable table:
                return RenderTable(table);
            default:
                return new Paragraph(new Run(Raw(node))) { Tag = "raw", FontFamily = Mono, FontSize = 13, Background = CodeBackground, Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 16) };
        }
    }

    private Paragraph ParagraphOf(ContainerInline? container)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
        if (container != null) AddInlines(p.Inlines, container);
        return p;
    }

    public static void SetHeading(Paragraph p, int level)
    {
        p.Tag = level > 0 ? "h" + level : null;
        p.FontSize = level switch { 1 => 29, 2 => 22, 3 => 18, 4 => 17, _ => 15 };
        p.FontWeight = level > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        p.LineHeight = level > 0 ? p.FontSize * 1.5 : 26;
        p.Margin = level > 0 ? new Thickness(0, level == 1 ? 2 : 14, 0, 14) : new Thickness(0, 0, 0, 16);
        p.ClearValue(TextElement.FontFamilyProperty);
        p.ClearValue(TextElement.BackgroundProperty);
        p.Padding = new Thickness(0);
    }

    public static Paragraph CodeParagraph(string text, string language)
        => new(new Run(text)) { Tag = "code:" + language, FontFamily = Mono, FontSize = 13, LineHeight = 22, Background = CodeBackground, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 4, 0, 18) };

    public static Paragraph RuleParagraph()
        => new() { Tag = "rule", BorderBrush = BrushOf("#DEE1E9"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 14, 0, 20), FontSize = 1, LineHeight = 1 };

    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var node in container)
        {
            switch (node)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case LineBreakInline br: target.Add(br.IsHard ? new LineBreak() : new Run("\n")); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case CodeInline code:
                    target.Add(new Span(new Run(code.Content)) { Tag = "inline-code", FontFamily = Mono, FontSize = 13, Background = CodeBackground, Foreground = Accent });
                    break;
                case LinkInline link when link.IsImage:
                    target.Add(RenderImage(link));
                    break;
                case LinkInline link:
                    var hyperlink = new Hyperlink { Foreground = Accent, ToolTip = link.Url, Tag = link.Url ?? "" };
                    if (Uri.TryCreate(link.Url, UriKind.RelativeOrAbsolute, out var uri)) hyperlink.NavigateUri = uri;
                    AddInlines(hyperlink.Inlines, link);
                    target.Add(hyperlink);
                    break;
                case AutolinkInline auto:
                    var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                    target.Add(new Hyperlink(new Run(auto.Url)) { Tag = url, Foreground = Accent, ToolTip = url });
                    break;
                case TaskList task:
                    var check = new CheckBox { IsChecked = task.Checked, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), ToolTip = "完了 / 未完了" };
                    check.Checked += (_, _) => InteractiveContentChanged?.Invoke();
                    check.Unchecked += (_, _) => InteractiveContentChanged?.Invoke();
                    target.Add(new InlineUIContainer(check) { BaselineAlignment = BaselineAlignment.Center, Tag = "task" });
                    break;
                case HtmlInline html: target.Add(new Run(html.Tag) { Tag = "raw" }); break;
                case ContainerInline nested: AddInlines(target, nested); break;
                default: target.Add(new Run(Raw(node)) { Tag = "raw" }); break;
            }
        }
    }

    private Inline RenderImage(LinkInline link)
    {
        var alt = new StringBuilder();
        foreach (var child in link) if (child is LiteralInline literal) alt.Append(literal.Content);
        var raw = $"![{alt}]({link.Url}{(string.IsNullOrEmpty(link.Title) ? "" : " \"" + link.Title + "\"")})";
        UIElement visual = new TextBlock { Text = "▧ " + alt, Foreground = Muted, Background = CodeBackground, Padding = new Thickness(8), ToolTip = link.Url };
        // Local images are loaded eagerly and closed; remote images remain offline placeholders.
        try
        {
            if (!string.IsNullOrWhiteSpace(_baseDirectory) && !string.IsNullOrWhiteSpace(link.Url)
                && !Uri.TryCreate(link.Url, UriKind.Absolute, out _))
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_baseDirectory, Uri.UnescapeDataString(link.Url)));
                if (System.IO.File.Exists(path))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.DecodePixelWidth = 1000; bitmap.EndInit(); bitmap.Freeze();
                    visual = new Image { Source = bitmap, MaxWidth = 480, MaxHeight = 320, Stretch = Stretch.Uniform, ToolTip = alt.ToString() };
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or ArgumentException) { }
        return new InlineUIContainer(visual) { Tag = raw };
    }

    private System.Windows.Documents.Table RenderTable(MdTable node)
    {
        var table = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 20), Tag = "table" };
        var group = new TableRowGroup();
        foreach (var mdRow in node.OfType<MdRow>())
        {
            var row = new System.Windows.Documents.TableRow { Tag = mdRow.IsHeader ? "header" : null };
            var columnIndex = 0;
            foreach (var mdCell in mdRow.OfType<MdCell>())
            {
                var cell = new System.Windows.Documents.TableCell { BorderBrush = BrushOf("#DFE2EB"), BorderThickness = new Thickness(0.5), Padding = new Thickness(11, 7, 11, 7) };
                if (mdRow.IsHeader) { cell.Background = CodeBackground; cell.FontWeight = FontWeights.SemiBold; }
                if (columnIndex < node.ColumnDefinitions.Count)
                    cell.TextAlignment = node.ColumnDefinitions[columnIndex].Alignment switch
                    { Markdig.Extensions.Tables.TableColumnAlign.Center => TextAlignment.Center, Markdig.Extensions.Tables.TableColumnAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
                foreach (var child in mdCell) cell.Blocks.Add(RenderBlock(child));
                foreach (var p in cell.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0);
                row.Cells.Add(cell);
                columnIndex++;
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    public static string SerializeBlock(Block block)
    {
        switch (block)
        {
            case Paragraph p:
                var kind = p.Tag as string ?? "";
                if (kind.StartsWith("code:", StringComparison.Ordinal))
                {
                    var text = PlainText(p).TrimEnd('\r', '\n');
                    var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
                    return fence + kind[5..] + "\n" + text + "\n" + fence;
                }
                if (kind == "rule") return "---";
                if (kind == "raw") return PlainText(p).TrimEnd('\r', '\n');
                var body = WriteInlines(p.Inlines, p.FontWeight, p.FontStyle, false).TrimEnd('\r', '\n');
                if (p.Inlines.FirstInline is InlineUIContainer { Child: CheckBox }) body = Regex.Replace(body, @"^(\[[ x]\])\s*", "$1 ");
                if (kind.Length == 2 && kind[0] == 'h' && char.IsDigit(kind[1])) body = new string('#', kind[1] - '0') + " " + body;
                return body;
            case Section section:
                var inner = string.Join("\n\n", section.Blocks.Cast<Block>().Select(SerializeBlock));
                return section.Tag as string == "quote" ? string.Join("\n", inner.Replace("\r", "").Split('\n').Select(line => "> " + line)) : inner;
            case WpfList list:
                var items = new System.Collections.Generic.List<string>();
                var number = list.StartIndex;
                foreach (var item in list.ListItems)
                {
                    var marker = list.MarkerStyle == TextMarkerStyle.Decimal ? number++.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
                    var content = string.Join("\n\n", item.Blocks.Cast<Block>().Select(SerializeBlock));
                    var lines = content.Replace("\r", "").Split('\n');
                    items.Add(marker + lines[0] + (lines.Length > 1 ? "\n" + string.Join("\n", lines.Skip(1).Select(line => new string(' ', marker.Length) + line)) : ""));
                }
                return string.Join("\n", items);
            case System.Windows.Documents.Table table:
                var rows = table.RowGroups.SelectMany(group => group.Rows).ToArray();
                if (rows.Length == 0) return "";
                var output = new StringBuilder();
                var width = rows.Max(row => row.Cells.Count);
                for (var i = 0; i < rows.Length; i++)
                {
                    var cells = rows[i].Cells.Select(cell => string.Join("<br>", cell.Blocks.Cast<Block>().Select(SerializeBlock)).Replace("\r", "").Replace("\n", "<br>")).ToList();
                    while (cells.Count < width) cells.Add("");
                    output.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                    if (i == 0)
                    {
                        var alignment = rows[0].Cells.Select(cell => cell.TextAlignment switch { TextAlignment.Center => ":---:", TextAlignment.Right => "---:", _ => "---" }).ToList();
                        while (alignment.Count < width) alignment.Add("---");
                        output.Append("| ").Append(string.Join(" | ", alignment)).Append(" |\n");
                    }
                }
                return output.ToString().TrimEnd('\n');
            default: return new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n');
        }
    }

    private static string WriteInlines(InlineCollection inlines, FontWeight parentWeight, FontStyle parentStyle, bool parentStrike)
        => string.Concat(inlines.Select(inline => WriteInline(inline, parentWeight, parentStyle, parentStrike)));

    private static string WriteInline(Inline inline, FontWeight parentWeight, FontStyle parentStyle, bool parentStrike)
    {
        if (inline is LineBreak) return "  \n";
        if (inline is InlineUIContainer ui)
        {
            if (ui.Child is CheckBox check) return check.IsChecked == true ? "[x] " : "[ ] ";
            return ui.Tag as string ?? "";
        }
        if (inline is Span { Tag: "inline-code" } code)
            return InlineCode(new TextRange(code.ContentStart, code.ContentEnd).Text);
        string text;
        var strike = inline.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true || parentStrike;
        if (inline is Run run) text = run.Tag as string == "raw" ? run.Text : Escape(run.Text);
        else if (inline is Span span) text = WriteInlines(span.Inlines, span.FontWeight, span.FontStyle, strike);
        else text = new TextRange(inline.ContentStart, inline.ContentEnd).Text;
        if (inline is Hyperlink link)
        {
            var url = link.NavigateUri?.OriginalString ?? link.Tag as string ?? "";
            text = "[" + text + "](" + url.Replace(" ", "%20").Replace(")", "%29").Replace("(", "%28") + ")";
        }
        if (inline.FontWeight >= FontWeights.Bold && parentWeight < FontWeights.Bold) text = Wrap(text, "**");
        if (inline.FontStyle == FontStyles.Italic && parentStyle != FontStyles.Italic) text = Wrap(text, "*");
        if (strike && !parentStrike) text = Wrap(text, "~~");
        return text;
    }

    private static string Wrap(string text, string delimiter)
    {
        var core = text.Trim();
        if (core.Length == 0) return text;
        var start = text.IndexOf(core, StringComparison.Ordinal);
        return text[..start] + delimiter + core + delimiter + text[(start + core.Length)..];
    }

    public static string InlineCode(string text)
    {
        var delimiter = new string('`', LongestRun(text, '`') + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') || (text.StartsWith(' ') && text.EndsWith(' ')) ? " " : "";
        return delimiter + pad + text + pad + delimiter;
    }

    private static int LongestRun(string text, char character)
    {
        var max = 0; var current = 0;
        foreach (var c in text) { current = c == character ? current + 1 : 0; max = Math.Max(max, current); }
        return max;
    }

    private static string Escape(string text)
    {
        var escaped = Regex.Replace(text, @"([\\`*_\[\]<>|~])", @"\$1");
        // Prevent normal paragraph text becoming a heading, list or thematic break after a rich edit.
        return Regex.Replace(escaped, @"(?m)^(\s*)(#{1,6}(?=\s)|[-+](?=\s)|\d+[.)](?=\s)|={3,}$|-{3,}$)", "$1\\$2");
    }

    private static string PlainText(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text;
    private static Brush BrushOf(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
}
