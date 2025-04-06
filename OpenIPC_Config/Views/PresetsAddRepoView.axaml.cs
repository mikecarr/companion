using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenIPC_Config.Views;

public partial class PresetsAddRepoView : UserControl
{
    public PresetsAddRepoView()
    {
        if (!Design.IsDesignMode)
            InitializeComponent();
    }
}