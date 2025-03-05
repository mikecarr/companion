using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenIPC_Config.Converters;

public class CanConnectConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // If the value is a Preset, try to find the CanConnect from the DataContext
        if (value is Models.Presets.Preset)
        {
            // Attempt to find the ViewModel through the DataContext
            var dataContext = Avalonia.Application.Current?.DataContext as ViewModels.PresetsTabViewModel;
            return dataContext?.CanConnect ?? false;
        }
            
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}