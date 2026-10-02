using System.Text.Json;
using NVIDIAFanControlUtility.Models;

namespace NVIDIAFanControlUtility.Services;

/// <summary>
/// Persists the Fan Control options as JSON next to the log file
/// (<c>%TEMP%\nvidia_fan_control_utility_settings.json</c>), so the user configures them once
/// and they survive restarts.
/// </summary>
public static class SettingsStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Full path of the settings file; shown in the UI so the user can find it.</summary>
    public static string SettingsPath { get; } =
        Path.Combine(Path.GetTempPath(), "nvidia_fan_control_utility_settings.json");

    /// <summary>
    /// Loads the saved options, or returns a fresh default instance when no file
    /// exists or it is corrupt (a bad file must never crash the monitor).
    /// </summary>
    public static FanControlSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<FanControlSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions);

                if (settings is not null)
                {
                    // Tolerate partially-written or older files.
                    settings.ExePath ??= string.Empty;
                    settings.Profiles ??= new List<FanControlProfile>();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("SettingsStore.Load", ex);
        }

        return new FanControlSettings();
    }

    /// <summary>Saves the options; never throws (a disk failure must not crash the monitor).</summary>
    public static void Save(FanControlSettings settings)
    {
        try
        {
            lock (Gate)
            {
                string json = JsonSerializer.Serialize(settings, JsonOptions);

                // Write to a temp file first, then replace: a crash mid-write
                // leaves the previous good file intact instead of a torn one.
                string tempPath = SettingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("SettingsStore.Save", ex);
        }
    }
}
