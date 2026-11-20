using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TakVPN.Windows;

internal static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/dearamir/TakVPN-Windows/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    internal static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.1.35";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TakVPN-Windows-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal sealed record UpdateInfo(string Version, string DownloadUrl, string Sha256, string Notes);

    internal static async Task<UpdateInfo?> CheckAsync()
    {
        using var response = await Client.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("نسخه انتشار مشخص نیست.");
        const string prefix = "windows-v";
        if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(tag[prefix.Length..], out var latest))
            throw new InvalidDataException("قالب نسخه انتشار معتبر نیست.");

        if (!Version.TryParse(CurrentVersion, out var current))
            throw new InvalidDataException("نسخه فعلی برنامه معتبر نیست.");
        if (latest <= current) return null;

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), "TakVPN-Setup-x64.exe", StringComparison.Ordinal));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("فایل نصب نسخه جدید در انتشار GitHub پیدا نشد.");

        var downloadUrl = asset.GetProperty("browser_download_url").GetString();
        var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(downloadUrl) || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("آدرس دانلود معتبر و امن نیست.");
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("هش SHA-256 فایل نصب در اطلاعات انتشار موجود نیست.");

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(latest.ToString(3), downloadUrl, digest["sha256:".Length..].ToLowerInvariant(), notes);
    }

    internal static async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TakVPN", "updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"TakVPN-Setup-{update.Version}.exe");
        using var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total is > 0) progress?.Report(Math.Min(100, received * 100d / total.Value));
            }
            await destination.FlushAsync();
        }

        await using var file = File.OpenRead(path);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actualHash), Convert.FromHexString(update.Sha256)))
        {
            File.Delete(path);
            throw new InvalidDataException("تأیید صحت فایل نصب ناموفق بود؛ فایل دانلودشده حذف شد.");
        }
        progress?.Report(100);
        return path;
    }

    internal static void StartInstaller(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("فایل نصب پیدا نشد.", installerPath);
        var start = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            Verb = "runas"
        };
        start.ArgumentList.Add("/VERYSILENT");
        start.ArgumentList.Add("/SUPPRESSMSGBOXES");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/CLOSEAPPLICATIONS");
        start.ArgumentList.Add("/RESTARTAPPLICATIONS");
        Process.Start(start) ?? throw new InvalidOperationException("اجرای نصب‌کننده ناموفق بود.");
    }
}
