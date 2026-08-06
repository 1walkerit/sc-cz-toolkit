using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ScCestinator.Services;

public record AppUpdateResult(
    bool IsUpdateAvailable,
    string LatestVersion,
    string StatusMessage,
    string? DownloadUrl = null);

public class AppUpdateService
{
    private readonly GitHubService _gitHubService = new();

    public async Task<AppUpdateResult> CheckForUpdateAsync(string currentVersion)
    {
        try
        {
            var latestAppVersion = await _gitHubService.GetLatestAppVersionAsync();

            if (Version.TryParse(latestAppVersion, out var latestVersion) &&
                Version.TryParse(currentVersion, out var installedVersion) &&
                latestVersion > installedVersion)
            {
                return new AppUpdateResult(
                    IsUpdateAvailable: true,
                    LatestVersion: latestAppVersion,
                    StatusMessage: $"⚠ Je dostupná nová verze SC CZ Toolkit ({currentVersion} → {latestAppVersion})");
            }

            return new AppUpdateResult(
                IsUpdateAvailable: false,
                LatestVersion: latestAppVersion ?? string.Empty,
                StatusMessage: string.Empty);
        }
        catch
        {
            // Nechceme rozbíjet start aplikace kvůli kontrole aktualizace
            return new AppUpdateResult(
                IsUpdateAvailable: false,
                LatestVersion: string.Empty,
                StatusMessage: string.Empty);
        }
    }

    public async Task<string> DownloadLatestAppImageAsync(IProgress<double> progress)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ScCestinator");

        var json = await client.GetStringAsync(Constants.GitHubLatestReleaseApi);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var assets = doc.RootElement.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name != null && name.EndsWith(".AppImage"))
            {
                var url = asset.GetProperty("browser_download_url").GetString();

                var downloadsDir = GetDownloadFolder();
                Directory.CreateDirectory(downloadsDir);

                var downloadPath = Path.Combine(downloadsDir, name);

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                var canReport = total != -1;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[8192];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;

                    if (canReport)
                    {
                        progress.Report((double)totalRead / total * 100);
                    }
                }

                return downloadPath;
            }
        }

        throw new InvalidOperationException("AppImage nebyl nalezen.");
    }

    private string GetDownloadFolder()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-user-dir",
                Arguments = "DOWNLOAD",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            var result = process?.StandardOutput.ReadToEnd().Trim();

            if (!string.IsNullOrWhiteSpace(result) && Directory.Exists(result))
                return result;
        }
        catch
        {
        }

        // fallback
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(home, "SC-Cestinator-downloads");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
