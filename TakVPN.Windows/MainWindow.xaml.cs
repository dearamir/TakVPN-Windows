using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace TakVPN.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VpnProfile> _profiles = new();
    private readonly string _storePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TakVPN", "profiles.json");
    private readonly XrayEngine _engine = new();
    private bool _busy;
    private bool _isClosing;
    private readonly System.Windows.Threading.DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastSent, _lastReceived, _baseSent, _baseReceived;
    private DateTime _lastTrafficSample;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        ProfilesList.ItemsSource = _profiles;
        LoadProfiles();
        UpdateButtons();
        _trafficTimer.Tick += (_, _) => UpdateTrafficStats();
        _trafficTimer.Start();
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
            var link = ConfigInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(link))
            {
                MessageBox.Show("ابتدا لینک کانفیگ VLESS را وارد کنید.", "TakVPN", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!VpnProfile.TryParse(link, out var profile, out var error) || profile is null)
            {
                MessageBox.Show(error, "TakVPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_profiles.Any(p => string.Equals(p.Link, link, StringComparison.Ordinal)))
            {
                MessageBox.Show("این کانفیگ قبلاً اضافه شده است.", "TakVPN", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _profiles.Add(profile);
            ProfilesList.SelectedItem = profile;
            ConfigInput.Clear();
            SaveProfiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Add VLESS config failed: {ex}");
            MessageBox.Show(
                $"افزودن کانفیگ انجام نشد؛ برنامه نباید بسته شود.\n{ex.Message}",
                "TakVPN - خطای افزودن کانفیگ",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            await _engine.StartAsync(profile);
            StatusText.Text = "متصل";
            StatusHint.Text = $"از طریق {profile.Name} و Xray/Wintun";
            ResetTrafficStats();
        }
        catch (Exception ex)
        {
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
            StatusText.Text = "قطع شده";
            StatusHint.Text = "تونل VPN بسته شد.";
            ResetTrafficStats();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "TakVPN"); }
        finally { SetBusy(false); UpdateButtons(); }
    }

    private void SetBusy(bool busy, string? hint = null)
    {
        if (_isClosing) return;
        _busy = busy;
        if (hint is not null) StatusHint.Text = hint;
        ConnectButton.IsEnabled = !busy && !_engine.IsRunning && ProfilesList.SelectedItem is VpnProfile;
        DisconnectButton.IsEnabled = !busy && _engine.IsRunning;
        TestButton.IsEnabled = !busy && !_engine.IsRunning && ProfilesList.SelectedItem is VpnProfile;
        TestAllButton.IsEnabled = !busy && !_engine.IsRunning && _profiles.Count > 0;
        ProfilesList.IsEnabled = !busy;
    }

    private void UpdateButtons() => SetBusy(false);

    private void SortProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || SortProfilesBox.SelectedIndex < 0 || _profiles.Count < 2) return;
        var selected = ProfilesList.SelectedItem as VpnProfile;
        IEnumerable<VpnProfile> ordered = SortProfilesBox.SelectedIndex switch
        {
            1 => _profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => _profiles.OrderBy(p => p.LatencyMs ?? int.MaxValue),
            3 => _profiles.OrderByDescending(p => p.LatencyMs ?? int.MinValue),
            _ => _profiles.ToList()
        };
        var snapshot = ordered.ToList();
        _profiles.Clear();
        foreach (var profile in snapshot) _profiles.Add(profile);
        if (selected != null) ProfilesList.SelectedItem = selected;
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelected();
        UpdateButtons();
    }

    private void RefreshSelected()
    {
        if (ProfilesList.SelectedItem is not VpnProfile p) { ClearDetails(); return; }
        DetailName.Text = p.Name;
        DetailProtocol.Text = p.Protocol;
        DetailHost.Text = p.Host;
        DetailPort.Text = p.Port.ToString();
        DetailLatency.Text = p.LatencyMs is null ? "اندازه‌گیری نشده" : $"{p.LatencyMs} ms";
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
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Contains("TakVPN", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("TakVPN", StringComparison.OrdinalIgnoreCase));
                if (nic != null) { var s = nic.GetIPv4Statistics(); _baseSent = s.BytesSent; _baseReceived = s.BytesReceived; }
            }
            catch (Exception ex) { Debug.WriteLine("Traffic baseline failed: " + ex); }
        }
        DetailSendTotal.Text = DetailReceiveTotal.Text = "0 B";
        DetailSendRate.Text = DetailReceiveRate.Text = "0 B/s";
    }

    private void UpdateTrafficStats()
    {
        if (_isClosing || !_engine.IsRunning) { ResetTrafficStats(); return; }
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Contains("TakVPN", StringComparison.OrdinalIgnoreCase)
                    || n.Description.Contains("TakVPN", StringComparison.OrdinalIgnoreCase));
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
            _lastSent = sent; _lastReceived = received; _lastTrafficSample = now;
        }
        catch (Exception ex) { Debug.WriteLine("Traffic stats read failed: " + ex); }
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.##} {units[unit]}";
    }

    private void ClearDetails()
    {
        DetailName.Text = DetailProtocol.Text = DetailHost.Text = DetailPort.Text = "—";
        DetailLatency.Text = "اندازه‌گیری نشده";
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
            if (protocol is not ("vless" or "vmess" or "trojan")) return false;
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