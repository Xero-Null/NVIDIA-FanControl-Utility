using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NVIDIAFanControlUtility.Controls;

/// <summary>
/// A dark, owner-drawn row of tabs (Statistics / Options). Each tab is a flat
/// button sized from its label text, so nothing overlaps at any DPI. The
/// selected tab gets a brighter fill, an NVIDIA-green underline, and bold
/// text; hovering an unselected tab brightens it. Raises <see cref="SelectionChanged"/>
/// when a different tab is clicked.
///
/// The strip compresses as the window narrows: normal → compact (smaller
/// font/padding) → collapsed (a single button showing the active tab's short
/// label; each click switches to the other tab), so both tabs stay reachable
/// even in a ~100px-wide window.
/// </summary>
public sealed class TabStrip : Panel
{
    private enum TabMode
    {
        Normal,
        Compact,
        Collapsed,
    }

    private sealed class TabInfo
    {
        public required string Text { get; init; }

        /// <summary>Short label used when the strip is collapsed into a
        /// single toggle button (defaults to the full text).</summary>
        public string ShortText { get; init; } = string.Empty;

        public Rectangle Bounds { get; set; }
    }

    /// <summary>Raised after <see cref="SelectedIndex"/> changes (from a click or a setter).</summary>
    public event EventHandler? SelectionChanged;

    private readonly List<TabInfo> _tabs = new();

    private int _selectedIndex;
    private int _hoverIndex = -1;

    // Sizing. Every horizontal extent is measured from the actual label text
    // (see MeasureTabWidth), so these constants are just gutters/padding.
    private const int TabPaddingH = 18;     // left+right breathing room inside a tab (normal)
    private const int CompactPaddingH = 10; // ...in compact mode
    private const int TabSpacing = 6;       // gap between adjacent tabs
    private const int LeftInset = 16;       // left gutter of the whole strip
    private const int UnderlineHeight = 3;  // green bar under the selected tab

    private TabMode _mode = TabMode.Normal;
    private Font? _compactFont; // owned by this control; disposed on mode exit / Dispose

    public TabStrip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        DoubleBuffered = true;
        BackColor = Theme.PanelBackground;
        ForeColor = Theme.TextPrimary;
        Font = new Font("Segoe UI", 9f);
        Height = 40;
        Padding = new Padding(LeftInset, 0, LeftInset, 0);
        Cursor = Cursors.Default;
    }

    public int TabCount => _tabs.Count;

    /// <summary>Zero-based index of the active tab; clamped to the valid range.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_tabs.Count == 0)
            {
                return;
            }

            int clamped = Math.Clamp(value, 0, _tabs.Count - 1);
            if (clamped == _selectedIndex)
            {
                return;
            }

            _selectedIndex = clamped;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Appends a tab. The first tab added becomes the selected one.
    /// <paramref name="shortText"/> is the compact label shown when the strip
    /// collapses to a single toggle button in a very narrow window.</summary>
    public void AddTab(string text, string? shortText = null)
    {
        _tabs.Add(new TabInfo { Text = text, ShortText = string.IsNullOrEmpty(shortText) ? text : shortText });
        if (_selectedIndex < 0)
        {
            _selectedIndex = 0;
        }

        LayoutTabs();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutTabs();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutTabs();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Font scaling (e.g. DPI change) can invalidate measured widths even
        // when the client size stays the same.
        LayoutTabs();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _compactFont?.Dispose();
            _compactFont = null;
        }

        base.Dispose(disposing);
    }

    // Measures a label's rendered width (GDI+, valid before a handle exists)
    // plus padding, floored at 64 so a tab is always comfortably clickable.
    private int MeasureTabWidth(string text, Font font, int paddingH)
    {
        var size = TextRenderer.MeasureText(
            text,
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return Math.Max(64, size.Width + paddingH * 2);
    }

    /// <summary>The font used by the current mode (compact/collapsed use a
    /// smaller, cached font).</summary>
    private Font CurrentFont
    {
        get
        {
            if (_mode == TabMode.Normal)
            {
                return Font;
            }

            return EnsureCompactFont();
        }
    }

    private Font EnsureCompactFont()
    {
        _compactFont ??= new Font(Font.FontFamily, 8f);
        return _compactFont;
    }

    /// <summary>
    /// Chooses the narrowest mode that fits the current width and lays the
    /// tab(s) out accordingly:
    ///   Normal    — full font, 18px padding;
    ///   Compact   — 8pt font, 10px padding;
    ///   Collapsed — one full-width button showing the ACTIVE tab's short
    ///               label; each click cycles to the next tab (last resort,
    ///               ~100px windows).
    /// </summary>
    private void LayoutTabs()
    {
        if (_tabs.Count == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        int available = ClientSize.Width - Padding.Horizontal;

        // Try normal, then compact. Fonts are passed explicitly so the
        // measurement matches what the chosen mode will actually paint.
        TabMode chosen =
            TotalWidths(Font, TabPaddingH) <= available ? TabMode.Normal :
            TotalWidths(EnsureCompactFont(), CompactPaddingH) <= available ? TabMode.Compact :
            TabMode.Collapsed;

        if (chosen != _mode)
        {
            _mode = chosen;
            if (chosen == TabMode.Normal)
            {
                _compactFont?.Dispose();
                _compactFont = null;
            }
        }

        if (_mode == TabMode.Collapsed)
        {
            // One combined button spanning the whole strip.
            _tabs[0].Bounds = new Rectangle(Padding.Left, 0, Math.Max(1, available), ClientSize.Height);
            return;
        }

        Font font = CurrentFont;
        int paddingH = _mode == TabMode.Normal ? TabPaddingH : CompactPaddingH;

        int x = Padding.Left;
        foreach (var tab in _tabs)
        {
            tab.Bounds = new Rectangle(x, 0, MeasureTabWidth(tab.Text, font, paddingH), ClientSize.Height);
            x += tab.Bounds.Width + TabSpacing;
        }
    }

    private int TotalWidths(Font font, int paddingH)
    {
        int total = 0;
        for (int i = 0; i < _tabs.Count; i++)
        {
            total += MeasureTabWidth(_tabs[i].Text, font, paddingH);
            if (i < _tabs.Count - 1)
            {
                total += TabSpacing;
            }
        }

        return total;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (_mode == TabMode.Collapsed)
        {
            // The single button toggles between the two tabs on every click.
            SelectedIndex = (_selectedIndex + 1) % _tabs.Count;
            return;
        }

        int index = IndexAt(e.Location);
        if (index >= 0)
        {
            SelectedIndex = index; // no-op if already selected
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = IndexAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    private int IndexAt(Point p)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Bounds.Contains(p))
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Bottom hairline that separates the strip from the page below.
        using (var separator = new SolidBrush(Theme.PanelBorder))
        {
            g.FillRectangle(separator, 0, ClientSize.Height - 1, ClientSize.Width, 1);
        }

        // Keep bounds fresh in case the last layout pass ran at size 0.
        LayoutTabs();

        int count = _mode == TabMode.Collapsed ? 1 : _tabs.Count;
        for (int i = 0; i < count; i++)
        {
            // In collapsed mode the combined button lives in _tabs[0].
            TabInfo tab = _tabs[i];
            bool selected = i == _selectedIndex || _mode == TabMode.Collapsed;
            bool hovered = i == _hoverIndex && !selected;

            // Subtle raised fill for the selected tab; a fainter one on hover.
            if (selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(34, 41, 51));
                g.FillRectangle(fill, tab.Bounds.Left, 0, tab.Bounds.Width, tab.Bounds.Height);
            }
            else if (hovered)
            {
                using var fill = new SolidBrush(Color.FromArgb(28, 34, 43));
                g.FillRectangle(fill, tab.Bounds.Left, 0, tab.Bounds.Width, tab.Bounds.Height);
            }

            // NVIDIA-green underline marks the active tab.
            if (selected)
            {
                using var underline = new SolidBrush(Theme.NvGreen);
                g.FillRectangle(underline, tab.Bounds.Left, ClientSize.Height - UnderlineHeight, tab.Bounds.Width, UnderlineHeight);
            }

            Color textColor = selected || hovered
                ? Theme.TextPrimary
                : Theme.TextSecondary;

            // Show which tab is active in collapsed mode by painting only
            // that tab's short label ("Stats" vs "Opts") on the one button.
            string text = _mode == TabMode.Collapsed
                ? _tabs[_selectedIndex].ShortText
                : tab.Text;

            if (selected)
            {
                // Bold font for the active tab, created per paint and disposed
                // here. The strip's own Font (and _compactFont) are shared and
                // must never be disposed.
                using var bold = new Font(CurrentFont.FontFamily, CurrentFont.Size, FontStyle.Bold);
                TextRenderer.DrawText(
                    g,
                    text,
                    bold,
                    tab.Bounds,
                    textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            else
            {
                TextRenderer.DrawText(
                    g,
                    text,
                    CurrentFont,
                    tab.Bounds,
                    textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}
