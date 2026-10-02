namespace NVIDIAFanControlUtility.Controls;

/// <summary>
/// A dark card with centered error text, shown when nvidia-smi cannot be read.
/// </summary>
public sealed class ErrorCard : Panel
{
    private readonly Label[] _labels;

    private bool _constructed;

    public ErrorCard(string message)
    {
        DoubleBuffered = true;
        BackColor = Theme.PanelBackground;
        Margin = new Padding(0, 0, 0, 14);

        // Create the labels BEFORE touching any property that can raise
        // SizeChanged (MinimumSize does) - handlers may run mid-construction.
        _labels = new[]
        {
            MakeLabel("No GPU data available", Theme.ErrorRed, 13, bold: true),
            MakeLabel(message, Theme.WarningYellow, 9.5f, bold: false),
            MakeLabel(
                "This app reads nvidia-smi output via PowerShell. Make sure the NVIDIA driver " +
                "is installed and that nvidia-smi works in a normal command prompt, then press Refresh.",
                Theme.TextSecondary, 9, bold: false),
        };

        foreach (var label in _labels)
        {
            Controls.Add(label);
        }

        // 150 matches GpuPanel's floor / MainForm's ReFitCards floor, so the
        // error card never forces a wider window than the main card would;
        // the labels re-center and wrap as needed.
        MinimumSize = new Size(150, 0);
        _constructed = true;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        CenterLabels();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var border = new SolidBrush(Theme.PanelBorder);
        e.Graphics.FillRectangle(border, 0, 0, ClientSize.Width, 1);
        e.Graphics.FillRectangle(border, 0, ClientSize.Height - 1, ClientSize.Width, 1);
        e.Graphics.FillRectangle(border, 0, 0, 1, ClientSize.Height);
        e.Graphics.FillRectangle(border, ClientSize.Width - 1, 0, 1, ClientSize.Height);
    }

    private void CenterLabels()
    {
        if (!_constructed || _labels is null || ClientSize.Width <= 0)
        {
            return;
        }

        int labelWidth = Math.Min(460, ClientSize.Width - 48);
        if (labelWidth <= 0)
        {
            return;
        }

        int totalHeight = 0;
        foreach (var label in _labels)
        {
            label.Width = labelWidth;
            label.Height = label.PreferredHeight;
            totalHeight += label.Height + label.Margin.Bottom;
        }

        int y = Math.Max(12, (ClientSize.Height - totalHeight) / 2);
        int x = (ClientSize.Width - labelWidth) / 2;

        foreach (var label in _labels)
        {
            label.Location = new Point(x, y);
            y += label.Height + label.Margin.Bottom;
        }
    }

    private static Label MakeLabel(string text, Color color, float size, bool bold)
    {
        return new Label
        {
            Text = text,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 12),
        };
    }
}
