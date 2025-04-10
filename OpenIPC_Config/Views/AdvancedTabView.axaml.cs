using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC_Config.ViewModels;

namespace OpenIPC_Config.Views;

public partial class AdvancedTabView : UserControl
{
    public AdvancedTabView()
    {
        if (!Design.IsDesignMode) 
            DataContext = App.ServiceProvider.GetService<AdvancedTabViewModel>();
        
        InitializeComponent();
    }
    
    
}