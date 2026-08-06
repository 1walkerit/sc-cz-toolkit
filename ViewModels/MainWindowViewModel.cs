using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using ScCestinator.Services;
using ScCestinator.Views;

namespace ScCestinator.ViewModels;


public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ShaderCacheService _shaderService = new();
    private readonly PathService _pathService = new();
    private readonly LocalizationService _localizationService = new();
    private readonly GitHubService _gitHubService = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly IFolderPickerService _folderPickerService;
    private readonly IConfirmationDialogService _confirmationDialogService;
    private readonly SettingsService _settingsService = new();

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand BrowseFolderCommand { get; }
    public ICommand FindInstallationCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand DownloadLatestVersionCommand { get; }
    public ICommand OpenDownloadFolderCommand { get; }

    public ICommand OpenGameFolderCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand ClearShadersCommand { get; }
    public ICommand OpenShaderCacheCommand { get; }
    public ICommand ShowCreditsCommand { get; }

    public string AppVersion { get; }
    public bool IsRunningInFlatpak =>
        File.Exists("/.flatpak-info");
    private string _appUpdateStatus = "";
    public string AppUpdateStatus
    {
        get => _appUpdateStatus;
        set
        {
            _appUpdateStatus = value;
            OnPropertyChanged();
        }
    }


    public MainWindowViewModel(
        IFolderPickerService folderPickerService,
        IConfirmationDialogService confirmationDialogService)
    {
        _folderPickerService = folderPickerService;
        _confirmationDialogService = confirmationDialogService;

        // Get app version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersion = $"SC CZ Toolkit v{version?.Major}.{version?.Minor}.{version?.Build ?? 0}";


        InstallCommand = new AsyncRelayCommand(
            execute: InstallAsync,
            canExecute: () => IsUpdateAvailable && !IsBusy
        );

        UninstallCommand = new AsyncRelayCommand(
            execute: UninstallAsync,
            canExecute: () => LocalVersion != "-" && LocalVersion != "nenainstalováno" && !IsBusy
        );

        BrowseFolderCommand = new AsyncRelayCommand(
            execute: BrowseFolderAsync,
            canExecute: () => !IsBusy
        );

        FindInstallationCommand = new AsyncRelayCommand(FindInstallationAsync);

        OpenUrlCommand = new RelayCommand(param =>
        {
            try
            {
                if (param is not string url || string.IsNullOrWhiteSpace(url))
                    return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                Status = "Nepodařilo se otevřít odkaz.";
            }
        });
        DownloadLatestVersionCommand = new AsyncRelayCommand(DownloadLatestVersionAsync);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);

        OpenGameFolderCommand = new RelayCommand(OpenGameFolder);
        ClearLogsCommand = new AsyncRelayCommand(ClearLogsAsync);
        ClearShadersCommand = new AsyncRelayCommand(ClearShadersAsync);
        OpenShaderCacheCommand = new RelayCommand(OpenShaderCache);
        ShowCreditsCommand = new RelayCommand(async _ => await ShowCreditsAsync());

        // Load last used path
        var settings = _settingsService.LoadSettings();

        if (!string.IsNullOrWhiteSpace(settings.LastUsedPath))
        {
            var savedPathResult = _pathService.ValidateStarCitizenPath(settings.LastUsedPath);

            if (savedPathResult.IsValidLivePath)
            {
                InputPath = settings.LastUsedPath;
            }
            else
            {
                _ = FindInstallationAsync();
            }
        }
        else
        {
            _ = FindInstallationAsync();
        }

        // Check for app update automatically on startup
        _ = CheckAppUpdateAsync();


    }

    private string? _inputPath;
    private bool _suppressBranchValidation;
    private List<string> _availableBranches = new() { "LIVE" };
    private string _selectedBranch = "LIVE";

    public IReadOnlyList<string> AvailableBranches => _availableBranches;

    public bool HasMultipleBranches => _availableBranches.Count > 1;

    public string SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "LIVE" : value;
            if (string.Equals(_selectedBranch, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedBranch = normalized;
            OnPropertyChanged();

            if (!_suppressBranchValidation)
            {
                _ = ValidatePathAsync(refreshBranches: false);
            }
        }
    }

    public string? InputPath
    {
        get => _inputPath;
        set
        {
            _inputPath = value;
            OnPropertyChanged();

            // Fire and forget with proper error handling
            _ = ValidatePathAsync();
        }
    }

    private string _status = "Zadej cestu ke Star Citizen";
    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            UpdateStatusBrush();
        }
    }

    private IBrush _statusBrush = Brushes.Black;
    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set
        {
            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    private string _localVersion = "-";
    public string LocalVersion
    {
        get => _localVersion;
        set
        {
            _localVersion = value;
            OnPropertyChanged();
            UninstallCommand?.RaiseCanExecuteChanged();
        }
    }

    private string _onlineVersion = "-";
    public string OnlineVersion
    {
        get => _onlineVersion;
        set
        {
            _onlineVersion = value;
            OnPropertyChanged();
        }
    }


    private bool _isAppUpdateAvailable;

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            _downloadProgress = value;
            OnPropertyChanged();
        }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            _isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDownloadButton));
            OnPropertyChanged(nameof(ShowCurrentVersionInfo));
        }
    }



    private string _lastDownloadFolder = "";
    public string LastDownloadFolder
    {
        get => _lastDownloadFolder;
        set
        {
            _lastDownloadFolder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanOpenDownloadFolder));
        }
    }

    public bool CanOpenDownloadFolder => !string.IsNullOrWhiteSpace(LastDownloadFolder) && Directory.Exists(LastDownloadFolder);


    public bool ShowDownloadButton => IsAppUpdateAvailable && !IsDownloading;
    public bool ShowCurrentVersionInfo => !IsAppUpdateAvailable && !IsDownloading;

    public bool IsAppUpdateAvailable
    {
        get => _isAppUpdateAvailable;
        set
        {
            _isAppUpdateAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDownloadButton));
            OnPropertyChanged(nameof(ShowCurrentVersionInfo));
        }

    }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            _isUpdateAvailable = value;
            OnPropertyChanged();
            InstallCommand?.RaiseCanExecuteChanged();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            InstallCommand?.RaiseCanExecuteChanged();
            UninstallCommand?.RaiseCanExecuteChanged();
            BrowseFolderCommand?.RaiseCanExecuteChanged();
        }
    }








    private void OpenGameFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(InputPath) || !Directory.Exists(InputPath))
            {
                Status = "Vybraná cesta není platná instalace Star Citizen.";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = InputPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Status = $"Nepodařilo se otevřít složku: {ex.Message}";
        }
    }



    private async Task ClearLogsAsync()
    {
        if (!await _confirmationDialogService.ConfirmAsync("Potvrzení mazání", "Opravdu chcete vymazat logy Star Citizen?", "Ano", "Ne"))
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(InputPath) || !Directory.Exists(InputPath))
            {
                Status = "Neplatná cesta ke hře.";
                return;
            }

            var subDirs = new[] { "LIVE", "PTU", "EPTU" };

            int totalDeleted = 0;
            int foldersProcessed = 0;

            foreach (var sub in subDirs)
            {
                var dir = Path.Combine(InputPath, sub);

                if (!Directory.Exists(dir))
                {
                    Console.WriteLine($"[Logs] Nenalezeno: {dir}");
                    continue;
                }

                foldersProcessed++;

                var logFiles = Directory.GetFiles(dir, "*.log", SearchOption.TopDirectoryOnly);

                foreach (var file in logFiles)
                {
                    try
                    {
                        File.Delete(file);
                        totalDeleted++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Logs] Chyba mazání {file}: {ex.Message}");
                    }
                }
            }

            Status = $"Logy vyčištěny ({foldersProcessed} složky)";
            await _confirmationDialogService.ShowInfoAsync(
               "Hotovo",
               "Logy byly úspěšně vyčištěny."
           );
            Console.WriteLine($"[Logs] Smazáno souborů: {totalDeleted}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Status = "Chyba při mazání logů";
        }
    }





    private async Task ClearShadersAsync()
    {
        if (!await _confirmationDialogService.ConfirmAsync(
                "Potvrzení mazání",
                "Vymazání shader cache může pomoci vyřešit grafické chyby nebo pády hry.\n\n" +
                "Budou vyčištěny nalezené cache složky:\n" +
                "• Mesa shader cache\n" +
                "• NVIDIA shader cache\n" +
                "• Wine prefix shader cache pro SC\n\n" +
                "Hra si shader cache při dalším spuštění znovu vytvoří.\n\n" +
                "Pokračovat?",
                "Ano",
                "Ne")) return;

        try
        {
            var totalDeleted = _shaderService.ClearShaders(InputPath);

            Status = $"Shader cache vyčištěna ({totalDeleted} souborů)";
            await _confirmationDialogService.ShowInfoAsync(
                "Hotovo",
                "Shader cache byla úspěšně vyčištěna."
            );

            Console.WriteLine($"[Shaders] Smazáno souborů: {totalDeleted}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Status = "Chyba při mazání shaderů";
        }
    }


    private void OpenShaderCache()
    {
        try
        {
            var shaderPaths = _shaderService.GetExistingShaderPaths(InputPath);


            var opened = 0;

            foreach (var path in shaderPaths)
            {
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        ArgumentList = { path },
                        UseShellExecute = false
                    });
                    opened++;
                }
            }

            Status = opened > 0
                ? $"Otevřeno shader cache ({opened} složky)"
                : "Shader cache nebyla nalezena.";
        }
        catch (Exception ex)
        {
            Status = $"Chyba: {ex.Message}";
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LastDownloadFolder) || !Directory.Exists(LastDownloadFolder))
            {
                Status = "Složka se staženým souborem neexistuje.";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = LastDownloadFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Status = $"Nepodařilo se otevřít složku: {ex.Message}";
        }
    }

    private async Task ShowCreditsAsync()
    {
        var desktop = App.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        if (desktop?.MainWindow == null)
        {
            await _confirmationDialogService.ShowInfoAsync(
                "Licence / Autoři",
                "SC CZ Toolkit – Linux verze\n\n" +
                "Tato aplikace není oficiálním nástrojem týmu Cestinator.\n\n" +
                "Česká lokalizace hry Star Citizen je dílem týmu Cestinator.\n" +
                "https://github.com/cestinator\n\n" +
                "Aplikace neodesílá žádná uživatelská data."
            );
            return;
        }

        var dialog = new CreditsDialog();
        await dialog.ShowDialog(desktop.MainWindow);
    }

    public async Task DownloadLatestVersionAsync()
    {
        try
        {
            Status = "Stahuji novou verzi...";

            IsDownloading = true;
            DownloadProgress = 0;

            var progress = new Progress<double>(value => DownloadProgress = value);
            var downloadPath = await _appUpdateService.DownloadLatestAppImageAsync(progress);

            LastDownloadFolder = Path.GetDirectoryName(downloadPath) ?? string.Empty;
            Status = $"Staženo: {downloadPath}";
            await _confirmationDialogService.ShowInfoAsync(
                "Staženo",
                "Nová verze aplikace byla úspěšně stažena."
            );

            try
            {
                var folder = Path.GetDirectoryName(downloadPath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        ArgumentList = { folder },
                        UseShellExecute = false
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Update] Nelze otevřít složku: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Status = ex is InvalidOperationException
                ? "AppImage nebyl nalezen."
                : $"Chyba při stahování: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task CheckAppUpdateAsync()
    {
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var currentVersion = $"{appVersion?.Major}.{appVersion?.Minor}.{appVersion?.Build ?? 0}";

        var updateResult = await _appUpdateService.CheckForUpdateAsync(currentVersion);
        AppUpdateStatus = updateResult.StatusMessage;
        IsAppUpdateAvailable = updateResult.IsUpdateAvailable;
    }

    private async Task BrowseFolderAsync()
    {
        try
        {
            Status = "Otevírám dialog...";

            var selectedPath = await _folderPickerService.PickFolderAsync("Vyberte složku Star Citizen");

            if (selectedPath != null)
            {
                InputPath = selectedPath;

                // Save path when selected via folder picker
                _settingsService.SaveSettings(new AppSettings { LastUsedPath = selectedPath });
            }
            else
            {
                Status = "Výběr složky byl zrušen";
            }
        }
        catch (Exception ex)
        {
            Status = $"Chyba: {ex.Message}";
        }
    }

    private async Task FindInstallationAsync()
    {
        var installs = await new InstallationService().FindStarCitizenInstallationsAsync();

        if (installs.Count == 0)
        {
            var message =
                "Instalace Star Citizen nebyla nalezena.\n\n" +
                "Vyber cestu ručně.";

            if (IsRunningInFlatpak)
            {
                message +=
                    "\n\n" +
"Pokud používáš Flatpak a máš hru mimo domovský adresář,\n" +
"je potřeba povolit přístup k danému umístění.\n\n" +
"Příklad:\n\n" +
                    "flatpak override --user --filesystem=/home/data com.sccommunity.SCCZToolkit";
            }

            await _confirmationDialogService.ShowInfoAsync(
                "Nenalezeno",
                message
            );

            return;
        }

        if (installs.Count == 1)
        {
            InputPath = installs[0];
            Status = $"Použita instalace: {InputPath}";
            await ValidatePathAsync();
            return;
        }

        foreach (var i in installs)
        {
            Console.WriteLine($"[SC] Candidate: {i}");
        }

        var dialog = new InstallationSelectionDialog(installs);

        var desktop = App.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        if (desktop?.MainWindow == null)
        {
            Status = "Nelze otevřít dialog výběru instalace.";
            return;
        }

        var selectedPath = await dialog.ShowDialog<string?>(desktop.MainWindow);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            Status = $"Nalezeno více instalací ({installs.Count}), výběr zrušen.";
            return;
        }

        InputPath = selectedPath;
        Status = $"Použita instalace: {InputPath}";
        await ValidatePathAsync();
    }

    private async Task ValidatePathAsync(bool refreshBranches = true)
    {
        try
        {
            if (refreshBranches)
            {
                var detectedBranches = _pathService.DetectExistingBranches(InputPath).ToList();

                if (detectedBranches.Count == 0)
                {
                    detectedBranches.Add("LIVE");
                }

                _availableBranches = detectedBranches;
                OnPropertyChanged(nameof(AvailableBranches));
                OnPropertyChanged(nameof(HasMultipleBranches));
            }

            var branchToUse = SelectedBranch;
            if (!_availableBranches.Any(x => string.Equals(x, branchToUse, StringComparison.OrdinalIgnoreCase)))
            {
                branchToUse = _availableBranches.FirstOrDefault(x => string.Equals(x, "LIVE", StringComparison.OrdinalIgnoreCase))
                              ?? _availableBranches[0];

                _suppressBranchValidation = true;
                SelectedBranch = branchToUse;
                _suppressBranchValidation = false;
            }

            var result = _pathService.ValidateStarCitizenPath(InputPath, branchToUse);

            LocalVersion = "-";
            OnlineVersion = "-";
            AppUpdateStatus = "";
            IsAppUpdateAvailable = false;
            IsUpdateAvailable = false;

            if (result.LivePath == null)
            {
                Status = "Vybraná cesta není platná.";
                return;
            }

            if (!result.LiveExists)
            {
                Status = $"LIVE složka neexistuje: {result.LivePath}";
                return;
            }

            if (!result.DataP4kExists)
            {
                Status = $"Chybí soubor Data.p4k ve větvi {result.BranchName}";
                return;
            }

            if (result.GlobalIniExists)
            {
                var local = await _localizationService.ReadLocalVersionAsync(result.GlobalIniPath);
                LocalVersion = local ?? "neznámá";
            }
            else
            {
                LocalVersion = "nenainstalováno";
            }

            var online = await _gitHubService.GetOnlineVersionAsync();
            OnlineVersion = online ?? "neznámá";
            var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
            var currentVersion = $"{appVersion?.Major}.{appVersion?.Minor}.{appVersion?.Build ?? 0}";


            if (LocalVersion == "nenainstalováno")
            {
                Status = "Čeština zatím není nainstalovaná.";
                IsUpdateAvailable = true;
            }
            else if (LocalVersion == "neznámá" || OnlineVersion == "neznámá")
            {
                Status = "Verzi češtiny se nepodařilo ověřit.";
                IsUpdateAvailable = false;
            }
            else if (LocalVersion != OnlineVersion)
            {
                Status = $"Je dostupná nová verze češtiny ({LocalVersion} → {OnlineVersion}).";
                IsUpdateAvailable = true;
            }
            else
            {
                Status = "Instalace Star Citizen je připravena ✔";
                IsUpdateAvailable = false;
            }
            var updateResult = await _appUpdateService.CheckForUpdateAsync(currentVersion);
            AppUpdateStatus = updateResult.StatusMessage;
            IsAppUpdateAvailable = updateResult.IsUpdateAvailable;

            // Save path when validation succeeds (path is valid and has data folder)
            if (!string.IsNullOrWhiteSpace(InputPath))
            {
                _settingsService.SaveSettings(new AppSettings { LastUsedPath = InputPath });
            }
        }
        catch
        {
            // Silently handle validation errors to avoid crashing on property change
            Status = "Chyba při ověřování cesty";
        }
    }

    public async Task InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
            return;

        Status = "Kontroluji cestu ke Star Citizenu...";
        var result = _pathService.ValidateStarCitizenPath(InputPath, SelectedBranch);

        if (result.LivePath == null)
            return;

        IsBusy = true;

        try
        {
            await _localizationService.InstallOrUpdateAsync(
                result.LivePath,
                true,
                progress => Status = progress
            );

            Status = "Hotovo ✔";
            await ValidatePathAsync();
        }
        catch (HttpRequestException ex)
        {
            Status = $"Chyba sítě: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            Status = "Chyba: Vypršel časový limit připojení";
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Chyba: Přístup odepřen - zkontrolujte oprávnění";
        }
        catch (IOException ex)
        {
            Status = $"Chyba I/O: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Chyba při instalaci: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UninstallAsync()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
            return;

        var confirmed = await _confirmationDialogService.ConfirmUninstallAsync();
        if (!confirmed)
        {
            Status = "Odinstalace zrušena";
            return;
        }

        Status = "Kontroluji cestu ke Star Citizenu...";
        var result = _pathService.ValidateStarCitizenPath(InputPath, SelectedBranch);

        if (result.LivePath == null)
            return;

        IsBusy = true;

        try
        {
            await _localizationService.UninstallAsync(
                result.LivePath,
                progress => Status = progress
            );

            Status = "Odinstalace dokončena ✔";
            await ValidatePathAsync();
        }
        catch (FileNotFoundException ex)
        {
            Status = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Chyba: Přístup odepřen - zkontrolujte oprávnění";
        }
        catch (IOException ex)
        {
            Status = $"Chyba I/O: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Chyba při odinstalaci: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void UpdateStatusBrush()
    {
        if (string.IsNullOrWhiteSpace(Status))
        {
            StatusBrush = Brushes.Black;
            return;
        }

        var s = Status;

        if (s.Contains("✔") || s.Contains("aktuální", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("dokončena", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("hotovo", StringComparison.OrdinalIgnoreCase))
        {
            StatusBrush = Brushes.ForestGreen;
            return;
        }

        if (s.Contains("chyba", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("neexistuje", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("neplatná", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("chybí", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("není nainstalována", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("nepodařilo", StringComparison.OrdinalIgnoreCase))
        {
            StatusBrush = Brushes.Firebrick;
            return;
        }

        StatusBrush = Brushes.Black;
    }
    private string? FindWinePrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var dir = new DirectoryInfo(path);

        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "drive_c")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }


}
