using System.Drawing.Drawing2D;

namespace NVIDIAFanControlUtility.Controls;

/// <summary>
/// A read-only slider that displays a value between 0 and 100 as a filled track with a thumb.
/// The user cannot drag it - it is a pure display control for usage percentages.
/// </summary>
public sealed class PercentSlider : Control
{
    private const int TrackHeight = 8;
    private const int TrackRadius = 4;
    private const int ThumbRadius = 7;

    private int _value;
    private Color _accentColor = Theme.NvGreen;

    public PercentSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        DoubleBuffered = true;
        BackColor = Theme.PanelBackground; // sliders always sit on a GPU card
        MinimumSize = new Size(0, ThumbRadius * 2);
        TabStop = false;
    }

    /// <summary>Value shown on the slider, clamped to 0-100.</summary>
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
        }
    }

    /// <summary>Color of the filled portion and the thumb ring.</summary>
    public Color AccentColor
    {
        get => _accentColor;
        set
        {
            if (value == _accentColor)
            {
                return;
            }

            _accentColor = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int width = ClientSize.Width;
        int centerY = ClientSize.Height / 2;
        int trackX = ThumbRadius;
        int trackWidth = Math.Max(1, width - ThumbRadius * 2);

        // Track (full 0-100).
        float trackY = centerY - TrackHeight / 2f;
        using (var trackPath = RoundedRect(trackX, trackY, trackWidth, TrackHeight, TrackRadius))
        using (var trackBrush = new SolidBrush(Theme.SliderTrack))
        {
            g.FillPath(trackBrush, trackPath);
        }

        // Filled portion up to the current value.
        if (_value > 0)
        {
            float fillWidth = trackWidth * _value / 100f;
            float fillRadius = Math.Min(TrackRadius, Math.Min(fillWidth / 2f, TrackHeight / 2f));
            using var fillPath = RoundedRect(trackX, trackY, fillWidth, TrackHeight, fillRadius);
            using var fillBrush = new SolidBrush(_accentColor);
            g.FillPath(fillBrush, fillPath);
        }

        // Thumb at the current value.
        float thumbX = trackX + trackWidth * _value / 100f;
        using (var ringBrush = new SolidBrush(_accentColor))
        {
            g.FillEllipse(ringBrush, thumbX - ThumbRadius, centerY - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);
        }

        using var innerBrush = new SolidBrush(Color.White);
        g.FillEllipse(innerBrush, thumbX - (ThumbRadius - 2.5f), centerY - (ThumbRadius - 2.5f), (ThumbRadius - 2.5f) * 2, (ThumbRadius - 2.5f) * 2);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = 2 * Math.Max(0f, Math.Min(r, Math.Min(w / 2f, h / 2f)));

        if (d <= 0f)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            path.CloseFigure();
            return path;
        }

        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
