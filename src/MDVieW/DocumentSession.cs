using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MDVieW;

internal enum SyncDirection { None, Source, Rich }

/// <summary>Owns both editors so selection, scrolling and native undo survive tab switches.</summary>
internal sealed class DocumentSession
{
    public DocumentFile File { get; } = new();
    public MarkdownCodec Codec { get; } = new();
    public TextBox Source { get; } = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Foreground = (Brush)new BrushConverter().ConvertFromString("#4A5063")!,
        FontFamily = new FontFamily("Cascadia Code, Consolas, Yu Gothic UI"), FontSize = 14,
        Padding = new Thickness(24, 22, 24, 22), SelectionBrush = (Brush)new BrushConverter().ConvertFromString("#C9C4F7")!,
        UndoLimit = 500, IsUndoEnabled = true
    };
    public RichTextBox Rich { get; } = new()
    {
        BorderThickness = new Thickness(0), Padding = new Thickness(30, 20, 30, 20), Background = Brushes.White,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        AcceptsTab = true, IsDocumentEnabled = true,
        SelectionBrush = (Brush)new BrushConverter().ConvertFromString("#C9C4F7")!, UndoLimit = 500
    };
    public TabItem Tab { get; } = new();
    public TextBlock Header { get; } = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 185, TextTrimming = TextTrimming.CharacterEllipsis };
    public string UntitledName { get; set; } = "無題.md";
    public string Name => File.FilePath == null ? UntitledName : File.DisplayName;
    public SyncDirection Pending { get; set; }
    public bool RichActive { get; set; }
    public bool IsDirty => Pending == SyncDirection.Rich || Source.Text != File.SavedText;

    public DocumentSession()
    {
        AutomationProperties.SetName(Source, "Markdown テキストエディタ");
        AutomationProperties.SetName(Rich, "WYSIWYG エディタ");
        SpellCheck.SetIsEnabled(Source, false);
        SpellCheck.SetIsEnabled(Rich, false);
    }
}
