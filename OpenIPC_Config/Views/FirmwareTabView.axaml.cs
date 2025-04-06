using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC_Config.ViewModels;

namespace OpenIPC_Config.Views;

public partial class FirmwareTabView : UserControl
{
    public FirmwareTabView()
    {
        InitializeComponent();

        if (!Design.IsDesignMode) 
            DataContext = App.ServiceProvider.GetService<FirmwareTabViewModel>();
    }
}