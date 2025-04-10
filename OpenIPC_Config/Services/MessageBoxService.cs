using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace OpenIPC_Config.Services;

public class MessageBoxService : IMessageBoxService
{
    
    public async Task ShowMessageBox(string title, string message)
    {
        var msgBox = MessageBoxManager.GetMessageBoxStandard(
            new MessageBoxStandardParams 
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = ButtonEnum.Ok,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true
            }
        );

        // Show the message box and ensure it's activated and focused
        var result = await msgBox.ShowAsync();
        
        // Optional: You can do something with the result if needed
        // For example, log which button was pressed or perform an action
    }
}