using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MDVieW;

internal enum PaneLayout { SideBySide, Stacked }

public partial class MainWindow : Window
{
    private readonly List<DocumentSession> _sessions = [];
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly LayoutSettingsStore _layoutSettingsStore;
    private bool _restoringLayout = true;
    private bool _layoutSaveFailed;
    private DocumentSession? _active;
    private bool _updating;
    private bool _ready;
    private bool _updatingStyle;
    private int _untitledCount;
    private int _zoom = 100;
    private string _viewMode = "Split";
    private bool _panesSwapped;
    private PaneLayout _paneLayout = PaneLayout.SideBySide;
    private Orientation _paletteOrientation = Orientation.Horizontal;
    private double _sideBySideRatio = 0.5;
    private double _stackedRatio = 0.5;
    private double SplitRatio
    {
        get => _paneLayout == PaneLayout.SideBySide ? _sideBySideRatio : _stackedRatio;
        set { if (_paneLayout == PaneLayout.SideBySide) _sideBySideRatio = value; else _stackedRatio = value; }
    }
    private Point _dragStart;
    private Border? _dragHeader;
    private bool _paneDragging;
    private TextBox SourceEditor => _active!.Source;
    private RichTextBox RichEditor => _active!.Rich;

    public MainWindow() : this(new LayoutSettingsStore(LayoutSettingsStore.DefaultFilePath)) { }

    internal MainWindow(LayoutSettingsStore layoutSettingsStore)
    {
        _layoutSettingsStore = layoutSettingsStore;
        InitializeComponent();
        _syncTimer.Tick += (_, _) => FlushPending();
        PreviewKeyDown += Window_PreviewKeyDown;
        _ready = true;
        AddSession(Welcome.Text, "ようこそ.md");
        var settings = _layoutSettingsStore.Load();
        _paneLayout = settings.PaneLayout;
        _panesSwapped = settings.PanesSwapped;
        _viewMode = settings.ViewMode;
        _sideBySideRatio = settings.SideBySideRatio;
        _stackedRatio = settings.StackedRatio;
        SetPaletteOrientation(settings.PaletteOrientation);
        ApplyPaneLayout();
        _restoringLayout = false;
    }

    private DocumentSession AddSession(string text, string? title = null, DocumentFile? loadedFile = null)
    {
        var session = new DocumentSession { UntitledName = title ?? (++_untitledCount == 1 ? "無題.md" : $"無題 {_untitledCount}.md") };
        session.File.Reset(text);
        if (loadedFile?.FilePath is { } path) text = session.File.Open(path);
        _updating = true;
        try
        {
            session.Source.Text = text;
            session.Rich.Document = session.Codec.Render(text, BaseDirectory(session));
            session.Source.IsUndoEnabled = false; session.Source.IsUndoEnabled = true;
            session.Rich.IsUndoEnabled = false; session.Rich.IsUndoEnabled = true;
        }
        finally { _updating = false; }
        session.Source.TextChanged += (_, _) => SourceChanged(session);
        session.Rich.TextChanged += (_, _) => RichChanged(session);
        session.Source.SelectionChanged += (_, _) => { if (_active == session) UpdateStatus(); };
        session.Rich.SelectionChanged += (_, _) => { if (_active == session) UpdateBlockStyle(); };
        session.Source.GotKeyboardFocus += (_, _) => { FlushPending(); session.RichActive = false; UpdateBlockStyle(); UpdateStatus(); };
        session.Rich.GotKeyboardFocus += (_, _) => { FlushPending(); session.RichActive = true; UpdateBlockStyle(); UpdateStatus(); };
        session.Codec.InteractiveContentChanged += () => RichChanged(session);
        session.Rich.PreviewKeyDown += (_, e) => RichEditor_KeyDown(session, e);
        // Paste plain text; unsupported clipboard formatting never enters the Markdown document.
        DataObject.AddPastingHandler(session.Rich, (_, args) =>
        {
            if (args.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                args.DataObject = new DataObject(DataFormats.UnicodeText, args.SourceDataObject.GetData(DataFormats.UnicodeText));
                args.FormatToApply = DataFormats.UnicodeText;
            }
            else args.CancelCommand();
        });
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(session.Header);
        var close = new Button { Content = "×", FontSize = 14, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(10, 0, -5, 0), Focusable = false, ToolTip = "タブを閉じる (Ctrl+W)" };
        System.Windows.Automation.AutomationProperties.SetName(close, session.Name + " を閉じる");
        close.Click += (_, e) => { e.Handled = true; CloseSession(session); };
        header.Children.Add(close);
        session.Tab.Header = header; session.Tab.Tag = session;
        session.Tab.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseSession(session); } };
        _sessions.Add(session);
        UpdateTab(session);
        DocumentTabs.Items.Add(session.Tab);
        DocumentTabs.SelectedItem = session.Tab;
        return session;
    }

    private static string? BaseDirectory(DocumentSession session) => session.File.FilePath == null ? null : Path.GetDirectoryName(session.File.FilePath);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source != DocumentTabs || DocumentTabs.SelectedItem is not TabItem { Tag: DocumentSession next }) return;
        FlushPending();
        _active = next;
        SourceHost.Content = next.Source; RichHost.Content = next.Rich;
        ApplyZoom(); UpdateStatus(); UpdateBlockStyle();
    }

    private void SourceChanged(DocumentSession session)
    {
        if (_updating) return;
        session.Pending = SyncDirection.Source;
        if (session == _active) { RestartSync(); UpdateStatus(); }
        UpdateTab(session);
    }

    private void RichChanged(DocumentSession session)
    {
        if (_updating) return;
        session.Pending = SyncDirection.Rich;
        if (session == _active) { RestartSync(); UpdateStatus(); }
        UpdateTab(session);
    }

    private void RestartSync() { _syncTimer.Stop(); _syncTimer.Start(); }

    internal void FlushPending()
    {
        _syncTimer.Stop();
        if (_active is not { } session || session.Pending == SyncDirection.None) return;
        _updating = true;
        try
        {
            if (session.Pending == SyncDirection.Source)
            {
                var offset = session.Rich.VerticalOffset;
                session.Rich.Document = session.Codec.Render(session.Source.Text, BaseDirectory(session));
                ApplyZoom();
                session.Rich.ScrollToVerticalOffset(offset);
            }
            else
            {
                var markdown = session.File.NormalizeNewLines(session.Codec.Serialize(session.Rich.Document));
                if (session.Source.Text != markdown)
                {
                    var start = session.Source.SelectionStart;
                    var length = session.Source.SelectionLength;
                    var scroll = session.Source.VerticalOffset;
                    session.Source.Text = markdown;
                    session.Source.Select(Math.Min(start, markdown.Length), Math.Min(length, Math.Max(0, markdown.Length - start)));
                    session.Source.ScrollToVerticalOffset(scroll);
                }
            }
            session.Pending = SyncDirection.None;
        }
        finally { _updating = false; }
        UpdateTab(session); UpdateStatus();
    }

    private void UpdateTab(DocumentSession session)
    {
        session.Header.Text = session.Name + (session.IsDirty ? "  ●" : "");
        session.Tab.ToolTip = (session.File.FilePath ?? "未保存のドキュメント") + (session.IsDirty ? "\n変更があります" : "");
        if (_active == session) Title = (session.IsDirty ? "● " : "") + session.Name + " — MDVieW";
    }

    private void UpdateStatus()
    {
        if (!_ready || _active == null) return;
        UpdateTab(_active);
        var text = SourceEditor.Text;
        var caret = SourceEditor.SelectionStart;
        var line = 1; var lastNewline = -1;
        for (var i = 0; i < caret; i++) if (text[i] == '\n') { line++; lastNewline = i; }
        PositionLabel.Text = $"{text.Length:N0} 文字  ·  {line} 行 {caret - lastNewline} 列";
        EncodingLabel.Text = $"UTF-8{(_active.File.HasBom ? " BOM" : "")}  ·  {(_active.File.NewLine == "\r\n" ? "CRLF" : "LF")}  ·  {_zoom}%";
        StateLabel.Text = (_active.IsDirty ? "変更あり" : "保存済み") + $"  ·  {_sessions.Count} ファイル" + (_viewMode == "Split" ? $"  ·  {(_paneLayout == PaneLayout.SideBySide ? "左右" : "上下")} {Math.Round(SplitRatio * 100)} : {Math.Round((1 - SplitRatio) * 100)}" : "") + (_layoutSaveFailed ? "  ·  配置を保存できませんでした" : "");
    }

    private void New_Click(object sender, RoutedEventArgs e) => AddSession("");

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Markdown ファイルを開く", Filter = "Markdown / テキスト|*.md;*.markdown;*.mdown;*.txt|すべてのファイル|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var path in dialog.FileNames) OpenPath(path);
    }

    public void OpenPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var existing = _sessions.Find(s => string.Equals(s.File.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { DocumentTabs.SelectedItem = existing.Tab; return; }
            var file = new DocumentFile();
            var text = file.Open(fullPath);
            AddSession(text, loadedFile: file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException or NotSupportedException)
        { MessageBox.Show(this, "ファイルを開けませんでした。UTF-8 のテキストファイルを指定してください。\n\n" + ex.Message, "MDVieW", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveActive(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveActive(true);
    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var selected = _active;
        foreach (var session in _sessions.ToArray())
        {
            DocumentTabs.SelectedItem = session.Tab;
            FlushPending();
            if (session.IsDirty && !SaveActive(false)) break;
        }
        if (selected != null) DocumentTabs.SelectedItem = selected.Tab;
    }

    private bool SaveActive(bool saveAs)
    {
        FlushPending();
        if (_active == null) return false;
        var path = _active.File.FilePath;
        if (saveAs || path == null)
        {
            var dialog = new SaveFileDialog { Title = "Markdown を保存", Filter = "Markdown|*.md|テキスト|*.txt|すべてのファイル|*.*", FileName = _active.Name, DefaultExt = ".md", AddExtension = true };
            if (path != null) dialog.InitialDirectory = Path.GetDirectoryName(path);
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        if (_sessions.Any(s => s != _active && string.Equals(s.File.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "このファイルは別のタブで開いています。別の名前で保存してください。", "MDVieW", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        try
        {
            var text = _active.File.NormalizeNewLines(SourceEditor.Text);
            _active.File.Save(path, text);
            if (SourceEditor.Text != text)
            {
                var caret = SourceEditor.CaretIndex;
                _updating = true;
                try { SourceEditor.Text = text; SourceEditor.CaretIndex = Math.Min(caret, text.Length); }
                finally { _updating = false; }
            }
            UpdateTab(_active); UpdateStatus();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { MessageBox.Show(this, "保存できませんでした。編集内容はタブに残っています。\n\n" + ex.Message, "MDVieW", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private bool ConfirmClose(DocumentSession session)
    {
        DocumentTabs.SelectedItem = session.Tab;
        FlushPending();
        if (!session.IsDirty) return true;
        var answer = MessageBox.Show(this, $"「{session.Name}」の変更を保存しますか？", "MDVieW", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveActive(false);
    }

    private void CloseSession(DocumentSession session)
    {
        if (!ConfirmClose(session)) return;
        _sessions.Remove(session);
        DocumentTabs.Items.Remove(session.Tab);
        if (_sessions.Count == 0) { _active = null; AddSession(""); }
        UpdateStatus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var session in _sessions.ToArray()) if (!ConfirmClose(session)) { e.Cancel = true; return; }
        SaveLayout();
        _syncTimer.Stop();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!ctrl) return;
        Action? action = e.Key switch
        {
            Key.N => () => AddSession(""), Key.O => () => Open_Click(this, new RoutedEventArgs()),
            Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) => () => SaveAll_Click(this, new RoutedEventArgs()),
            Key.S => () => SaveActive(shift), Key.W => () => { if (_active != null) CloseSession(_active); },
            Key.Tab => () => DocumentTabs.SelectedIndex = (DocumentTabs.SelectedIndex + (shift ? _sessions.Count - 1 : 1)) % _sessions.Count,
            Key.F => () => Find_Click(this, new RoutedEventArgs()),
            Key.B => () => Format("Bold"), Key.I => () => Format("Italic"),
            Key.OemPlus or Key.Add => () => SetZoom(_zoom + 10), Key.OemMinus or Key.Subtract => () => SetZoom(_zoom - 10),
            Key.D0 or Key.NumPad0 => () => SetZoom(100), _ => null
        };
        if (action != null) { e.Handled = true; action(); }
    }

    private void Format_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string format }) Format(format); }

    internal void Format(string format)
    {
        FlushPending();
        if (_active == null) return;
        if (format == "Undo" || format == "Redo")
        {
            if (_active.RichActive) { RichEditor.Focus(); if (format == "Undo") RichEditor.Undo(); else RichEditor.Redo(); }
            else { SourceEditor.Focus(); if (format == "Undo") SourceEditor.Undo(); else SourceEditor.Redo(); }
            return;
        }
        if (format == "Link")
        {
            var url = TextPrompt.Ask(this, "リンクを挿入", "リンク先の URL", "https://");
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out _)) return;
            if (!_active.RichActive) { WrapSource("[", "](" + url.Replace(")", "%29") + ")", "リンク"); return; }
            RichEditor.Focus();
            if (RichEditor.Selection.Start.Paragraph != RichEditor.Selection.End.Paragraph)
            {
                MessageBox.Show(this, "リンクにするテキストを同じ段落の中で選択してください。", "MDVieW", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            RichEditor.BeginChange();
            try
            {
                var parent = RichEditor.CaretPosition.Parent as TextElement;
                while (parent != null && parent is not Hyperlink) parent = parent.Parent as TextElement;
                if (parent is Hyperlink existingLink) { existingLink.NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute); existingLink.Tag = url; }
                else
                {
                    if (RichEditor.Selection.IsEmpty) RichEditor.Selection.Text = "リンク";
                    _ = new Hyperlink(RichEditor.Selection.Start, RichEditor.Selection.End) { NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute), Tag = url, Foreground = (Brush)FindResource("Accent") };
                }
            }
            finally { RichEditor.EndChange(); }
            RichChanged(_active); return;
        }
        if (!_active.RichActive)
        {
            switch (format)
            {
                case "Bold": WrapSource("**", "**"); break;
                case "Italic": WrapSource("*", "*"); break;
                case "Strike": WrapSource("~~", "~~"); break;
                case "InlineCode":
                    var selected = SourceEditor.SelectedText;
                    ReplaceSourceSelection(MarkdownCodec.InlineCode(string.IsNullOrEmpty(selected) ? "コード" : selected)); break;
                case "Bullet": PrefixSourceLines("- "); break;
                case "Numbered": PrefixSourceLines("1. "); break;
                case "Task": PrefixSourceLines("- [ ] "); break;
                case "Quote": PrefixSourceLines("> "); break;
                case "CodeBlock": WrapSource("\n```\n", "\n```\n", "コード"); break;
                case "Rule": ReplaceSourceSelection("\n\n---\n\n"); break;
                case "Table": ReplaceSourceSelection("\n\n| 見出し 1 | 見出し 2 |\n| --- | --- |\n| テキスト | テキスト |\n\n"); break;
            }
            return;
        }
        RichEditor.Focus();
        RichEditor.BeginChange();
        try
        {
            switch (format)
            {
                case "Bold": EditingCommands.ToggleBold.Execute(null, RichEditor); break;
                case "Italic": EditingCommands.ToggleItalic.Execute(null, RichEditor); break;
                case "Strike":
                    var decorated = RichEditor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations && decorations.Contains(TextDecorations.Strikethrough[0]);
                    RichEditor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, decorated ? new TextDecorationCollection() : TextDecorations.Strikethrough); break;
                case "InlineCode":
                    if (RichEditor.Selection.Start.Paragraph != RichEditor.Selection.End.Paragraph) break;
                    if (RichEditor.Selection.IsEmpty) RichEditor.Selection.Text = "コード";
                    _ = new Span(RichEditor.Selection.Start, RichEditor.Selection.End) { Tag = "inline-code", FontFamily = new FontFamily("Consolas"), Background = (Brush)new BrushConverter().ConvertFromString("#F2F3F8")! };
                    break;
                case "Bullet": EditingCommands.ToggleBullets.Execute(null, RichEditor); break;
                case "Numbered": EditingCommands.ToggleNumbering.Execute(null, RichEditor); break;
                case "Task": InsertMarkdownBlock("- [ ] タスク"); break;
                case "Quote":
                    if (RichEditor.CaretPosition.Paragraph is { } quoteParagraph)
                    {
                        if (quoteParagraph.Parent is Section { Tag: "quote" } quote && BlockCollectionOf(quote.Parent) is { } siblings)
                        {
                            var blocks = quote.Blocks.Cast<Block>().ToArray();
                            foreach (var block in blocks) { quote.Blocks.Remove(block); siblings.InsertBefore(quote, block); }
                            siblings.Remove(quote);
                        }
                        else if (quoteParagraph.Parent == RichEditor.Document)
                        {
                            var section = new Section { Tag = "quote", BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(15, 4, 0, 4), Margin = new Thickness(0, 4, 0, 18) };
                            RichEditor.Document.Blocks.InsertBefore(quoteParagraph, section);
                            RichEditor.Document.Blocks.Remove(quoteParagraph); section.Blocks.Add(quoteParagraph);
                        }
                    }
                    break;
                case "CodeBlock":
                    if (RichEditor.CaretPosition.Paragraph is { } cp)
                    {
                        cp.Tag = "code:"; cp.FontFamily = new FontFamily("Consolas"); cp.FontSize = 13;
                        cp.Background = (Brush)new BrushConverter().ConvertFromString("#F2F3F8")!; cp.Padding = new Thickness(14);
                    }
                    break;
                case "Rule": InsertMarkdownBlock("---"); break;
                case "Table": InsertMarkdownBlock("| 見出し 1 | 見出し 2 |\n| --- | --- |\n| テキスト | テキスト |"); break;
            }
        }
        finally { RichEditor.EndChange(); }
        RichChanged(_active);
    }

    private void InsertMarkdownBlock(string markdown)
    {
        var parsed = _active!.Codec.Render(markdown, BaseDirectory(_active));
        var blocks = parsed.Blocks.Cast<Block>().ToArray();
        var anchor = RichEditor.CaretPosition.Paragraph as Block;
        // Inserting inside a list/table places a new top-level block after that container.
        var element = anchor as TextElement;
        while (element?.Parent is TextElement parent) { if (parent is Block block) anchor = block; element = parent; }
        if (anchor?.Parent != RichEditor.Document) anchor = RichEditor.Document.Blocks.LastBlock;
        foreach (var block in blocks)
        {
            parsed.Blocks.Remove(block);
            if (anchor == null) RichEditor.Document.Blocks.Add(block); else RichEditor.Document.Blocks.InsertAfter(anchor, block);
            anchor = block;
        }
        var after = new Paragraph();
        if (anchor != null) RichEditor.Document.Blocks.InsertAfter(anchor, after); else RichEditor.Document.Blocks.Add(after);
        RichEditor.CaretPosition = after.ContentStart;
    }

    private static BlockCollection? BlockCollectionOf(DependencyObject parent) => parent switch
    { FlowDocument doc => doc.Blocks, Section section => section.Blocks, ListItem item => item.Blocks, TableCell cell => cell.Blocks, _ => null };

    private void RichEditor_KeyDown(DocumentSession session, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        var rich = session.Rich;
        if (rich.CaretPosition.Paragraph is not { } paragraph) return;
        if (paragraph.Tag is string kind && kind.StartsWith("code:", StringComparison.Ordinal))
        {
            EditingCommands.EnterLineBreak.Execute(null, rich); e.Handled = true;
        }
        else if (paragraph.Tag is string { Length: 2 } tag && tag[0] == 'h' && rich.Selection.IsEmpty
            && new TextRange(rich.CaretPosition, paragraph.ContentEnd).Text.Length == 0 && BlockCollectionOf(paragraph.Parent) is { } blocks)
        {
            rich.BeginChange();
            try
            {
                var next = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
                blocks.InsertAfter(paragraph, next); rich.CaretPosition = next.ContentStart;
            }
            finally { rich.EndChange(); }
            e.Handled = true;
        }
    }

    private void WrapSource(string before, string after, string placeholder = "テキスト")
    {
        SourceEditor.Focus();
        var start = SourceEditor.SelectionStart;
        var text = SourceEditor.SelectedText;
        if (text.Length == 0) text = placeholder;
        SourceEditor.SelectedText = before + text + after;
        SourceEditor.Select(start + before.Length, text.Length);
    }

    private void ReplaceSourceSelection(string text)
    {
        SourceEditor.Focus(); var start = SourceEditor.SelectionStart;
        SourceEditor.SelectedText = _active!.File.NormalizeNewLines(text);
        SourceEditor.CaretIndex = start + _active.File.NormalizeNewLines(text).Length;
    }

    private void PrefixSourceLines(string prefix, bool heading = false)
    {
        SourceEditor.Focus();
        var text = SourceEditor.Text;
        var start = SourceEditor.SelectionStart;
        var end = start + SourceEditor.SelectionLength;
        while (start > 0 && text[start - 1] != '\n') start--;
        while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
        var lines = text[start..end].Replace("\r", "").Split('\n');
        var result = string.Join(_active!.File.NewLine, lines.Select(line => prefix + (heading ? Regex.Replace(line, @"^#{1,6}\s+", "") : line)));
        SourceEditor.Select(start, end - start); SourceEditor.SelectedText = result; SourceEditor.Select(start, result.Length);
    }

    private void BlockStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingStyle || _active == null || BlockStyle.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        FlushPending();
        var level = int.Parse(value);
        if (!_active.RichActive) PrefixSourceLines(level > 0 ? new string('#', level) + " " : "", true);
        else
        {
            RichEditor.Focus();
            RichEditor.BeginChange();
            try
            {
                var start = RichEditor.Selection.Start.Paragraph;
                var end = RichEditor.Selection.End.Paragraph;
                for (var p = start; p != null; p = p.NextBlock as Paragraph)
                { MarkdownCodec.SetHeading(p, level); if (p == end) break; }
            }
            finally { RichEditor.EndChange(); }
            RichChanged(_active);
        }
    }

    private void UpdateBlockStyle()
    {
        if (!_ready || _active == null) return;
        var level = 0;
        if (_active.RichActive && RichEditor.CaretPosition.Paragraph?.Tag is string { Length: 2 } tag && tag[0] == 'h') level = Math.Clamp(tag[1] - '0', 0, 3);
        _updatingStyle = true;
        try { BlockStyle.SelectedIndex = level; }
        finally { _updatingStyle = false; }
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string mode }) SetViewMode(mode); }

    internal void SetViewMode(string mode)
    {
        if (_viewMode == "Split") RememberRatio();
        FlushPending(); _viewMode = mode;
        ApplyPaneLayout();
        SaveLayout();
    }

    private void PaneLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value } && Enum.TryParse<PaneLayout>(value, out var layout)) SetPaneLayout(layout);
    }

    internal void SetPaneLayout(PaneLayout layout)
    {
        if (_viewMode == "Split") RememberRatio();
        FlushPending(); EndPaneDrag();
        _paneLayout = layout; _viewMode = "Split";
        ApplyPaneLayout();
        SaveLayout();
    }

    private void ApplyPaneLayout()
    {
        var mode = _viewMode;
        var sideBySide = _paneLayout == PaneLayout.SideBySide;
        var split = mode == "Split";
        var showFirst = mode == "Source" ? !_panesSwapped : _panesSwapped;
        var first = split ? SplitRatio : showFirst ? 1 : 0;
        var second = split ? 1 - SplitRatio : showFirst ? 0 : 1;
        SourcePane.Visibility = mode == "Rich" ? Visibility.Collapsed : Visibility.Visible;
        RichPane.Visibility = mode == "Source" ? Visibility.Collapsed : Visibility.Visible;
        PaneSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        FirstColumn.MinWidth = SecondColumn.MinWidth = sideBySide && split ? 180 : 0;
        FirstRow.MinHeight = SecondRow.MinHeight = !sideBySide && split ? 120 : 0;
        FirstColumn.Width = new GridLength(sideBySide ? first : 1, GridUnitType.Star);
        SecondColumn.Width = new GridLength(sideBySide ? second : 0, GridUnitType.Star);
        DividerColumn.Width = new GridLength(sideBySide && split ? 9 : 0);
        FirstRow.Height = new GridLength(sideBySide ? 1 : first, GridUnitType.Star);
        SecondRow.Height = new GridLength(sideBySide ? 0 : second, GridUnitType.Star);
        DividerRow.Height = new GridLength(!sideBySide && split ? 9 : 0);
        Grid.SetColumn(SourcePane, sideBySide && _panesSwapped ? 2 : 0);
        Grid.SetColumn(RichPane, sideBySide && !_panesSwapped ? 2 : 0);
        Grid.SetRow(SourcePane, !sideBySide && _panesSwapped ? 2 : 0);
        Grid.SetRow(RichPane, !sideBySide && !_panesSwapped ? 2 : 0);
        Grid.SetColumn(PaneSplitter, sideBySide ? 1 : 0);
        Grid.SetRow(PaneSplitter, sideBySide ? 0 : 1);
        PaneSplitter.Width = sideBySide ? 9 : double.NaN;
        PaneSplitter.Height = sideBySide ? double.NaN : 9;
        PaneSplitter.ResizeDirection = sideBySide ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        PaneSplitter.Cursor = sideBySide ? Cursors.SizeWE : Cursors.SizeNS;
        PaneSplitter.ToolTip = $"ドラッグして{(sideBySide ? "幅" : "高さ")}を変更 / ダブルクリックで均等に分割";
        System.Windows.Automation.AutomationProperties.SetName(PaneSplitter, sideBySide ? "左右の編集領域の幅を変更" : "上下の編集領域の高さを変更");
        SideBySideButton.Background = split && sideBySide ? Brushes.White : Brushes.Transparent;
        StackedButton.Background = split && !sideBySide ? Brushes.White : Brushes.Transparent;
        SourceModeButton.Background = mode == "Source" ? Brushes.White : Brushes.Transparent;
        RichModeButton.Background = mode == "Rich" ? Brushes.White : Brushes.Transparent;
        SideBySideMenu.IsChecked = split && sideBySide;
        StackedMenu.IsChecked = split && !sideBySide;
        SwapPanesMenu.IsEnabled = split;
        UpdateStatus();
    }

    private void PaletteOrientation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value } && Enum.TryParse<Orientation>(value, out var orientation)) SetPaletteOrientation(orientation);
    }
    private void TogglePalette_Click(object sender, RoutedEventArgs e)
        => SetPaletteOrientation(_paletteOrientation == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal);

    internal void SetPaletteOrientation(Orientation orientation)
    {
        _paletteOrientation = orientation;
        var vertical = orientation == Orientation.Vertical;
        PaletteItems.Orientation = orientation;
        FormatPalette.Width = vertical ? 144 : double.NaN;
        FormatPalette.Margin = vertical ? new Thickness(0, 0, 10, 0) : new Thickness(0);
        FormatPalette.Padding = vertical ? new Thickness(8, 9, 8, 9) : new Thickness(12, 9, 12, 9);
        FormatPalette.CornerRadius = vertical ? new CornerRadius(9) : new CornerRadius(9, 9, 0, 0);
        FormatPalette.BorderThickness = vertical ? new Thickness(1) : new Thickness(1, 1, 1, 0);
        Grid.SetRowSpan(FormatPalette, vertical ? 3 : 1);
        Grid.SetColumnSpan(FormatPalette, vertical ? 1 : 2);
        foreach (var surface in new FrameworkElement[] { FindBar, EditorBorder })
        {
            Grid.SetColumn(surface, vertical ? 1 : 0);
            Grid.SetColumnSpan(surface, vertical ? 1 : 2);
        }
        EditorBorder.CornerRadius = vertical ? new CornerRadius(9) : new CornerRadius(0, 0, 9, 9);
        DockPanel.SetDock(PaletteOrientationButton, vertical ? Dock.Top : Dock.Right);
        PaletteOrientationButton.Margin = vertical ? new Thickness(0, 0, 0, 8) : new Thickness(8, 0, 0, 0);
        PaletteOrientationButton.Content = vertical ? "↔ 横置き" : "↕ 縦置き";
        PaletteOrientationButton.ToolTip = vertical ? "書式パレットを横置きに切り替え" : "書式パレットを縦置きに切り替え";
        PaletteScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PaletteScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        BlockStyle.Width = vertical ? 110 : 102;
        BlockStyle.Margin = vertical ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 10, 0);
        foreach (var separator in PaletteItems.Children.OfType<Border>())
        {
            separator.Width = vertical ? double.NaN : 1;
            separator.Height = vertical ? 1 : double.NaN;
            separator.Margin = vertical ? new Thickness(4, 8, 4, 8) : new Thickness(10, 4, 10, 4);
        }
        HorizontalPaletteMenu.IsChecked = !vertical;
        VerticalPaletteMenu.IsChecked = vertical;
        SaveLayout();
    }

    internal void SaveLayout()
    {
        if (!_ready || _restoringLayout) return;
        var failed = !_layoutSettingsStore.Save(new LayoutSettings
        {
            PaneLayout = _paneLayout, PaletteOrientation = _paletteOrientation, PanesSwapped = _panesSwapped,
            ViewMode = _viewMode, SideBySideRatio = _sideBySideRatio, StackedRatio = _stackedRatio
        });
        if (failed != _layoutSaveFailed) { _layoutSaveFailed = failed; UpdateStatus(); }
    }

    private void RememberRatio()
    {
        var first = _paneLayout == PaneLayout.SideBySide ? FirstColumn.ActualWidth : FirstRow.ActualHeight;
        var second = _paneLayout == PaneLayout.SideBySide ? SecondColumn.ActualWidth : SecondRow.ActualHeight;
        if (first + second > 0) SplitRatio = Math.Clamp(first / (first + second), 0.05, 0.95);
    }

    private void ResetSplit_Click(object sender, RoutedEventArgs e)
    {
        SplitRatio = 0.5; _viewMode = "Split";
        ApplyPaneLayout();
        SaveLayout();
    }
    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e) { ResetSplit_Click(sender, e); e.Handled = true; }
    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) { RememberRatio(); UpdateStatus(); SaveLayout(); }
    private void Splitter_KeyUp(object sender, KeyEventArgs e)
    {
        if (_viewMode != "Split" || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        EditorGrid.UpdateLayout(); RememberRatio(); UpdateStatus(); SaveLayout();
    }

    private void PaneHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewMode != "Split" || sender is not Border header) return;
        _dragStart = e.GetPosition(this); _dragHeader = header;
        header.CaptureMouse(); e.Handled = true;
    }
    private void PaneHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragHeader == null) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _paneDragging = true;
        var target = _dragHeader == SourcePaneHeader ? RichPaneHeader : SourcePaneHeader;
        target.Background = (Brush)new BrushConverter().ConvertFromString("#EAE7FC")!;
        Mouse.OverrideCursor = Cursors.SizeAll;
    }
    private void PaneHeader_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragHeader == null) return;
        var target = _dragHeader == SourcePaneHeader ? RichPane : SourcePane;
        var bounds = new Rect(target.TranslatePoint(new Point(), this), target.RenderSize);
        var swap = _paneDragging && bounds.Contains(e.GetPosition(this));
        EndPaneDrag();
        if (swap) SwapPanes();
        e.Handled = true;
    }
    private void PaneHeader_LostCapture(object sender, MouseEventArgs e) => EndPaneDrag();
    private void EndPaneDrag()
    {
        var header = _dragHeader;
        _dragHeader = null; _paneDragging = false; Mouse.OverrideCursor = null;
        SourcePaneHeader.Background = RichPaneHeader.Background = Brushes.Transparent;
        header?.ReleaseMouseCapture();
    }

    internal void SwapPanes()
    {
        if (_viewMode == "Split") RememberRatio();
        _panesSwapped = !_panesSwapped;
        ApplyPaneLayout();
        SaveLayout();
    }
    private void SwapPanes_Click(object sender, RoutedEventArgs e) => SwapPanes();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (GetDroppedFiles(e.Data) is not { } paths) return;
        var accepted = paths.Length > 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        FileDropCountLabel.Text = $"{paths.Length} ファイルをそれぞれタブで開きます";
        FileDropOverlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        FileDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        FileDropOverlay.Visibility = Visibility.Collapsed;
        if (GetDroppedFiles(e.Data) is not { } paths) return;
        // Handle the tunneling event before TextBox/RichTextBox can consume the file payload as text.
        e.Handled = true;
        e.Effects = paths.Length > 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects == DragDropEffects.None) return;
        foreach (var path in paths) OpenPath(path);
    }

    private static string[]? GetDroppedFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        return data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    private void Find_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); }
    private void CloseFind_Click(object sender, RoutedEventArgs e) { FindBar.Visibility = Visibility.Collapsed; if (_active?.RichActive == true) RichEditor.Focus(); else _active?.Source.Focus(); }
    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { FindNext_Click(sender, e); e.Handled = true; } else if (e.Key == Key.Escape) CloseFind_Click(sender, e); }
    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || FindBox.Text.Length == 0) return;
        FlushPending();
        if (_viewMode == "Rich") SetViewMode("Split");
        var start = Math.Min(SourceEditor.SelectionStart + SourceEditor.SelectionLength, SourceEditor.Text.Length);
        var index = SourceEditor.Text.IndexOf(FindBox.Text, start, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) index = SourceEditor.Text.IndexOf(FindBox.Text, StringComparison.CurrentCultureIgnoreCase);
        FindResult.Text = index < 0 ? "見つかりません" : "";
        if (index >= 0) { SourceEditor.Focus(); SourceEditor.Select(index, FindBox.Text.Length); SourceEditor.ScrollToLine(SourceEditor.GetLineIndexFromCharacterIndex(index)); }
    }

    private void SetZoom(int value) { _zoom = Math.Clamp(value, 70, 180); ApplyZoom(); UpdateStatus(); }
    private void ApplyZoom()
    {
        if (_active == null) return;
        SourceEditor.FontSize = 14 * _zoom / 100.0;
        RichEditor.LayoutTransform = new ScaleTransform(_zoom / 100.0, _zoom / 100.0);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + 10);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - 10);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(100);
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"MDVieW {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n\nWindows ネイティブの Markdown / WYSIWYG エディタ\n\n・ファイルをドロップしてタブで開く\n・左右 / 上下に配置し、見出しをドラッグして入れ替え\n・中央の境界線で幅や高さを調整\n・書式パレットを横置き / 縦置きに切り替え\n・Ctrl+Tab でタブを切り替え\n・Ctrl+W でタブを閉じる\n\nMarkdown 解析: Markdig (BSD-2-Clause)", "MDVieW について", MessageBoxButton.OK, MessageBoxImage.Information);
}
