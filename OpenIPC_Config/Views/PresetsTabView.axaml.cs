using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenIPC_Config.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using OpenIPC_Config.Models.Presets;

namespace OpenIPC_Config.Views;

public partial class PresetsTabView : UserControl
{
    public PresetsTabView()
    {
        InitializeComponent();

        if (!Design.IsDesignMode)
        {
            try
            {
                // Resolve the ViewModel from the DI container
                var viewModel = App.ServiceProvider.GetService<PresetsTabViewModel>();
                if (viewModel == null)
                {
                    throw new InvalidOperationException("Failed to resolve PresetsTabViewModel from the service provider.");
                }

                // Set the DataContext
                DataContext = viewModel;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing PresetsTabView: {ex.Message}");
                // Optionally, provide a fallback or handle errors gracefully
            }
        }
        
    }
    
    private void OnShowPresetDetailsClicked(object? sender, RoutedEventArgs e)
    {
        // Get the DataContext of the current view
        var viewModel = DataContext as PresetsTabViewModel;
            
        // Get the Preset from the clicked button's DataContext
        var button = sender as Button;
        var preset = button?.DataContext as Preset;

        // Call the method to show preset details
        viewModel?.ShowPresetDetails(preset);
    }

    private void OnApplyPresetClicked(object? sender, RoutedEventArgs e)
    {
        // Get the DataContext of the current view
        var viewModel = DataContext as PresetsTabViewModel;
            
        // Get the Preset from the clicked button's DataContext
        var button = sender as Button;
        var preset = button?.DataContext as Preset;

        // Call the method to apply preset
        viewModel?.ApplyPresetAsync(preset);
    }

    
}