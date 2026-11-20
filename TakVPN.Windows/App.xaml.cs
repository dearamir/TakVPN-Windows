using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TakVPN.Windows;

public partial class App : Application
{
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;
    private static readonly object LogLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            WriteCrashLog("AppDomain unhandled exception", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) => _allowExit = true;

        WriteDiagnostic("Startup", "Process startup entered.");
        try
        {
            base.OnStartup(e);
            WriteDiagnostic("Startup", "WPF application initialized.");

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Closing += MainWindow_Closing;
            mainWindow.StateChanged += MainWindow_StateChanged;
            WriteDiagnostic("Startup", "Main window constructed.");

            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TakVPN.ico");
                if (File.Exists(iconPath))
                {
                    using var icon = new System.Drawing.Icon(iconPath);
                    _trayIcon = new Forms.NotifyIcon
                    {
                        Icon = (System.Drawing.Icon)icon.Clone(),
                        Text = "TakVPN",
                        Visible = true
                    };
                    var menu = new Forms.ContextMenuStrip();
                    var openItem = new Forms.ToolStripMenuItem("Open TakVPN");
                    openItem.Click += (_, _) => ShowMainWindow();
                    var exitItem = new Forms.ToolStripMenuItem("Exit");
                    exitItem.Click += (_, _) => ExitFromTray();
                    menu.Items.Add(openItem);
                    menu.Items.Add(new Forms.ToolStripSeparator());
                    menu.Items.Add(exitItem);
                    _trayIcon.ContextMenuStrip = menu;
                    _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
                }
                else
                {
                    WriteDiagnostic("Tray icon", $"Icon file not found: {iconPath}");
                }
            }
            catch (Exception ex)
            {
                WriteCrashLog("Tray icon initialization failed", ex);
                _trayIcon?.Dispose();
                _trayIcon = null;
            }

            mainWindow.Show();
            WriteDiagnostic("Startup", "Main window shown successfully.");
        }
        catch (Exception ex)
        {
            var logPath = WriteCrashLog("Fatal startup failure", ex);
            MessageBox.Show(
                $"TakVPN نتوانست اجرا شود. جزئیات خطا در این مسیر ذخیره شد:\n{logPath}\n\n{ex}",
                "TakVPN - خطای راه‌اندازی",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit || _trayIcon is null) return;
        if (sender is not Window window) return;
        e.Cancel = true;
        window.Hide();
        try { _trayIcon.ShowBalloonTip(1800, "TakVPN", "برنامه در کنار ساعت ویندوز در حال اجراست.", Forms.ToolTipIcon.Info); }
        catch (Exception ex) { WriteCrashLog("Tray notification failed", ex); }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window && window.WindowState == WindowState.Minimized)
            window.Hide();
    }

    private void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        if (MainWindow is not null)
            MainWindow.Close();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WriteDiagnostic("Shutdown", $"Application exiting with code {e.ApplicationExitCode}.");
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = WriteCrashLog("WPF dispatcher exception", e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                $"یک خطای پیش‌بینی‌نشده رخ داد. جزئیات در این مسیر ذخیره شد:\n{logPath}\n\n{e.Exception}",
                "TakVPN - خطا",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
    }

    private static string WriteCrashLog(string stage, Exception exception)
    {
        var entry = $"[{DateTimeOffset.Now:O}] [{stage}]\n{exception}\n{new string('-', 72)}\n";
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "logs", "crash.log"),
            Path.Combine(Path.GetTempPath(), "TakVPN", "crash.log")
        };

        foreach (var path in paths)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, entry);
                }
                return path;
            }
            catch { }
        }

        return "ذخیره گزارش ناموفق بود؛ لطفاً در Event Viewer بخش Windows Logs > Application خطای TakVPN یا .NET Runtime را بررسی کنید.";
    }

    private static void WriteDiagnostic(string stage, string message)
    {
        _ = WriteCrashLog(stage, new InvalidOperationException(message));
    }
}
