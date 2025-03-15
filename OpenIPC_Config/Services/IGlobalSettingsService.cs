using System.Threading.Tasks;

namespace OpenIPC_Config.Services;

public interface IGlobalSettingsService
{
    bool IsWfbYamlEnabled { get; }
    Task ReadDevice();
}