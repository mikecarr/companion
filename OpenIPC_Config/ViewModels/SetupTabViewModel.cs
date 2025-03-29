using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData.Binding;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using OpenIPC_Config.Events;
using OpenIPC_Config.Models;
using OpenIPC_Config.Services;
using Serilog;

namespace OpenIPC_Config.ViewModels;

/// <summary>
/// ViewModel for managing device setup and configuration
/// </summary>
public partial class SetupTabViewModel : ViewModelBase
{
    #region Private Fields
    private readonly List<string> keyMessages = new()
    {
        "Checking for sysupgrade update...",
        "Version checking failed, proceeding with the installed version.",
        "Kernel",
        "New version, going to update",
        "RootFS",
        "RootFS updated to",
        "OverlayFS",
        "Unconditional reboot"
    };
    #endregion

    #region Observable Properties
    [ObservableProperty] private bool _canConnect;
    [ObservableProperty] private string _chkSumStatusColor;
    [ObservableProperty] private int _downloadProgress;
    [ObservableProperty] private ObservableCollection<string> _droneKeyActionItems;
    [ObservableProperty] private ObservableCollection<string> _firmwareVersions;
    [ObservableProperty] private bool _isCamera;
    [ObservableProperty] private bool _isGS;
    [ObservableProperty] private bool _isRadxa;
    [ObservableProperty] private bool _isProgressBarVisible;
    [ObservableProperty] private string _keyChecksum;
    [ObservableProperty] private string _localIp;
    [ObservableProperty] private ObservableCollection<string> _localSensors;
    [ObservableProperty] private string _progressText;
    [ObservableProperty] private string _scanIpLabel;
    [ObservableProperty] private string _scanIPResultTextBox;
    [ObservableProperty] private string _scanMessages;
    [ObservableProperty] private ObservableCollection<string> _scriptFileActionItems;
    [ObservableProperty] private string _selectedDroneKeyAction;
    [ObservableProperty] private string _selectedFwVersion;
    [ObservableProperty] private string _selectedScriptFileAction;
    [ObservableProperty] private string _selectedSensor;
    #endregion

    #region Commands
    private ICommand _encryptionKeyActionCommand;
    private ICommand _firmwareUpdateCommand;
    private ICommand _generateKeysCommand;
    private ICommand _offlineUpdateCommand;
    private ICommand _recvDroneKeyCommand;
    private ICommand _recvGSKeyCommand;
    private ICommand _resetCameraCommand;
    private ICommand _scanCommand;
    private ICommand _scriptFilesCommand;
    private ICommand _scriptFilesBackupCommand;
    private ICommand _scriptFilesRestoreCommand;
    private ICommand _sendDroneKeyCommand;
    private ICommand _sendGSKeyCommand;
    private ICommand _sensorDriverUpdateCommand;
    private ICommand _sensorFilesBackupCommand;
    private ICommand _sensorFilesUpdateCommand;
    #endregion
    
    // Command Properties

    #region Command Properties
    public ICommand ShowProgressBarCommand { get; private set; }
    public ICommand SendGSKeyCommand => _sendGSKeyCommand ??= new RelayCommand(SendGSKey);
    public ICommand RecvGSKeyCommand => _recvGSKeyCommand ??= new RelayCommand(RecvGSKey);
    public ICommand ScriptFilesCommand => _scriptFilesCommand ??= new RelayCommand(ScriptFilesAction);
    public ICommand EncryptionKeyActionCommand =>
        _encryptionKeyActionCommand ??= new RelayCommand<string>(EncryptionKeyAction);
    public ICommand SensorFilesUpdateCommand =>
        _sensorFilesUpdateCommand ??= new RelayCommand(SensorFilesUpdate);
    public ICommand FirmwareUpdateCommand =>
        _firmwareUpdateCommand ??= new RelayCommand(SysUpgradeFirmwareUpdate);
    public ICommand SendDroneKeyCommand =>
        _sendDroneKeyCommand ??= new RelayCommand(SendDroneKey);
    public ICommand RecvDroneKeyCommand =>
        _recvDroneKeyCommand ??= new RelayCommand(RecvDroneKey);
    public ICommand ResetCameraCommand =>
        _resetCameraCommand ??= new RelayCommand(ResetCamera);
    public ICommand OfflineUpdateCommand =>
        _offlineUpdateCommand ??= new RelayCommand(OfflineUpdate);
    public ICommand ScanCommand =>
        _scanCommand ??= new RelayCommand(ScanNetwork);
    #endregion

    #region Public Properties
    public bool IsMobile => App.OSType == "Mobile";
    public bool IsEnabledForView => CanConnect && !IsMobile;
    #endregion

    #region Constructor
    /// <summary>
    /// Initializes a new instance of SetupTabViewModel
    /// </summary>
    public SetupTabViewModel(
        ILogger logger,
        ISshClientService sshClientService,
        IEventSubscriptionService eventSubscriptionService)
        : base(logger, sshClientService, eventSubscriptionService)
    {
        InitializeCollections();
        InitializeProperties();
        SubscribeToEvents();
        InitializeCommands();
    }
    #endregion

    #region Initialization Methods
    private void InitializeProperties()
    {
        KeyChecksum = string.Empty;
        ChkSumStatusColor = "Green";
        ScanIpLabel = "192.168.1.";
    }

    private void InitializeCommands()
    {
        ShowProgressBarCommand = new RelayCommand(() => IsProgressBarVisible = true);
    }

    private void SubscribeToEvents()
    {
        EventSubscriptionService.Subscribe<AppMessageEvent, AppMessage>(OnAppMessage);
        EventSubscriptionService
            .Subscribe<DeviceContentUpdateEvent, DeviceContentUpdatedMessage>(OnDeviceContentUpdate);
        EventSubscriptionService.Subscribe<DeviceTypeChangeEvent, DeviceType>(OnDeviceTypeChange);
    }

    private void InitializeCollections()
    {
        ScriptFileActionItems = new ObservableCollectionExtended<string> { "Backup", "Restore" };
        DroneKeyActionItems = new ObservableCollectionExtended<string> { "Send", "Receive" };

        //var binariesPath = OpenIPC.GetBinariesPath();
        //var directoryPath = Path.Combine(binariesPath, "sensors");
        //PopulateSensorFileNames(directoryPath);

        InitializeFirmwareVersions();
    }

    private void InitializeFirmwareVersions()
    {
        FirmwareVersions = new ObservableCollection<string>
        {
            "ssc338q_fpv_emax-wyvern-link-nor",
            "ssc338q_fpv_openipc-mario-aio-nor",
            "ssc338q_fpv_openipc-urllc-aio-nor",
            "ssc338q_fpv_openipc-thinker-aio-nor",
            "ssc338q_fpv_emax-wyvern-link-nor",
            "ssc338q_fpv_runcam-wifilink-nor",
            "openipc.ssc338q-nor-fpv",
            "openipc.ssc338q-nor-rubyfpv",
            "openipc.ssc338q-nand-fpv",
            "openipc.ssc338q-nand-rubyfpv",
            "openipc.ssc30kq-nor-fpv",
            "openipc.ssc30kq-nor-rubyfpv",
            "openipc.hi3536dv100-nor-fpv",
            "openipc.gk7205v200-nor-fpv",
            "openipc.gk7205v200-nor-rubyfpv",
            "openipc.gk7205v210-nor-fpv",
            "openipc.gk7205v210-nor-rubyfpv",
            "openipc.gk7205v300-nor-fpv",
            "openipc.gk7205v300-nor-rubyfpv",
            "openipc.hi3516ev300-nor-fpv",
            "openipc.hi3516ev200-nor-fpv"
        };
    }
    #endregion

    #region Event Handlers
    private void OnDeviceTypeChange(DeviceType deviceType)
    {
        if (deviceType != null)
            switch (deviceType)
            {
                case DeviceType.Camera:
                    IsCamera = true;
                    IsRadxa = false;
                    break;
                case DeviceType.Radxa:
                    IsCamera = false;
                    IsRadxa = true;
                    break;
            }
    }

    private void OnDeviceContentUpdate(DeviceContentUpdatedMessage message)
    {
        if (message?.DeviceConfig?.KeyChksum != null)
        {
            KeyChecksum = message.DeviceConfig.KeyChksum;
            ChkSumStatusColor = KeyChecksum != OpenIPC.KeyMD5Sum ? "Red" : "Green";
        }
    }

    private void OnAppMessage(AppMessage appMessage)
    {
        CanConnect = appMessage.CanConnect;
    }
    #endregion

    #region Command Handlers
    private async void ScriptFilesAction()
    {
        var action = SelectedScriptFileAction;
    }

    private async void EncryptionKeyAction(string comboBoxName)
    {
        var action = SelectedDroneKeyAction;
        switch (action)
        {
            case "Send":
                if (comboBoxName.Equals("CameraKeyComboBox")) SendDroneKey();
                if (comboBoxName.Equals("RadxaKeyComboBox")) SendGSKey();
                break;
            case "Receive":
                if (comboBoxName.Equals("CameraKeyComboBox")) RecvDroneKey();
                if (comboBoxName.Equals("RadxaKeyComboBox")) RecvGSKey();
                break;
        }
    }

    private async void ScriptFilesBackup()
    {
        Log.Debug("Backup script executed");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/usr/bin/channels.sh", "channels.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/816.sh", "816.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1080.sh", "1080.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1080b.sh", "1080b.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1264.sh", "1264.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/3K.sh", "3K.sh");

        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/4K.sh", "4K.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1184p100.sh", "1184p100.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1304p80.sh", "1304p80.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1440p60.sh", "1440p60.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1920p30.sh", "1920p30.sh");

        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1080p60.sh", "1080p60.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/720p120.sh", "720p120.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/720p90.sh", "720p90.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/720p60.sh", "720p60.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1080p120.sh", "1080p120.sh");

        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1248p90.sh", "1248p90.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1304p80.sh", "1304p80.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/1416p70.sh", "1416p70.sh");
        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, "/root/kill.sh", "kill.sh");
        Log.Debug("Backup script executed...done");
    }

    private async void ScriptFilesRestore()
    {
        Log.Debug("Restore script executed...not implemented yet");
    }

    private void PopulateSensorFileNames(string directoryPath)
    {
        try
        {
            Log.Debug($"Directory path: {directoryPath}");
            var files = Directory.GetFiles(directoryPath);
            LocalSensors = new ObservableCollection<string>(files.Select(f => Path.GetFileName(f)));
        }
        catch (Exception ex)
        {
            Log.Debug($"Error populating file names: {ex.Message}");
        }
    }

    private async void SensorDriverUpdate()
    {
        Log.Debug("SensorDriverUpdate executed");
        DownloadProgress = 0;
        IsProgressBarVisible = true;
        //TODO: finish this
        //try=""
        //koup
        //echo y | pscp -scp -pw %3 %4 root@%2:/lib/modules/4.9.84/sigmastar/

        DownloadProgress = 100;
        ProgressText = "Sensor driver updated!";

        Log.Debug("SensorDriverUpdate executed..done");
    }

    private async void SensorFilesUpdate()
    {
        DownloadProgress = 0;
        IsProgressBarVisible = true;

        var selectedSensor = SelectedSensor;
        if (selectedSensor == null)
        {
            MessageBoxManager.GetMessageBoxStandard("Error", "No sensor selected");

            var box = MessageBoxManager
                .GetMessageBoxStandard("error!", "No sensor selected!");
            await box.ShowAsync();
            return;
        }

        ProgressText = "Starting upload...";
        DownloadProgress = 50;
        await SshClientService.UploadBinaryAsync(DeviceConfig.Instance, OpenIPC.RemoteSensorsFolder,
            OpenIPC.FileType.Sensors, selectedSensor);

        ProgressText = "Updating Majestic file...";
        DownloadProgress = 75;
        // update majestic file
        // what is .video0.sensorConfig used for?
        //SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, $"yaml-cli -s .video0.sensorConfig {OpenIPC_Config.RemoteSensorsFolder}/{selectedSensor}");
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance,
            $"yaml-cli -s .isp.sensorConfig {OpenIPC.RemoteSensorsFolder}/{selectedSensor}");

        // echo y | pscp -scp -pw %3 sensors/%4 root@%2:/etc/sensors/ 
        //     plink -ssh root@%2 -pw %3 yaml-cli -s .isp.sensorConfig /etc/sensors/%4
        //echo y | pscp -scp -pw %3 %4 root@%2:/etc/sensors/

        //SshClientService.UploadDirectoryAsync(DeviceConfig.Instance, OpenIPC_Config.LocalSensorsFolder,
        // OpenIPC_Config.RemoteSensorsFolder);
        ProgressText = "Restarting Majestic...";
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.MajesticRestartCommand);

        ProgressText = "Done updating sensor...";
        DownloadProgress = 100;
    }

    private async void OfflineUpdate()
    {
        Log.Debug("OfflineUpdate executed");
        IsProgressBarVisible = true;
        DownloadStart();
        //Log.Debug("OfflineUpdate executed..done");
    }

    private async void ScanNetwork()
    {
        ScanMessages = "Starting scan...";
        //ScanIPResultTextBox = "Available IP Addresses on your network:";
        await Task.Delay(500); // Replace Thread.Sleep with async-friendly delay

        var pingTasks = new List<Task>();

        ScanIPResultTextBox = string.Empty;

        for (var i = 0; i < 254; i++)
        {
            var host = ScanIpLabel + i;
            Log.Debug($"Scanning {host}()");

            // Use async ping operation
            var pingTask = Task.Run(async () =>
            {
                var ping = new Ping();
                var pingReply = await ping.SendPingAsync(host);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ScanMessages = $"Scanned {host}, result: {pingReply.Status}";
                    //ScanIPResultTextBox += Environment.NewLine + host + ": " + pingReply.Status.ToString();
                    if (pingReply.Status == IPStatus.Success) ScanIPResultTextBox += host + Environment.NewLine;
                });
            });
            pingTasks.Add(pingTask);
        }

        ScanMessages = "Waiting for scan results.....";
        // Wait for all ping tasks to complete
        await Task.WhenAll(pingTasks);

        ScanMessages = "Scan completed";
        var confirmBox = MessageBoxManager.GetMessageBoxStandard("Scan completed", "Scan completed");
        await confirmBox.ShowAsync();
    }
    #endregion

    /// <summary>
    ///     Extracts a value from a string using a regular expression pattern.
    /// </summary>
    /// <param name="input">The string to extract the value from.</param>
    /// <param name="pattern">The regular expression pattern to use for extraction.</param>
    /// <returns>The extracted value, or null if the pattern does not match.</returns>
    public static string ExtractValue(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        if (match.Success)
        {
            if (match.Groups.Count > 1)
                return match.Groups[1].Value;
            return match.Groups[0].Value;
        }

        return null;
    }

    private async Task UploadFirmwareAsync(string firmwarePath, string remotePath)
    {
        DownloadProgress = 50;
        ProgressText = "Uploading firmware...";
        await SshClientService.UploadFileAsync(DeviceConfig.Instance, firmwarePath, remotePath);
    }

    private async Task DecompressFirmwareAsync(string remoteFilePath)
    {
        DownloadProgress = 75;
        ProgressText = "Decompressing firmware...";
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, $"gzip -d {remoteFilePath}");
    }

    private async Task ExtractFirmwareAsync(string tarFilePath, string destinationPath)
    {
        DownloadProgress = 100;
        ProgressText = "Extracting firmware...";
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance,
            $"tar -xvf {tarFilePath} -C {destinationPath}");
    }

    /// <summary>
    ///     Downloads the latest firmware version of the selected type from the official OpenIPC_Config repositories.
    /// </summary>
    /// <param name="SelectedFwVersion">
    ///     The firmware version to download. This should be one of the following:
    ///     "ssc338q_fpv_emax-wyvern-link-nor", "ssc338q_fpv_openipc-mario-aio-nor", "ssc338q_fpv_openipc-urllc-aio-nor",
    ///     "ssc338q_fpv_runcam-wifilink-nor".
    /// </param>
    public async Task DownloadStart()
    {
        //TODO: add more checks here, this can brick a device
        UpdateUIMessage("Upgrading device...");
        IsProgressBarVisible = true; // Show the progress bar when the download starts
        var kernelPath = string.Empty;
        var rootfsPath = string.Empty;
        var sensorType = string.Empty;

        var url = string.Empty;
        if (SelectedFwVersion == "ssc338q_fpv_emax-wyvern-link-nor" ||
            SelectedFwVersion == "ssc338q_fpv_openipc-mario-aio-nor" ||
            SelectedFwVersion == "ssc338q_fpv_openipc-urllc-aio-nor" ||
            SelectedFwVersion == "ssc338q_fpv_openipc-thinker-aio-nor" ||
            SelectedFwVersion == "ssc338q_fpv_emax-wyvern-link-nor" ||
            SelectedFwVersion == "ssc338q_fpv_runcam-wifilink-nor")
        {
            url = $"https://github.com/OpenIPC/builder/releases/download/latest/{SelectedFwVersion}.tgz";
            var aioPattern = "^[^_]+";
            sensorType = ExtractValue($"{SelectedFwVersion}", aioPattern);
        }
        else
        {
            url = $"https://github.com/OpenIPC/firmware/releases/download/latest/{SelectedFwVersion}.tgz";
            var openipcPattern = @"openipc\.([^-]+)";
            sensorType = ExtractValue($"{SelectedFwVersion}", openipcPattern);
        }

        if (SelectedFwVersion != string.Empty && sensorType != string.Empty)
        {
            var firmwarePath = Path.Combine(OpenIPC.AppDataConfigDirectory, "firmware",
                $"{SelectedFwVersion}.tgz");


            //var firmwarePath = $"{Models.OpenIPC.AppDataConfigDirectory}/firmware/{SelectedFwVersion}.tgz";
            var localTmpPath = $"{OpenIPC.LocalTempFolder}";
            if (!Directory.Exists(localTmpPath)) Directory.CreateDirectory(localTmpPath);

            var firmwareUrl = new Uri(url).ToString();
            Log.Debug($"Downloading firmware {firmwareUrl}");

            // Reset progress and attach progress event
            DownloadProgress = 0;
            ProgressText = "Starting download...";

            // Use HttpClient instead of WebClient
            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync(firmwareUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    // Download file with progress
                    var totalBytes = response.Content.Headers.ContentLength ?? 1;
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(firmwarePath, FileMode.Create, FileAccess.Write,
                               FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        int bytesRead;
                        double totalRead = 0;

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                            DownloadProgress =
                                (int)(totalRead / totalBytes * 50); // Assume download is 50% of the process
                            ProgressText = $"Downloading... {DownloadProgress}%";
                        }
                    }
                }
            }

            // Continue with the rest of the process
            DownloadProgress = 50;
            ProgressText = "Download complete, starting upload...";

            // Step 2: Upload file
            var remotePath = $"/tmp/{SelectedFwVersion}.tgz";
            await UploadFirmwareAsync(firmwarePath, remotePath);

            ProgressText = "Upload complete, decompressing...";

            // Step 3: Decompress using gzip
            await DecompressFirmwareAsync(remotePath);

            ProgressText = "Decompression complete, extracting files...";

            // Step 4: Extract firmware
            var tarFilePath = remotePath.Replace(".tgz", ".tar");
            await ExtractFirmwareAsync(tarFilePath, "/tmp");

            DownloadProgress = 100;
            ProgressText = "Extraction complete, upgrading system...";

            // Step 5: Execute sysupgrade

            var msgBox = MessageBoxManager.GetMessageBoxStandard("Confirm",
                $"This will download and update your camera to {SelectedFwVersion}, continue?", ButtonEnum.OkAbort);

            var result = await msgBox.ShowAsync();
            if (result == ButtonResult.Abort)
            {
                Log.Debug("Upgrade Cancelled!");
                ;
                UpdateUIMessage("Upgrade Cancelled!");
                DownloadProgress = 100;
                ProgressText = "Upgrade Cancelled!!";
                return;
            }


            kernelPath = $"/tmp/uImage.{sensorType}";
            rootfsPath = $"/tmp/rootfs.squashfs.{sensorType}";

            //sysupgrade --kernel=/tmp/uImage.%4 --rootfs=/tmp/rootfs.squashfs.%4 -n
            // await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance,
            //     $"sysupgrade --kernel={kernelPath} --rootfs={rootfsPath} -n");

            using var cts = new CancellationTokenSource();

            // Provide a way for the user to cancel (e.g., a button)
            var cancelToken = cts.Token;

            PerformSystemUpgradeAsync(kernelPath, rootfsPath, cancelToken);
        }
    }

    public async Task PerformSystemUpgradeAsync(string kernelPath, string rootfsPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ProgressText = "Starting system upgrade...";
            DownloadProgress = 0;
            var outputBuffer = new StringBuilder();

            Log.Information($"Running command: sysupgrade --kernel={kernelPath} --rootfs={rootfsPath} -n");

            // Pass cancellation token to the command
            await SshClientService.ExecuteCommandWithProgressAsync(
                DeviceConfig.Instance,
                $"sysupgrade --kernel={kernelPath} --rootfs={rootfsPath} -n",
                output =>
                {
                    outputBuffer.AppendLine(output);


                    // Process buffer at intervals
                    if (outputBuffer.Length > 500 || output.Contains("Conditional reboot"))
                    {
                        var bufferContent = outputBuffer.ToString();
                        outputBuffer.Clear();

                        var MaxProgressTextLength = 100;
                        // Update the UI incrementally
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            // Trim the output if it exceeds the maximum length
                            var trimmedOutput = output.Length > MaxProgressTextLength
                                ? output.Substring(0, MaxProgressTextLength) + "..."
                                : output;

                            //ProgressText = trimmedOutput;
                            //ProcessOutputAsync(trimmedOutput);
                            if (bufferContent.Contains("Update kernel"))
                            {
                                ProgressText = "Updating Kernel";
                                DownloadProgress = 25;
                            }

                            if (bufferContent.Contains("Update rootfs"))
                            {
                                ProgressText = "Updating RootFS";
                                DownloadProgress = 50;
                            }

                            if (bufferContent.Contains("Erase overlay partition"))
                            {
                                ProgressText = "Erasing overlay partition";
                                DownloadProgress = 75;
                            }

                            // Dynamically update progress based on command output (if possible)
                            //if (bufferContent.Contains("sysupgrade")) DownloadProgress = 80;
                            if (bufferContent.Contains("Conditional reboot"))
                            {
                                ProgressText = "Rebooting...";
                                DownloadProgress = 98;
                            }

                            //ProgressText = bufferContent;
                            Log.Debug(bufferContent);
                        });
                    }
                },
                cancellationToken
            );

            // Complete progress after execution
            DownloadProgress = 100;
            ProgressText = "System upgrade complete, reboting device!";
            UpdateUIMessage("Upgrading device...done");
        }
        catch (OperationCanceledException)
        {
            ProgressText = "System upgrade canceled.";
            Log.Warning("System upgrade operation was canceled.");
        }
        catch (Exception ex)
        {
            ProgressText = $"Error during system upgrade: {ex.Message}";
            Log.Error($"Error during system upgrade: {ex}");
        }
    }

    private async Task ProcessOutputAsync(string output)
    {
        // Check if the output contains any key message
        if (keyMessages.Any(key => output.Contains(key, StringComparison.OrdinalIgnoreCase)))
        {
            // Update ProgressText with the key message
            await Dispatcher.UIThread.InvokeAsync(() => ProgressText = output);
            //ProgressText = output;

            // Optionally log the key message
            Log.Information($"Key message displayed: {output}");
        }
        else
        {
            // Log non-key messages for debugging (optional)
            Log.Debug($"Non-key message ignored: {output}");
        }
    }

    private async void SysUpgradeFirmwareUpdate()
    {
        Log.Debug("FirmwareUpdate executed");
        // if "%1" == "sysup" (
        //     plink -ssh root@%2 -pw %3 sysupgrade -k -r -n --force_ver
        //     )
        Log.Debug("This command will only succeed if the device has access to the internet");
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.FirmwareUpdateCommand);
        Log.Debug("FirmwareUpdate executed..done");
    }

    private async void RecvDroneKey()
    {
        Log.Debug("RecvDroneKeyCommand executed");

        var droneKeyPath = Path.Combine(OpenIPC.AppDataConfigDirectory, "drone.key");
        if (File.Exists(droneKeyPath))
        {
            Log.Debug("drone.key already exists locally, do you want to overwrite it?");
            var msBox = MessageBoxManager.GetMessageBoxStandard("File exists!",
                "File drone.key already exists locally, do you want to overwrite it?", ButtonEnum.OkCancel);

            var result = await msBox.ShowAsync();
            if (result == ButtonResult.Cancel)
            {
                Log.Debug("local drone.key was not overwritten");
                return;
            }
        }

        await SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance,
            OpenIPC.RemoteEtcFolder + "/drone.key",
            droneKeyPath);
        if (!File.Exists(droneKeyPath)) Log.Debug("RecvDroneKeyCommand failed");

        Log.Debug("RecvDroneKeyCommand executed...done");
    }

    private async void SendDroneKey()
    {
        Log.Debug("SendDroneKey executed");
        // if "%1" == "keysulcam" (
        //     echo y | pscp -scp -pw %3 drone.key root@%2:/etc
        //     )
        await SshClientService.UploadFileAsync(DeviceConfig.Instance, OpenIPC.DroneKeyPath,
            OpenIPC.RemoteDroneKeyPath);

        Log.Debug("SendDroneKey executed...done");
    }

    private async void ResetCamera()
    {
        Log.Debug("ResetCamera executed");
        // if "%1" == "resetcam" (
        //     plink -ssh root@%2 -pw %3 firstboot
        //     )
        var box = MessageBoxManager
            .GetMessageBoxStandard("Warning!", "All OpenIPC_Config camera settings will be restored to default.",
                ButtonEnum.OkAbort);
        var result = await box.ShowAsync();
        if (result == ButtonResult.Ok)
        {
            SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.ResetCameraCommand);
            await Task.Delay(1000); // Non-blocking pause
        }
        else
        {
            Log.Debug("ResetCamera Aborted!");
            var confirmBox = MessageBoxManager
                .GetMessageBoxStandard("Warning!", "No changes applied.");
            await confirmBox.ShowAsync();
            return;
        }

        return;
        Log.Debug("ResetCamera executed...done");
    }

    private async void SensorFilesBackup()
    {
        // if "%1" == "bindl" (
        //     echo y | mkdir backup
        // echo y | pscp -scp -pw %3 root@%2:/etc/sensors/%4 ./backup/
        //     )
        Log.Debug("SensorFilesBackup executed");
        await SshClientService.DownloadDirectoryAsync(DeviceConfig.Instance, "/etc/sensors",
            $"{OpenIPC.LocalBackUpFolder}");
        Log.Debug("SensorFilesBackup executed...done");
    }

    private async void GenerateKeys()
    {
        // keysgen " + String.Format("{0}", txtIP.Text) + " " + txtPassword.Text
        // plink -ssh root@%2 -pw %3 wfb_keygen
        // plink -ssh root@%2 -pw %3 cp /root/gs.key /etc/

        try
        {
            UpdateUIMessage("Generating keys");
            await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.BackUpGsKeysIfExist);
            await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.GenerateKeys);
            await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.CopyGenerateKeys);
            UpdateUIMessage("Generating keys...done");
        }
        catch (Exception e)
        {
            Log.Error(e.ToString());
        }
    }

    private async void SendGSKey()
    {
        try
        {
            UpdateUIMessage("Sending keys...");
            await SshClientService.UploadFileAsync(DeviceConfig.Instance, OpenIPC.GsKeyPath,
                OpenIPC.RemoteGsKeyPath);

            UpdateUIMessage("Restarting OpenIPC Service on GS");
            await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.GsWfbStopCommand);
            await Task.Delay(500); // Non-blocking pause
            await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.GsWfbStartCommand);

            UpdateUIMessage("Sending keys...done");
        }
        catch (Exception e)
        {
            Log.Error(e.ToString());
            throw;
        }
    }

    private async void RecvGSKey()
    {
        UpdateUIMessage("Receiving keys...");

        SshClientService.DownloadFileLocalAsync(DeviceConfig.Instance, OpenIPC.RemoteGsKeyPath,
            $"{OpenIPC.LocalTempFolder}/gs.key");
        await Task.Delay(1000); // Non-blocking pause

        UpdateUIMessage("Receiving keys...done");
    }
}