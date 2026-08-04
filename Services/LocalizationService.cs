using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ScCestinator.Services;

public sealed class LocalizationService
{
    private static readonly HttpClient _httpClient = HttpClientFactory.GetSharedClient();

    public async Task<string?> ReadLocalVersionAsync(string globalIniPath)
    {
        if (string.IsNullOrWhiteSpace(globalIniPath))
            return null;

        if (!File.Exists(globalIniPath))
            return null;

        await using var stream = File.OpenRead(globalIniPath);
        using var reader = new StreamReader(stream);

        var firstLine = await reader.ReadLineAsync();

        return VersionParser.ParseVersionFromFirstLine(firstLine);
    }

    public async Task InstallOrUpdateAsync(string livePath, bool createBackup, Action<string>? progressCallback = null)
    {
        var dataPath = Path.Combine(livePath, "data");
        var localizationPath = Path.Combine(dataPath, "Localization");
        var englishPath = Path.Combine(localizationPath, "english");
        var globalIniPath = Path.Combine(englishPath, "global.ini");

        // 🔹 vytvoření složek
        progressCallback?.Invoke("Připravuji složky...");
        Directory.CreateDirectory(englishPath);

        // 🔹 záloha
        if (createBackup && Directory.Exists(localizationPath))
        {
            progressCallback?.Invoke("Vytvářím zálohu původního souboru...");
            var backupPath = localizationPath + ".bak";

            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, true);

            DirectoryCopy(localizationPath, backupPath);
        }

        // 🔹 stažení souboru
        progressCallback?.Invoke("Stahuji češtinu z GitHubu...");
        var contentBytes = await _httpClient.GetByteArrayAsync(Constants.GitHubLocalizationUrl);

        var temporaryPath = Path.Combine(englishPath, $"global.ini.{Guid.NewGuid():N}.tmp");

        try
        {
            // 🔹 bezpečné uložení a nahrazení
            progressCallback?.Invoke("Instaluji novou verzi...");
            await File.WriteAllBytesAsync(temporaryPath, contentBytes);

            if (new FileInfo(temporaryPath).Length == 0)
                throw new InvalidDataException("Stažený soubor lokalizace je prázdný.");

            File.Move(temporaryPath, globalIniPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task UninstallAsync(string livePath, Action<string>? progressCallback = null)
    {
        var dataPath = Path.Combine(livePath, "data");
        var localizationPath = Path.Combine(dataPath, "Localization");
        var englishPath = Path.Combine(localizationPath, "english");
        var globalIniPath = Path.Combine(englishPath, "global.ini");

        // Check if file exists
        if (!File.Exists(globalIniPath))
        {
            throw new FileNotFoundException("Soubor global.ini nebyl nalezen");
        }

        // Create backup with timestamp
        progressCallback?.Invoke("Vytvářím zálohu...");
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
        var backupPath = $"{globalIniPath}.backup-{timestamp}";
        File.Copy(globalIniPath, backupPath, true);

        // Delete the file
        progressCallback?.Invoke("Odstraňuji češtinu...");
        File.Delete(globalIniPath);
    }

    private void DirectoryCopy(string sourceDir, string destDir)
    {
        var dir = new DirectoryInfo(sourceDir);

        Directory.CreateDirectory(destDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            var newDestDir = Path.Combine(destDir, subDir.Name);
            DirectoryCopy(subDir.FullName, newDestDir);
        }
    }
}
