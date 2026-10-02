using System.Diagnostics;
using NVIDIAFanControlUtility.Models;

namespace NVIDIAFanControlUtility.Services;

/// <summary>
/// The brain of the Fan Control integration. The main form calls <see cref="Update"/>
/// with the hottest GPU's temperature on every refresh; the service walks the profile
/// ladder (with hysteresis on the way down) and starts Fan Control only when the
/// selected profile actually changes.
///
/// Mechanism (per the Fan Control wiki): <c>FanControl.exe -c "Gaming.json"</c>
/// starts a new instance with that profile when none is running, and — when an
/// instance IS running — the new process just tells the running one to switch
/// configs and exits. The working directory must be the folder that contains
/// the exe and the config files, so that is set explicitly here.
/// </summary>
public sealed class FanControlService
{
    /// <summary>
    /// How far BELOW the trigger of the profile being LEFT the GPU must cool
    /// before stepping down to the next cooler profile. Without this, a
    /// temperature oscillating around a boundary would make Fan Control flap
    /// between two profiles on every poll. (Hysteresis is measured at the
    /// boundary being crossed — the outgoing profile's trigger — so that
    /// every step down the ladder remains reachable.)
    /// </summary>
    public const int HysteresisDegreesC = 3;

    private readonly List<FanControlProfile> _profiles = new(); // sorted ascending by temperature

    private int _activeIndex = -1; // -1 = nothing applied yet
    private string _exePath = string.Empty;

    /// <summary>The config file name currently active in Fan Control, or null.</summary>
    public string? ActiveConfigName { get; private set; }

    /// <summary>True when the last launch attempt failed; the UI surfaces this.</summary>
    public bool LastLaunchFailed { get; private set; }

    /// <summary>Rebuilds the ladder from the latest options (called on startup and after edits).</summary>
    public void SetSettings(FanControlSettings settings)
    {
        _profiles.Clear();

        if (settings.Enabled)
        {
            foreach (var profile in settings.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(profile.ConfigName))
                {
                    _profiles.Add(profile);
                }
            }

            // Ties keep the later profile on top (it is "hotter" for the same temp).
            _profiles.Sort((a, b) => a.TemperatureC.CompareTo(b.TemperatureC));
        }

        _exePath = settings.ExePath.Trim();

        // Any change to the options invalidates "what Fan Control has now" — the
        // next Update() re-applies the (possibly new) ladder from scratch.
        _activeIndex = -1;
        ActiveConfigName = null;
        LastLaunchFailed = false;
    }

    /// <summary>
    /// Feeds the current hottest GPU temperature (°C) into the ladder.
    /// Returns the config name that should now be active in Fan Control
    /// (null when the feature is off, there are no profiles, or no temperature
    /// has been seen yet). Fan Control is launched only when the selected
    /// profile changes, so a steady temperature costs nothing.
    /// </summary>
    public string? Update(int? maxTempC)
    {
        if (_profiles.Count == 0)
        {
            return null;
        }

        if (maxTempC is not int temp)
        {
            // No temperature data this tick (nvidia-smi reported none) — keep
            // whatever profile is active instead of guessing.
            return ActiveConfigName;
        }

        int current = _activeIndex;

        // Step UP: one profile at a time, immediately, while the temperature is
        // at or above the next hotter profile's trigger. (Heating is always
        // allowed — the fans should react as soon as it gets hot.)
        int next = current < 0 ? 0 : current;
        if (current < 0)
        {
            // First application after (re)start or option change: plain ladder,
            // no hysteresis to apply yet.
            for (int i = 0; i < _profiles.Count; i++)
            {
                if (_profiles[i].TemperatureC <= temp)
                {
                    next = i;
                }
            }
        }
        else
        {
            while (next + 1 < _profiles.Count && _profiles[next + 1].TemperatureC <= temp)
            {
                next++;
            }

            // Step DOWN: one profile at a time, but only while the temperature
            // has cooled at least HysteresisDegreesC below the trigger of the
            // profile being LEFT. Measuring at the outgoing trigger (the
            // boundary just crossed) keeps every rung of the ladder reachable
            // on the way down — e.g. from Max at 88, cooling to 84 crosses
            // Max's boundary (88-3) and lands on Gaming at 75, where it holds
            // until cooling 3° below 75.
            while (next > 0 && temp <= _profiles[next].TemperatureC - HysteresisDegreesC)
            {
                next--;
            }
        }

        if (next == current)
        {
            return ActiveConfigName;
        }

        // First application, step up, or a hysteresis-verified step down.
        _activeIndex = next;
        ActiveConfigName = _profiles[next].ConfigFileName;
        LaunchFanControl(_profiles[next].ConfigFileName);
        return ActiveConfigName;
    }

    /// <summary>
    /// Starts FanControl.exe with <c>-c "config"</c>. Never throws: every failure
    /// is logged and reported through <see cref="LastLaunchFailed"/> so the monitor
    /// itself keeps running. The start is attempted on a background thread —
    /// <see cref="Update"/> is called on the UI thread, and we never want to block it.
    /// </summary>
    private void LaunchFanControl(string configFileName)
    {
        _ = Task.Run(() => LaunchAndCheck(configFileName));
    }

    private void LaunchAndCheck(string configFileName)
    {
        try
        {
            if (_exePath.Length == 0 || !File.Exists(_exePath))
            {
                throw new FileNotFoundException(
                    $"FanControl.exe was not found at '{_exePath}'. Fix the path in Options.", _exePath);
            }

            string workingDirectory = Path.GetDirectoryName(Path.GetFullPath(_exePath))
                ?? string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configFileName);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Two outcomes: Fan Control was already running → this process just
            // tells it to switch and exits with 0 within a couple of seconds; or
            // nothing was running → this process BECOMES Fan Control and keeps
            // going (still a success, it shows up in the tray).
            if (process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                if (process.ExitCode != 0)
                {
                    LastLaunchFailed = true;
                    AppLogger.LogText(
                        "Fan Control exited with a non-zero code",
                        $"config={configFileName} exitCode={process.ExitCode}");
                    return;
                }
            }
            // else: still running after 5 s = it became the live instance.

            LastLaunchFailed = false;
            AppLogger.LogText(
                "Fan Control profile switched",
                $"config={configFileName} exe={_exePath}");
        }
        catch (Exception ex)
        {
            LastLaunchFailed = true;
            AppLogger.Log("Fan Control launch failed", ex);
        }
    }
}
