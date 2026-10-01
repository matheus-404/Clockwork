namespace Clockwork.ViewModels;

/// <summary>A sidebar section (Performance, CPU, GPU, ...) and its options.</summary>
public sealed class SectionViewModel
{
    public SectionViewModel(
        string name,
        string iconPath,
        string description,
        IReadOnlyList<OptionViewModel> options,
        bool isOverlaySettings = false)
    {
        Name = name;
        IconPath = iconPath;
        Description = description;
        Options = options;
        IsOverlaySettings = isOverlaySettings;
    }

    public string Name { get; }
    public string IconPath { get; }
    public string Description { get; }
    public IReadOnlyList<OptionViewModel> Options { get; }
    public bool IsOverlaySettings { get; }
}
