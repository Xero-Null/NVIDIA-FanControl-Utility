namespace NVIDIAFanControlUtility;

internal static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Capture anything that escapes normal handling (UI-thread exceptions
        // and thread-pool faults) so the user sees a message AND we get a
        // stack trace in %TEMP%\nvidia_fan_control_utility_errors.log instead of a crash.
        AppLogger.WriteStartupBanner();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // IsTerminating == true: this exception is killing the process.
            // Log it and show the full stack so the user can report it.
            if (args.IsTerminating)
            {
                CrashReport.Show("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Log("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
        {
            CrashReport.Show("Application.ThreadException", args.Exception);
        };

        try
        {
            // new MainForm() runs BEFORE Application.Run's message loop, so an
            // exception thrown there would bypass the handlers above and kill
            // the process with only the default Windows dialog. Catch it here.
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            CrashReport.Show("Application.Run (startup)", ex);
        }
    }
}

/// <summary>
/// Formats and displays fatal errors: full stack trace on screen (so the user
/// can read it without opening the log file) plus a pointer to the log file.
/// </summary>
internal static class CrashReport
{
    public static void Show(string what, Exception? ex)
    {
        AppLogger.Log(what, ex);

        try
        {
            string detail = ex?.ToString() ?? "(no exception object was supplied)";
            if (detail.Length > 2500)
            {
                detail = detail[..2500] + "\n\n… (truncated — the log file has the full text)";
            }

            MessageBox.Show(
                "NVIDIA Fan Control Utility hit an unexpected error.\n\n" +
                "The full details are below, and were also saved to:\n" +
                $"{AppLogger.LogPath}\n\n" +
                "Please send that log file when reporting the problem.\n\n" +
                "────────────────────────────────────────\n\n" +
                detail,
                "NVIDIA Fan Control Utility — crash details",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // If even the dialog fails, the log file already has everything.
        }
    }
}
