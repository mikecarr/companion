using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Newtonsoft.Json.Linq;
using OpenIPC_Config.Logging;
using OpenIPC_Config.Models;
using OpenIPC_Config.Services;
using OpenIPC_Config.Services.Presets;
using OpenIPC_Config.ViewModels;
using OpenIPC_Config.Views;
using Prism.Events;
using Serilog;

namespace OpenIPC_Config;

public class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; }

    public static string OSType { get; private set; }

#if DEBUG
    private bool _ShouldCheckForUpdates = false;
#else
    private bool _ShouldCheckForUpdates = true;
#endif

    private void DetectOsType()
    {
        // Detect OS Type
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            OSType = "Mobile";
        else if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            OSType = "Desktop";
        else
            OSType = "Unknown";
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        DetectOsType();
    }

    private IConfigurationRoot LoadConfiguration()
    {
        var configPath = GetConfigPath();

        // Create default settings if not present
        if (!File.Exists(configPath))
        {
            // create the file
            var defaultSettings = createDefaultAppSettings();
            File.WriteAllText(configPath, defaultSettings.ToString());
            Log.Information($"Default appsettings.json created at {configPath}");
        }
        else
        {
            // Update existing settings with new sections if needed
            UpdateExistingSettings(configPath);
        }

        Console.WriteLine($"Loading configuration from: {configPath}");
        // Build configuration
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, false, true)
            .AddJsonFile("appsettings.json", true, true)
            .Build();

        return configuration;
    }

    private void UpdateExistingSettings(string configPath)
    {
        try
        {
            // Read existing settings
            string json = File.ReadAllText(configPath);
            JObject existingSettings = JObject.Parse(json);
            
            bool hasChanges = false;
            
            var deviceHostnameMappingSection = existingSettings["DeviceHostnameMapping"] as JObject;
            if (deviceHostnameMappingSection != null)
            {
                var cameraArray = deviceHostnameMappingSection["Camera"] as JArray;
                if (cameraArray != null)
                {
                    string targetHostname = "openipc-gk7205v300";
                    if (!cameraArray.Any(token => token.ToString() == targetHostname))
                    {
                        cameraArray.Add(targetHostname);
                        hasChanges = true;
                    }
                }
            }
            
            // Check and update Serilog sinks if needed
            var serilogSection = existingSettings["Serilog"] as JObject;
            if (serilogSection != null)
            {
                var usingArray = serilogSection["Using"] as JArray;
                if (usingArray != null)
                {
                    for (int i = 0; i < usingArray.Count; i++)
                    {
                        if (usingArray[i].ToString() == "Serilog.Sinks.RollingFile")
                        {
                            usingArray[i] = "Serilog.Sinks.File";
                            hasChanges = true;
                            break;
                        }
                    }
                }
            }

            // Change due to rename
            if (existingSettings["UpdateChecker"] != null)
            {
                existingSettings["UpdateChecker"] = new JObject(
                    new JProperty("LatestJsonUrl", "https://github.com/OpenIPC/companion/releases/latest/download/latest.json")
                );
                
                hasChanges = true;
                Log.Information("Updated UpdateChecker section");
            }
            // Check if Presets section exists, add if missing
            if (existingSettings["Presets"] == null)
            {
                existingSettings["Presets"] = new JObject(
                    new JProperty("Repositories", 
                        new JArray(
                            new JObject(
                                new JProperty("Url", "https://github.com/OpenIPC/fpv-presets"),
                                new JProperty("Branch", "master"),
                                new JProperty("Description", "Official OpenIPC presets repository"),
                                new JProperty("IsActive", true)
                            )
                        )
                    )
                );
                hasChanges = true;
                Log.Information("Added Presets section to existing settings");
            }
            
            // Save changes if needed
            if (hasChanges)
            {
                File.WriteAllText(configPath, existingSettings.ToString());
                Log.Information($"Updated existing settings at {configPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error updating existing settings: {ex.Message}");
        }
    }
    
    private void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register IEventAggregator as a singleton
        services.AddSingleton<IEventAggregator, EventAggregator>();
        services.AddSingleton<IEventSubscriptionService, EventSubscriptionService>();
        services.AddSingleton<ISshClientService, SshClientService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();

        services.AddSingleton<IYamlConfigService, YamlConfigService>();
        
        // Register IConfiguration
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTransient<DeviceConfigValidator>();
        services.AddSingleton<IGlobalSettingsService, GlobalSettingsService>();

        // Register IConfiguration
        services.AddTransient<DeviceConfigValidator>();

        services.AddSingleton<HttpClient>();
        // for release info
        services.AddSingleton<IGitHubService, GitHubService>();
        // for presets
        services.AddSingleton<IGitHubPresetService, GitHubPresetService>();
        services.AddSingleton<IPresetService, PresetService>();
        
        // add memory cache
        services.AddMemoryCache();

        // Register ViewModels
        RegisterViewModels(services);

        // Register Views
        RegisterViews(services);

        // Register Logger using factory
        services.AddSingleton<ILogger>(sp =>
        {
            var eventAggregator = sp.GetRequiredService<IEventAggregator>();
            return new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                //.WriteTo.Console() // Keep console logging
                .WriteTo.Sink(new EventAggregatorSink(eventAggregator)) // Add EventAggregatorSink
                .CreateLogger();
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Step 1: Load configuration
        var configuration = LoadConfiguration();

        // Step 2: Configure DI container
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection, configuration);
        ServiceProvider = serviceCollection.BuildServiceProvider();

        // Step 3: Initialize logger (resolve it from service provider)
        Log.Logger = ServiceProvider.GetRequiredService<ILogger>();
        var logger = Log.ForContext<App>();
        
        logger.Information(
            "**********************************************************************************************");
        logger.Information($"Starting up log for OpenIPC Configurator {VersionHelper.GetAppVersion()}");
        logger.Information("Logger initialized successfully.");
        logger.Information("Starting up....");

        // check for updates
        if (_ShouldCheckForUpdates)
            CheckForUpdatesAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Remove Avalonia's default data validation plugin to avoid conflicts
            BindingPlugins.DataValidators.RemoveAt(0);

            // Resolve MainWindow and its DataContext from DI container
            desktop.MainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            // Resolve MainView and its DataContext from DI container
            singleViewPlatform.MainView = ServiceProvider.GetRequiredService<MainView>();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Gets the appropriate configuration directory path for the current platform and environment
    /// </summary>
    private string GetConfigPath()
    {
        var appName = Assembly.GetExecutingAssembly().GetName().Name;
        string configDirectory;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        }
        else if (OperatingSystem.IsWindows())
        {
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        }
        else // Linux
        {
            configDirectory = GetLinuxConfigDirectory(appName);
        }

        // Ensure directory exists
        if (!Directory.Exists(configDirectory))
            Directory.CreateDirectory(configDirectory);

        return Path.Combine(configDirectory, "appsettings.json");
    }

    /// <summary>
    /// Gets the appropriate configuration directory for Linux, handling Flatpak sandboxing
    /// </summary>
    private string GetLinuxConfigDirectory(string appName)
    {
        // Check if we're running in Flatpak
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        
        // Debug output for troubleshooting
        Console.WriteLine($"XDG_DATA_HOME: {xdgDataHome}");
        Console.WriteLine($"XDG_CONFIG_HOME: {xdgConfigHome}");
        Console.WriteLine($"Current working directory: {Directory.GetCurrentDirectory()}");
        
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            // Running in Flatpak - use the sandboxed data directory for config
            // This maps to ~/.var/app/com.openipc.OpenIPC_Config/data/OpenIPC_Config
            return Path.Combine(xdgDataHome, appName);
        }
        else if (!string.IsNullOrEmpty(xdgConfigHome))
        {
            // Standard Linux with XDG_CONFIG_HOME set
            return Path.Combine(xdgConfigHome, appName);
        }
        else
        {
            // Fallback: check if we can write to current directory (traditional install)
            var currentDirConfig = Path.Combine(Directory.GetCurrentDirectory(), "config", appName);
            try
            {
                // Test if we can write to this location
                var testFile = Path.Combine(currentDirConfig, "test_write.tmp");
                Directory.CreateDirectory(currentDirConfig);
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return currentDirConfig;
            }
            catch
            {
                // Can't write to current directory, use XDG standard location
                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(homeDir, ".config", appName);
            }
        }
    }

    /// <summary>
    /// Gets the appropriate data directory for storing logs and other data files
    /// </summary>
    public static string GetDataDirectory()
    {
        var appName = Assembly.GetExecutingAssembly().GetName().Name;
        
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", appName);
        }
        else // Linux
        {
            // Check if we're running in Flatpak
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdgDataHome))
            {
                // Running in Flatpak - use the sandboxed data directory
                return Path.Combine(xdgDataHome, appName);
            }
            else
            {
                // Standard Linux - use XDG data directory
                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(homeDir, ".local", "share", appName);
            }
        }
    }

    public virtual async Task ShowUpdateDialogAsync(string releaseNotes, string downloadUrl, string newVersion)
    {
        var msgBox = MessageBoxManager.GetMessageBoxStandard("Update Available",
            $"New version available: {newVersion}\n\n{releaseNotes}\n\nDo you want to download the update?",
            ButtonEnum.YesNo);

        var result = await msgBox.ShowAsync();

        if (result == ButtonResult.Yes) OpenBrowser(downloadUrl);
    }

    private void OpenBrowser(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private async Task CheckForUpdatesAsync()
    {
        // Set up the necessary dependencies
        var httpClient = new HttpClient();

        // Use the same config path logic as the main application
        var configPath = GetConfigPath();

        Console.WriteLine($"Loading configuration from: {configPath}");

        // Create an IConfiguration instance
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, false, true)
            .Build();

        // Pass the dependencies to the constructor
        var updateChecker = new UpdateChecker(httpClient, configuration);

        try
        {
            string currentVersion;
#if DEBUG
            // In debug mode, read the version from VERSION.txt
            var versionFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VERSION");
            if (File.Exists(versionFilePath))
                currentVersion = File.ReadAllText(versionFilePath).Trim();
            else
                currentVersion = "0.0.0.0"; // Default version for debugging
#else
            currentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
#endif

            var result = await updateChecker.CheckForUpdateAsync(currentVersion);

            if (result.HasUpdate)
            {
                await ShowUpdateDialogAsync(result.ReleaseNotes, result.DownloadUrl, result.NewVersion);
                Log.Information($"Update Available! Version: {result.NewVersion}, {result.ReleaseNotes}");
            }
            else
            {
                Log.Information("No updates found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"An error occurred while checking for updates: {ex.Message}");
        }
    }

    private static void RegisterViewModels(IServiceCollection services)
    {
        // Register ViewModels
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<CameraSettingsTabViewModel>();
        services.AddSingleton<ConnectControlsViewModel>();
        services.AddSingleton<LogViewerViewModel>();
        services.AddSingleton<SetupTabViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<TelemetryTabViewModel>();
        services.AddSingleton<VRXTabViewModel>();
        services.AddSingleton<WfbGSTabViewModel>();
        services.AddSingleton<WfbTabViewModel>();
        services.AddSingleton<FirmwareTabViewModel>();
        services.AddSingleton<PresetsTabViewModel>();
        services.AddSingleton<AdvancedTabViewModel>();
    }

    private static void RegisterViews(IServiceCollection services)
    {
        // Register Views
        services.AddTransient<MainWindow>();
        services.AddTransient<MainView>();
        services.AddTransient<CameraSettingsTabView>();
        services.AddTransient<ConnectControlsView>();
        services.AddTransient<LogViewer>();
        services.AddTransient<SetupTabView>();
        services.AddTransient<StatusBarView>();
        services.AddTransient<TelemetryTabView>();
        services.AddTransient<VRXTabView>();
        services.AddTransient<WfbGSTabView>();
        services.AddTransient<FirmwareTabView>();
        services.AddTransient<WfbTabView>();
        services.AddTransient<PresetsTabView>();
        services.AddTransient<AdvancedTabView>();
    }

    private JObject createDefaultAppSettings()
    {
        // Use the new data directory method for logs
        string logPath = Path.Combine(GetDataDirectory(), "Logs", "configurator.log");

        // Ensure the log directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));

        // Create default settings
        var defaultSettings = new JObject(
            new JProperty("UpdateChecker",
                new JObject(
                    new JProperty("LatestJsonUrl",
                        "https://github.com/OpenIPC/companion/releases/latest/download/latest.json")
                )
            ),
            new JProperty("Presets",
                new JObject(
                    new JProperty("Repositories", 
                        new JArray(
                            new JObject(
                                new JProperty("Url", "https://github.com/OpenIPC/fpv-presets"),
                                new JProperty("Branch", "master"),
                                new JProperty("Description", "Official OpenIPC presets repository"),
                                new JProperty("IsActive", true)
                            )
                        )
                    )
                )
            ),
            new JProperty("Serilog",
                new JObject(
                    new JProperty("Using", new JArray("Serilog.Sinks.Console", "Serilog.Sinks.File")),
                    new JProperty("MinimumLevel", "Debug"),
                    new JProperty("WriteTo",
                        new JArray(
                            new JObject(
                                new JProperty("Name", "Console"),
                                new JProperty("Args",
                                    new JObject(
                                        new JProperty("outputTemplate", "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                                    )
                                )
                            ),
                            new JObject(
                                new JProperty("Name", "File"),
                                new JProperty("Args",
                                    new JObject(
                                        new JProperty("path", logPath),
                                        new JProperty("rollingInterval", "Day"),
                                        new JProperty("retainedFileCountLimit", "5"),
                                        new JProperty("outputTemplate", "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                                    )
                                )
                            )
                        )
                    ),
                    new JProperty("Properties",
                        new JObject(
                            new JProperty("Application", "OpenIPC_Config")
                        )
                    )
                )
            ),
            new JProperty("DeviceHostnameMapping",
                new JObject(
                    new JProperty("Camera", new JArray("openipc-ssc338q", "openipc-ssc30kq", "openipc-gk7205v300")),
                    new JProperty("Radxa", new JArray("radxa", "raspberrypi")),
                    new JProperty("NVR", new JArray("openipc-hi3536dv100"))
                )
            )
        );

        return defaultSettings;
    }
}