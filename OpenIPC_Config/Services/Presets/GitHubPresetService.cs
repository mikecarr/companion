using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using OpenIPC_Config.Models.Presets;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenIPC_Config.Services.Presets;

/// <summary>
/// Service for fetching and managing presets from GitHub repositories
/// </summary>
public class GitHubPresetService : IGitHubPresetService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private const string GithubApiBaseUrl = "https://api.github.com";

    public GitHubPresetService(ILogger logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "OpenIPC-Config");
    }

    /// <summary>
    /// Gets a platform-independent temporary directory path
    /// </summary>
    private string GetTempDirectory()
    {
        // Use the system's temp directory as a base
        string baseDir = Path.Combine(Path.GetTempPath(), "OpenIPC_Config", "Presets");
        
        // Create the directory if it doesn't exist
        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }
        
        return baseDir;
    }

    /// <summary>
    /// Fetches preset files from a GitHub repository
    /// </summary>
    /// <param name="repository">Repository to fetch presets from</param>
    /// <returns>List of GitHub files containing preset configurations</returns>
    public async Task<List<GitHubFile>> FetchPresetFilesAsync(Repository repository)
    {
        try 
        {
            // Fetch PRESET_INDEX.yaml directly from raw GitHub content
            var indexUrl = repository.GetPresetIndexUrl();
            _logger.Information($"Fetching preset index from: {indexUrl}");
            
            var response = await _httpClient.GetStringAsync(indexUrl);
        
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var presetIndex = deserializer.Deserialize<PresetIndex>(response);

            // Convert index entries to GitHubFiles
            return presetIndex.Presets.Select(preset => new GitHubFile
            {
                Name = preset.Name,
                Path = preset.Path,
                DownloadUrl = repository.GetPresetDownloadUrl(preset.Path)
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error fetching preset index: {ex.Message}");
            return new List<GitHubFile>();
        }
    }

    /// <summary>
    /// Downloads a preset configuration file
    /// </summary>
    /// <param name="githubFile">GitHub file to download</param>
    /// <param name="localBaseDirectory">Base directory to save the preset</param>
    /// <returns>Local path of the downloaded preset configuration</returns>
    public async Task<string?> DownloadPresetConfigAsync(GitHubFile githubFile, string localBaseDirectory)
    {
        try
        {
            _logger.Information($"Downloading preset from: {githubFile.DownloadUrl}");
            
            // Download file content
            var fileContent = await _httpClient.GetStringAsync(githubFile.DownloadUrl);

            // Create a proper directory structure to avoid overwriting files
            // Include the preset name in the path to ensure uniqueness
            var localPresetDir = Path.Combine(localBaseDirectory, githubFile.Path);
            
            _logger.Information($"Creating directory: {localPresetDir}");
            Directory.CreateDirectory(localPresetDir);

            // Save the preset configuration
            var localConfigPath = Path.Combine(localPresetDir, "preset-config.yaml");
            await File.WriteAllTextAsync(localConfigPath, fileContent);

            _logger.Information($"Saved preset to: {localConfigPath}");
            return localConfigPath;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error downloading preset {githubFile.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Synchronizes presets from a repository
    /// </summary>
    /// <param name="repository">Repository to sync</param>
    /// <param name="localPresetsDirectory">Local directory to save presets</param>
    /// <returns>List of downloaded preset configurations</returns>
    public async Task<List<string>> SyncRepositoryPresetsAsync(
        Repository repository,
        string localPresetsDirectory)
    {
        var downloadedPresets = new List<string>();

        try
        {
            _logger.Information($"Syncing presets from repository: {repository.Name}");
            
            // Use temp directory if supplied directory is null or empty
            if (string.IsNullOrWhiteSpace(localPresetsDirectory))
            {
                localPresetsDirectory = Path.Combine(GetTempDirectory(), repository.RepositoryName);
                _logger.Information($"Using temporary directory: {localPresetsDirectory}");
            }
            
            // Create the base directory if it doesn't exist
            if (!Directory.Exists(localPresetsDirectory))
            {
                Directory.CreateDirectory(localPresetsDirectory);
            }
            
            // Fetch preset files
            var presetFiles = await FetchPresetFilesAsync(repository);
            _logger.Information($"Found {presetFiles.Count} presets in index");

            // Download each preset configuration
            foreach (var presetFile in presetFiles)
            {
                var localConfigPath = await DownloadPresetConfigAsync(
                    presetFile,
                    localPresetsDirectory
                );

                if (localConfigPath != null)
                    downloadedPresets.Add(localConfigPath);
            }

            _logger.Information($"Successfully downloaded {downloadedPresets.Count} presets from {repository.Name}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Error syncing repository {repository.Name}: {ex.Message}");
        }

        return downloadedPresets;
    }
}