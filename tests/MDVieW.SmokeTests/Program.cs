using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            Console.WriteLine($"PASS: {_passed} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(string label, Action action) { action(); _passed++; Console.WriteLine("PASS " + label); }
    private static void Equal(string expected, string actual) { if (expected != actual) throw new Exception($"Expected:\n{expected}\nActual:\n{actual}"); }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }

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
        var window = new MainWindow();
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
                var window = new MainWindow();
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
}
