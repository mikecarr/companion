using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json.Linq;
using OpenIPC_Config.Events;
using OpenIPC_Config.Models;
using OpenIPC_Config.Services;
using Serilog;
using SharpCompress.Archives;

namespace OpenIPC_Config.ViewModels;

/// <summary>
/// ViewModel for managing firmware updates and device configuration
/// </summary>
public partial class FirmwareTabViewModel : ViewModelBase
{
    #region Private Fields

    private readonly HttpClient _httpClient;
    private readonly SysUpgradeService _sysupgradeService;
    private CancellationTokenSource _cancellationTokenSource;
    private FirmwareData _firmwareData;
    private readonly IGitHubService _gitHubService;

    #endregion

    #region Observable Properties

    [ObservableProperty] private bool _canConnect;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isFirmwareSelected;
    [ObservableProperty] private bool _isFirmwareBySocSelected;
    [ObservableProperty] private bool _isManufacturerSelected;
    [ObservableProperty] private bool _canDownloadFirmware;
    [ObservableProperty] private bool _isManualUpdateEnabled = true;
    [ObservableProperty] private string _selectedDevice;
    [ObservableProperty] private string _selectedFirmware;
    [ObservableProperty] private string _selectedFirmwareBySoc;
    [ObservableProperty] private string _selectedManufacturer;
    [ObservableProperty] private string _manualFirmwareFile;
    [ObservableProperty] private int _progressValue;

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets whether dropdowns should be enabled based on connection and firmware selection state
    /// </summary>
    public bool CanUseDropdowns => IsConnected && !IsFirmwareSelected && !IsFirmwareBySocSelected;

    /// <summary>
    /// Gets whether soc dropdowns should be enabled based on connection and firmware selection state
    /// </summary>
    public bool CanUseDropdownsBySoc => IsConnected && !IsManufacturerSelected;

    
    /// <summary>
    /// Gets whether firmware selection is available based on connection and manufacturer selection state
    /// </summary>
    public bool CanUseSelectFirmware => IsConnected && !IsManufacturerSelected && !IsFirmwareBySocSelected;

    /// <summary>
    /// Collection of available manufacturers
    /// </summary>
    public ObservableCollection<string> Manufacturers { get; set; } = new();

    /// <summary>
    /// Collection of available devices for selected manufacturer
    /// </summary>
    public ObservableCollection<string> Devices { get; set; } = new();

    /// <summary>
    /// Collection of available firmware versions
    /// </summary>
    public ObservableCollection<string> Firmwares { get; set; } = new();

    /// <summary>
    /// Collection of available firmware versions
    /// </summary>
    public ObservableCollection<string> FirmwareBySoc { get; set; } = new();

    #endregion

    #region Commands

    public ICommand SelectFirmwareCommand { get; set; }
    public ICommand PerformFirmwareUpgradeAsyncCommand { get; set; }
    public ICommand ClearFormCommand { get; set; }
    public IRelayCommand DownloadFirmwareAsyncCommand { get; set; }

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of FirmwareTabViewModel
    /// </summary>
    public FirmwareTabViewModel(
        ILogger logger,
        ISshClientService sshClientService,
        IEventSubscriptionService eventSubscriptionService,
        IGitHubService gitHubService)
        : base(logger, sshClientService, eventSubscriptionService)
    {
        _gitHubService = gitHubService;
        _httpClient = new HttpClient();
        _sysupgradeService = new SysUpgradeService(sshClientService, logger);

        InitializeProperties();
        InitializeCommands();
        SubscribeToEvents();
    }

    #endregion

    #region Initialization Methods

    private void InitializeProperties()
    {
        CanConnect = false;
        IsConnected = false;
        IsFirmwareSelected = false;
        IsManufacturerSelected = false;
    }

    private void InitializeCommands()
    {
        DownloadFirmwareAsyncCommand = new RelayCommand(
            async () => await DownloadAndPerformFirmwareUpgradeAsync(), 
            CanExecuteDownloadFirmware);

        PerformFirmwareUpgradeAsyncCommand = new RelayCommand(
            async () => await DownloadAndPerformFirmwareUpgradeAsync()); 

        SelectFirmwareCommand = new RelayCommand<Window>(async window =>
            await SelectFirmware(window));

        ClearFormCommand = new RelayCommand(ClearForm);
    }

    private void SubscribeToEvents()
    {
        EventSubscriptionService.Subscribe<AppMessageEvent, AppMessage>(OnAppMessage);
    }

    #endregion

    #region Event Handlers

    private void OnAppMessage(AppMessage message)
    {
        CanConnect = message.CanConnect;
        IsConnected = message.CanConnect;

        LoadManufacturers();

        if (!IsConnected)
        {
            IsFirmwareSelected = false;
            IsManufacturerSelected = false;
        }

        UpdateCanExecuteCommands();
    }

    partial void OnSelectedFirmwareBySocChanged(string value)
    {
        IsFirmwareBySocSelected = !string.IsNullOrEmpty(value);
        
        UpdateCanExecuteCommands();
    }
    
    partial void OnSelectedManufacturerChanged(string value)
    {
        LoadDevices(value);
        IsManufacturerSelected = !string.IsNullOrEmpty(value);
        IsFirmwareSelected = false;
        UpdateCanExecuteCommands();
    }

    partial void OnSelectedDeviceChanged(string value)
    {
        LoadFirmwares(value);
        UpdateCanExecuteCommands();
    }

    partial void OnSelectedFirmwareChanged(string value)
    {
        UpdateCanExecuteCommands();
    }

    partial void OnManualFirmwareFileChanged(string value)
    {
        UpdateCanExecuteCommands();
    }

    #endregion

    #region Public Methods

    public async void LoadManufacturers()
    {
        try
        {
            Logger.Information("Loading firmware list...");
            Manufacturers.Clear();
            var data = await FetchFirmwareListAsync();

            if (data?.Manufacturers != null && data.Manufacturers.Any())
            {
                Manufacturers.Clear();
                foreach (var manufacturer in data.Manufacturers)
                {
                    var hasValidFirmwareType = manufacturer.Devices.Any(device =>
                        device.Firmware.Any(firmware =>
                            firmware.Contains("fpv") || firmware.Contains("rubyfpv")));

                    if (hasValidFirmwareType)
                        Manufacturers.Add(manufacturer.Name);
                }

                if (!Manufacturers.Any())
                    Logger.Warning("No manufacturers with valid firmware types found.");
            }
            else
            {
                Logger.Warning("No manufacturers found in the fetched firmware data.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error loading manufacturers.");
        }
    }

    public void LoadDevices(string manufacturer)
    {
        Devices.Clear();

        if (string.IsNullOrEmpty(manufacturer))
        {
            Logger.Warning("Manufacturer is null or empty. Devices cannot be loaded.");
            return;
        }

        var manufacturerData = _firmwareData?.Manufacturers
            .FirstOrDefault(m => m.Name == manufacturer);

        if (manufacturerData == null || !manufacturerData.Devices.Any())
        {
            Logger.Warning($"No devices found for manufacturer: {manufacturer}");
            return;
        }

        foreach (var device in manufacturerData.Devices)
            Devices.Add(device.Name);

        Logger.Information($"Loaded {Devices.Count} devices for manufacturer: {manufacturer}");
    }

    
    public void LoadFirmwares(string device)
    {
        Firmwares.Clear();

        if (string.IsNullOrEmpty(device))
        {
            Logger.Warning("Device is null or empty. Firmwares cannot be loaded.");
            return;
        }

        var deviceData = _firmwareData?.Manufacturers
            .FirstOrDefault(m => m.Name == SelectedManufacturer)?.Devices
            .FirstOrDefault(d => d.Name == device);

        if (deviceData == null || !deviceData.Firmware.Any())
        {
            Logger.Warning($"No firmware found for device: {device}");
            return;
        }

        foreach (var firmware in deviceData.Firmware)
        {
            var components = firmware.Split('-');
            if (components.Length > 0)
            {
                var firmwareType = components[0];
                if (!Firmwares.Contains(firmwareType))
                    Firmwares.Add(firmwareType);
            }
        }

        Logger.Information($"Loaded {Firmwares.Count} firmware types for device: {device}");
    }

    #endregion

    #region Private Methods

    private void ClearForm()
    {
        SelectedManufacturer = string.Empty;
        SelectedDevice = string.Empty;
        SelectedFirmware = string.Empty;
        SelectedFirmwareBySoc = string.Empty;
        ManualFirmwareFile = string.Empty;
        
        IsFirmwareSelected = false;
        IsManufacturerSelected = false;
        IsManualUpdateEnabled = true;
        
        UpdateCanExecuteCommands();
    }

    private bool CanExecuteDownloadFirmware()
    {
        return CanConnect &&
               (!string.IsNullOrEmpty(SelectedManufacturer) &&
                !string.IsNullOrEmpty(SelectedDevice) &&
                !string.IsNullOrEmpty(SelectedFirmware)) ||
               !string.IsNullOrEmpty(ManualFirmwareFile);
    }

    private void UpdateCanExecuteCommands()
    {
        CanDownloadFirmware = CanExecuteDownloadFirmware();

        OnPropertyChanged(nameof(CanUseDropdowns));
        OnPropertyChanged(nameof(CanUseDropdownsBySoc));
        OnPropertyChanged(nameof(CanUseSelectFirmware));


        (DownloadFirmwareAsyncCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (PerformFirmwareUpgradeAsyncCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SelectFirmwareCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task<FirmwareData> FetchFirmwareListAsync()
    {
        try
        {
            Logger.Information("Fetching firmware list...");

            IEnumerable<string> filenames = await GetFilenamesAsync();

            Logger.Information($"Fetched {filenames.Count()} firmware files.");

            FirmwareData firmwareData = ProcessFilenames(filenames);
            
            // Populate FirmwareBySoc
            PopulateFirmwareBySoc(filenames); // Calling populate method here

            _firmwareData = firmwareData;
            return firmwareData;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error fetching firmware list.");
            return null;
        }
    }
    
    private void PopulateFirmwareBySoc(IEnumerable<string> filenames)
    {
        FirmwareBySoc.Clear(); // Clear existing list

        var chipType = DeviceConfig.Instance.ChipType;

        foreach (var filename in filenames)
        {
            //string pattern = $@"^(?=.*{Regex.Escape(chipType)})(?=.*fpv).*?(?<memoryType>nand|nor)\.tgz$";  //Dynamically create regex with escaped chipType
            string simplePattern = $@".*{chipType}.*fpv.*";
            var match = Regex.Match(filename, simplePattern, RegexOptions.IgnoreCase); //Added RegexOptions.IgnoreCase to compare 
            if (match.Success)
            {
                if (!FirmwareBySoc.Contains(filename))
                {
                    FirmwareBySoc.Add(filename);
                    Logger.Information($"Added FirmwareBySoc: {filename}");
                }
            }
        }

        Logger.Information($"Populated FirmwareBySoc with {FirmwareBySoc.Count} entries.");
    }

    private async Task<IEnumerable<string>> GetFilenamesAsync()
    {
        var response = await _gitHubService.GetGitHubDataAsync(OpenIPC.OpenIPCBuilderGitHubApiUrl);
        var releaseData = JObject.Parse(response.ToString());
        var assets = releaseData["assets"];
        return assets?.Select(asset => asset["name"]?.ToString()).Where(name => !string.IsNullOrEmpty(name)) ??
               Enumerable.Empty<string>();
    }

    private FirmwareData ProcessFilenames(IEnumerable<string> filenames)
    {
        var firmwareData = new FirmwareData { Manufacturers = new ObservableCollection<Manufacturer>() };

        foreach (var filename in filenames)
        {
            ProcessFilenameByManufacturer(filename, firmwareData);
        }

        return firmwareData;
    }
    
    private void ProcessFilenameByManufacturer(string filename, FirmwareData firmwareData)
    {
        var match = Regex.Match(filename,
            @"^(?<sensor>[^_]+)_(?<firmwareType>[^_]+)_(?<manufacturer>[^-]+)-(?<device>.+?)-(?<memoryType>nand|nor)");
        if (match.Success)
        {
            ProcessFirmwareMatch(match, firmwareData);
        }
        else
        {
            Debug.WriteLine($"Filename '{filename}' does not match the expected pattern.");
        }
    }

    
    private void ProcessFirmwareMatch(Match match, FirmwareData firmwareData)
    {
        var sensor = match.Groups["sensor"].Value;

        // only show firmware that matches the selected sensor/soc
        if (DeviceConfig.Instance.ChipType != sensor)
            return; // using `return` to exit the method. continue is no longer relevant here

        var firmwareType = match.Groups["firmwareType"].Value;
        var manufacturerName = match.Groups["manufacturer"].Value;
        var deviceName = match.Groups["device"].Value;
        var memoryType = match.Groups["memoryType"].Value;

        Debug.WriteLine(
            $"Parsed file: Sensor={sensor}, FirmwareType={firmwareType}, Manufacturer={manufacturerName}, Device={deviceName}, MemoryType={memoryType}");

        AddFirmwareData(firmwareData, manufacturerName, deviceName, firmwareType, sensor, memoryType);
    }

    private void AddFirmwareData(FirmwareData firmwareData, string manufacturerName, string deviceName,
        string firmwareType, string sensor, string memoryType)
    {
        var manufacturer = firmwareData.Manufacturers.FirstOrDefault(m => m.Name == manufacturerName);
        if (manufacturer == null)
        {
            manufacturer = CreateAndAddManufacturer(firmwareData, manufacturerName);
        }

        var device = manufacturer.Devices.FirstOrDefault(d => d.Name == deviceName);
        if (device == null)
        {
            device = CreateAndAddDevice(manufacturer, deviceName);
        }

        var firmwareIdentifier = $"{firmwareType}-{sensor}-{memoryType}";
        if (!device.Firmware.Contains(firmwareIdentifier)) device.Firmware.Add(firmwareIdentifier);
    }
    
    private Manufacturer CreateAndAddManufacturer(FirmwareData firmwareData, string manufacturerName)
    {
        var manufacturer = new Manufacturer
        {
            Name = manufacturerName,
            Devices = new ObservableCollection<Device>()
        };
        firmwareData.Manufacturers.Add(manufacturer);
        return manufacturer;
    }

    private Device CreateAndAddDevice(Manufacturer manufacturer, string deviceName)
    {
        var device = new Device
        {
            Name = deviceName,
            Firmware = new ObservableCollection<string>()
        };
        manufacturer.Devices.Add(device);
        return device;
    }

    private async Task<string> DownloadFirmwareAsync(string url = null, string filename = null)
    {
        try
        {
            var filePath = Path.Combine(Path.GetTempPath(), filename);
            Logger.Information($"Downloading firmware from {url} to {filePath}");

            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);

                Logger.Information($"Firmware successfully downloaded to: {filePath}");
                return filePath;
            }
            else
            {
                Logger.Warning($"Failed to download firmware. Status code: {response.StatusCode}");
                ProgressValue = 100;
                return null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error downloading firmware");
            return null;
        }
    }

    private string DecompressTgzToTar(string tgzFilePath)
    {
        try
        {
            var tarFilePath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(tgzFilePath)}.tar");

            using (var fileStream = File.OpenRead(tgzFilePath))
            using (var gzipStream =
                   new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress))
            using (var tarFileStream = File.Create(tarFilePath))
            {
                gzipStream.CopyTo(tarFileStream);
            }

            Logger.Information($"Decompressed .tgz to .tar: {tarFilePath}");
            return tarFilePath;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error decompressing .tgz file");
            throw;
        }
    }

    private string UncompressFirmware(string tgzFilePath)
    {
        try
        {
            var tarFilePath = DecompressTgzToTar(tgzFilePath);

            ProgressValue = 4;
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(tgzFilePath));
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);

            Directory.CreateDirectory(tempDir);

            ProgressValue = 8;
            using (var archive = SharpCompress.Archives.Tar.TarArchive.Open(tarFilePath))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                {
                    var destinationPath = Path.Combine(tempDir, entry.Key);
                    var directoryPath = Path.GetDirectoryName(destinationPath);

                    if (!Directory.Exists(directoryPath))
                        Directory.CreateDirectory(directoryPath);

                    using (var entryStream = entry.OpenEntryStream())
                    using (var fileStream = File.Create(destinationPath))
                    {
                        entryStream.CopyTo(fileStream);
                    }
                }
            }

            Logger.Information($"Firmware extracted to {tempDir}");
            return tempDir;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error uncompressing firmware file");
            throw;
        }
    }

    private void ValidateFirmwareFiles(string extractedDir)
    {
        try
        {
            var allFiles = Directory.GetFiles(extractedDir);

            var md5Files = allFiles.Where(file => file.EndsWith(".md5sum")).ToList();

            if (!md5Files.Any())
                throw new InvalidOperationException("No MD5 checksum files found in the extracted directory.");

            foreach (var md5File in md5Files)
            {
                var baseFileName = Path.GetFileNameWithoutExtension(md5File);

                var dataFile = allFiles.FirstOrDefault(file => Path.GetFileName(file) == baseFileName);
                if (dataFile == null)
                    throw new FileNotFoundException(
                        $"Data file '{baseFileName}' referenced by '{md5File}' is missing.");

                ValidateMd5Checksum(md5File, dataFile);
            }

            Logger.Information("Firmware files validated successfully.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error validating firmware files");
            throw;
        }
    }

    private void ValidateMd5Checksum(string md5FilePath, string dataFilePath)
    {
        try
        {
            var md5Line = File.ReadAllText(md5FilePath).Trim();

            var parts = md5Line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new InvalidOperationException($"Invalid format in MD5 file: {md5FilePath}");
            }

            var expectedMd5 = parts[0];
            var expectedFilename = parts[1];

            if (Path.GetFileName(dataFilePath) != expectedFilename)
            {
                throw new InvalidOperationException(
                    $"Filename mismatch: expected '{expectedFilename}', found '{Path.GetFileName(dataFilePath)}'");
            }

            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = File.OpenRead(dataFilePath);
            var actualMd5 = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();

            if (expectedMd5 != actualMd5)
            {
                throw new InvalidOperationException(
                    $"MD5 mismatch for file: {Path.GetFileName(dataFilePath)}. Expected: {expectedMd5}, Actual: {actualMd5}");
            }

            Logger.Information($"File '{Path.GetFileName(dataFilePath)}' passed MD5 validation.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Error validating MD5 checksum for file: {dataFilePath}");
            throw;
        }
    }

    private async Task DownloadAndPerformFirmwareUpgradeAsync()
    {
        try
        {
            ProgressValue = 0;

            if (!string.IsNullOrEmpty(ManualFirmwareFile))
            {
                Logger.Information("Performing firmware upgrade using manual file.");
                await UpgradeFirmwareFromFileAsync(ManualFirmwareFile);
            }
            else if (!string.IsNullOrEmpty(SelectedFirmwareBySoc))
            {
                Logger.Information("Performing firmware upgrade using firmware by soc.");
                await PerformFirmwareUpgradeFromSocAsync();
            }
            else
            {
                if (string.IsNullOrEmpty(SelectedManufacturer) ||
                    string.IsNullOrEmpty(SelectedDevice) ||
                    string.IsNullOrEmpty(SelectedFirmware))
                {
                    Logger.Warning("Cannot perform firmware upgrade. Missing dropdown selections.");
                    return;
                }

                Logger.Information("Performing firmware upgrade using selected dropdown options.");
                await PerformFirmwareUpgradeFromDropdownAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error performing firmware upgrade");
        }
    }

    private async Task PerformFirmwareUpgradeFromSocAsync()
    {
        try
        {
            ProgressValue = 0;
            
            Logger.Information("Performing firmware upgrade using selected dropdown options.");
            
            var firmwwareFile = SelectedFirmwareBySoc;
            
            var downloadUrl = string.Empty;
            var filename = String.Empty;
            
            if (!string.IsNullOrEmpty(firmwwareFile))
            {
                filename = firmwwareFile;
                downloadUrl = $"https://github.com/OpenIPC/builder/releases/download/latest/{firmwwareFile}";
            }
            
            else
            {
                Logger.Warning("Failed to construct firmware URL. Missing or invalid data.");
                return;
            }

            string firmwareFilePath = await DownloadFirmwareAsync(downloadUrl, filename);
            if (!string.IsNullOrEmpty(firmwareFilePath))
            {
                await UpgradeFirmwareFromFileAsync(firmwareFilePath);
            }
            
            
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error performing firmware upgrade from dropdown");
        }
    }
    private async Task PerformFirmwareUpgradeFromDropdownAsync()
    {
        try
        {
            ProgressValue = 0;

            if (string.IsNullOrEmpty(SelectedManufacturer) ||
                string.IsNullOrEmpty(SelectedDevice) ||
                string.IsNullOrEmpty(SelectedFirmware))
            {
                Logger.Warning("Cannot perform firmware upgrade. Missing dropdown selections.");
                return;
            }

            Logger.Information("Performing firmware upgrade using selected dropdown options.");

            var manufacturer = _firmwareData?.Manufacturers
                .FirstOrDefault(m => m.Name == SelectedManufacturer);

            var device = manufacturer?.Devices
                .FirstOrDefault(d => d.Name == SelectedDevice);

            var firmwareIdentifier = device?.Firmware
                .FirstOrDefault(f => f.StartsWith(SelectedFirmware));

            var downloadUrl = string.Empty;
            var filename = String.Empty;
            

            if (!string.IsNullOrEmpty(manufacturer?.Name) &&
                !string.IsNullOrEmpty(device?.Name) &&
                !string.IsNullOrEmpty(firmwareIdentifier))
            {
                var components = firmwareIdentifier.Split('-');
                var firmwareType = components[0];
                var sensor = components[1];
                var memoryType = components[2];

                filename = $"{sensor}_{firmwareType}_{SelectedManufacturer}-{device.Name}-{memoryType}.tgz";
                downloadUrl = $"https://github.com/OpenIPC/builder/releases/download/latest/{filename}";
            }
            
            else
            {
                Logger.Warning("Failed to construct firmware URL. Missing or invalid data.");
                return;
            }

            string firmwareFilePath = await DownloadFirmwareAsync(downloadUrl, filename);
            if (!string.IsNullOrEmpty(firmwareFilePath))
            {
                await UpgradeFirmwareFromFileAsync(firmwareFilePath);
            }
            
            
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error performing firmware upgrade from dropdown");
        }
    }


    public void CancelSysupgrade()
    {
        _cancellationTokenSource?.Cancel();
    }

    private async Task UpgradeFirmwareFromFileAsync(string firmwareFilePath)
    {
        try
        {
            Logger.Information($"Upgrading firmware from file: {firmwareFilePath}");

            ProgressValue = 5;
            Logger.Debug("UncompressFirmware ProgressValue: " + ProgressValue);
            var extractedDir = UncompressFirmware(firmwareFilePath);

            ProgressValue = 10;
            Logger.Debug("ValidateFirmwareFiles ProgressValue: " + ProgressValue);
            ValidateFirmwareFiles(extractedDir);

            ProgressValue = 20;
            Logger.Debug("Before PerformSysupgradeAsync ProgressValue: " + ProgressValue);
            var kernelFile = Directory.GetFiles(extractedDir)
                .FirstOrDefault(f => f.Contains("uImage") && !f.EndsWith(".md5sum"));

            var rootfsFile = Directory.GetFiles(extractedDir)
                .FirstOrDefault(f => f.Contains("rootfs") && !f.EndsWith(".md5sum"));

            if (kernelFile == null || rootfsFile == null)
                throw new InvalidOperationException("Kernel or RootFS file is missing after validation.");

            var sysupgradeService = new SysUpgradeService(SshClientService, Logger);

            await Task.Run(async () =>
            {
                await sysupgradeService.PerformSysupgradeAsync(DeviceConfig.Instance, kernelFile, rootfsFile,
                    progress =>
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            switch (progress)
                            {
                                case var s when s.Contains("Update kernel from"):
                                    ProgressValue = 30;
                                    Logger.Debug("Update kernel from ProgressValue: " + ProgressValue);
                                    break;
                                case var s when s.Contains("Kernel updated to"):
                                    ProgressValue = 50;
                                    Logger.Debug("Kernel updated to ProgressValue: " + ProgressValue);
                                    break;
                                case var s when s.Contains("Update rootfs from"):
                                    ProgressValue = 60;
                                    Logger.Debug("Update rootfs from ProgressValue: " + ProgressValue);
                                    break;
                                case var s when s.Contains("Root filesystem uploaded successfully"):
                                    ProgressValue = 70;
                                    Logger.Debug(
                                        "Root filesystem uploaded successfully ProgressValue: " + ProgressValue);
                                    break;
                                case var s when s.Contains("Erase overlay partition"):
                                    ProgressValue = 90;
                                    Logger.Debug("Erase overlay partition ProgressValue: " + ProgressValue);
                                    break;
                            }

                            Logger.Debug(progress);
                        });
                    },
                    CancellationToken.None);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProgressValue = 100;
                    Logger.Information("Firmware upgrade completed successfully.");
                });
            });

            ProgressValue = 100;
            Logger.Information("Firmware upgrade completed successfully.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error upgrading firmware from file");
        }
    }

    public async Task SelectFirmware(Window window)
    {
        IsFirmwareSelected = true;
        IsManufacturerSelected = false;

        UpdateCanExecuteCommands();

        var dialog = new OpenFileDialog
        {
            Title = "Select a File",
            Filters = new List<FileDialogFilter>
            {
                new() { Name = "Compressed", Extensions = { "tgz" } },
                new() { Name = "Bin Files", Extensions = { "bin" } },
                new() { Name = "All Files", Extensions = { "*" } }
            }
        };

        var result = await dialog.ShowAsync(window);

        if (result != null && result.Length > 0)
        {
            var selectedFile = result[0];
            var fileName = Path.GetFileName(selectedFile);
            Console.WriteLine($"Selected File: {selectedFile}");
            ManualFirmwareFile = selectedFile;
        }
    }

    #endregion
}

#region Support Classes

public class FirmwareData
{
    public ObservableCollection<Manufacturer> Manufacturers { get; set; }
    
}

public class Manufacturer
{
    public string Name { get; set; }
    public ObservableCollection<Device> Devices { get; set; }
}

public class Device
{
    public string Name { get; set; }
    public ObservableCollection<string> Firmware { get; set; }
}

#endregion