namespace NVIDIAFanControlUtility.Models;

/// <summary>
/// A point-in-time snapshot of a single GPU, parsed from nvidia-smi output.
/// Every field is nullable so a missing or "[N/A]" value can be shown as "--".
/// </summary>
public sealed class GpuSnapshot
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;

    public int? TemperatureC { get; init; }         // °C
    public int? Utilization { get; init; }          // GPU core utilization, 0-100 %
    public int? MemoryUtilization { get; init; }    // memory controller/bus, 0-100 %
    public int? MemoryTotalMiB { get; init; }
    public int? MemoryUsedMiB { get; init; }
    public double? PowerDrawW { get; init; }        // watts
    public double? PowerLimitW { get; init; }       // watts
    public int? FanSpeed { get; init; }             // %
    public int? PcieGen { get; init; }              // current PCIe generation
    public int? PcieWidth { get; init; }            // current PCIe lanes (x16, x8, ...)
    public int? ClockMhz { get; init; }             // current graphics clock
    public int? MaxClockMhz { get; init; }

    /// <summary>Memory usage in percent (0-100), derived from used/total.</summary>
    public int? MemoryPercent =>
        MemoryUsedMiB is int used && MemoryTotalMiB is int total && total > 0
            ? (int)Math.Round(used * 100.0 / total)
            : null;
}
