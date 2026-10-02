namespace NVIDIAFanControlUtility.Models;

/// <summary>
/// One rung of the Fan Control profile ladder: when the hottest GPU's temperature
/// reaches <see cref="TemperatureC"/> or above, Fan Control should be switched to the
/// config file <see cref="ConfigName"/> (with ".json" appended if it is missing).
/// </summary>
public sealed class FanControlProfile
{
    /// <summary>GPU temperature (°C) at which this profile becomes eligible.</summary>
    public int TemperatureC { get; set; }

    /// <summary>Fan Control config name, e.g. "Gaming" or "Gaming.json".</summary>
    public string ConfigName { get; set; } = string.Empty;

    /// <summary>The file name passed to Fan Control's -c argument (always ends in .json).</summary>
    public string ConfigFileName
    {
        get
        {
            string name = ConfigName.Trim();
            return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + ".json";
        }
    }
}

/// <summary>
/// All user-configurable Fan Control options. Persisted as JSON by <see cref="Services.SettingsStore"/>
/// so the user only has to enter them once.
/// </summary>
public sealed class FanControlSettings
{
    /// <summary>Master switch: when off, NVIDIA Fan Control Utility never touches Fan Control.</summary>
    public bool Enabled { get; set; }

    /// <summary>Full path to FanControl.exe.</summary>
    public string ExePath { get; set; } = @"C:\FanControlFolder\FanControl.exe";

    /// <summary>
    /// Profile ladder. The consumer sorts this ascending by <see cref="FanControlProfile.TemperatureC"/>;
    /// the active profile is the hottest one whose trigger is at or below the current max GPU temperature,
    /// or the first (coolest) one when the temperature is below every trigger.
    /// </summary>
    public List<FanControlProfile> Profiles { get; set; } = new();
}
