using System.Threading.Tasks;
using MsBox.Avalonia.Enums;

namespace OpenIPC_Config.Services;

public interface IMessageBoxService
{
    Task ShowMessageBox(string title, string message);
    Task<ButtonResult> ShowMessageBoxWithFolderLink(string title, string message, string filePath);
    Task<ButtonResult> ShowCustomMessageBox(string title, string message, ButtonEnum buttons, Icon icon = Icon.Info);
}