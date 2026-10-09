using System.Collections.ObjectModel;
using Clockwork.ViewModels;

namespace Clockwork.Overlay;

public sealed class OverlayViewModel : ViewModelBase
{
    public ObservableCollection<OverlayLine> Lines { get; } = [];
}

public class OverlayLine(string label) : ViewModelBase
{
    private string _value = "…";
    public string Label { get; } = label;
    public string Value { get => _value; set => SetField(ref _value, value); }
}