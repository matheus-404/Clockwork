using Clockwork.Overlay;

namespace Clockwork.ViewModels;

/// <summary>A single on/off setting shown as a row in a section.</summary>
public sealed class OptionViewModel : ViewModelBase
{
    private bool _isOn;

    public OptionViewModel(StatDefinition stat, string iconPath, bool isOn = false)
    {
        Stat = stat;
        Name = stat.Label;
        IconPath = iconPath;
        _isOn = isOn;
    }

    /// <summary>The statistic this option switches on or off.</summary>
    public StatDefinition Stat { get; }

    public string Name { get; }
    public string IconPath { get; }

    public bool IsOn
    {
        get => _isOn;
        set => SetField(ref _isOn, value);
    }
}
