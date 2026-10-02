using System.Drawing;

namespace NVIDIAFanControlUtility;

/// <summary>
/// Central place for the dark theme colors so every control stays consistent.
/// </summary>
internal static class Theme
{
    // Surfaces
    public static readonly Color WindowBackground = Color.FromArgb(13, 17, 23);
    public static readonly Color PanelBackground = Color.FromArgb(22, 27, 34);
    public static readonly Color PanelBorder = Color.FromArgb(45, 55, 68);
    public static readonly Color SliderTrack = Color.FromArgb(35, 42, 52);

    // Text
    public static readonly Color TextPrimary = Color.FromArgb(230, 237, 243);
    public static readonly Color TextSecondary = Color.FromArgb(140, 152, 165);

    // Accents
    public static readonly Color NvGreen = Color.FromArgb(118, 185, 0);      // NVIDIA brand green
    public static readonly Color MemoryBlue = Color.FromArgb(56, 152, 255);
    public static readonly Color PowerOrange = Color.FromArgb(255, 159, 67);
    public static readonly Color WarningYellow = Color.FromArgb(255, 209, 102);
    public static readonly Color HotRed = Color.FromArgb(255, 99, 99);
    public static readonly Color ErrorRed = Color.FromArgb(255, 99, 99);

    /// <summary>Picks a color for a temperature: green when cool, yellow when warm, red when hot.</summary>
    public static Color ForTemperature(int? celsius)
    {
        if (celsius is not int t)
        {
            return TextSecondary;
        }

        return t < 65 ? NvGreen : t < 80 ? WarningYellow : HotRed;
    }
}
