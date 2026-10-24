using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BleTerminal;

public enum LineKind { Rx, Tx, Info, Error }

/// <summary>Line-oriented, colour-coded terminal on top of a RichTextBox.</summary>
public sealed class TerminalView
{
    const int MaxBlocks = 6000;
    const int TrimTo = 5000;

    readonly RichTextBox _rtb;
    Paragraph? _open;
    LineKind _openKind;

    public bool Timestamps { get; set; }
    public bool AutoScroll { get; set; } = true;

    static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static readonly Brush RxBrush = Make(0xD4, 0xD4, 0xD4);
    static readonly Brush TxBrush = Make(0x6A, 0xD1, 0x7A);
    static readonly Brush InfoBrush = Make(0x6C, 0xB6, 0xFF);
    static readonly Brush ErrBrush = Make(0xF2, 0x6D, 0x6D);
    static readonly Brush TsBrush = Make(0x80, 0x80, 0x80);

    public TerminalView(RichTextBox rtb)
    {
        _rtb = rtb;
        _rtb.Document.Blocks.Clear();
        _rtb.Document.PagePadding = new Thickness(0);
    }

    static Brush BrushFor(LineKind k) => k switch
    {
        LineKind.Tx => TxBrush,
        LineKind.Info => InfoBrush,
        LineKind.Error => ErrBrush,
        _ => RxBrush
    };

    /// <summary>Append text; '\n' ends a line. Text of a different kind always starts a new line.</summary>
    public void Append(string text, LineKind kind)
    {
        if (text.Length == 0) return;
        if (_open != null && _openKind != kind) CloseLine();

        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            string piece = nl < 0 ? text.Substring(start) : text.Substring(start, nl - start);
            if (piece.Length > 0) AddPiece(piece, kind);
            if (nl < 0) break;
            if (_open != null) CloseLine();
            else _rtb.Document.Blocks.Add(NewParagraph());   // empty line
            start = nl + 1;
        }

        Trim();
        if (AutoScroll) _rtb.ScrollToEnd();
    }

    public void CloseLine() { _open = null; }

    public void Clear()
    {
        _rtb.Document.Blocks.Clear();
        _open = null;
    }

    public string GetText() =>
        new TextRange(_rtb.Document.ContentStart, _rtb.Document.ContentEnd).Text;

    static Paragraph NewParagraph() => new Paragraph { Margin = new Thickness(0) };

    void AddPiece(string piece, LineKind kind)
    {
        if (_open == null)
        {
            _open = NewParagraph();
            _openKind = kind;
            if (Timestamps)
                _open.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss.fff ")) { Foreground = TsBrush });
            _rtb.Document.Blocks.Add(_open);
        }
        _open.Inlines.Add(new Run(piece) { Foreground = BrushFor(kind) });
    }

    void Trim()
    {
        var blocks = _rtb.Document.Blocks;
        if (blocks.Count <= MaxBlocks) return;
        while (blocks.Count > TrimTo && blocks.FirstBlock != null && blocks.FirstBlock != _open)
            blocks.Remove(blocks.FirstBlock);
    }
}
