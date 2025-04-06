using System.Collections.Generic;
using System.Threading.Tasks;
using OpenIPC_Config.Models.Presets;

namespace OpenIPC_Config.Services.Presets;

public interface IGitHubPresetService
{
    Task<List<GitHubFile>> FetchPresetFilesAsync(Repository repository);

    // Task<string?> DownloadPresetAsync(string repoOwner, string repoName, string presetPath, string localBaseDirectory);
    Task<List<string>> SyncRepositoryPresetsAsync(Repository repository, string localPresetsDirectory);
}