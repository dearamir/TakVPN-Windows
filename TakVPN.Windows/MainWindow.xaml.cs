using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Reflection;

namespace TakVPN.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VpnProfile> _profiles = new();
    private readonly LayoutCustomization _layoutCustomization;
    private readonly string _storePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "profiles.json");
    private readonly XrayEngine _engine = new();
    private bool _busy;
    private bool _oceanTheme;
    private bool _isClosing;
    private readonly System.Windows.Threading.DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastSent, _lastReceived, _baseSent, _baseReceived;
    private DateTime _lastTrafficSample;
    private VpnProfile? _activeProfile;
    private readonly HashSet<string> _shownLogLines = new(StringComparer.Ordinal);

    public MainWindow()
    {
        InitializeComponent();
        ApplySavedTheme();
        _layoutCustomization = new LayoutCustomization(this,
            new FrameworkElement[] { HeaderPanel, ConnectionPanel, ConfigPanel, DetailsPanel, FooterPanel },
            ConfigToolbar, EditLayoutButton, ResetLayoutButton);
        FitWindowToWorkArea();
        LoadWindowIcon();
        AppVersionText.Text = "نسخه " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.35");
        UpdateVpnStatusIcon(false);
        ProfilesList.ItemsSource = _profiles;
        LoadProfiles();
        UpdateButtons();
        _trafficTimer.Tick += (_, _) => { UpdateTrafficStats(); RefreshEngineLogs(); };
        _trafficTimer.Start();
    }



    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _oceanTheme = !_oceanTheme;
        ApplyThemeColors();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "ui-theme.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _oceanTheme ? "ocean" : "gold");
        }
        catch { }
    }

    private void ApplySavedTheme()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "ui-theme.txt");
            _oceanTheme = File.Exists(path) && File.ReadAllText(path).Trim().Equals("ocean", StringComparison.OrdinalIgnoreCase);
        }
        catch { _oceanTheme = false; }
        ApplyThemeColors();
    }

    private void ApplyThemeColors()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        var panel = (SolidColorBrush)Application.Current.Resources["PanelBrush"];
        var window = (SolidColorBrush)Application.Current.Resources["WindowBrush"];
        accent.Color = _oceanTheme ? Color.FromRgb(0, 205, 235) : Color.FromRgb(212, 175, 55);
        panel.Color = _oceanTheme ? Color.FromRgb(23, 39, 61) : Color.FromRgb(35, 35, 40);
        window.Color = _oceanTheme ? Color.FromRgb(12, 22, 38) : Color.FromRgb(16, 16, 18);
        ThemeToggleButton.Content = _oceanTheme ? "پوسته مشکی‌طلایی" : "پوسته آبی اقیانوسی";
    }


    private async void CheckXray_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _updateBusy) return;
        CheckXrayButton.IsEnabled = false;
        CheckXrayButton.Content = "در حال بررسی...";
        var report = new List<string>();
        try
        {
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
            var xrayPath = Path.Combine(toolsDir, "xray", "xray.exe");
            var wintunPath = Path.Combine(toolsDir, "xray", "wintun.dll");
            report.Add(File.Exists(xrayPath) ? "✓ فایل xray.exe پیدا شد." : "✗ فایل xray.exe وجود ندارد.");
            report.Add(File.Exists(wintunPath) ? "✓ فایل Wintun پیدا شد." : "✗ فایل wintun.dll وجود ندارد.");
            if (File.Exists(xrayPath))
            {
                try
                {
                    var start = new ProcessStartInfo(xrayPath)
                    {
                        WorkingDirectory = Path.GetDirectoryName(xrayPath)!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    start.ArgumentList.Add("version");
                    using var process = Process.Start(start);
                    if (process is null) throw new InvalidOperationException("فرایند Xray اجرا نشد.");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                    catch (TimeoutException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        throw new TimeoutException("پاسخ Xray بیشتر از ۸ ثانیه طول کشید.");
                    }
                    var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
                    if (process.ExitCode == 0)
                        report.Add("✓ اجرای موتور Xray موفق بود." + (string.IsNullOrWhiteSpace(output) ? "" : Environment.NewLine + output));
                    else
                        report.Add("✗ Xray اجرا شد اما کد خطا داد: " + process.ExitCode + (string.IsNullOrWhiteSpace(output) ? "" : Environment.NewLine + output));
                }
                catch (Exception ex) { report.Add("✗ خطای اجرای Xray: " + ex.Message); }
            }
            var passed = File.Exists(xrayPath) && File.Exists(wintunPath) &&
                report.Any(line => line.StartsWith("✓ اجرای موتور Xray"));
            var summary = string.Join(Environment.NewLine + Environment.NewLine, report);
            Log("بررسی موتور Xray:" + Environment.NewLine + summary);
            MessageBox.Show(summary, passed ? "بررسی موتور Xray — موفق" : "بررسی موتور Xray — نیازمند بررسی",
                MessageBoxButton.OK, passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("بررسی موتور Xray ناموفق: " + ex);
            MessageBox.Show(ex.Message, "بررسی موتور Xray", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CheckXrayButton.IsEnabled = true;
            CheckXrayButton.Content = "بررسی موتور Xray";
        }
    }

    private UpdateService.UpdateInfo? _availableUpdate;
    private bool _updateBusy;

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "در حال بررسی نسخه جدید...";
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update is null)
            {
                _availableUpdate = null;
                UpdateStatusText.Text = "نسخه فعلی به‌روز است.";
                MessageBox.Show("در حال حاضر نسخه جدیدتری منتشر نشده است.", "بروزرسانی TakVPN", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _availableUpdate = update;
            UpdateStatusText.Text = $"نسخه جدید {update.Version} آماده است.";
            InstallUpdateButton.Visibility = Visibility.Visible;
            MessageBox.Show($"نسخه فعلی: {UpdateService.CurrentVersion}\nنسخه جدید: {update.Version}\n\n{(string.IsNullOrWhiteSpace(update.Notes) ? "بهبودها و اصلاحات برنامه" : update.Notes)}\n\nبرای دریافت و نصب، روی «دانلود و نصب بروزرسانی» بزنید.", "بروزرسانی TakVPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "بررسی بروزرسانی ناموفق بود.";
            Log("بررسی بروزرسانی ناموفق: " + ex.Message);
            MessageBox.Show("ارتباط با سرور بروزرسانی برقرار نشد:\n" + ex.Message, "TakVPN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _availableUpdate is null) return;
        if (MessageBox.Show($"نسخه {_availableUpdate.Version} دانلود و نصب شود؟ برنامه هنگام نصب بسته و پس از نصب دوباره اجرا می‌شود.", "نصب بروزرسانی", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        try
        {
            UpdateStatusText.Text = "در حال دانلود بروزرسانی...";
            var progress = new Progress<double>(value => UpdateProgressBar.Value = value);
            var installerPath = await UpdateService.DownloadInstallerAsync(_availableUpdate, progress);
            UpdateStatusText.Text = "دانلود و بررسی فایل کامل شد؛ در حال آماده‌سازی نصب...";
            await _engine.StopAsync();
            _isClosing = true;
            _trafficTimer.Stop();
            UpdateService.StartInstaller(installerPath);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isClosing = false;
            UpdateStatusText.Text = "نصب بروزرسانی انجام نشد.";
            Log("نصب بروزرسانی ناموفق: " + ex);
            MessageBox.Show("بروزرسانی نصب نشد. نسخه فعلی بدون تغییر باقی مانده است.\n\n" + ex.Message, "TakVPN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!_isClosing)
            {
                _updateBusy = false;
                CheckUpdateButton.IsEnabled = true;
                InstallUpdateButton.IsEnabled = true;
            }
        }
    }

    private void FitWindowToWorkArea()
    {
        // Keep the native title bar and its minimize/maximize/close buttons
        // inside the usable desktop area, including low-resolution displays.
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(760, Math.Max(400, workArea.Width - 40));
        MinHeight = Math.Min(560, Math.Max(360, workArea.Height - 40));
        Width = Math.Min(1100, Math.Max(MinWidth, workArea.Width - 48));
        Height = Math.Min(720, Math.Max(MinHeight, workArea.Height - 48));
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void LoadWindowIcon()
    {
        // The ICO is deployed as a content file, not a WPF Resource. Resolve it
        // from the executable directory after XAML has loaded so a missing icon
        // can never prevent the main window from starting.
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TakVPN.ico");
            if (!File.Exists(iconPath)) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            Icon = bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Window icon could not be loaded: {ex}");
        }
    }

    private void AddConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var links = ConfigInput.Text.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (links.Length == 0) { MessageBox.Show("لینک یک یا چند کانفیگ را وارد کنید.", "TakVPN"); return; }
            var added = 0; var skipped = 0; var errors = new List<string>();
            foreach (var link in links)
            {
                if (_profiles.Any(p => string.Equals(p.Link, link, StringComparison.Ordinal))) { skipped++; continue; }
                if (!VpnProfile.TryParse(link, out var profile, out var error) || profile is null) { errors.Add(error); continue; }
                _profiles.Add(profile); ProfilesList.SelectedItem = profile; added++;
            }
            ConfigInput.Clear();
            SaveProfiles();
            Log("افزودن گروهی: " + added + " اضافه، " + skipped + " تکراری، " + errors.Count + " نامعتبر");
            if (errors.Count > 0) MessageBox.Show("افزوده شد: " + added + "\nتکراری: " + skipped + "\nنامعتبر: " + errors.Count + "\n\n" + string.Join("\n", errors.Take(5)), "نتیجه افزودن گروهی");
        }
        catch (Exception ex) { Log("خطا در افزودن کانفیگ: " + ex.Message); MessageBox.Show(ex.Message, "TakVPN"); }
    }

    private void EditConfig_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not VpnProfile profile)
        {
            MessageBox.Show("ابتدا یک کانفیگ را انتخاب کنید.", "TakVPN");
            return;
        }
        if (_engine.IsRunning)
        {
            MessageBox.Show("برای ویرایش کانفیگ، ابتدا اتصال VPN را قطع کنید.", "TakVPN");
            return;
        }

        var editor = new Window
        {
            Title = "ویرایش کامل کانفیگ",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 760,
            Height = 570,
            MinWidth = 580,
            MinHeight = 420,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 22, 26)),
            Foreground = System.Windows.Media.Brushes.White,
            FlowDirection = FlowDirection.RightToLeft,
            ResizeMode = ResizeMode.CanResize
        };

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "نام و تمام پارامترهای کانفیگ را ویرایش کنید",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        layout.Children.Add(title);

        var namePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        namePanel.Children.Add(new TextBlock { Text = "نام کانفیگ", Foreground = System.Windows.Media.Brushes.LightGray, Margin = new Thickness(0, 0, 0, 5) });
        var nameBox = new TextBox
        {
            Text = profile.Name,
            Height = 34,
            Padding = new Thickness(8, 5, 8, 5),
            FlowDirection = FlowDirection.RightToLeft,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 37, 44)),
            Foreground = System.Windows.Media.Brushes.White,
            CaretBrush = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 78, 88))
        };
        namePanel.Children.Add(nameBox);
        Grid.SetRow(namePanel, 1);
        layout.Children.Add(namePanel);

        var linkPanel = new DockPanel { LastChildFill = true };
        var hint = new TextBlock
        {
            Text = "لینک کامل کانفیگ — همه تنظیمات پروتکل، سرور، پورت، امنیت، TLS، SNI، شبکه، مسیر و سایر پارامترهای موجود در لینک را از این قسمت تغییر دهید.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(hint, Dock.Top);
        linkPanel.Children.Add(hint);

        var linkBox = new TextBox
        {
            Text = profile.Link,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            FlowDirection = FlowDirection.LeftToRight,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 18, 22)),
            Foreground = System.Windows.Media.Brushes.White,
            CaretBrush = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 78, 88)),
            Padding = new Thickness(10)
        };
        linkPanel.Children.Add(linkBox);
        Grid.SetRow(linkPanel, 2);
        layout.Children.Add(linkPanel);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Content = "ذخیره تغییرات", Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 8, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 140, 40)), Foreground = System.Windows.Media.Brushes.White };
        var cancel = new Button { Content = "انصراف", Padding = new Thickness(18, 9, 18, 9) };
        save.Click += (_, _) =>
        {
            var updatedLink = linkBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(updatedLink))
            {
                MessageBox.Show(editor, "لینک کانفیگ نمی‌تواند خالی باشد.", "TakVPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!VpnProfile.TryParse(updatedLink, out var parsed, out var error) || parsed is null)
            {
                MessageBox.Show(editor, error, "لینک نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            profile.Link = updatedLink;
            profile.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? parsed.Name : nameBox.Text.Trim();
            profile.Protocol = parsed.Protocol;
            profile.Host = parsed.Host;
            profile.Port = parsed.Port;
            profile.LatencyMs = null;
            editor.DialogResult = true;
            editor.Close();
        };
        cancel.Click += (_, _) => { editor.DialogResult = false; editor.Close(); };
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);

        editor.Content = layout;
        if (editor.ShowDialog() == true)
        {
            ProfilesList.Items.Refresh();
            RefreshSelected();
            SaveProfiles();
            Log("کانفیگ و تمام پارامترهای لینک ویرایش و ذخیره شد: " + profile.Name);
        }
    }

    private void RemoveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not VpnProfile profile) return;
        if (_engine.IsRunning) { MessageBox.Show("ابتدا VPN را قطع کنید."); return; }
        _profiles.Remove(profile);
        SaveProfiles();
        ClearDetails();
    }

    private async void TestProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _busy || sender is not Button { Tag: VpnProfile profile }) return;
        SetBusy(true, $"در حال پینگ {profile.Name}…");
        try
        {
            profile.LatencyMs = await _engine.MeasureProxyLatencyAsync(profile);
            StatusText.Text = $"پینگ {profile.Name}: {profile.LatencyMs} ms";
            StatusHint.Text = "تأخیر از طریق درخواست HTTPS در مسیر Xray اندازه‌گیری شد.";
        }
        catch (Exception ex)
        {
            profile.LatencyMs = null;
            StatusText.Text = "پینگ ناموفق";
            StatusHint.Text = ex.Message;
            Debug.WriteLine(ex);
        }
        finally
        {
            if (!_isClosing)
            {
                ProfilesList.Items.Refresh();
                if (ProfilesList.SelectedItem == profile) RefreshSelected();
                SaveProfiles();
                SetBusy(false);
            }
        }
    }

    private void DetailLatency_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        TestLatency_Click(sender, e);
    }

    private async void TestLatency_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || ProfilesList.SelectedItem is not VpnProfile profile || _busy) return;
        SetBusy(true, "در حال راه‌اندازی هسته و سنجش تأخیر واقعی…");
        try
        {
            profile.LatencyMs = await _engine.MeasureProxyLatencyAsync(profile);
            DetailLatency.Text = $"{profile.LatencyMs} ms";
            StatusText.Text = "تست تونل موفق بود";
            StatusHint.Text = "زمان درخواست HTTPS از مسیر Xray اندازه‌گیری شد.";
        }
        catch (Exception ex)
        {
            profile.LatencyMs = null;
            DetailLatency.Text = "ناموفق";
            StatusText.Text = "تست کانفیگ انجام نشد";
            StatusHint.Text = ex.Message;
            Debug.WriteLine(ex);
            MessageBox.Show(ex.Message, "TakVPN - تست کانفیگ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_isClosing)
            {
                try { RefreshSelected(); } catch (Exception ex) { Debug.WriteLine(ex); }
                SaveProfiles();
                try { SetBusy(false); } catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _busy || _profiles.Count == 0) return;
        SetBusy(true, "آغاز تست گروهی؛ هر کانفیگ جداگانه از مسیر Xray بررسی می‌شود…");
        var passed = 0;
        try
        {
            foreach (var profile in _profiles)
            {
                StatusText.Text = $"در حال تست: {profile.Name}";
                StatusHint.Text = "در حال برقراری اتصال آزمایشی امن…";
                try
                {
                    profile.LatencyMs = await _engine.MeasureProxyLatencyAsync(profile);
                    passed++;
                }
                catch (Exception ex)
                {
                    profile.LatencyMs = null;
                    Debug.WriteLine($"Group test failed for {profile.Name}: {ex}");
                }
                ProfilesList.Items.Refresh();
                if (ProfilesList.SelectedItem == profile) RefreshSelected();
            }
            StatusText.Text = $"تست گروهی تمام شد: {passed} از {_profiles.Count} موفق";
            StatusHint.Text = "مقادیر، زمان درخواست HTTPS از مسیر Xray هستند؛ نه پینگ ICMP.";
            SaveProfiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Group test failed: {ex}");
            if (!_isClosing)
            {
                StatusText.Text = "تست گروهی متوقف شد";
                StatusHint.Text = ex.Message;
                MessageBox.Show(ex.Message, "TakVPN - تست گروهی", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (!_isClosing)
            {
                try { SetBusy(false); UpdateButtons(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || ProfilesList.SelectedItem is not VpnProfile profile || _busy) return;
        SetBusy(true, "در حال ایجاد تونل امن…");
        try
        {
            if (_engine.IsRunning) { Log("تغییر کانفیگ: توقف " + (_activeProfile?.Name ?? "اتصال قبلی")); await _engine.StopAsync(); }
            Log("در حال اتصال به " + profile.Name + " (" + profile.Protocol + ")");
            await _engine.StartAsync(profile);
            _activeProfile = profile;
            UpdateVpnStatusIcon(true);
            Log("اتصال برقرار شد: " + profile.Name);
            StatusText.Text = "متصل";
            StatusHint.Text = $"از طریق {profile.Name} و Xray/Wintun";
            ResetTrafficStats();
        }
        catch (Exception ex)
        {
            Log("خطای اتصال: " + ex.Message);
            _activeProfile = null;
            UpdateVpnStatusIcon(false);
            StatusText.Text = "اتصال برقرار نشد";
            StatusHint.Text = ex.Message;
            MessageBox.Show(ex.Message, "TakVPN - اتصال", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); UpdateButtons(); }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _busy) return;
        SetBusy(true, "در حال قطع اتصال و بازگرداندن مسیرها…");
        try
        {
            await _engine.StopAsync();
            _activeProfile = null;
            UpdateVpnStatusIcon(false);
            Log("اتصال VPN قطع شد.");
            StatusText.Text = "قطع شده";
            StatusHint.Text = "تونل VPN بسته شد.";
            ResetTrafficStats();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "TakVPN"); }
        finally { SetBusy(false); UpdateButtons(); }
    }

    private void UpdateVpnStatusIcon(bool connected)
    {
        var accent = connected
            ? System.Windows.Media.Color.FromRgb(35, 210, 125)
            : System.Windows.Media.Color.FromRgb(92, 160, 255);
        ConnectionStatusGlow.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(42, accent.R, accent.G, accent.B));
        ConnectionStatusGlow.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(210, accent.R, accent.G, accent.B));
        StatusText.Text = connected ? "متصل" : "قطع";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(accent);
        StatusHint.Text = connected
            ? $"اتصال امن برقرار است • {_activeProfile?.Name ?? "TakVPN"}"
            : "تونل VPN غیرفعال است";
        ConnectionStatusGlow.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = accent, BlurRadius = connected ? 24 : 12,
            ShadowDepth = 0, Opacity = connected ? 0.9 : 0.35
        };
    }

    private void SetBusy(bool busy, string? hint = null)
    {
        if (_isClosing) return;
        _busy = busy;
        if (hint is not null) StatusHint.Text = hint;
        ConnectButton.IsEnabled = !busy && ProfilesList.SelectedItem is VpnProfile && (!_engine.IsRunning || ProfilesList.SelectedItem != _activeProfile);
        DisconnectButton.IsEnabled = !busy && _engine.IsRunning;
        TestButton.IsEnabled = !busy && !_engine.IsRunning && ProfilesList.SelectedItem is VpnProfile;
        TestAllButton.IsEnabled = !busy && !_engine.IsRunning && _profiles.Count > 0;
        ProfilesList.IsEnabled = !busy;
    }

    private void UpdateButtons() => SetBusy(false);

    private void SortByPing_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProfilesList.SelectedItem as VpnProfile;
        var sorted = _profiles.OrderBy(p => p.LatencyMs ?? int.MaxValue).ToList();
        _profiles.Clear();
        foreach (var profile in sorted) _profiles.Add(profile);
        if (selected != null) ProfilesList.SelectedItem = selected;
        Log("فهرست بر اساس پینگ از کم به زیاد مرتب شد.");
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelected();
        UpdateButtons();
    }


    private static string BuildFullConfigDetails(VpnProfile profile)
    {
        var lines = new List<string>
        {
            $"پروتکل: {profile.Protocol}",
            $"سرور: {profile.Host}",
            $"پورت: {profile.Port}"
        };
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add($"{label}: {value}");
        }

        try
        {
            if (profile.Link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var payload = profile.Link["vmess://".Length..].Split('#', '?')[0]
                    .Replace('-', '+').Replace('_', '/');
                payload += new string('=', (4 - payload.Length % 4) % 4);
                using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
                var root = doc.RootElement;
                var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"]="شناسه UUID", ["aid"]="Alter ID", ["scy"]="رمزنگاری",
                    ["net"]="نوع انتقال", ["type"]="نوع هدر", ["host"]="Host / CDN",
                    ["path"]="مسیر", ["tls"]="امنیت TLS", ["sni"]="SNI",
                    ["alpn"]="ALPN", ["fp"]="Fingerprint", ["ps"]="نام کانفیگ",
                    ["v"]="نسخه", ["port"]="پورت", ["add"]="آدرس"
                };
                foreach (var property in root.EnumerateObject())
                    Add(labels.GetValueOrDefault(property.Name, property.Name), property.Value.ToString());
            }
            else if (Uri.TryCreate(profile.Link, UriKind.Absolute, out var uri))
            {
                Add("شناسه / کاربر", Uri.UnescapeDataString(uri.UserInfo));
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["type"]="نوع انتقال", ["security"]="لایه امنیت", ["sni"]="نام سرور (SNI)",
                    ["host"]="Host / CDN", ["path"]="مسیر", ["serviceName"]="نام سرویس gRPC",
                    ["flow"]="Flow", ["encryption"]="رمزنگاری", ["fp"]="Fingerprint",
                    ["alpn"]="ALPN", ["pbk"]="کلید عمومی Reality", ["sid"]="شناسه کوتاه Reality",
                    ["spx"]="مسیر SpiderX", ["mode"]="حالت XHTTP", ["headerType"]="نوع هدر",
                    ["allowInsecure"]="اجازه گواهی ناامن", ["insecure"]="گواهی ناامن",
                    ["obfs"]="Obfuscation", ["obfs-password"]="رمز Obfuscation",
                    [" pinSHA256"]="اثر انگشت گواهی"
                };
                foreach (var key in query.AllKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
                    Add(labels.GetValueOrDefault(key!, key!), query[key!]);
                Add("نام کانفیگ", Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));
                if (uri.Scheme.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("hy2", StringComparison.OrdinalIgnoreCase))
                    Add("نوع پروتکل", "Hysteria 2 / مبتنی بر QUIC");
                if (query["security"]?.Equals("reality", StringComparison.OrdinalIgnoreCase) == true)
                    Add("نوع امنیت", "Reality");
                else if (query["security"]?.Equals("tls", StringComparison.OrdinalIgnoreCase) == true)
                    Add("نوع امنیت", "TLS");
                if (!string.IsNullOrWhiteSpace(query["host"]))
                    Add("نوع مسیر", "دارای Host؛ قابل استفاده برای CDN یا دامنه واسط");
            }
            else
            {
                Add("لینک خام", profile.Link);
            }
        }
        catch (Exception ex)
        {
            Add("توضیح", "تجزیه بعضی پارامترها ممکن نشد: " + ex.Message);
            Add("لینک کامل", profile.Link);
        }

        Add("لینک کامل کانفیگ", profile.Link);
        return string.Join(Environment.NewLine, lines);
    }

    private void RefreshSelected()
    {
        if (ProfilesList.SelectedItem is not VpnProfile p) { ClearDetails(); return; }
        DetailName.Text = p.Name;
        DetailProtocol.Text = p.Protocol;
        DetailHost.Text = p.Host;
        DetailPort.Text = p.Port.ToString();
        DetailFullConfig.Text = BuildFullConfigDetails(p);
        DetailLatency.Text = p.LatencyMs is null ? "اندازه‌گیری نشده" : $"{p.LatencyMs} ms";
        DetailServerLocation.Text = p.Host + "  •  در حال شناسایی IP و کشور";
        _ = UpdateServerLocationAsync(p);
        ProfilesList.Items.Refresh();
    }

    private void ResetTrafficStats()
    {
        _lastSent = _lastReceived = _baseSent = _baseReceived = 0;
        _lastTrafficSample = DateTime.MinValue;
        if (_engine.IsRunning)
        {
            try
            {
                var nic = FindVpnAdapter();
                if (nic != null) { var s = nic.GetIPv4Statistics(); _baseSent = s.BytesSent; _baseReceived = s.BytesReceived; }
            }
            catch (Exception ex) { Debug.WriteLine("Traffic baseline failed: " + ex); }
        }
        DetailSendTotal.Text = DetailReceiveTotal.Text = "0 B";
        DetailTotalTraffic.Text = "مجموع مصرف: 0 B";
        DetailSendRate.Text = DetailReceiveRate.Text = "0 B/s";
    }

    private void UpdateTrafficStats()
    {
        if (_isClosing || !_engine.IsRunning) { ResetTrafficStats(); return; }
        try
        {
            var adapter = FindVpnAdapter();
            if (adapter is null) return;
            var stats = adapter.GetIPv4Statistics();
            long sent = Math.Max(0, stats.BytesSent - _baseSent), received = Math.Max(0, stats.BytesReceived - _baseReceived);
            var now = DateTime.UtcNow;
            if (_lastTrafficSample != DateTime.MinValue)
            {
                var seconds = (now - _lastTrafficSample).TotalSeconds;
                if (seconds > 0)
                {
                    DetailSendRate.Text = FormatBytes(Math.Max(0, sent - _lastSent) / seconds) + "/s";
                    DetailReceiveRate.Text = FormatBytes(Math.Max(0, received - _lastReceived) / seconds) + "/s";
                }
            }
            DetailSendTotal.Text = FormatBytes(sent);
            DetailReceiveTotal.Text = FormatBytes(received);
            DetailTotalTraffic.Text = "مجموع مصرف: " + FormatBytes(sent + received);
            _lastSent = sent; _lastReceived = received; _lastTrafficSample = now;
        }
        catch (Exception ex) { Debug.WriteLine("Traffic stats read failed: " + ex); }
    }

    private static NetworkInterface? FindVpnAdapter()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        return adapters.FirstOrDefault(n =>
            n.Name.Contains("TakVPN", StringComparison.OrdinalIgnoreCase) ||
            n.Description.Contains("TakVPN", StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(n =>
                n.NetworkInterfaceType == NetworkInterfaceType.Tunnel &&
                n.OperationalStatus == OperationalStatus.Up);
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.##} {units[unit]}";
    }

    private void RefreshEngineLogs()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "xray-startup.log");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path).TakeLast(120))
                if (_shownLogLines.Add(line)) LogsBox.AppendText(line + Environment.NewLine);
            LogsBox.ScrollToEnd();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void Log(string message)
    {
        var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(message)); return; }
        LogsBox.AppendText(line + Environment.NewLine);
        LogsBox.ScrollToEnd();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "app.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async Task UpdateServerLocationAsync(VpnProfile profile)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(profile.Host);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString()
                ?? addresses.FirstOrDefault()?.ToString() ?? profile.Host;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var json = await client.GetStringAsync("https://ipwho.is/" + Uri.EscapeDataString(ip));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var code = root.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
            var flag = string.IsNullOrWhiteSpace(code) ? "" : string.Concat(code.ToUpperInvariant().Select(ch => char.ConvertFromUtf32(ch + 127397)));
            if (ProfilesList.SelectedItem == profile) DetailServerLocation.Text = flag + "  " + ip + "  •  " + (country ?? "کشور نامشخص");
        }
        catch (Exception ex)
        {
            if (ProfilesList.SelectedItem == profile) DetailServerLocation.Text = profile.Host + "  •  شناسایی کشور ناموفق";
            Log("شناسایی IP/کشور: " + ex.Message);
        }
    }

    private void ClearDetails()
    {
        DetailName.Text = DetailProtocol.Text = DetailHost.Text = DetailPort.Text = "—";
        DetailFullConfig.Text = "—";
        DetailLatency.Text = "اندازه‌گیری نشده";
        DetailServerLocation.Text = "—";
        DetailTotalTraffic.Text = "مجموع مصرف: 0 B";
    }

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var data = JsonSerializer.Deserialize<List<VpnProfile>>(File.ReadAllText(_storePath));
            if (data is not null) foreach (var p in data) _profiles.Add(p);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(_profiles));
        }
        catch (Exception ex) { MessageBox.Show($"ذخیره کانفیگ‌ها ناموفق بود: {ex.Message}", "TakVPN"); }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _trafficTimer.Stop();
        base.OnClosed(e);
        try { await _engine.StopAsync(); }
        catch (Exception ex) { Debug.WriteLine($"VPN shutdown cleanup failed: {ex}"); }
    }
}

public sealed class VpnProfile
{
    public string Name { get; set; } = "کانفیگ بدون نام";
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Link { get; set; } = "";
    public int? LatencyMs { get; set; }
    public string LatencyLabel => LatencyMs is null ? "—" : $"{LatencyMs} ms";
    public string ProtocolDisplay => Protocol.ToUpperInvariant();
    public string CompactLine => (LatencyMs is null ? "—" : $"{LatencyMs} ms") + "   " + Host + ":" + Port;

    public static bool TryParse(string link, out VpnProfile? profile, out string error)
    {
        profile = null;
        error = "فرمت لینک پشتیبانی نمی‌شود. فعلاً VLESS، VMess و Trojan قابل افزودن هستند.";
        try
        {
            var protocol = link[..Math.Max(0, link.IndexOf("://", StringComparison.Ordinal))].ToLowerInvariant();
            if (protocol is not ("vless" or "vmess" or "trojan" or "hysteria2" or "hy2")) return false;
            string host;
            int port = 443;
            string name = "";
            if (protocol == "vmess")
            {
                var payload = link["vmess://".Length..].Split('#', '?')[0].Replace('-', '+').Replace('_', '/');
                payload += new string('=', (4 - payload.Length % 4) % 4);
                using var doc = JsonDocument.Parse(System.Convert.FromBase64String(payload));
                var root = doc.RootElement;
                host = root.GetProperty("add").GetString() ?? "";
                if (root.TryGetProperty("port", out var p) && int.TryParse(p.ToString(), out var parsedPort)) port = parsedPort;
                if (root.TryGetProperty("ps", out var ps)) name = ps.GetString() ?? "";
            }
            else
            {
                var uri = new Uri(link);
                host = uri.Host;
                if (!uri.IsDefaultPort) port = uri.Port;
                name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            }
            if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return false;
            profile = new VpnProfile { Name = string.IsNullOrWhiteSpace(name) ? host : name, Protocol = protocol.ToUpperInvariant(), Host = host, Port = port, Link = link };
            error = "";
            return true;
        }
        catch (Exception ex) { error = $"لینک معتبر نیست: {ex.Message}"; return false; }
    }
}