using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenIPC_Config.Events;
using OpenIPC_Config.Models;
using OpenIPC_Config.Services;
using Serilog;

namespace OpenIPC_Config.ViewModels;

public partial class AdvancedTabViewModel : ViewModelBase
{
    #region Fields

    private bool _isAdaptiveLinkInstalling;
    private string _adaptiveLinkInstallStatus = "";
    private int _adaptiveLinkInstallProgress;
    private string _customScriptContent = "";
    private string _scriptFilename = "";
    private string _selectedScriptType;
    private readonly IYamlConfigService _yamlConfigService;
    private bool _isAdaptiveLinkInstalled;
    private bool _isAdaptiveLinkRunning;
    private string _adaptiveLinkVersion = "Unknown";

    #endregion

    #region Properties

    [ObservableProperty] private bool _canConnect;

    public bool IsAlinkDroneDisabled => !IsAlinkDroneEnabled;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAlinkDroneDisabled))]
    private bool _isAlinkDroneEnabled;

    public bool InstalledButNotRunning => IsAdaptiveLinkInstalled && !IsAdaptiveLinkRunning;

    public bool NotInstalled => !IsAdaptiveLinkInstalled;

    public bool CanInstall
    {
        get => !IsAdaptiveLinkInstalling && DeviceConfig.Instance.CanConnect;
    }

    public bool IsAdaptiveLinkInstalling
    {
        get => _isAdaptiveLinkInstalling;
        set => SetProperty(ref _isAdaptiveLinkInstalling, value);
    }

    public string AdaptiveLinkInstallStatus
    {
        get => _adaptiveLinkInstallStatus;
        set => SetProperty(ref _adaptiveLinkInstallStatus, value);
    }

    public int AdaptiveLinkInstallProgress
    {
        get => _adaptiveLinkInstallProgress;
        set => SetProperty(ref _adaptiveLinkInstallProgress, value);
    }

    public bool IsAdaptiveLinkInstalled
    {
        get => _isAdaptiveLinkInstalled;
        set => SetProperty(ref _isAdaptiveLinkInstalled, value);
    }

    public bool IsAdaptiveLinkRunning
    {
        get => _isAdaptiveLinkRunning;
        set => SetProperty(ref _isAdaptiveLinkRunning, value);
    }

    public string AdaptiveLinkVersion
    {
        get => _adaptiveLinkVersion;
        set => SetProperty(ref _adaptiveLinkVersion, value);
    }

    #endregion

    #region Commands

    public IAsyncRelayCommand InstallAdaptiveLinkCommand { get; }
    public IAsyncRelayCommand CheckAdaptiveLinkStatusCommand { get; }
    public IAsyncRelayCommand GenerateSystemReportCommand { get; }
    public IAsyncRelayCommand ViewSystemLogsCommand { get; }
    public IAsyncRelayCommand NetworkDiagnosticsCommand { get; }

    // Command property for toggling alink_drone
    public IAsyncRelayCommand ToggleAlinkDroneCommand { get; set; }

    #endregion

    public AdvancedTabViewModel(
        ILogger logger,
        ISshClientService sshClientService,
        IEventSubscriptionService eventSubscriptionService,
        IYamlConfigService yamlConfigService)
        : base(logger, sshClientService, eventSubscriptionService)
    {
        SubscribeToEvents();

        _yamlConfigService = yamlConfigService ?? throw new ArgumentNullException(nameof(yamlConfigService));

        // Initialize commands
        InstallAdaptiveLinkCommand = new AsyncRelayCommand(InstallAdaptiveLinkOfflineAsync);
        CheckAdaptiveLinkStatusCommand = new AsyncRelayCommand(CheckAdaptiveLinkStatusAsync);
        GenerateSystemReportCommand = new AsyncRelayCommand(GenerateSystemReportAsync);
        ViewSystemLogsCommand = new AsyncRelayCommand(ViewSystemLogsAsync);
        NetworkDiagnosticsCommand = new AsyncRelayCommand(NetworkDiagnosticsAsync);
        ToggleAlinkDroneCommand = new AsyncRelayCommand(async () => await ToggleAlinkDroneAsync());
    }

    #region Adaptive Link Methods

    // Method to toggle the alink_drone status
    private async Task ToggleAlinkDroneAsync()
    {
        if (_isUpdatingAlinkDroneStatus)
            return;

        _isUpdatingAlinkDroneStatus = true;
        try
        {
            if (!DeviceConfig.Instance.CanConnect)
            {
                UpdateUIMessage("Device not connected. Cannot toggle Adaptive Link status.");
                return;
            }

            string command = IsAlinkDroneEnabled
                ? DeviceCommands.RemoveAlinkDroneFromRcLocal
                : DeviceCommands.AddAlinkDroneToRcLocal;

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 60 seconds timeout
            var result =
                await SshClientService.ExecuteCommandWithResponseAsync(DeviceConfig.Instance, command, cts.Token);

            // Refresh status to confirm changes
            await CheckAlinkDroneStatusAsync();

            UpdateUIMessage(IsAlinkDroneEnabled
                ? "Adaptive Link enabled on boot."
                : "Adaptive Link disabled on boot.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error toggling Adaptive Link status");
            UpdateUIMessage($"Error toggling Adaptive Link status: {ex.Message}");
        }
        finally
        {
            _isUpdatingAlinkDroneStatus = false;
        }
    }

    private bool _isUpdatingAlinkDroneStatus = false;

    partial void OnIsAlinkDroneEnabledChanged(bool value)
    {
        if (CanConnect && !_isUpdatingAlinkDroneStatus)
        {
            _ = ApplyAlinkDroneStatus(value);
        }
    }

    // Method to check the current status

    private async Task CheckAlinkDroneStatusAsync()
    {
        if (!DeviceConfig.Instance.CanConnect || _isUpdatingAlinkDroneStatus)
            return;

        _isUpdatingAlinkDroneStatus = true;
        try
        {
            var cts = new CancellationTokenSource(10000); // 10 seconds timeout
            var cmdResult = await SshClientService.ExecuteCommandWithResponseAsync(
                DeviceConfig.Instance,
                DeviceCommands.IsAlinkDroneEnabled,
                cts.Token);

            bool newStatus = cmdResult?.Result?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

            // Only update if status actually changed to avoid recursive property change notifications
            if (IsAlinkDroneEnabled != newStatus)
            {
                IsAlinkDroneEnabled = newStatus;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error checking Adaptive Link status");
        }
        finally
        {
            _isUpdatingAlinkDroneStatus = false;
        }
    }


    private async Task ApplyAlinkDroneStatus(bool enable)
    {
        _isUpdatingAlinkDroneStatus = true;
        try
        {
            if (enable)
            {
                await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance,
                    DeviceCommands.AddAlinkDroneToRcLocal);
            }
            else
            {
                await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance,
                    DeviceCommands.RemoveAlinkDroneFromRcLocal);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error setting Alink Drone status");
        }
        finally
        {
            _isUpdatingAlinkDroneStatus = false;
        }
    }

    private async Task InstallAdaptiveLinkOfflineAsync()
    {
        try
        {
            var deviceConfig = DeviceConfig.Instance;
            if (!deviceConfig.CanConnect)
            {
                UpdateUIMessage("Device not connected. Cannot install Adaptive Link.");
                return;
            }

            // Set UI state to installing
            IsAdaptiveLinkInstalling = true;
            AdaptiveLinkInstallProgress = 0;
            AdaptiveLinkInstallStatus = "Starting installation...";

            Logger.Information("Starting Adaptive Link installation via local PC download");

            // Create temporary directory
            string tempPath = Path.Combine(Path.GetTempPath(), "AdaptiveLinkTemp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            Logger.Information("Created temporary directory: {TempPath}", tempPath);

            try
            {
                // Define GitHub repository info
                const string repoOwner = "OpenIPC";
                const string repoName = "adaptive-link";

                // Step 1: Download files from GitHub to local temp directory
                AdaptiveLinkInstallStatus = "Downloading files from GitHub...";
                AdaptiveLinkInstallProgress = 10;

                // Required files list
                var requiredFiles = new Dictionary<string, string>
                {
                    { "alink_drone", "/usr/bin/alink_drone" },
                    { "txprofiles.conf", "/etc/txprofiles.conf" },
                    { "alink.conf", "/etc/alink.conf" }
                };

                // Download each file from the latest release
                using (var httpClient = new HttpClient())
                {
                    // GitHub API requires a user agent
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "AdaptiveLinkInstaller");

                    // Get the latest release info
                    AdaptiveLinkInstallStatus = "Getting latest release information...";
                    var releasesJson =
                        await httpClient.GetStringAsync(
                            $"https://api.github.com/repos/{repoOwner}/{repoName}/releases");

                    // Parse JSON to get download URLs (using System.Text.Json)
                    using (JsonDocument doc = JsonDocument.Parse(releasesJson))
                    {
                        // Get the first (latest) release
                        var latestRelease = doc.RootElement[0];
                        var assets = latestRelease.GetProperty("assets");

                        foreach (var file in requiredFiles.Keys)
                        {
                            AdaptiveLinkInstallStatus = $"Downloading {file}...";

                            // Find the asset with matching name
                            string downloadUrl = null;
                            foreach (var asset in assets.EnumerateArray())
                            {
                                var name = asset.GetProperty("name").GetString();
                                if (name == file)
                                {
                                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                                    break;
                                }
                            }

                            if (string.IsNullOrEmpty(downloadUrl))
                            {
                                throw new Exception($"File {file} not found in the latest release");
                            }

                            // Download the file
                            var fileBytes = await httpClient.GetByteArrayAsync(downloadUrl);
                            await File.WriteAllBytesAsync(Path.Combine(tempPath, file), fileBytes);
                            Logger.Information("Downloaded {File} to {Path}", file, Path.Combine(tempPath, file));
                        }
                    }
                }

                // Step 2: Upload files from temp directory to device
                AdaptiveLinkInstallStatus = "Copying files to device...";
                AdaptiveLinkInstallProgress = 50;

                foreach (var file in requiredFiles)
                {
                    var localFilePath = Path.Combine(tempPath, file.Key);
                    var remoteFilePath = file.Value;

                    AdaptiveLinkInstallStatus = $"Copying {file.Key} to device...";
                    await SshClientService.UploadFileAsync(
                        deviceConfig,
                        localFilePath,
                        remoteFilePath
                    );
                }

                // Step 3: Make the drone file executable
                AdaptiveLinkInstallStatus = "Making alink_drone executable...";
                AdaptiveLinkInstallProgress = 70;

                var cts = new CancellationTokenSource(10000); // 10 seconds
                var cancellationToken = cts.Token;
                var chmodResult = await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig,
                    "chmod +x /usr/bin/alink_drone", cancellationToken);

                if (chmodResult?.ExitStatus != 0)
                {
                    AdaptiveLinkInstallStatus = $"Chmod failed: {chmodResult?.Error ?? "Unknown error"}";
                    throw new Exception("Failed to make alink_drone executable");
                }

                // Step 4: Configure required settings
                AdaptiveLinkInstallStatus = "Configuring Adaptive Link...";
                AdaptiveLinkInstallProgress = 80;

                // Run necessary configuration commands
                var configCommands = new List<string>
                {
                    "cli -s .video0.qpDelta -12",
                    "cli -s .fpv.enabled true",
                    "cli -s .fpv.noiseLevel 0",
                    "sed -i 's/tunnel=.*/tunnel=true/' /etc/datalink.conf",
                    "sed -i -e '$i \\/usr/bin/alink_drone --ip 10.5.0.10 --port 9999 &' /etc/rc.local"
                };

                foreach (var cmd in configCommands)
                {
                    cts = new CancellationTokenSource(10000);
                    cancellationToken = cts.Token;
                    var cmdResult = await SshClientService.ExecuteCommandWithResponseAsync(
                        deviceConfig, cmd, cancellationToken);

                    if (cmdResult?.ExitStatus != 0)
                    {
                        Logger.Warning("Command {Cmd} finished with exit code {Code}",
                            cmd, cmdResult?.ExitStatus);
                        // Continue despite errors for flexibility
                    }
                }

                // Step 5: Prepare for reboot
                AdaptiveLinkInstallStatus = "Installation complete. Preparing to reboot the device...";
                AdaptiveLinkInstallProgress = 90;

                // Let the user know before rebooting
                UpdateUIMessage("Adaptive Link installation successful. Device will reboot in 5 seconds.");

                // Wait briefly to ensure the message is seen
                await Task.Delay(5000);

                // Step 6: Reboot the device
                AdaptiveLinkInstallStatus = "Rebooting device...";
                AdaptiveLinkInstallProgress = 100;
                await SshClientService.ExecuteCommandAsync(deviceConfig, "reboot");

                // Reset UI state after a brief delay (device will disconnect due to reboot)
                await Task.Delay(3000);
                IsAdaptiveLinkInstalling = false;

                // Inform the user
                UpdateUIMessage("Device is rebooting. Please reconnect after the reboot completes.");
            }
            finally
            {
                // Clean up temp directory when done
                try
                {
                    if (Directory.Exists(tempPath))
                    {
                        Directory.Delete(tempPath, true);
                        Logger.Information("Cleaned up temporary directory");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to clean up temporary directory: {ErrorMessage}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to install Adaptive Link");
            AdaptiveLinkInstallStatus = $"Installation failed: {ex.Message}";
            UpdateUIMessage($"Error installing Adaptive Link: {ex.Message}");

            // Reset UI state
            IsAdaptiveLinkInstalling = false;
        }
    }


// Helper method to extract files to the temp directory - implement this based on how you store the files
    private void ExtractFilesToTempDirectory(string tempPath, List<string> fileNames)
    {
        // Implementation depends on how you're storing these files in your application
        // Options include:
        // 1. Embedded resources
        // 2. Files included in your application folder
        // 3. Files from a zip package

        // Example for embedded resources:
        foreach (var fileName in fileNames)
        {
            var resourceName = $"YourNamespace.Resources.{fileName}";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException($"Required resource not found: {resourceName}");
                }

                using (var fileStream = new FileStream(Path.Combine(tempPath, fileName), FileMode.Create))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }
    }

    private async Task CheckAdaptiveLinkStatusAsync()
    {
        try
        {
            var deviceConfig = DeviceConfig.Instance;
            if (!deviceConfig.CanConnect)
            {
                UpdateUIMessage("Device not connected. Cannot check Adaptive Link status.");
                return;
            }

            UpdateUIMessage("Checking Adaptive Link status...");

            // Reset status properties
            IsAdaptiveLinkInstalled = false;
            IsAdaptiveLinkRunning = false;
            AdaptiveLinkVersion = "Unknown";

            // Check if the alink_drone executable exists
            string checkCommand = "[ -f /usr/bin/alink_drone ] && echo 'INSTALLED' || echo 'NOT_INSTALLED'";
            var cts = new CancellationTokenSource(10000); // 10 seconds
            var cancellationToken = cts.Token;

            var checkResult =
                await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig, checkCommand, cancellationToken);
            string statusOutput = checkResult?.Result?.Trim() ?? "Failed to check status";

            if (statusOutput != "INSTALLED")
            {
                UpdateUIMessage("Adaptive Link is not installed on this device.");
                return;
            }

            // File exists, so Adaptive Link is installed
            IsAdaptiveLinkInstalled = true;

            // Check if it's running
            cts = new CancellationTokenSource(10000);
            cancellationToken = cts.Token;
            var runningCheck = await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig,
                "ps | grep alink_drone | grep -v grep | wc -l", cancellationToken);

            // If count > 0, it's running
            int processCount = int.TryParse(runningCheck?.Result?.Trim(), out var count) ? count : 0;
            IsAdaptiveLinkRunning = processCount > 0;

            // Check configuration file
            cts = new CancellationTokenSource(10000);
            cancellationToken = cts.Token;
            var configResult = await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig,
                "cat /etc/alink.conf 2>/dev/null || echo 'Configuration not found'", cancellationToken);

            string configOutput = configResult?.Result ?? "No configuration available";

            StringBuilder statusInfo = new StringBuilder();
            statusInfo.AppendLine(
                $"Adaptive Link is installed and {(IsAdaptiveLinkRunning ? "running" : "not running")}.");


            statusInfo.AppendLine();
            statusInfo.AppendLine("Configuration:");
            statusInfo.AppendLine(configOutput);

            UpdateUIMessage(statusInfo.ToString());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check Adaptive Link status");
            UpdateUIMessage($"Error checking Adaptive Link status: {ex.Message}");
        }
    }

    #endregion

    #region Initialization Methods

    private void SubscribeToEvents()
    {
        EventSubscriptionService.Subscribe<AppMessageEvent, AppMessage>(OnAppMessage);
        // Subscribe to alink drone status updates
        EventSubscriptionService.Subscribe<AlinkDroneStatusEvent, bool>(status => IsAlinkDroneEnabled = status);
    }

    #endregion

    #region Debug Tool Methods

    private async Task GenerateSystemReportAsync()
    {
        try
        {
            var deviceConfig = DeviceConfig.Instance;
            if (!deviceConfig.CanConnect)
            {
                UpdateUIMessage("Device not connected. Cannot generate system report.");
                return;
            }

            UpdateUIMessage("Generating system report...");

            // System commands to gather information
            var systemCommands = new List<(string Section, string Command)>
            {
                ("System Information", "uname -a"),
                ("CPU Info", "cat /proc/cpuinfo | grep -i 'model name\\|processor'"),
                ("Memory Info", "free -h"),
                ("Disk Usage", "df -h"),
                ("Network Interfaces", "ip addr"),
                ("Loaded Modules", "lsmod"),
                ("Active Services", "systemctl list-units --type=service --state=running")
            };

            // Configuration files to include
            var configFiles = new List<string>
            {
                "/etc/os-release",
                "/etc/majestic.yaml",
                "/etc/wfb.yaml",
                "/etc/wfb.conf",
                "/etc/telemetry.conf",
                "/etc/alink.conf",
                "/etc/vtxmenu.ini",
                "/etc/txprofiles.conf",
                "/etc/datalink.conf"
                // Add new files here as needed
            };

            // Build the command string
            var commandBuilder = new StringBuilder();

            // Add system commands
            foreach (var (section, command) in systemCommands)
            {
                commandBuilder.Append($"echo '=== {section} ===' && {command} && ");
            }

            // Add configuration files section header
            commandBuilder.Append("echo '=== OpenIPC Configurations ===' && ");

            // Add each configuration file
            foreach (var file in configFiles)
            {
                string filename = Path.GetFileName(file);
                commandBuilder.Append($"echo '--- {file} ---' && ");
                commandBuilder.Append($"cat {file} 2>/dev/null || echo 'File not found' && ");
            }

            // Remove the trailing " && "
            string reportCommands = commandBuilder.ToString();
            if (reportCommands.EndsWith(" && "))
            {
                reportCommands = reportCommands.Substring(0, reportCommands.Length - 4);
            }

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 60 seconds timeout
            var result =
                await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig, reportCommands, cts.Token);

            if (result == null || string.IsNullOrEmpty(result.Result))
            {
                UpdateUIMessage("Failed to generate system report. No data received.");
                return;
            }

            string reportContent = result.Result;

            // Let the user know the report was generated
            UpdateUIMessage("System report generated. Select a location to save it...");

            // Now prompt the user for a save location
            // Use Avalonia's SaveFileDialog to let the user pick a location
            var saveFileDialog = new Avalonia.Controls.SaveFileDialog
            {
                Title = "Save System Report",
                DefaultExtension = "txt",
                InitialFileName = $"system_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filters = new List<Avalonia.Controls.FileDialogFilter>
                {
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "Text Files",
                        Extensions = new List<string> { "txt" }
                    },
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "All Files",
                        Extensions = new List<string> { "*" }
                    }
                }
            };

            // Get the main window to show the dialog
            var mainWindow =
                Avalonia.Application.Current.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

            if (mainWindow == null)
            {
                UpdateUIMessage("Could not show save dialog. Using default location.");
                // Save to a default location if we can't show the dialog
                string defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    $"system_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                File.WriteAllText(defaultPath, reportContent);
                UpdateUIMessage($"System report saved to: {defaultPath}");
                return;
            }

            // Show the save dialog and get the selected path
            string filePath = await saveFileDialog.ShowAsync(mainWindow);

            if (string.IsNullOrEmpty(filePath))
            {
                // User canceled the save operation
                UpdateUIMessage("Save operation canceled.");
                return;
            }

            // Save the report to the selected file
            File.WriteAllText(filePath, reportContent);

            UpdateUIMessage($"System report saved to: {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to generate system report");
            UpdateUIMessage($"Error generating system report: {ex.Message}");
        }
    }

    private async Task ViewSystemLogsAsync()
    {
        try
        {
            var deviceConfig = DeviceConfig.Instance;
            if (!deviceConfig.CanConnect)
            {
                UpdateUIMessage("Device not connected. Cannot view system logs.");
                return;
            }

            UpdateUIMessage("Retrieving system logs...");

            // Execute command to get both journalctl and readlog output
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 60 seconds timeout

            // Build combined log command
            string logCommand =
                "echo '=== Journal Logs (Last 100 Entries) ===' && " +
                "journalctl -n 100 2>/dev/null || echo 'journalctl not available' && " +
                "echo -e '\\n\\n=== OpenIPC readlog Output ===' && " +
                "logread 2>/dev/null || echo 'logread command not available'";

            var logResult = await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig, logCommand, cts.Token);

            if (logResult == null || string.IsNullOrEmpty(logResult.Result))
            {
                UpdateUIMessage("Failed to retrieve system logs. No data received.");
                return;
            }

            string logContent = logResult.Result;

            // Let the user know the logs were retrieved
            UpdateUIMessage("System logs retrieved. Select a location to save them...");

            // Prompt the user for a save location using SaveFileDialog
            var saveFileDialog = new Avalonia.Controls.SaveFileDialog
            {
                Title = "Save System Logs",
                DefaultExtension = "txt",
                InitialFileName = $"system_logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filters = new List<Avalonia.Controls.FileDialogFilter>
                {
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "Text Files",
                        Extensions = new List<string> { "txt" }
                    },
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "All Files",
                        Extensions = new List<string> { "*" }
                    }
                }
            };

            // Get the main window to show the dialog
            var mainWindow =
                Avalonia.Application.Current.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

            if (mainWindow == null)
            {
                UpdateUIMessage("Could not show save dialog. Using default location.");
                // Save to a default location if we can't show the dialog
                string defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    $"system_logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                File.WriteAllText(defaultPath, logContent);
                UpdateUIMessage($"System logs saved to: {defaultPath}");
                return;
            }

            // Show the save dialog and get the selected path
            string filePath = await saveFileDialog.ShowAsync(mainWindow);

            if (string.IsNullOrEmpty(filePath))
            {
                // User canceled the save operation
                UpdateUIMessage("Save operation canceled.");
                return;
            }

            // Save the logs to the selected file
            File.WriteAllText(filePath, logContent);

            UpdateUIMessage($"System logs saved to: {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to retrieve system logs");
            UpdateUIMessage($"Error retrieving system logs: {ex.Message}");
        }
    }

    private async Task NetworkDiagnosticsAsync()
    {
        try
        {
            var deviceConfig = DeviceConfig.Instance;
            if (!deviceConfig.CanConnect)
            {
                UpdateUIMessage("Device not connected. Cannot run network diagnostics.");
                return;
            }

            UpdateUIMessage("Running network diagnostics...");

            // Execute commands to diagnose network issues
            string diagnosticCommands =
                "echo '=== Network Interfaces ===' && " +
                "ip addr && " +
                "echo '=== Routing Table ===' && " +
                "ip route && " +
                "echo '=== DNS Configuration ===' && " +
                "cat /etc/resolv.conf && " +
                "echo '=== Ping Test (Google DNS) ===' && " +
                "ping -c 4 8.8.8.8 || echo 'Ping failed' && " +
                "echo '=== Wireless Interfaces ===' && " +
                "iwconfig 2>/dev/null || echo 'iwconfig not available' && " +
                "echo '=== Wireless Status ===' && " +
                "iwconfig wlan0 2>/dev/null || echo 'iwconfig wlan0 not available' && " +
                "echo '=== Network Connection Stats ===' && " +
                "netstat -tuln || echo 'netstat not available'";

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 60 seconds timeout
            var diagnosticsResult =
                await SshClientService.ExecuteCommandWithResponseAsync(deviceConfig, diagnosticCommands, cts.Token);

            if (diagnosticsResult == null || string.IsNullOrEmpty(diagnosticsResult.Result))
            {
                UpdateUIMessage("Failed to run network diagnostics. No data received.");
                return;
            }

            string diagnosticsContent = diagnosticsResult.Result;

            // Let the user know the diagnostics were completed
            UpdateUIMessage("Network diagnostics completed. Select a location to save the results...");

            // Prompt the user for a save location using SaveFileDialog
            var saveFileDialog = new Avalonia.Controls.SaveFileDialog
            {
                Title = "Save Network Diagnostics",
                DefaultExtension = "txt",
                InitialFileName = $"network_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Filters = new List<Avalonia.Controls.FileDialogFilter>
                {
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "Text Files",
                        Extensions = new List<string> { "txt" }
                    },
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "All Files",
                        Extensions = new List<string> { "*" }
                    }
                }
            };

            // Get the main window to show the dialog
            var mainWindow =
                Avalonia.Application.Current.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

            if (mainWindow == null)
            {
                UpdateUIMessage("Could not show save dialog. Using default location.");
                // Save to a default location if we can't show the dialog
                string defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    $"network_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                File.WriteAllText(defaultPath, diagnosticsContent);
                UpdateUIMessage($"Network diagnostics saved to: {defaultPath}");
                return;
            }

            // Show the save dialog and get the selected path
            string filePath = await saveFileDialog.ShowAsync(mainWindow);

            if (string.IsNullOrEmpty(filePath))
            {
                // User canceled the save operation
                UpdateUIMessage("Save operation canceled.");
                return;
            }

            // Save the diagnostics to the selected file
            File.WriteAllText(filePath, diagnosticsContent);

            UpdateUIMessage($"Network diagnostics saved to: {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to run network diagnostics");
            UpdateUIMessage($"Error running network diagnostics: {ex.Message}");
        }
    }

    #endregion

    #region Event Handlers

    private void OnAppMessage(AppMessage message)
    {
        // Ensure update happens on UI thread
        Dispatcher.UIThread.Invoke(async () =>
        {
            // Force update even if the value seems the same
            bool wasConnected = CanConnect;
            if (CanConnect != message.CanConnect)
            {
                CanConnect = message.CanConnect;
            }

            // Explicitly trigger property changed notification
            OnPropertyChanged(nameof(CanConnect));

            // If we've just connected, check the status
            if (!wasConnected && CanConnect)
            {
                await CheckAdaptiveLinkStatusAsync();
                await CheckAlinkDroneStatusAsync();
            }
        });
    }

    #endregion
}