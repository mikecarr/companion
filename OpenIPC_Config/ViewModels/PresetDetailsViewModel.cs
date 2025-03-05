using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenIPC_Config.Models.Presets;

namespace OpenIPC_Config.ViewModels;

public class PresetDetailsViewModel : INotifyPropertyChanged
{
    private Preset? _preset;

    public Preset? Preset
    {
        get => _preset;
        set
        {
            if (_preset != value)
            {
                _preset = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public PresetDetailsViewModel()
    {

    }
}