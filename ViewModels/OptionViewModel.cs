namespace Clockwork.ViewModels;

/// <summary>A single on/off setting shown as a row in a section.</summary>
public sealed class OptionViewModel : ViewModelBase
{
    private bool _isOn;

    public OptionViewModel(string name, string iconPath, bool isOn = false)
    {
        Name = name;
        IconPath = iconPath;
        _isOn = isOn;
    }

    public string Name { get; }
    public string IconPath { get; }

    public bool IsOn
    {
        get => _isOn;
        set => SetField(ref _isOn, value);
    }
}
