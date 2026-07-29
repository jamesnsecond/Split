using Microsoft.Win32;
using Split.Audio;
using Split.UI;

namespace Split;

internal static class Program
{
    /// <summary>
    /// Windows 10 1803. Per-app output device assignment is the entire product
    /// and it doesn't exist before this build — the Settings UI that exposes
    /// the same mechanism shipped here too.
    /// </summary>
    private const int MinimumBuild = 17134;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // One instance only: two supervisors would fight over the same
        // endpoint assignments once a second, each undoing the other.
        using var mutex = new Mutex(initiallyOwned: true, "Split.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "Split is already running — look for it in the notification area, "
                    + "next to the clock.",
                "Split",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (Environment.OSVersion.Version.Build < MinimumBuild)
        {
            MessageBox.Show(
                "Split needs Windows 10 version 1803 or later.\n\n"
                    + "Per-app audio routing is what this app does, and Windows didn't "
                    + "gain the ability before that update.",
                "Split",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Anything that escapes a timer tick would otherwise kill the app with
        // routing assignments still applied to the user's machine.
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, fatal: true);

        SplitContext context;
        try
        {
            // Startup touches the filesystem and the audio service, either of
            // which can refuse. Without this the app dies before it has a
            // window, so the user gets a bare crash with no explanation.
            context = new SplitContext();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Split couldn't start.\n\n"
                    + ex.Message
                    + "\n\nIf the Windows Audio service isn't running, starting it "
                    + "and reopening Split should fix this.",
                "Split",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(context);
    }

    private static void Report(Exception? ex, bool fatal)
    {
        MessageBox.Show(
            (fatal ? "Split hit an unexpected error and has to close.\n\n" : "Split hit an unexpected error.\n\n")
                + (ex?.Message ?? "Unknown error.")
                + (fatal
                    ? "\n\nIf your audio ends up in the wrong place, reopen Split and use "
                      + "Diagnostics → Restore system audio."
                    : ""),
            "Split",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}

/// <summary>
/// Keeps Split alive in the notification area with no window open, which is
/// the closest Windows equivalent of the macOS build's menu-bar residency.
/// Closing the main window hides it; quitting happens from the tray.
/// </summary>
internal sealed class SplitContext : ApplicationContext
{
    private readonly RouteSupervisor _supervisor;
    private readonly TrayController _tray;
    private MainForm? _main;
    private int _cleanedUp;

    public SplitContext()
    {
        _supervisor = new RouteSupervisor();
        _tray = new TrayController(_supervisor);
        _tray.OpenRequested += ShowMain;
        _tray.QuitRequested += Quit;

        // A first run has nothing to look at in the tray, so open the window.
        // Later runs stay quiet and let the tray icon speak for itself.
        if (_supervisor.Table.Routes.Count == 0) ShowMain();

        // Endpoint assignments are persisted system-wide, so a log-off or
        // shutdown must still put them back.
        //
        // Deliberately NOT AppDomain.ProcessExit: that can run on whatever
        // thread called Environment.Exit, and teardown makes raw vtable calls
        // on interface pointers acquired here on the STA. Raw vtable dispatch
        // skips COM marshalling entirely, so running it from another apartment
        // is undefined behaviour rather than a slow path. SessionEnding is
        // raised on the UI thread, which is what this needs.
        Application.ApplicationExit += (_, _) => Cleanup();
        SystemEvents.SessionEnding += (_, _) => Cleanup();
    }

    private void ShowMain()
    {
        if (_main is null || _main.IsDisposed)
        {
            _main = new MainForm(_supervisor);
            _main.FormClosing += (sender, e) =>
            {
                // Hide rather than close, so routing keeps running.
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    ((Form)sender!).Hide();
                }
            };
        }

        _main.Show();
        if (_main.WindowState == FormWindowState.Minimized)
        {
            _main.WindowState = FormWindowState.Normal;
        }
        _main.Activate();
    }

    private void Quit()
    {
        Cleanup();
        ExitThread();
    }

    private void Cleanup()
    {
        // Atomic, because ApplicationExit and SessionEnding can both fire for
        // one shutdown and a plain bool test-and-set would let both through.
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0) return;

        try { _supervisor.Dispose(); } catch { }
        try { _tray.Dispose(); } catch { }
    }
}
