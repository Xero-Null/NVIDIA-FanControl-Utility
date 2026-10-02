using System.Diagnostics;
using System.Reflection;

namespace NVIDIAFanControlUtility;

/// <summary>
/// Minimal file logger shared by every layer. Writes timestamped entries with
/// the FULL exception (message + stack trace + inner exceptions) to
/// %TEMP%\nvidia_fan_control_utility_errors.log so crashes can be diagnosed without a debugger.
/// Kept WinForms-free so the service layer can be tested on its own.
/// </summary>
internal static class AppLogger
{
    private static readonly object Gate = new();

    /// <summary>Full path of the error log; shown in dialogs so the user can send it.</summary>
    public static string LogPath { get; } =
        Path.Combine(Path.GetTempPath(), "nvidia_fan_control_utility_errors.log");

    /// <summary>Writes a plain-text diagnostic entry (not tied to an exception); never throws.</summary>
    public static void LogText(string what, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {what}{(string.IsNullOrEmpty(detail) ? string.Empty : $"\n{detail}")}\n\n");
            }
        }
        catch
        {
            // Last-ditch logging; ignore failures.
        }
    }

    /// <summary>Writes one timestamped exception entry (full stack included); never throws.</summary>
    public static void Log(string what, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {what}: {ex?.ToString() ?? "null"}\n\n");
            }
        }
        catch
        {
            // Last-ditch logging; ignore failures.
        }
    }

    /// <summary>Records environment details at startup so a log file is self-describing.</summary>
    public static void WriteStartupBanner()
    {
        try
        {
            lock (Gate)
            {
                string osInfo;
                try
                {
                    osInfo = Environment.OSVersion.ToString();
                }
                catch
                {
                    osInfo = "(unavailable)";
                }

                string psVersion = ProbePowerShell();
                string nvSmiVersion = ProbeNvidiaSmi();

                File.AppendAllText(LogPath,
$@"
================ NVIDIA Fan Control Utility startup ================
Time:        {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
OS:          {osInfo}
.NET:        {Environment.Version}
Build:       {Assembly.GetExecutingAssembly().GetName().Version}
Exe path:    {Environment.ProcessPath ?? "(unknown)"}
Working dir: {Environment.CurrentDirectory}
User:        {Environment.UserDomainName}\{Environment.UserName}
Session ID:  {Environment.ProcessId}
PowerShell:  {psVersion}
nvidia-smi:  {nvSmiVersion}
======================================================

");
            }
        }
        catch
        {
            // Never let the banner take the app down.
        }
    }

    private static string ProbePowerShell()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null)
            {
                return "(could not start)";
            }

            string outp = p.StandardOutput.ReadToEnd();
            string errp = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(outp) ? $"(no output; stderr: {errp.Trim()})" : outp.Trim();
        }
        catch (Exception ex)
        {
            return $"(probe failed: {ex.Message})";
        }
    }

    private static string ProbeNvidiaSmi()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null)
            {
                return "(could not start)";
            }

            string outp = p.StandardOutput.ReadToEnd();
            string errp = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            string combined = (outp + errp).Trim();
            return string.IsNullOrWhiteSpace(combined)
                ? "(no output — nvidia-smi may be missing)"
                : combined.Split('\n')[0].Trim();
        }
        catch (Exception ex)
        {
            return $"(probe failed: {ex.Message})";
        }
    }
}
