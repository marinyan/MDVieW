using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace MDVieW;

/// <summary>Matches document blocks between the native editors without feeding programmatic scrolls back.</summary>
internal sealed class EditorScrollSynchronizer
{
    private readonly TextBox _source;
    private readonly RichTextBox _rich;
    private readonly MarkdownCodec _codec;
    private List<(double Source, double Rich)>? _anchors;
    private bool _suspended;
    private double? _expectedSource;
    private double? _expectedRich;
    public Func<bool> IsEnabled { get; set; } = () => false;
    public bool SourceIsLeader { get; private set; } = true;

    public EditorScrollSynchronizer(TextBox source, RichTextBox rich, MarkdownCodec codec)
    {
        _source = source; _rich = rich; _codec = codec;
        foreach (var (editor, fromSource) in new (TextBoxBase, bool)[] { (source, true), (rich, false) })
        {
            editor.PreviewMouseWheel += (_, _) => SelectLeader(fromSource);
            editor.PreviewMouseDown += (_, _) => SelectLeader(fromSource);
            editor.PreviewKeyDown += (_, _) => SelectLeader(fromSource);
            editor.GotKeyboardFocus += (_, _) => SelectLeader(fromSource);
            editor.SizeChanged += (_, _) => Invalidate();
            editor.TextChanged += (_, _) => Invalidate();
            editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => Scrolled(fromSource, e)));
        }
    }

    public void SelectLeader(bool source) => SourceIsLeader = source;
    public void Invalidate() => _anchors = null;

    private void Scrolled(bool fromSource, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        { Invalidate(); return; }
        var expected = fromSource ? _expectedSource : _expectedRich;
        if (fromSource) _expectedSource = null; else _expectedRich = null;
        if (expected.HasValue && Math.Abs(e.VerticalOffset - expected.Value) < 0.75) return;
        if (_suspended || e.VerticalChange == 0 || fromSource != SourceIsLeader || !IsEnabled()) return;
        Synchronize();
    }

    public void Synchronize()
    {
        if (_suspended || !IsEnabled()) return;
        var fromSource = SourceIsLeader;
        var offset = fromSource ? _source.VerticalOffset : _rich.VerticalOffset;
        var sourceMax = MaxOffset(_source);
        var richMax = MaxOffset(_rich);
        var fromMax = fromSource ? sourceMax : richMax;
        var toMax = fromSource ? richMax : sourceMax;
        if (toMax <= 0) return;
        if (fromMax <= 0) { SetOffset(!fromSource, 0); return; }
        var target = MapOffset(offset, fromMax, toMax, fromSource, GetAnchors(sourceMax, richMax));
        SetOffset(!fromSource, target);
    }

    private List<(double Source, double Rich)> GetAnchors(double sourceMax, double richMax)
    {
        if (_anchors != null) return _anchors;
        var result = new List<(double Source, double Rich)> { (0, 0) };
        var lineStarts = new List<int> { 0 };
        var text = _source.Text;
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
        foreach (var (block, sourceLine) in _codec.ScrollAnchors(_rich.Document))
        {
            if (sourceLine >= lineStarts.Count) continue;
            try
            {
                var index = lineStarts[sourceLine];
                var sourceRect = _source.GetRectFromCharacterIndex(index);
                var richRect = block.ContentStart.GetInsertionPosition(LogicalDirection.Forward)?.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
                if (richRect.IsEmpty) continue;
                var sourceY = sourceRect.IsEmpty
                    ? _source.GetLineIndexFromCharacterIndex(index) * Math.Max(1, (_source.ExtentHeight - _source.Padding.Top - _source.Padding.Bottom) / Math.Max(1, _source.LineCount))
                    : sourceRect.Top + _source.VerticalOffset - _source.Padding.Top;
                var richY = richRect.Top + _rich.VerticalOffset - _rich.Padding.Top;
                sourceY = Math.Clamp(sourceY, 0, sourceMax); richY = Math.Clamp(richY, 0, richMax);
                if (double.IsFinite(sourceY) && double.IsFinite(richY) && sourceY > result[^1].Source + 0.5 && richY > result[^1].Rich + 0.5
                    && sourceY < sourceMax && richY < richMax) result.Add((sourceY, richY));
            }
            catch (InvalidOperationException) { } // Unformatted blocks fall back to the surrounding anchors.
        }
        result.Add((sourceMax, richMax));
        return _anchors = result;
    }

    private static double MapOffset(double offset, double fromMax, double toMax, bool fromSource, List<(double Source, double Rich)> anchors)
    {
        if (offset <= 0) return 0;
        if (offset >= fromMax - 0.5) return toMax;
        for (var i = 1; i < anchors.Count; i++)
        {
            var start = fromSource ? anchors[i - 1].Source : anchors[i - 1].Rich;
            var end = fromSource ? anchors[i].Source : anchors[i].Rich;
            if (offset > end) continue;
            var targetStart = fromSource ? anchors[i - 1].Rich : anchors[i - 1].Source;
            var targetEnd = fromSource ? anchors[i].Rich : anchors[i].Source;
            return Math.Clamp(targetStart + (offset - start) / Math.Max(0.001, end - start) * (targetEnd - targetStart), 0, toMax);
        }
        return offset / fromMax * toMax;
    }

    public void PreservePositions(Action layoutChange)
    {
        var sourceProgress = _source.VerticalOffset / Math.Max(1, MaxOffset(_source));
        var richProgress = _rich.VerticalOffset / Math.Max(1, MaxOffset(_rich));
        _suspended = true;
        try
        {
            layoutChange();
            _source.UpdateLayout(); _rich.UpdateLayout();
            Invalidate();
            SetOffset(true, sourceProgress * MaxOffset(_source));
            SetOffset(false, richProgress * MaxOffset(_rich));
            _source.UpdateLayout(); _rich.UpdateLayout();
        }
        finally { _suspended = false; }
        Synchronize();
    }

    private void SetOffset(bool source, double value)
    {
        var editor = source ? (TextBoxBase)_source : _rich;
        value = Math.Clamp(value, 0, MaxOffset(editor));
        if (Math.Abs(editor.VerticalOffset - value) < 0.5) return;
        if (source) _expectedSource = value; else _expectedRich = value;
        editor.ScrollToVerticalOffset(value);
    }

    private static double MaxOffset(TextBoxBase editor) => Math.Max(0, editor.ExtentHeight - editor.ViewportHeight);
}
