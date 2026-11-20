using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TakVPN.Windows;

public partial class App : Application
{
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception."));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) => _allowExit = true;
        base.OnStartup(e);

        MainWindow mainWindow;
        try
        {
            mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Closing += MainWindow_Closing;
            mainWindow.StateChanged += MainWindow_StateChanged;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show("راه‌اندازی پنجره اصلی ناموفق بود. گزارش خطا ذخیره شد.\n" + ex.Message,
                "TakVPN - خطای راه‌اندازی", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

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
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            _trayIcon?.Dispose();
            _trayIcon = null;
            MessageBox.Show("آیکون کنار ساعت بارگذاری نشد؛ برنامه بدون آیکون سینی اجرا می‌شود.\n" + ex.Message,
                "TakVPN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        mainWindow.Show();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit || _trayIcon is null) return;
        if (sender is not Window window) return;
        e.Cancel = true;
        window.Hide();
        try { _trayIcon.ShowBalloonTip(1800, "TakVPN", "برنامه در کنار ساعت ویندوز در حال اجراست.", Forms.ToolTipIcon.Info); }
        catch (Exception ex) { WriteCrashLog(ex); }
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
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                "یک خطای پیش‌بینی‌نشده رخ داد و برنامه برای جلوگیری از بسته‌شدن ناگهانی آن را ثبت کرد.\n" +
                "گزارش خطا در پوشهٔ LocalAppData\\TakVPN\\logs\\crash.log ذخیره شد.\n\n" +
                e.Exception.Message,
                "TakVPN - خطا",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TakVPN", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {exception}\n{new string('-', 72)}\n");
        }
        catch { }
    }
}