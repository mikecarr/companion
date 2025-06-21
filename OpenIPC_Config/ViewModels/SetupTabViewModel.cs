using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
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
    [ObservableProperty] private ObservableCollectionExtended<string> _keyManagementActionItems;
    [ObservableProperty] private string _selectedKeyManagementAction;
    [ObservableProperty] private IBrush _keyValidationColor;
    [ObservableProperty] private string _keyValidationMessage;

    private string _localKeyPath;
    private string _localDefaultKeyPath;
    private bool _isGeneratingKey;
    
    IMessageBoxService _messageBoxService;
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
    public ICommand KeyManagementCommand => 
        _keyManagementCommand ??= new AsyncRelayCommand(ExecuteKeyManagementActionAsync);
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
        IEventSubscriptionService eventSubscriptionService,
        IMessageBoxService messageBoxService)
        : base(logger, sshClientService, eventSubscriptionService)
    {
        _messageBoxService = messageBoxService;
        ;
        InitializeKeyManagement();
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

    private void InitializeKeyManagement()
    {
        // Initialize key management properties
        KeyManagementActionItems = new ObservableCollectionExtended<string>
        {
            //"Generate New Key",
            "Use Default Key",
            "Upload Key",
            "Download Key from Device",
            "Verify Key",
            "Get Key Checksum"
        };
    
        SelectedKeyManagementAction = KeyManagementActionItems[0];
        KeyValidationColor = Brushes.Gray;
        KeyValidationMessage = "No key validation performed";
    
        // Create key directories if they don't exist
        Directory.CreateDirectory(Path.Combine(OpenIPC.AppDataConfigDirectory, "keys"));
    
        // Initialize local key path
        _localKeyPath = Path.Combine(OpenIPC.AppDataConfigDirectory, "keys", "drone.key");
        
        _localDefaultKeyPath = Path.Combine(OpenIPC.GetBinariesPath(), "drone.key");
    
        Logger.Debug("Key management initialized");
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

        var binariesPath = OpenIPC.GetBinariesPath();
        var directoryPath = Path.Combine(binariesPath, "sensors");
        PopulateSensorFileNames(directoryPath);

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

    // Key Management Methods to add to your SetupTabViewModel.cs file

private ICommand _keyManagementCommand;

private async Task ExecuteKeyManagementActionAsync()
{
    try
    {
        if (!CanConnect)
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Connection Required", 
                "Please connect to a device before performing key management actions.",
                ButtonEnum.Ok);
            await msgBox.ShowAsync();
            return;
        }
        
        UpdateUIMessage($"Executing: {SelectedKeyManagementAction}");
        
        switch (SelectedKeyManagementAction)
        {
            case "Use Default Key":
                await UploadKeyAsync(_localDefaultKeyPath);
                break;
            
            case "Generate New Key":
                await GenerateNewKeyAsync();
                break;
            case "Upload Key":
                await UploadKeyAsync();
                break;
            case "Download Key from Device":
                await DownloadKeyFromDeviceAsync();
                break;
            case "Verify Key":
                await VerifyKeyAsync();
                break;
            case "Get Key Checksum":
                await GetKeyChecksumAsync();
                break;
        }
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error in key management");
        UpdateUIMessage($"Error: {ex.Message}");
        
        var msgBox = MessageBoxManager.GetMessageBoxStandard(
            "Error", 
            $"An error occurred during key management: {ex.Message}",
            ButtonEnum.Ok);
        await msgBox.ShowAsync();
    }
}

private async Task GenerateNewKeyAsync()
{
    try
    {
        _isGeneratingKey = true;
        IsProgressBarVisible = true;
        ProgressText = "Generating secure key...";
        DownloadProgress = 0;
        
        // Create keys directory if it doesn't exist
        var keysDir = Path.Combine(OpenIPC.AppDataConfigDirectory, "keys");
        if (!Directory.Exists(keysDir))
        {
            Directory.CreateDirectory(keysDir);
        }
        
        // Generate key file with timestamp
        string keyFileName = $"drone_key_{DateTime.Now:yyyyMMdd_HHmmss}.key";
        _localKeyPath = Path.Combine(keysDir, keyFileName);
        
        await Task.Run(() => 
        {
            // Generate a secure random key
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] keyData = new byte[32]; // 256-bit key
                rng.GetBytes(keyData);
                
                // Save key to file
                File.WriteAllBytes(_localKeyPath, keyData);
                
                // Create a copy as drone.key for compatibility
                File.Copy(_localKeyPath, Path.Combine(keysDir, "drone.key"), true);
            }
        });
        
        DownloadProgress = 50;
        ProgressText = "Key generated, calculating checksum...";
        
        // Calculate checksum
        var keyData = await File.ReadAllBytesAsync(_localKeyPath);
        var checksum = CalculateChecksum(keyData);
        
        DownloadProgress = 100;
        ProgressText = "Key generation complete";
        
        // Update the UI
        KeyChecksum = checksum;
        ChkSumStatusColor = "Green";
        KeyValidationColor = Brushes.Green;
        KeyValidationMessage = "New key generated successfully";
        
        var msgBox = MessageBoxManager.GetMessageBoxStandard(
            "Key Generated", 
            $"New encryption key generated and saved to:\n{_localKeyPath}\n\nChecksum: {checksum}",
            ButtonEnum.Ok);
        await msgBox.ShowAsync();
        
        // Ask if user wants to upload the key to the device
        var uploadMsg = MessageBoxManager.GetMessageBoxStandard(
            "Upload Key", 
            "Do you want to upload this key to the connected device?",
            ButtonEnum.YesNo);
        var result = await uploadMsg.ShowAsync();
        
        if (result == ButtonResult.Yes)
        {
            await UploadKeyAsync(_localKeyPath);
        }
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error generating key");
        KeyValidationColor = Brushes.Red;
        KeyValidationMessage = "Error generating key";
        throw;
    }
    finally
    {
        _isGeneratingKey = false;
        IsProgressBarVisible = false;
    }
}
private async Task UploadKeyAsync(string specificKeyPath = null)
{
    try
    {
        IsProgressBarVisible = true;
        ProgressText = "Preparing to upload key...";
        DownloadProgress = 0;
        
        string keyPath = specificKeyPath;
        
        // If no specific key path was provided, ask the user to select a key file
        if (string.IsNullOrEmpty(keyPath))
        {
            var openFileDialog = new Avalonia.Controls.OpenFileDialog
            {
                Title = "Select Key File",
                Filters = new List<Avalonia.Controls.FileDialogFilter>
                {
                    new Avalonia.Controls.FileDialogFilter { Name = "Key Files", Extensions = new List<string> { "key" } }
                },
                Directory = Path.Combine(OpenIPC.AppDataConfigDirectory, "keys")
            };
            
            var window = Avalonia.Application.Current.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var result = await openFileDialog.ShowAsync(window?.MainWindow);
            
            if (result == null || result.Length == 0)
            {
                IsProgressBarVisible = false;
                return;
            }
            
            keyPath = result[0];
        }
        
        ProgressText = "Reading key file...";
        DownloadProgress = 25;
        
        // Read the key file
        byte[] keyData = await File.ReadAllBytesAsync(keyPath);
        
        // Get the checksum before uploading
        string localChecksum = CalculateChecksum(keyData);
        
        ProgressText = "Uploading key to device...";
        DownloadProgress = 50;
        
        // Upload the key to the device
        await SshClientService.UploadFileAsync(DeviceConfig.Instance, keyPath, OpenIPC.RemoteDroneKeyPath);
        
        // Set proper permissions
        ProgressText = "Setting key permissions...";
        DownloadProgress = 75;
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, "chmod 600 /etc/drone.key");
        
        // Restart services if needed
        ProgressText = "Restarting services...";
        DownloadProgress = 90;
        await SshClientService.ExecuteCommandAsync(DeviceConfig.Instance, DeviceCommands.WfbRestartCommand);
        
        // Get the checksum from the device
        ProgressText = "Verifying key upload...";
        string remoteChecksum = await GetDeviceKeyChecksumAsync();
        
        DownloadProgress = 100;
        ProgressText = "Key upload complete";
        
        // Verify the checksums match
        if (localChecksum == remoteChecksum)
        {
            KeyChecksum = remoteChecksum;
            ChkSumStatusColor = "Green";
            KeyValidationColor = Brushes.Green;
            KeyValidationMessage = "Key uploaded and verified successfully";
            
            // Update local drone.key if a different file was uploaded
            if (keyPath != Path.Combine(OpenIPC.AppDataConfigDirectory, "keys", "drone.key"))
            {
                File.Copy(keyPath, Path.Combine(OpenIPC.AppDataConfigDirectory, "keys", "drone.key"), true);
            }
        }
        else
        {
            KeyChecksum = $"Local: {localChecksum}, Device: {remoteChecksum}";
            ChkSumStatusColor = "Red";
            KeyValidationColor = Brushes.Red;
            KeyValidationMessage = "Key upload failed verification";
            
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Verification Failed", 
                "The uploaded key could not be verified. Checksums do not match.",
                ButtonEnum.Ok);
            await msgBox.ShowAsync();
        }
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error uploading key");
        KeyValidationColor = Brushes.Red;
        KeyValidationMessage = "Error uploading key";
        throw;
    }
    finally
    {
        IsProgressBarVisible = false;
    }
}

private async Task DownloadKeyFromDeviceAsync()
{
    try
    {
        IsProgressBarVisible = true;
        ProgressText = "Downloading key from device...";
        DownloadProgress = 0;
        
        // Create keys directory if it doesn't exist
        var keysDir = Path.Combine(OpenIPC.AppDataConfigDirectory, "keys");
        if (!Directory.Exists(keysDir))
        {
            Directory.CreateDirectory(keysDir);
        }
        
        // Check if drone.key already exists locally
        var droneKeyPath = Path.Combine(keysDir, "drone.key");
        if (File.Exists(droneKeyPath))
        {
            Logger.Debug("drone.key already exists locally, checking if user wants to overwrite");
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "File Exists", 
                "A drone.key file already exists locally. Do you want to overwrite it?",
                ButtonEnum.YesNo);
            
            var result = await msgBox.ShowAsync();
            if (result == ButtonResult.No)
            {
                Logger.Debug("User chose not to overwrite existing drone.key");
                IsProgressBarVisible = false;
                return;
            }
        }
        
        DownloadProgress = 25;
        
        // Create a timestamped version as well
        string keyFileName = $"drone_key_from_device_{DateTime.Now:yyyyMMdd_HHmmss}.key";
        string timestampedKeyPath = Path.Combine(keysDir, keyFileName);
        
        DownloadProgress = 50;
        
        // Download the key from the device
        await SshClientService.DownloadFileLocalAsync(
            DeviceConfig.Instance,
            OpenIPC.RemoteDroneKeyPath,
            droneKeyPath);
        
        DownloadProgress = 75;
        
        // Make a timestamped copy
        if (File.Exists(droneKeyPath))
        {
            File.Copy(droneKeyPath, timestampedKeyPath, true);
        }
        
        ProgressText = "Calculating key checksum...";
        DownloadProgress = 90;
        
        // Calculate and display checksum
        if (File.Exists(droneKeyPath))
        {
            var keyData = await File.ReadAllBytesAsync(droneKeyPath);
            var checksum = CalculateChecksum(keyData);
            
            KeyChecksum = checksum;
            ChkSumStatusColor = "Green";
            KeyValidationColor = Brushes.Green;
            KeyValidationMessage = "Key downloaded successfully";
            
            DownloadProgress = 100;
            ProgressText = "Key download complete";
            
            await _messageBoxService.ShowMessageBoxWithFolderLink(
                "Download Complete", 
                $"Key downloaded successfully to:\n{droneKeyPath}\n\nChecksum: {checksum}",
                droneKeyPath);
            
        }
        else
        {
            KeyValidationColor = Brushes.Red;
            KeyValidationMessage = "Key download failed";
            
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Download Failed", 
                "Failed to download key from device.",
                ButtonEnum.Ok);
            await msgBox.ShowAsync();
        }
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error downloading key");
        KeyValidationColor = Brushes.Red;
        KeyValidationMessage = "Error downloading key";
        throw;
    }
    finally
    {
        IsProgressBarVisible = false;
    }
}

private async Task VerifyKeyAsync()
{
    try
    {
        IsProgressBarVisible = true;
        ProgressText = "Verifying key...";
        DownloadProgress = 0;
        
        // Get the device key checksum
        DownloadProgress = 30;
        ProgressText = "Getting device key checksum...";
        string deviceChecksum = await GetDeviceKeyChecksumAsync();
        
        if (string.IsNullOrEmpty(deviceChecksum))
        {
            KeyValidationColor = Brushes.Red;
            KeyValidationMessage = "Could not get device key checksum";
            return;
        }
        
        DownloadProgress = 60;
        ProgressText = "Comparing with local key...";
        
        // Check if we have a local key to compare with
        var droneKeyPath = Path.Combine(OpenIPC.AppDataConfigDirectory, "keys", "drone.key");
        if (File.Exists(droneKeyPath))
        {
            // Calculate local key checksum
            var localKeyData = await File.ReadAllBytesAsync(droneKeyPath);
            var localChecksum = CalculateChecksum(localKeyData);
            
            DownloadProgress = 100;
            ProgressText = "Key verification complete";
            
            // Compare checksums
            if (deviceChecksum.Equals(localChecksum, StringComparison.OrdinalIgnoreCase))
            {
                KeyChecksum = deviceChecksum;
                ChkSumStatusColor = "Green";
                KeyValidationColor = Brushes.Green;
                KeyValidationMessage = "Key verified successfully";
                
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Verification Success", 
                    "The device key matches the local key.\nChecksum: " + deviceChecksum,
                    ButtonEnum.Ok);
                await msgBox.ShowAsync();
            }
            else
            {
                KeyChecksum = $"Local: {localChecksum}, Device: {deviceChecksum}";
                ChkSumStatusColor = "Red";
                KeyValidationColor = Brushes.Red;
                KeyValidationMessage = "Key verification failed - checksums don't match";
                
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Verification Failed", 
                    "The device key does not match the local key.\n\n" +
                    $"Local: {localChecksum}\nDevice: {deviceChecksum}",
                    ButtonEnum.Ok);
                await msgBox.ShowAsync();
            }
        }
        else
        {
            // We only have the device key checksum
            KeyChecksum = deviceChecksum;
            ChkSumStatusColor = "Yellow";
            KeyValidationColor = Brushes.Yellow;
            KeyValidationMessage = "No local key to compare with";
            
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "No Local Key", 
                "No local key file found to compare with the device key.\n\n" +
                $"Device key checksum: {deviceChecksum}\n\n" +
                "Would you like to download the key from the device?",
                ButtonEnum.YesNo);
            
            var result = await msgBox.ShowAsync();
            if (result == ButtonResult.Yes)
            {
                await DownloadKeyFromDeviceAsync();
            }
        }
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error verifying key");
        KeyValidationColor = Brushes.Red;
        KeyValidationMessage = "Error verifying key";
        throw;
    }
    finally
    {
        IsProgressBarVisible = false;
    }
}

private async Task GetKeyChecksumAsync()
{
    try
    {
        IsProgressBarVisible = true;
        ProgressText = "Getting key checksum...";
        DownloadProgress = 50;
        
        // Get the checksum from the device
        string deviceChecksum = await GetDeviceKeyChecksumAsync();
        
        DownloadProgress = 100;
        ProgressText = "Checksum retrieved";
        
        if (!string.IsNullOrEmpty(deviceChecksum))
        {
            KeyChecksum = deviceChecksum;
            ChkSumStatusColor = "Green";
            KeyValidationColor = Brushes.Green;
            KeyValidationMessage = "Checksum retrieved successfully";
        }
        else
        {
            KeyValidationColor = Brushes.Red;
            KeyValidationMessage = "Could not get key checksum from device";
        }
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error getting key checksum");
        KeyValidationColor = Brushes.Red;
        KeyValidationMessage = "Error getting key checksum";
        throw;
    }
    finally
    {
        IsProgressBarVisible = false;
    }
}

private async Task<string> GetDeviceKeyChecksumAsync()
{
    try
    {
        // Download the key file from the device as bytes
        var keyData = await SshClientService.DownloadFileBytesAsync(
            DeviceConfig.Instance, 
            OpenIPC.RemoteDroneKeyPath);
        
        if (keyData == null || keyData.Length == 0)
        {
            Logger.Warning("Key file not found on device or is empty");
            return null;
        }
        
        // Calculate the checksum on the client side
        string checksum = CalculateChecksum(keyData);
        Logger.Debug($"Device key checksum: {checksum}");
        
        return checksum;
    }
    catch (Exception ex)
    {
        Logger.Error(ex, "Error getting device key checksum");
        return null;
    }
}

private string CalculateChecksum(byte[] data)
{
    using (var md5 = MD5.Create())
    {
        byte[] hashBytes = md5.ComputeHash(data);
        var sb = new StringBuilder();
        
        // Convert bytes to hex string
        for (int i = 0; i < hashBytes.Length; i++)
        {
            sb.Append(hashBytes[i].ToString("x2"));
        }
        
        return sb.ToString();
    }
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

    public async void SensorFilesUpdate()
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
    await Task.Delay(500);

    ScanIPResultTextBox = string.Empty;
    var foundHosts = new ConcurrentBag<string>(); // Thread-safe collection
    var totalHosts = 254;
    var completedScans = 0;

    // Get the PingService instance
    var pingService = PingService.Instance(Logger);

    // Batch processing to avoid overwhelming the system
    const int batchSize = 20; // Process 20 IPs at a time
    var allHosts = Enumerable.Range(1, totalHosts).Select(i => ScanIpLabel + i).ToList();
    
    Logger.Information($"Starting network scan of {totalHosts} hosts in batches of {batchSize}");

    for (int batchStart = 0; batchStart < allHosts.Count; batchStart += batchSize)
    {
        var batch = allHosts.Skip(batchStart).Take(batchSize).ToList();
        
        // Process current batch
        var batchTasks = batch.Select(async host =>
        {
            try
            {
                Logger.Verbose($"Scanning {host}");
                
                // Use the scan-specific ping method with shorter timeout
                var result = await pingService.SendScanPingAsync(host, 1500); // Reduced timeout for faster scanning
                
                // Thread-safe increment
                var currentCompleted = Interlocked.Increment(ref completedScans);
                var progressPercent = (currentCompleted * 100) / totalHosts;
                
                // Update UI on main thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ScanMessages = $"Scanned {currentCompleted}/{totalHosts} hosts ({progressPercent}%) - Current: {host}";
                    
                    if (result.Success)
                    {
                        foundHosts.Add(host);
                        ScanIPResultTextBox += $"{host} - {result.Method} ({result.RoundtripTime}ms)" + Environment.NewLine;
                        Logger.Information($"Found active host: {host} via {result.Method}");
                    }
                    else
                    {
                        Logger.Verbose($"Host {host} not reachable: {result.ErrorMessage}");
                    }
                });
                
                return result;
            }
            catch (Exception ex)
            {
                var currentCompleted = Interlocked.Increment(ref completedScans);
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var progressPercent = (currentCompleted * 100) / totalHosts;
                    ScanMessages = $"Scanned {currentCompleted}/{totalHosts} hosts ({progressPercent}%) - Error on: {host}";
                });
                
                Logger.Warning($"Error scanning {host}: {ex.Message}");
                return null;
            }
        }).ToList();
        
        // Wait for current batch to complete before starting next batch
        await Task.WhenAll(batchTasks);
        
        // Small delay between batches to prevent overwhelming the network/system
        if (batchStart + batchSize < allHosts.Count)
        {
            await Task.Delay(100); // 100ms delay between batches
        }
        
        Logger.Debug($"Completed batch {(batchStart / batchSize) + 1}/{(int)Math.Ceiling((double)allHosts.Count / batchSize)}");
    }

    // Convert thread-safe collection to list for final processing
    var foundHostsList = foundHosts.ToList();
    
    // Update final results
    await Dispatcher.UIThread.InvokeAsync(() =>
    {
        ScanMessages = $"Scan completed - Found {foundHostsList.Count} active hosts";
        
        if (foundHostsList.Count > 0)
        {
            ScanIPResultTextBox += Environment.NewLine + $"=== Found {foundHostsList.Count} active hosts ===";
            
            // Sort and display found hosts
            var sortedHosts = foundHostsList.OrderBy(h => 
            {
                var parts = h.Split('.');
                return int.Parse(parts[3]); // Sort by last octet
            }).ToList();
            
            ScanIPResultTextBox += Environment.NewLine + "Sorted list:";
            foreach (var host in sortedHosts)
            {
                ScanIPResultTextBox += Environment.NewLine + "• " + host;
            }
        }
        else
        {
            ScanIPResultTextBox = "No active hosts found in the specified range." + Environment.NewLine +
                                 "This could mean:" + Environment.NewLine +
                                 "• No devices are online in this subnet" + Environment.NewLine +
                                 "• Devices have firewalls blocking ping/connections" + Environment.NewLine +
                                 "• Wrong subnet range specified";
        }
    });

    Logger.Information($"Network scan completed. Found {foundHostsList.Count} active hosts out of {totalHosts} scanned.");

    var confirmBox = MessageBoxManager.GetMessageBoxStandard(
        "Scan Completed", 
        $"Network scan completed!\n\n" +
        $"• Scanned: {totalHosts} hosts\n" +
        $"• Found: {foundHostsList.Count} active hosts\n" +
        $"• Success rate: {(foundHostsList.Count * 100.0 / totalHosts):F1}%",
        ButtonEnum.Ok);
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

            Log.Information($"Running command: sysupgrade --force_all -n--kernel={kernelPath} --rootfs={rootfsPath}");

            // Pass cancellation token to the command
            await SshClientService.ExecuteCommandWithProgressAsync(
                DeviceConfig.Instance,
                $"sysupgrade --force_all -n --kernel={kernelPath} --rootfs={rootfsPath}",
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