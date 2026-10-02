using NVIDIAFanControlUtility.Models;

namespace NVIDIAFanControlUtility.Controls;

/// <summary>
/// One self-contained card per GPU: name, temperature, usage sliders, and a
/// details grid (power, fan, PCIe, clocks). The card sizes itself to its
/// content and switches between a full-width layout and a compact layout
/// (stacked values, tighter padding) when the window is resized narrow,
/// so it stays readable down to ~100px of content width.
/// </summary>
public sealed class GpuPanel : Panel
{
    private const int SliderHeight = 14;

    // Full (roomy) layout metrics.
    private const int FullValueColumn = 150;
    private const int FullDetailLabelWidth = 110;
    private const int DetailRowHeight = 18;
    private const int GapBeforeSlider = 8;
    private const int GapAfterSlider = 14;
    private const int SectionGap = 16;

    // Compact (narrow) layout metrics.
    private const int CompactBlockGap = 14;

    // Below this CONTENT width (client width minus left/right padding) the
    // card switches to the compact layout. 340 was chosen so the full layout
    // keeps its side-by-side value columns until they would start colliding.
    private const int CompactThreshold = 340;

    private readonly Label _titleLabel;
    private readonly Label _tempLabel;
    private readonly Label _usageCaption;
    private readonly Label _usageValue;
    private readonly PercentSlider _usageSlider;
    private readonly Label _memoryCaption;
    private readonly Label _memoryValue;
    private readonly PercentSlider _memorySlider;
    private readonly Label _powerCaption;
    private readonly Label _powerValue;
    private readonly Label _fanCaption;
    private readonly Label _fanValue;
    private readonly Label _pcieCaption;
    private readonly Label _pcieValue;
    private readonly Label _clockCaption;
    private readonly Label _clockValue;

    // Guards against layout firing before the labels exist. Setting
    // MinimumSize in the ctor is wider than a Panel's default, which triggers
    // OnSizeChanged -> LayoutContent while _titleLabel & co are still null.
    // Layout only runs once this is true.
    private bool _layoutReady;

    /// <summary>True while the compact (narrow) layout is active. Tracked so a
    /// padding change can be detected and applied exactly once per switch.</summary>
    private bool _compact;

    public GpuPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.PanelBackground;
        Padding = new Padding(18, 14, 18, 16);
        // 150 matches MainForm's ReFitCards floor, so a card is never wider
        // than the (narrowest) content area can show — no horizontal scroll
        // at the 170px minimum window.
        MinimumSize = new Size(150, 0);

        _titleLabel = MakeLabel("", Theme.TextPrimary, 11, bold: true);
        _tempLabel = MakeLabel("Temperature  --", Theme.TextPrimary, 10, bold: true);

        _usageCaption = MakeLabel("GPU usage", Theme.TextSecondary, 9, bold: false);
        _usageValue = MakeLabel("--", Theme.TextPrimary, 9, bold: false, ContentAlignment.MiddleRight);
        _memoryCaption = MakeLabel("Memory", Theme.TextSecondary, 9, bold: false);
        _memoryValue = MakeLabel("--", Theme.TextPrimary, 9, bold: false, ContentAlignment.MiddleRight);

        _powerCaption = MakeLabel("Power draw", Theme.TextSecondary, 9, bold: false);
        _fanCaption = MakeLabel("Fan speed", Theme.TextSecondary, 9, bold: false);
        _pcieCaption = MakeLabel("PCIe link", Theme.TextSecondary, 9, bold: false);
        _clockCaption = MakeLabel("Graphics clock", Theme.TextSecondary, 9, bold: false);
        _powerValue = MakeDetailValue();
        _fanValue = MakeDetailValue();
        _pcieValue = MakeDetailValue();
        _clockValue = MakeDetailValue();

        // Single-line ellipsis so a narrow card truncates gracefully
        // ("GeForce RTX 409…") instead of hard-clipping mid-glyph.
        foreach (Label label in new Label[]
        {
            _titleLabel, _tempLabel,
            _usageCaption, _usageValue, _memoryCaption, _memoryValue,
            _powerCaption, _powerValue, _fanCaption, _fanValue,
            _pcieCaption, _pcieValue, _clockCaption, _clockValue,
        })
        {
            label.AutoEllipsis = true;
        }

        _usageSlider = new PercentSlider { AccentColor = Theme.NvGreen };
        _memorySlider = new PercentSlider { AccentColor = Theme.MemoryBlue };

        foreach (Control c in new Control[]
        {
            _titleLabel, _tempLabel,
            _usageCaption, _usageValue, _usageSlider,
            _memoryCaption, _memoryValue, _memorySlider,
            _powerCaption, _powerValue, _fanCaption, _fanValue,
            _pcieCaption, _pcieValue, _clockCaption, _clockValue,
        })
        {
            Controls.Add(c);
        }

        _layoutReady = true;
        LayoutContent();
    }

    /// <summary>Refreshes every value shown on the card from a fresh snapshot.</summary>
    public void UpdateFromSnapshot(GpuSnapshot gpu)
    {
        _titleLabel.Text = string.IsNullOrWhiteSpace(gpu.Name) ? $"GPU {gpu.Index}" : gpu.Name;

        _tempLabel.Text = $"Temperature  {FormatValue(gpu.TemperatureC, v => $"{v} °C")}";
        _tempLabel.ForeColor = Theme.ForTemperature(gpu.TemperatureC);

        _usageSlider.Value = gpu.Utilization ?? 0;
        _usageValue.Text = FormatValue(gpu.Utilization, v => $"{v}%");

        _memorySlider.Value = gpu.MemoryPercent ?? 0;
        _memoryValue.Text = gpu.MemoryPercent is int pct
            ? $"{pct}%   ({FormatGiB(gpu.MemoryUsedMiB)} / {FormatGiB(gpu.MemoryTotalMiB)} GiB)"
            : "--";

        _powerValue.Text = FormatValue(gpu.PowerDrawW, v => $"{v:0.0} W");
        _fanValue.Text = FormatValue(gpu.FanSpeed, v => $"{v}%");
        _pcieValue.Text = FormatPcie(gpu);
        _clockValue.Text = FormatClock(gpu);

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // 1px border so each card reads as a distinct container.
        using var border = new SolidBrush(Theme.PanelBorder);
        e.Graphics.FillRectangle(border, 0, 0, ClientSize.Width, 1);
        e.Graphics.FillRectangle(border, 0, ClientSize.Height - 1, ClientSize.Width, 1);
        e.Graphics.FillRectangle(border, 0, 0, 1, ClientSize.Height);
        e.Graphics.FillRectangle(border, ClientSize.Width - 1, 0, 1, ClientSize.Height);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutContent();
    }

    private void LayoutContent()
    {
        if (!_layoutReady)
        {
            return;
        }

        int clientWidth = ClientSize.Width;
        if (clientWidth <= 0)
        {
            return;
        }

        // Pick the layout mode from the CURRENT width, then switch padding to
        // match. Re-reading the client size afterwards matters: assigning
        // Padding changes the client size, which also re-enters this method —
        // on that pass the mode already matches, so no second Padding write.
        bool compact = clientWidth < CompactThreshold;
        if (compact != _compact)
        {
            _compact = compact;
            Padding = compact
                ? new Padding(14, 10, 14, 12)
                : new Padding(18, 14, 18, 16);
        }

        int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;
        if (contentWidth <= 0)
        {
            return;
        }

        int x = Padding.Left;
        int y = Padding.Top;

        // Title + temperature (always full width, ellipsized when cramped).
        PlaceLabel(_titleLabel, x, y, contentWidth);
        y += _titleLabel.Height + 6;
        PlaceLabel(_tempLabel, x, y, contentWidth);
        y += _tempLabel.Height + SectionGap;

        if (_compact)
        {
            // ── Compact: value on its own line, right-aligned above the slider.
            // Keeps every number fully visible (never a 40px-wide column next
            // to a caption) at the cost of card height, which the window's
            // vertical scroll absorbs.
            PlaceLabel(_usageCaption, x, y, contentWidth);
            y += _usageCaption.Height + 2;
            PlaceLabel(_usageValue, x, y, contentWidth);
            y += _usageValue.Height + 4;
            _usageSlider.SetBounds(x, y, contentWidth, SliderHeight);
            y += SliderHeight + CompactBlockGap;

            PlaceLabel(_memoryCaption, x, y, contentWidth);
            y += _memoryCaption.Height + 2;
            PlaceLabel(_memoryValue, x, y, contentWidth);
            y += _memoryValue.Height + 4;
            _memorySlider.SetBounds(x, y, contentWidth, SliderHeight);
            y += SliderHeight + SectionGap;

            // Details: label line + right-aligned value line, stacked.
            y = PlaceDetailStack(x, y, _powerCaption, _powerValue, contentWidth);
            y = PlaceDetailStack(x, y, _fanCaption, _fanValue, contentWidth);
            y = PlaceDetailStack(x, y, _pcieCaption, _pcieValue, contentWidth);
            y = PlaceDetailStack(x, y, _clockCaption, _clockValue, contentWidth);
        }
        else
        {
            // ── Full: caption left, value in a right column, slider below.
            PlaceLabel(_usageCaption, x, y, contentWidth - FullValueColumn);
            PlaceLabel(_usageValue, x + contentWidth - FullValueColumn, y, FullValueColumn);
            int captionHeight = Math.Max(_usageCaption.Height, _usageValue.Height);
            _usageSlider.SetBounds(x, y + captionHeight + GapBeforeSlider, contentWidth, SliderHeight);
            y += captionHeight + GapBeforeSlider + SliderHeight + GapAfterSlider;

            PlaceLabel(_memoryCaption, x, y, contentWidth - FullValueColumn);
            PlaceLabel(_memoryValue, x + contentWidth - FullValueColumn, y, FullValueColumn);
            int memoryCaptionHeight = Math.Max(_memoryCaption.Height, _memoryValue.Height);
            _memorySlider.SetBounds(x, y + memoryCaptionHeight + GapBeforeSlider, contentWidth, SliderHeight);
            y += memoryCaptionHeight + GapBeforeSlider + SliderHeight + SectionGap;

            // Details grid: four rows, label left / value right-aligned.
            PlaceLabel(_powerCaption, x, y, FullDetailLabelWidth);
            PlaceLabel(_powerValue, x + FullDetailLabelWidth, y, contentWidth - FullDetailLabelWidth);
            y += DetailRowHeight;
            PlaceLabel(_fanCaption, x, y, FullDetailLabelWidth);
            PlaceLabel(_fanValue, x + FullDetailLabelWidth, y, contentWidth - FullDetailLabelWidth);
            y += DetailRowHeight;
            PlaceLabel(_pcieCaption, x, y, FullDetailLabelWidth);
            PlaceLabel(_pcieValue, x + FullDetailLabelWidth, y, contentWidth - FullDetailLabelWidth);
            y += DetailRowHeight;
            PlaceLabel(_clockCaption, x, y, FullDetailLabelWidth);
            PlaceLabel(_clockValue, x + FullDetailLabelWidth, y, contentWidth - FullDetailLabelWidth);
            y += DetailRowHeight;
        }

        int desiredHeight = y + Padding.Bottom;
        if (desiredHeight > 0 && desiredHeight != Height)
        {
            // Sizing ourselves triggers OnSizeChanged -> LayoutContent again;
            // the height check above makes that second pass a no-op.
            Size = new Size(Width, desiredHeight);
        }
    }

    /// <summary>Places a compact details pair (label line, value line) and
    /// returns the y just below it.</summary>
    private static int PlaceDetailStack(int x, int y, Label caption, Label value, int contentWidth)
    {
        PlaceLabel(caption, x, y, contentWidth);
        y += caption.Height + 1;
        PlaceLabel(value, x, y, contentWidth);
        y += value.Height + 7;
        return y;
    }

    private static void PlaceLabel(Label label, int x, int y, int width)
    {
        label.SetBounds(x, y, Math.Max(1, width), label.PreferredHeight);
    }

    private static Label MakeLabel(string text, Color color, float size, bool bold, ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        return new Label
        {
            Text = text,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = align,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        };
    }

    private Label MakeDetailValue()
    {
        return MakeLabel("--", Theme.TextPrimary, 9, bold: false, ContentAlignment.MiddleRight);
    }

    private static string FormatValue<T>(T? value, Func<T, string> format)
    {
        return value is T v ? format(v) : "--";
    }

    private static string FormatGiB(int? miB)
    {
        return miB is int m ? (m / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "--";
    }

    private static string FormatPcie(GpuSnapshot gpu)
    {
        if (gpu.PcieGen is int gen && gpu.PcieWidth is int width)
        {
            return $"Gen {gen}  ·  x{width}";
        }

        if (gpu.PcieGen is int g)
        {
            return $"Gen {g}";
        }

        return "--";
    }

    private static string FormatClock(GpuSnapshot gpu)
    {
        if (gpu.ClockMhz is int clock && gpu.MaxClockMhz is int max)
        {
            return $"{clock} / {max} MHz";
        }

        if (gpu.ClockMhz is int c)
        {
            return $"{c} MHz";
        }

        return "--";
    }
}
