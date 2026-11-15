using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using MDVieW;
using WpfList = System.Windows.Documents.List;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        try
        {
            Check("Markdown source survives native rendering exactly", RoundTrips);
            Check("Rich edits preserve untouched source blocks", EditPreservesOtherBlocks);
            Check("Native formatting serializes to Markdown", Formatting);
            Check("Tables, nested lists, tasks and fenced code", StructuredContent);
            Check("Literal Markdown punctuation stays literal", LiteralText);
            Check("UTF-8 BOM, Japanese and CRLF survive atomic save", FileRoundTrip);
            Check("Tabs retain editor state and synchronize independently", TabsAndSync);
            Check("File drops work over editors and tabs without replacing edits", FileDrops);
            Check("Pane and palette layouts preserve editing and independent split sizes", LayoutChanges);
            Check("Layout preferences and both split ratios survive fresh windows", LayoutPersistence);
            Check("Invalid settings and failed writes preserve a usable editor", InvalidLayoutSettings);
            Console.WriteLine($"PASS: {_passed} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(string label, Action action) { action(); _passed++; Console.WriteLine("PASS " + label); }
    private static void Equal(string expected, string actual) { if (expected != actual) throw new Exception($"Expected:\n{expected}\nActual:\n{actual}"); }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static MainWindow NewWindow(LayoutSettingsStore? settings = null) => new(settings ?? new LayoutSettingsStore(null));

    private static void RoundTrips()
    {
        string[] samples =
        [
            "", "\n\n", "日本語と English\n", "# 見出し\r\n\r\n**太字** と _斜体_\r\n",
            "Title\n=====\n\n本文  \n改行\n\n", "> 引用\n>\n> - 入れ子\n\n終わり",
            "3. third\n4. fourth\n\n- [x] done\n- [ ] todo\n",
            "~~~csharp\nvar x = `value`;\n~~~\n", "| 左 | 右 |\n| :--- | ---: |\n| あ | い |\n",
            "[参照][id]\n\n[id]: https://example.com \"title\"\n", "<!-- comment -->\n\n<div>raw HTML</div>\n\ntext\n",
            "![画像](missing.png)\n\n~~削除~~ と **bold _italic_**\n", Welcome.Text
        ];
        foreach (var sample in samples) { var codec = new MarkdownCodec(); Equal(sample, codec.Serialize(codec.Render(sample))); }
    }

    private static void EditPreservesOtherBlocks()
    {
        var codec = new MarkdownCodec();
        const string original = "Title\n=====\n\n最初の段落。\n\n* unusual bullet\n* another\n\n<!-- keep me -->\n";
        var doc = codec.Render(original);
        var paragraph = doc.Blocks.OfType<Paragraph>().Skip(1).First();
        paragraph.Inlines.Add(new Run("追加。"));
        Equal(original.Replace("最初の段落。", "最初の段落。追加。"), codec.Serialize(doc));
    }

    private static void Formatting()
    {
        var codec = new MarkdownCodec(); var doc = codec.Render(""); var p = doc.Blocks.FirstBlock as Paragraph ?? throw new Exception();
        p.Inlines.Add(new Bold(new Run("太字"))); p.Inlines.Add(new Run(" と ")); p.Inlines.Add(new Italic(new Run("斜体")));
        Equal("**太字** と *斜体*", codec.Serialize(doc));
        MarkdownCodec.SetHeading(p, 2);
        Equal("## **太字** と *斜体*", codec.Serialize(doc));
        var link = new Hyperlink(new Run("example")) { NavigateUri = new Uri("https://example.com") };
        p.Inlines.Clear(); p.Inlines.Add(link); MarkdownCodec.SetHeading(p, 0);
        Equal("[example](https://example.com)", codec.Serialize(doc));
    }

    private static void StructuredContent()
    {
        var codec = new MarkdownCodec();
        var doc = codec.Render("- [ ] task\n- parent\n  - nested\n\n| A | B |\n| --- | ---: |\n| C | D |\n\n```csharp\nhello\n```\n");
        var list = doc.Blocks.OfType<WpfList>().Single();
        var paragraph = list.ListItems.FirstListItem!.Blocks.OfType<Paragraph>().First();
        var check = paragraph.Inlines.OfType<InlineUIContainer>().Select(i => i.Child).OfType<CheckBox>().Single();
        check.IsChecked = true;
        var text = codec.Serialize(doc);
        True(text.Contains("- [x] task"), text);
        True(text.Contains("  - nested"), "Nested list was lost");
        var table = doc.Blocks.OfType<Table>().Single();
        var cell = table.RowGroups[0].Rows[1].Cells[0];
        ((Paragraph)cell.Blocks.FirstBlock).Inlines.Add(new Run(" changed"));
        text = codec.Serialize(doc); True(text.Contains("C changed"), text); True(text.Contains("---:"), "Table alignment was lost");
        var code = doc.Blocks.OfType<Paragraph>().Single(p => p.Tag as string == "code:csharp");
        code.Inlines.Add(new Run("\n```"));
        text = codec.Serialize(doc); True(text.Contains("````csharp"), "Code fence should grow around embedded backticks");
    }

    private static void LiteralText()
    {
        var codec = new MarkdownCodec(); var doc = codec.Render(""); var p = (Paragraph)doc.Blocks.FirstBlock;
        p.Inlines.Add(new Run("# literal *stars* [brackets]"));
        var markdown = codec.Serialize(doc);
        var rendered = codec.Render(markdown);
        Equal("# literal *stars* [brackets]", new TextRange(rendered.Blocks.FirstBlock.ContentStart, rendered.Blocks.FirstBlock.ContentEnd).Text.TrimEnd('\r', '\n'));
    }

    private static void FileRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MDVieW-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "日本語.md"); const string text = "# 日本語\r\n\r\n本文\r\n";
            File.WriteAllText(path, text, new UTF8Encoding(true));
            var file = new DocumentFile(); Equal(text, file.Open(path)); True(file.HasBom && file.NewLine == "\r\n", "Encoding detection failed");
            var edited = file.NormalizeNewLines(text + "追記\n"); file.Save(path, edited);
            True(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble), "BOM was lost"); Equal(edited, file.Open(path));
            True(Directory.GetFiles(dir).Length == 1, "Temporary save file remained");
            var badPath = Path.Combine(dir, "bad.md"); File.WriteAllBytes(badPath, [0xff, 0xfe, 0x00]);
            try { file.Open(badPath); throw new Exception("Invalid UTF-8 accepted"); } catch (DecoderFallbackException) { }
            Equal(path, file.FilePath!); Equal(edited, file.SavedText);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void TabsAndSync()
    {
        var window = NewWindow();
        var tabs = (TabControl)window.FindName("DocumentTabs");
        var initial = (DocumentSession)((TabItem)tabs.SelectedItem).Tag;
        initial.Source.Text = "# First\n\nこんにちは"; initial.Source.CaretIndex = 3; window.FlushPending();
        True(initial.Rich.Document.Blocks.OfType<Paragraph>().First().Tag as string == "h1", "Source did not update rich content");
        var temp = Path.Combine(Path.GetTempPath(), "MDVieW-second-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(temp, "# Second", new UTF8Encoding(false));
        try
        {
            window.OpenPath(temp); True(tabs.Items.Count == 2, "Expected two tabs");
            var second = (DocumentSession)((TabItem)tabs.SelectedItem).Tag;
            ((Paragraph)second.Rich.Document.Blocks.FirstBlock).Inlines.Add(new Run(" changed")); window.FlushPending();
            Equal("# Second changed", second.Source.Text);
            tabs.SelectedItem = initial.Tab;
            Equal("# First\n\nこんにちは", initial.Source.Text); True(initial.Source.CaretIndex == 3, "Tab lost caret");
            window.OpenPath(temp); True(tabs.Items.Count == 2, "Same file opened twice");
            window.SwapPanes();
            True(Grid.GetColumn((UIElement)window.FindName("SourcePane")) == 2, "Source pane did not move");
            True(Grid.GetColumn((UIElement)window.FindName("RichPane")) == 0, "Rich pane did not move");
            window.SwapPanes();
            True(Grid.GetColumn((UIElement)window.FindName("SourcePane")) == 0, "Source pane did not restore");
            second.RichActive = true;
            second.Rich.CaretPosition = second.Rich.Document.ContentEnd;
            window.Format("InlineCode"); window.FlushPending();
            True(second.Source.Text.Contains("`コード`"), "Inline code button did not insert code: " + second.Source.Text);
        }
        finally { File.Delete(temp); }
    }

    private static DragEventArgs RaiseDrag(UIElement target, RoutedEvent routedEvent, IDataObject data, DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move)
    {
        // WPF's drag event constructor is internal; exercise the real routed events without an OLE mouse loop.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [data, DragDropKeyStates.LeftMouseButton, allowed, target, new Point(20, 20)], culture: null)!;
        args.RoutedEvent = routedEvent;
        target.RaiseEvent(args);
        return args;
    }

    private static void FileDrops()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MDVieW-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var firstPath = Path.Combine(directory, "日本語のメモ.md");
            var secondPath = Path.Combine(directory, "second.txt");
            File.WriteAllText(firstPath, "# ドロップした文書", new UTF8Encoding(false));
            File.WriteAllText(secondPath, "Second document", new UTF8Encoding(false));
            foreach (var surface in new[] { "Source", "Rich", "Tabs", "Window" })
            {
                var window = NewWindow();
                if (surface is "Rich" or "Window") window.SetPaneLayout(PaneLayout.Stacked);
                if (surface is "Tabs" or "Window") window.SetPaletteOrientation(Orientation.Vertical);
                window.Measure(new Size(1380, 900)); window.Arrange(new Rect(0, 0, 1380, 900)); window.UpdateLayout();
                var tabs = (TabControl)window.FindName("DocumentTabs");
                var original = (DocumentSession)((TabItem)tabs.SelectedItem).Tag;
                original.Source.Text = "# 未保存の編集内容"; original.Source.CaretIndex = 5; window.FlushPending();
                UIElement target = surface switch { "Source" => original.Source, "Rich" => original.Rich, "Tabs" => tabs, _ => window };
                var data = new DataObject(DataFormats.FileDrop, new[] { firstPath, secondPath, firstPath, directory, Path.Combine(directory, "missing.md") });
                data.SetData(DataFormats.UnicodeText, "This must not replace the open document.");
                var over = RaiseDrag(target, DragDrop.PreviewDragOverEvent, data);
                True(over.Handled && over.Effects == DragDropEffects.Copy, $"{surface}: file drag was not accepted as a copy");
                var drop = RaiseDrag(target, DragDrop.PreviewDropEvent, data);
                True(drop.Handled, $"{surface}: file drop was not handled before the editor's default handler");
                True(tabs.Items.Count == 3, $"{surface}: expected two new document tabs, got {tabs.Items.Count}");
                Equal("# 未保存の編集内容", original.Source.Text);
                True(original.IsDirty && original.Source.CaretIndex == 5, $"{surface}: unsaved editor state was changed");
                var second = (DocumentSession)((TabItem)tabs.SelectedItem).Tag;
                Equal(secondPath, second.File.FilePath!); Equal("Second document", second.Source.Text);
                True(File.Exists(firstPath) && File.Exists(secondPath), "Dropping files moved the originals");
                var existingDrop = RaiseDrag(second.Rich, DragDrop.PreviewDropEvent, new DataObject(DataFormats.FileDrop, new[] { firstPath }));
                True(existingDrop.Handled && tabs.Items.Count == 3, "Dropping an open file created a duplicate tab");
                Equal(firstPath, ((DocumentSession)((TabItem)tabs.SelectedItem).Tag).File.FilePath!);
                var overlay = (Border)window.FindName("FileDropOverlay");
                True(overlay.Visibility == Visibility.Collapsed, "Drop feedback remained visible after opening files");
                var enter = RaiseDrag(window, DragDrop.PreviewDragEnterEvent, data);
                True(enter.Effects == DragDropEffects.Copy && overlay.Visibility == Visibility.Visible, "File drag feedback did not appear");
                RaiseDrag(window, DragDrop.PreviewDragLeaveEvent, data);
                True(overlay.Visibility == Visibility.Collapsed, "Drag cancellation left stale feedback");
                var folder = RaiseDrag(window, DragDrop.PreviewDropEvent, new DataObject(DataFormats.FileDrop, new[] { directory }));
                True(folder.Handled && folder.Effects == DragDropEffects.None && tabs.Items.Count == 3, "A folder was accepted as a file");
                var moveOnly = RaiseDrag(window, DragDrop.PreviewDropEvent, data, DragDropEffects.Move);
                True(moveOnly.Handled && moveOnly.Effects == DragDropEffects.None && tabs.Items.Count == 3, "A move-only drag was accepted");
                var plainText = RaiseDrag(window, DragDrop.PreviewDragOverEvent, new DataObject(DataFormats.UnicodeText, "plain text"));
                True(!plainText.Handled, "Normal text dragging was intercepted by file handling");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void ArrangeWorkspace(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1340, 840)); root.Arrange(new Rect(0, 0, 1340, 840)); root.UpdateLayout();
    }

    private static void LayoutChanges()
    {
        var window = NewWindow();
        var tabs = (TabControl)window.FindName("DocumentTabs");
        var session = (DocumentSession)((TabItem)tabs.SelectedItem).Tag;
        session.Source.Text = "# Layout\n\nbody"; session.Source.CaretIndex = session.Source.Text.Length;
        session.Source.SelectedText = " 追加"; window.FlushPending();
        var source = session.Source; var document = session.Rich.Document;
        var caret = source.CaretIndex; var text = source.Text; var undo = source.CanUndo;
        var sourcePane = (FrameworkElement)window.FindName("SourcePane");
        var richPane = (FrameworkElement)window.FindName("RichPane");
        var editor = (FrameworkElement)window.FindName("EditorGrid");
        var splitter = (GridSplitter)window.FindName("PaneSplitter");
        var palette = (FrameworkElement)window.FindName("FormatPalette");
        var paletteItems = (StackPanel)window.FindName("PaletteItems");
        ArrangeWorkspace(window);
        True(sourcePane.ActualWidth > 180 && richPane.TranslatePoint(new Point(), editor).X > sourcePane.ActualWidth, "Side-by-side panes overlap");
        ((ColumnDefinition)window.FindName("FirstColumn")).Width = new GridLength(2, GridUnitType.Star);
        ((ColumnDefinition)window.FindName("SecondColumn")).Width = new GridLength(1, GridUnitType.Star);
        ArrangeWorkspace(window);
        var widthRatio = sourcePane.ActualWidth / (sourcePane.ActualWidth + richPane.ActualWidth);
        window.SetPaneLayout(PaneLayout.Stacked); ArrangeWorkspace(window);
        True(splitter.ResizeDirection == GridResizeDirection.Rows && sourcePane.ActualHeight >= 120 && richPane.TranslatePoint(new Point(), editor).Y > sourcePane.ActualHeight, "Stacked panes overlap or cannot resize vertically");
        ((RowDefinition)window.FindName("FirstRow")).Height = new GridLength(3, GridUnitType.Star);
        ((RowDefinition)window.FindName("SecondRow")).Height = new GridLength(2, GridUnitType.Star);
        ArrangeWorkspace(window);
        var heightRatio = sourcePane.ActualHeight / (sourcePane.ActualHeight + richPane.ActualHeight);
        window.SetPaneLayout(PaneLayout.SideBySide); ArrangeWorkspace(window);
        True(Math.Abs(sourcePane.ActualWidth / (sourcePane.ActualWidth + richPane.ActualWidth) - widthRatio) < 0.01, "Horizontal split size was lost");
        window.SetPaneLayout(PaneLayout.Stacked); ArrangeWorkspace(window);
        True(Math.Abs(sourcePane.ActualHeight / (sourcePane.ActualHeight + richPane.ActualHeight) - heightRatio) < 0.01, "Vertical split size was lost");
        window.SwapPanes(); ArrangeWorkspace(window);
        True(sourcePane.TranslatePoint(new Point(), editor).Y > richPane.ActualHeight, "Stacked panes did not swap");
        window.SetViewMode("Source"); ArrangeWorkspace(window);
        True(richPane.Visibility == Visibility.Collapsed && Math.Abs(sourcePane.ActualHeight - editor.ActualHeight) < 1, "Single-pane mode left an empty stacked slot");
        window.SetViewMode("Rich"); ArrangeWorkspace(window);
        True(sourcePane.Visibility == Visibility.Collapsed && Math.Abs(richPane.ActualHeight - editor.ActualHeight) < 1, "WYSIWYG-only mode did not fill the workspace");
        window.SetViewMode("Split"); window.SwapPanes(); ArrangeWorkspace(window);
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal, Orientation.Vertical })
        {
            window.SetPaletteOrientation(orientation); ArrangeWorkspace(window);
            True(paletteItems.Orientation == orientation, "Palette tools did not change orientation");
            True(palette.ActualHeight > 0 && palette.ActualWidth > 0 && editor.ActualHeight > 240, "Palette consumed the editor workspace");
            if (orientation == Orientation.Vertical)
                True(editor.TranslatePoint(new Point(), (UIElement)window.Content).X > palette.ActualWidth, "Vertical palette overlaps the editor");
            Equal(text, source.Text);
            True(ReferenceEquals(session.Source, source) && ReferenceEquals(session.Rich.Document, document), "Layout changes recreated an editor or document");
            True(source.CaretIndex == caret && source.CanUndo == undo && session.IsDirty, "Layout changes lost editing state");
        }
        source.Select(source.Text.IndexOf("body", StringComparison.Ordinal), 4);
        paletteItems.Children.OfType<Button>().Single(button => button.Tag as string == "Bold").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FlushPending(); True(source.Text.Contains("**body**"), "Formatting stopped working after rotating the palette");
    }

    private static void FinishSplitResize(MainWindow window)
        => ((GridSplitter)window.FindName("PaneSplitter")).RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

    private static double FirstPaneRatio(MainWindow window, bool stacked)
    {
        var first = stacked ? ((RowDefinition)window.FindName("FirstRow")).ActualHeight : ((ColumnDefinition)window.FindName("FirstColumn")).ActualWidth;
        var second = stacked ? ((RowDefinition)window.FindName("SecondRow")).ActualHeight : ((ColumnDefinition)window.FindName("SecondColumn")).ActualWidth;
        return first / (first + second);
    }

    private static void LayoutPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MDVieW-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state", "layout.json");
        try
        {
            var store = new LayoutSettingsStore(path);
            var first = NewWindow(store); ArrangeWorkspace(first);
            ((ColumnDefinition)first.FindName("FirstColumn")).Width = new GridLength(3, GridUnitType.Star);
            ((ColumnDefinition)first.FindName("SecondColumn")).Width = new GridLength(2, GridUnitType.Star);
            ArrangeWorkspace(first); FinishSplitResize(first);
            True(File.Exists(path), "Dragging a splitter did not automatically create the settings file");
            True(Math.Abs(store.Load().SideBySideRatio - 0.6) < 0.01, "Horizontal ratio was not saved on drag completion");
            first.SetPaneLayout(PaneLayout.Stacked); ArrangeWorkspace(first);
            ((RowDefinition)first.FindName("FirstRow")).Height = new GridLength(2, GridUnitType.Star);
            ((RowDefinition)first.FindName("SecondRow")).Height = new GridLength(3, GridUnitType.Star);
            ArrangeWorkspace(first); FinishSplitResize(first);
            first.SetPaletteOrientation(Orientation.Vertical); ArrangeWorkspace(first);
            first.SwapPanes(); ArrangeWorkspace(first);
            var saved = File.ReadAllText(path);
            first.Close();

            var second = NewWindow(new LayoutSettingsStore(path)); ArrangeWorkspace(second);
            Equal(saved, File.ReadAllText(path));
            var editor = (FrameworkElement)second.FindName("EditorGrid");
            var sourcePane = (FrameworkElement)second.FindName("SourcePane");
            var richPane = (FrameworkElement)second.FindName("RichPane");
            True(((StackPanel)second.FindName("PaletteItems")).Orientation == Orientation.Vertical, "Palette orientation was not restored");
            True(((GridSplitter)second.FindName("PaneSplitter")).ResizeDirection == GridResizeDirection.Rows, "Stacked layout was not restored");
            True(sourcePane.TranslatePoint(new Point(), editor).Y > richPane.ActualHeight, "Pane order was not restored");
            True(Math.Abs(FirstPaneRatio(second, stacked: true) - 0.4) < 0.01, "Startup measurements overwrote the saved vertical ratio");
            second.SetPaneLayout(PaneLayout.SideBySide); ArrangeWorkspace(second);
            True(Math.Abs(FirstPaneRatio(second, stacked: false) - 0.6) < 0.01, "The inactive horizontal ratio was not restored");
            second.SetViewMode("Rich"); second.Close();

            var third = NewWindow(new LayoutSettingsStore(path)); ArrangeWorkspace(third);
            True(((FrameworkElement)third.FindName("SourcePane")).Visibility == Visibility.Collapsed, "Single-pane view mode was not restored");
            var visible = (FrameworkElement)third.FindName("RichPane");
            True(Math.Abs(visible.ActualWidth - ((FrameworkElement)third.FindName("EditorGrid")).ActualWidth) < 1, "The restored single pane did not fill the editor");
            third.SetViewMode("Split"); ArrangeWorkspace(third);
            True(Math.Abs(FirstPaneRatio(third, stacked: false) - 0.6) < 0.01, "Single-pane startup destroyed the saved width ratio");
            third.SetPaneLayout(PaneLayout.Stacked); ArrangeWorkspace(third);
            True(Math.Abs(FirstPaneRatio(third, stacked: true) - 0.4) < 0.01, "Single-pane startup destroyed the saved height ratio");
            third.Close();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void InvalidLayoutSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MDVieW-settings-errors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "layout.json");
            var store = new LayoutSettingsStore(path);
            True(store.Load() == new LayoutSettings(), "Missing settings did not use defaults");
            foreach (var json in new[] { "{broken", "[]", "null", "{\"PaneLayout\":\"Unknown\"}", "{\"PaletteOrientation\":123}" })
            {
                File.WriteAllText(path, json);
                True(store.Load() == new LayoutSettings(), "Malformed settings were not handled: " + json);
            }
            File.WriteAllText(path, "{\"PaneLayout\":\"Stacked\",\"PaletteOrientation\":\"Vertical\",\"PanesSwapped\":true,\"ViewMode\":\"Unknown\",\"SideBySideRatio\":-1,\"StackedRatio\":2}");
            var sanitized = store.Load();
            True(sanitized.PaneLayout == PaneLayout.Stacked && sanitized.PaletteOrientation == Orientation.Vertical && sanitized.PanesSwapped, "Valid preferences were lost while repairing invalid fields");
            True(sanitized.ViewMode == "Split" && sanitized.SideBySideRatio == 0.5 && sanitized.StackedRatio == 0.5, "Invalid ratios or view mode were accepted");
            True(store.Save(new LayoutSettings { SideBySideRatio = double.NaN, StackedRatio = double.PositiveInfinity }), "Non-finite ratios caused a save failure");
            True(store.Load() == new LayoutSettings(), "Non-finite ratios were not sanitized");
            var original = File.ReadAllText(path);
            using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                True(!store.Save(new LayoutSettings { PanesSwapped = true }), "Saving over a locked file incorrectly reported success");
                Equal(original, File.ReadAllText(path));
            }
            True(Directory.GetFiles(directory, "*.tmp").Length == 0, "A failed save left a temporary settings file");
            var invalidTarget = Path.Combine(directory, "directory-as-settings-file");
            Directory.CreateDirectory(invalidTarget);
            var window = NewWindow(new LayoutSettingsStore(invalidTarget));
            window.SetPaletteOrientation(Orientation.Vertical);
            True(((StackPanel)window.FindName("PaletteItems")).Orientation == Orientation.Vertical, "A settings write failure prevented changing the layout");
            True(((TextBlock)window.FindName("StateLabel")).Text.Contains("配置を保存できませんでした"), "A failed settings save was hidden from the user");
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    }
}
