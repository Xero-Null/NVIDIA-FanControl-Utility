using System.Diagnostics;
using System.Globalization;
using NVIDIAFanControlUtility.Models;

namespace NVIDIAFanControlUtility.Services;

/// <summary>
/// Runs <c>nvidia-smi</c> through PowerShell and parses its CSV output into <see cref="GpuSnapshot"/> objects.
/// The app never talks to the GPU directly; everything comes from nvidia-smi's console output.
/// </summary>
public sealed class NvSmiService
{
    /// <summary>
    /// The exact list of fields we ask nvidia-smi for. The column order here defines
    /// the parsing indices below, so keep both in sync.
    /// </summary>
    private const string QueryFields =
        "index,name,driver_version,temperature.gpu,utilization.gpu,utilization.memory," +
        "memory.total,memory.used,power.draw,power.limit,fan.speed," +
        "pcie.link.gen.current,pcie.link.width.current,clocks.current.graphics,clocks.max.graphics";

    private const int F_Index = 0;
    private const int F_Name = 1;
    private const int F_Driver = 2;
    private const int F_Temp = 3;
    private const int F_Usage = 4;
    private const int F_MemUtil = 5;
    private const int F_MemTotal = 6;
    private const int F_MemUsed = 7;
    private const int F_PowerDraw = 8;
    private const int F_PowerLimit = 9;
    private const int F_Fan = 10;
    private const int F_PcieGen = 11;
    private const int F_PcieWidth = 12;
    private const int F_Clock = 13;
    private const int F_MaxClock = 14;

    private const int FieldCount = 15;

    /// <summary>
    /// Runs nvidia-smi once via PowerShell and returns one snapshot per GPU.
    /// </summary>
    public IReadOnlyList<GpuSnapshot> QueryGpus()
    {
        string script =
            "$ProgressPreference = 'SilentlyContinue'; " +
            $"& nvidia-smi --query-gpu={QueryFields} --format=csv,noheader,nounits 2>&1 | Out-String";

        string output = RunPowerShell(script);
        try
        {
            return ParseOutput(output);
        }
        catch
        {
            // Parsing failed — log what nvidia-smi actually returned so the
            // problem can be diagnosed from the log file alone.
            string snippet = output?.Trim() ?? string.Empty;
            if (snippet.Length > 4000)
            {
                snippet = snippet[..4000] + "… (truncated)";
            }

            AppLogger.LogText(
                "nvidia-smi query failed; raw output below:",
                snippet.Length == 0 ? "(empty)" : snippet);
            throw;
        }
    }

    /// <summary>
    /// True when the machine has a working nvidia-smi (used for the startup check).
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            return QueryGpus().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = new Process();
        process.StartInfo = psi;

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start PowerShell.");
        }

        // Read both streams asynchronously so a full stderr buffer can never
        // deadlock the stdout read.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        // WaitForExitAsync (unlike WaitForExit(timeout)) is the supported way
        // to combine a timeout with the async stream reads; it cannot race
        // the process-handle teardown that made WaitForExit throw on some
        // Windows builds. Task.Wait(timeout) is fine here because we are on
        // a thread-pool thread, not the UI thread.
        Task exitTask = process.WaitForExitAsync();
        bool exited = exitTask.Wait(TimeSpan.FromMilliseconds(20000));
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            throw new TimeoutException("nvidia-smi did not finish in time.");
        }

        // If a stream reader faulted for some reason, degrade to empty text
        // instead of letting a BCL exception bubble up as an unexplained
        // NullReferenceException in the UI.
        string stdout = SafeGetResult(stdoutTask);
        string stderr = SafeGetResult(stderrTask);

        return stdout + "\n" + stderr;
    }

    private static string SafeGetResult(Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<GpuSnapshot> ParseOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("nvidia-smi returned no output.");
        }

        string text = output.Trim();

        if (text.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("The term 'nvidia-smi'", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "nvidia-smi was not found. Make sure the NVIDIA driver is installed.");
        }

        if (text.Contains("No devices were found", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<GpuSnapshot>();
        }

        var snapshots = new List<GpuSnapshot>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();

            // Skip blank lines, the nvidia-smi banner, and anything that is not a data row.
            if (line.Length == 0 || line.StartsWith("N", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line.Split(',');
            if (fields.Length < FieldCount)
            {
                continue;
            }

            snapshots.Add(new GpuSnapshot
            {
                Index = ToInt(fields[F_Index]) ?? 0,
                Name = fields[F_Name].Trim(),
                DriverVersion = fields[F_Driver].Trim(),
                TemperatureC = ToInt(fields[F_Temp]),
                Utilization = ToInt(fields[F_Usage]),
                MemoryUtilization = ToInt(fields[F_MemUtil]),
                MemoryTotalMiB = ToInt(fields[F_MemTotal]),
                MemoryUsedMiB = ToInt(fields[F_MemUsed]),
                PowerDrawW = ToDouble(fields[F_PowerDraw]),
                PowerLimitW = ToDouble(fields[F_PowerLimit]),
                FanSpeed = ToInt(fields[F_Fan]),
                PcieGen = ToInt(fields[F_PcieGen]),
                PcieWidth = ToInt(fields[F_PcieWidth]),
                ClockMhz = ToInt(fields[F_Clock]),
                MaxClockMhz = ToInt(fields[F_MaxClock]),
            });
        }

        if (snapshots.Count == 0)
        {
            throw new InvalidOperationException(
                "nvidia-smi ran, but no GPU data could be parsed from its output.");
        }

        return snapshots;
    }

    /// <summary>Parses an integer; returns null for empty, "[N/A]", or non-numeric values.</summary>
    private static int? ToInt(string raw)
    {
        return int.TryParse(Normalize(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>Parses a double (power draw can have a decimal); null for "[N/A]" etc.</summary>
    private static double? ToDouble(string raw)
    {
        return double.TryParse(Normalize(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static string Normalize(string raw)
    {
        string value = raw.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            // nvidia-smi reports "[N/A]" for unavailable values.
            return string.Empty;
        }

        return value;
    }
}
