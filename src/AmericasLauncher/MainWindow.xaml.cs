// SPDX-License-Identifier: GPL-2.0-only
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using AmericasLauncher.Models;
using AmericasLauncher.Services;
using Microsoft.Win32;

namespace AmericasLauncher;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly RunnerService _runners = new();
    private readonly LaunchService _launcher = new();
    private readonly LauncherUpdateService _launcherUpdates = new();
    private readonly PortStatusService _portStatus = new();
    private readonly AppConfig _config;
    private readonly List<RunnerProfile> _profiles;
    private bool _portLoadedOnce;
    private string? _latestReleaseUrl;
    private string? _latestDownloadUrl;
    private string? _latestVersion;
    private string? _latestReleaseNotes;
    private const int ToastSeconds = 15;

    private DispatcherTimer? _toastTimer;
    private int _toastCountdown;
    private Process? _runningProcess;

    // Non-null from the moment Launch is clicked (pipeline included) until the game has exited or was stopped.
    private CancellationTokenSource? _launchCts;
    private RunnerProfile? _launchProfile;

    public MainWindow()
    {
        InitializeComponent();

        // The version always comes from the assembly (csproj / release tag), never from hard-coded text.
        var version = LauncherUpdateService.CurrentVersionString;
        Title = $"Americas Launcher - V{version}";
        TitleBarText.Text = Title;
        SidebarVersionText.Text = $"v{version}";
        LauncherUpdateStatusText.Text = $"Current version: v{version}";

        _config = _configService.Load();
        _profiles = ConfigService.LoadProfiles();

        RunnerBox.ItemsSource = _profiles;
        RunnerBox.SelectedItem = _profiles.FirstOrDefault(p => p.Id == _config.SelectedRunnerId) ?? _profiles.FirstOrDefault();
        GameFolderBox.Text = _config.GameFolder;
        GameExeBox.Text = _config.GameExe;
        PipelineToggleButton.IsChecked = _config.UsePipeline;
        AutoUpdateToggle.IsChecked = _config.AutoUpdate;
        AutoUpdateToggle.Content = _config.AutoUpdate ? "Auto Update: ON" : "Auto Update: OFF";
        UpdateMissingPathsBanner();
        UpdatePipelineState();

        if (!string.IsNullOrWhiteSpace(_config.PendingUpdateNotes))
        {
            ShowReleaseNoteToast(_config.PendingUpdateVersion ?? "New Version", _config.PendingUpdateNotes);
            _config.PendingUpdateNotes = null;
            _config.PendingUpdateVersion = null;
            _configService.Save(_config);
        }

        if (_config.AutoUpdate)
        {
            Loaded += async (_, _) => await AutoCheckUpdateAsync();
        }

        Loaded += async (_, _) => await CheckAllRunnersHealthAsync();
        UpdateRunnerLegendAndButtons(Selected);

        // Attached after the initial selection so it does not fire during setup.
        RunnerBox.SelectionChanged += async (_, _) =>
        {
            if (Selected is { } profile)
            {
                _config.SelectedRunnerId = profile.Id;
                _configService.Save(_config);
            }
            UpdatePipelineState();
            UpdateRunnerLegendAndButtons(Selected);
            await RefreshVersionAsync();
        };
        Loaded += async (_, _) => await RefreshVersionAsync();
        StateChanged += (_, _) =>
        {
            var isMax = WindowState == WindowState.Maximized;
            RootGrid.Margin = isMax ? new Thickness(7) : new Thickness(0);
            MaxRestoreIcon.Data = Geometry.Parse(isMax ? "M0,2 H7 V9 H0 Z M2,2 V0 H9 V7 H7" : "M0,0 H9 V9 H0 Z");
        };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private RunnerProfile? Selected => RunnerBox.SelectedItem as RunnerProfile;

    // Safe to call from any thread.
    private void Log(string msg) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(msg + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private int _refreshCounter;

    private void SetStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusText.Foreground = color;
    }

    private void UpdateMissingPathsBanner()
    {
        if (MissingPathsBanner == null) return;
        var missing = string.IsNullOrWhiteSpace(_config.GameFolder) || string.IsNullOrWhiteSpace(_config.GameExe);
        MissingPathsBanner.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePipelineState()
    {
        if (PipelineToggleButton == null || PipelineWarningBanner == null) return;
        var supportsPipeline = Selected is { PreLaunchArgs.Count: > 0 };
        PipelineToggleButton.IsEnabled = supportsPipeline;
        var showWarning = supportsPipeline && PipelineToggleButton.IsChecked == true;
        PipelineWarningBanner.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PipelineToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (PipelineToggleButton == null) return;
        _config.UsePipeline = PipelineToggleButton.IsChecked == true;
        _configService.Save(_config);
        UpdatePipelineState();
    }

    private async void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (LaunchView == null || PortStatusView == null || SettingsView == null) return;
        LaunchView.Visibility = LaunchNavButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PortStatusView.Visibility = PortStatusNavButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = SettingsNavButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (PortStatusNavButton.IsChecked == true && !_portLoadedOnce)
        {
            _portLoadedOnce = true;
            await LoadPortStatusAsync();
        }

        if (SettingsNavButton.IsChecked == true)
        {
            await RefreshStorageAsync();
        }
    }

    private sealed record RunnerStorageItem(RunnerProfile Profile, string Name, string SizeText, bool CanDelete);

    private async void RefreshStorage_Click(object sender, RoutedEventArgs e) => await RefreshStorageAsync();

    private async Task RefreshStorageAsync()
    {
        var cacheDir = RunnerService.PipelineCacheDir;
        PipelineCachePathText.Text = cacheDir;

        var (cacheBytes, items) = await Task.Run(() =>
        {
            var cBytes = RunnerService.GetDirectorySize(cacheDir);
            var list = _profiles.Select(p =>
            {
                var dir = RunnerService.SourceDir(p);
                var bytes = RunnerService.GetDirectorySize(dir);
                var exists = Directory.Exists(dir);
                return new RunnerStorageItem(
                    p,
                    p.Name,
                    exists ? RunnerService.FormatBytes(bytes) : "Not installed",
                    exists);
            }).ToList();
            return (cBytes, list);
        });

        PipelineCacheSizeText.Text = Directory.Exists(cacheDir) ? RunnerService.FormatBytes(cacheBytes) : "0 B";
        DeletePipelineCacheButton.IsEnabled = Directory.Exists(cacheDir);
        RunnerStorageList.ItemsSource = items;
    }

    private async void DeletePipelineCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => RunnerService.DeleteDirectorySafe(RunnerService.PipelineCacheDir));
            await RefreshStorageAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to delete pipeline cache: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteRunner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RunnerProfile profile }) return;
        try
        {
            await Task.Run(() => RunnerService.DeleteDirectorySafe(RunnerService.SourceDir(profile)));
            _config.InstalledVersions.Remove(profile.Id);
            _configService.Save(_config);
            await RefreshStorageAsync();
            await RefreshVersionAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete {profile.Name}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void PortRefresh_Click(object sender, RoutedEventArgs e) => await LoadPortStatusAsync();

    private async Task LoadPortStatusAsync()
    {
        PortRefreshButton.IsEnabled = false;
        PortUpdatedText.Text = "Loading...";
        try
        {
            var report = await _portStatus.FetchAsync();
            PortUpdatedText.Text = string.IsNullOrWhiteSpace(report.UpdatedAt) ? "Updated" : $"Updated: {report.UpdatedAt}";
            PortLeaderNameText.Text = report.LeaderName;
            PortLeaderRepoText.Text = report.LeaderRepo;
            PortOverallScoreText.Text = $"{report.LeaderScore:0.0}%";
            PortTop3Text.Text = $"Top 3 avg: {report.Top3Average:0.0}% · Reached: {report.ReachedLabel}";
            PortOverallProgressBar.Value = report.LeaderScore;
            PortMilestonesList.ItemsSource = report.Milestones;
            PortStoryText.Text = report.StoryLabel;
            PortRenderText.Text = report.RenderLabel;
            PortPerfText.Text = $"{report.Fps:0.#} FPS";
            PortStabilityText.Text = report.StabilityLabel;
            PortChangesList.ItemsSource = report.Changes;
        }
        catch (Exception ex)
        {
            PortUpdatedText.Text = "Failed to load: " + ex.Message;
        }
        finally
        {
            PortRefreshButton.IsEnabled = true;
        }
    }

    private async void CheckLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckLauncherUpdateButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        LauncherUpdateStatusText.Text = "Checking for updates...";
        LauncherUpdateStatusText.Foreground = Brushes.LightSlateGray;

        try
        {
            var res = await _launcherUpdates.CheckForUpdateAsync();
            LauncherUpdateStatusText.Text = res.Message;
            LauncherUpdateStatusText.Foreground = res.IsUpdateAvailable
                ? Brushes.Orange
                : res.HasRelease ? Brushes.LimeGreen : Brushes.LightSlateGray;

            _latestReleaseUrl = res.ReleaseUrl;
            _latestDownloadUrl = res.DownloadUrl;
            _latestVersion = res.LatestVersion;
            _latestReleaseNotes = res.ReleaseNotes;
            OpenReleaseButton.Content = !string.IsNullOrWhiteSpace(res.DownloadUrl) ? "Update Now" : "Open Release";
            OpenReleaseButton.Visibility = res.IsUpdateAvailable &&
                (!string.IsNullOrWhiteSpace(res.DownloadUrl) || !string.IsNullOrWhiteSpace(res.ReleaseUrl))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LauncherUpdateStatusText.Text = "Could not check for updates: " + ex.Message;
            LauncherUpdateStatusText.Foreground = Brushes.IndianRed;
        }
        finally
        {
            CheckLauncherUpdateButton.IsEnabled = true;
        }
    }

    private async void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_latestDownloadUrl))
        {
            CheckLauncherUpdateButton.IsEnabled = false;
            OpenReleaseButton.IsEnabled = false;
            try
            {
                _config.PendingUpdateNotes = _latestReleaseNotes;
                _config.PendingUpdateVersion = _latestVersion;
                _configService.Save(_config);

                await _launcherUpdates.DownloadAndApplyUpdateAsync(
                    _latestDownloadUrl,
                    msg => Dispatcher.Invoke(() => LauncherUpdateStatusText.Text = msg));
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                LauncherUpdateStatusText.Text = "Update failed: " + ex.Message;
                LauncherUpdateStatusText.Foreground = Brushes.IndianRed;
                CheckLauncherUpdateButton.IsEnabled = true;
                OpenReleaseButton.IsEnabled = true;
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(_latestReleaseUrl))
            Process.Start(new ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
    }

    private async Task AutoCheckUpdateAsync()
    {
        try
        {
            var res = await _launcherUpdates.CheckForUpdateAsync();
            if (res.IsUpdateAvailable && !string.IsNullOrWhiteSpace(res.DownloadUrl))
            {
                _config.PendingUpdateNotes = res.ReleaseNotes;
                _config.PendingUpdateVersion = res.LatestVersion;
                _configService.Save(_config);
                await _launcherUpdates.DownloadAndApplyUpdateAsync(res.DownloadUrl, _ => { });
                Application.Current.Shutdown();
            }
        }
        catch
        {
            // Silent on startup auto-update check
        }
    }

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = AutoUpdateToggle.IsChecked == true;
        AutoUpdateToggle.Content = enabled ? "Auto Update: ON" : "Auto Update: OFF";
        _config.AutoUpdate = enabled;
        _configService.Save(_config);
    }

    private void ShowReleaseNoteToast(string version, string notes)
    {
        ToastVersionText.Text = $"Updated to {version}";
        ToastNotesText.Text = notes;
        ReleaseNoteToast.Visibility = Visibility.Visible;
        _toastCountdown = ToastSeconds;
        ToastCountdownText.Text = $"Closes in {ToastSeconds}s";
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastCountdown--;
            if (_toastCountdown <= 0) CloseToast();
            else ToastCountdownText.Text = $"Closes in {_toastCountdown}s";
        };
        _toastTimer.Start();
    }

    private void CloseToast()
    {
        _toastTimer?.Stop();
        _toastTimer = null;
        ReleaseNoteToast.Visibility = Visibility.Collapsed;
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) => CloseToast();

    /// <summary>Shows the installed version and whether a newer one exists upstream.</summary>
    private async Task RefreshVersionAsync()
    {
        var ticket = ++_refreshCounter;   // ignore results if the selection changed meanwhile

        if (Selected is not { } profile)
        {
            VersionText.Text = "Emulator version: -";
            SetStatus("", Brushes.Gray);
            return;
        }

        try
        {
            if (!_runners.IsInstalled(profile))
            {
                VersionText.Text = "Emulator version: not installed";
                SetStatus("Not installed", Brushes.LightSlateGray);
                return;
            }

            var version = await _runners.GetInstalledVersionAsync(profile);
            if (ticket != _refreshCounter) return;
            VersionText.Text = "Emulator version: " + (version == "" ? "unknown" : version);
            SetStatus("Checking for updates...", Brushes.LightSlateGray);

            var status = await _runners.CheckForUpdateAsync(profile);
            if (ticket != _refreshCounter) return;

            switch (status)
            {
                case UpdateStatus.UpToDate:
                    SetStatus("Up to date", Brushes.LimeGreen);
                    break;
                case UpdateStatus.UpdateAvailable:
                    SetStatus("New update available", Brushes.Orange);
                    break;
                default:
                    SetStatus("Could not check for updates", Brushes.LightSlateGray);
                    break;
            }
        }
        catch
        {
            if (ticket == _refreshCounter) SetStatus("Could not check for updates", Brushes.LightSlateGray);
        }
    }

    private void SetBusy(bool busy)
    {
        var isBroken = Selected?.HealthState == RunnerHealthState.Broken;
        InstallButton.IsEnabled = !busy && !isBroken;
        LaunchButton.IsEnabled = !busy;
        RunnerBox.IsEnabled = !busy;
        Progress.IsIndeterminate = busy;
    }

    /// <summary>Switches the Launch button between "Launch Game" and "Stop Game" and locks the rest of the UI.</summary>
    private void SetLaunchUi(bool active)
    {
        var isBroken = Selected?.HealthState == RunnerHealthState.Broken;
        LaunchButton.Content = active ? "■  Stop Game" : "▶  Launch Game";
        LaunchButton.IsEnabled = true;   // while active this is the Stop button, so it must stay clickable
        InstallButton.IsEnabled = !active && !isBroken;
        RunnerBox.IsEnabled = !active;
        Progress.IsIndeterminate = active;
    }

    private void EndLaunch(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_launchCts, cts)) return;   // already ended
        _launchCts = null;
        _launchProfile = null;
        _runningProcess = null;
        cts.Dispose();
        SetLaunchUi(false);
    }

    /// <summary>
    /// Works at any time after Launch was clicked: during pipeline translation as well as while the game runs.
    /// Cancels the pipeline, stops the game and any leftover process of the runner's exe (e.g. victory.exe).
    /// </summary>
    private void StopGame()
    {
        var cts = _launchCts;
        var profile = _launchProfile;
        if (cts == null) return;

        Log("Stopping...");
        LaunchButton.IsEnabled = false;   // prevent double clicks until the launch has fully ended

        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        try { _runningProcess?.Kill(entireProcessTree: true); }
        catch { /* already exited */ }

        if (profile != null)
        {
            var killed = RunnerService.KillRunnerProcesses(profile);
            if (killed > 0) Log($"Closed {killed} background {profile.Name} process(es).");
        }

        Log("Stopped.");
        EndLaunch(cts);
    }

    private async Task CheckAllRunnersHealthAsync()
    {
        var tasks = _profiles.Select(async p =>
        {
            var state = await _runners.CheckHealthAsync(p, _config);
            Dispatcher.Invoke(() =>
            {
                p.HealthState = state;
                if (Selected?.Id == p.Id)
                    UpdateRunnerLegendAndButtons(p);
            });
        });
        await Task.WhenAll(tasks);
    }

    private void UpdateRunnerLegendAndButtons(RunnerProfile? profile)
    {
        if (profile == null)
        {
            RunnerColorLegendText.Text = "Please select a runner";
            InstallButton.IsEnabled = false;
            return;
        }

        RunnerColorLegendText.Text = profile.HealthState switch
        {
            RunnerHealthState.UpToDate => "Green: Runner is installed and up to date.",
            RunnerHealthState.Broken => "Red: Runner repository was deleted or is no longer working.",
            RunnerHealthState.UpdateAvailable => "Yellow: Runner is installed, but an update is available.",
            _ => "Black: Runner is not installed yet."
        };

        InstallButton.IsEnabled = profile.HealthState != RunnerHealthState.Broken && !Progress.IsIndeterminate;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game folder" };
        if (dlg.ShowDialog() == true)
        {
            GameFolderBox.Text = _config.GameFolder = dlg.FolderName;
            _configService.Save(_config);
            UpdateMissingPathsBanner();
        }
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select the game exe (Game.exe)",
            Filter = "Game executable (*.exe)|*.exe|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(_config.GameFolder) && Directory.Exists(_config.GameFolder))
            dlg.InitialDirectory = _config.GameFolder;

        if (dlg.ShowDialog() == true)
        {
            GameExeBox.Text = _config.GameExe = dlg.FileName;

            // Convenience: if no folder was chosen yet, use the exe's folder.
            if (string.IsNullOrWhiteSpace(_config.GameFolder))
                GameFolderBox.Text = _config.GameFolder = Path.GetDirectoryName(dlg.FileName) ?? "";

            _configService.Save(_config);
            UpdateMissingPathsBanner();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        SetBusy(true);
        try
        {
            var ok = await _runners.InstallOrUpdateAsync(profile, _config, Log);
            _config.SelectedRunnerId = profile.Id;
            _configService.Save(_config);

            if (ok)
            {
                Log("Done. You can launch the game now.");
                if (!string.IsNullOrWhiteSpace(profile.Notes)) Log("NOTE: " + profile.Notes);
            }
            else
            {
                Log("Setup did not finish. Check the messages above.");
            }

            await RefreshVersionAsync();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        // The button is "Stop Game" from the first moment of the launch (pipeline included).
        if (_launchCts != null)
        {
            StopGame();
            return;
        }

        if (Selected is not { } profile) return;

        if (!_runners.IsInstalled(profile)) { Log("Click 'Install / Update' first."); return; }
        if (string.IsNullOrWhiteSpace(_config.GameFolder) || string.IsNullOrWhiteSpace(_config.GameExe))
        {
            UpdateMissingPathsBanner();
            Log("Game Folder and Game Exe are not selected. Please select them in Settings.");
            return;
        }

        var folder = Path.GetFullPath(_config.GameFolder).TrimEnd('\\') + "\\";
        var exe = Path.GetFullPath(_config.GameExe);
        if (!exe.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            Log("The game exe must be inside the selected game folder.");
            return;
        }

        var cts = new CancellationTokenSource();
        _launchCts = cts;
        _launchProfile = profile;
        SetLaunchUi(true);

        try
        {
            var usePipeline = PipelineToggleButton.IsEnabled && PipelineToggleButton.IsChecked == true;
            var process = await _launcher.Launch(
                profile, _config.GameFolder, _config.GameExe, Log, usePipeline,
                onExited: () =>
                {
                    try { Dispatcher.Invoke(() => EndLaunch(cts)); }
                    catch { /* the window is closing */ }
                },
                ct: cts.Token);

            // Stop was clicked just as the game process was being started: close it right away.
            if (!ReferenceEquals(_launchCts, cts))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return;
            }

            _runningProcess = process;
            Log("Started.");
        }
        catch (OperationCanceledException)
        {
            // Stop Game during the pipeline: StopGame() has already cleaned up and logged.
            EndLaunch(cts);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_launchCts, cts)) Log("ERROR: " + ex.Message);
            EndLaunch(cts);
        }
    }
}
