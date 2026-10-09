using Avalonia.Threading;
using Clockwork.Services;

namespace Clockwork.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const string IconRoot = "avares://Clockwork/Assets/Icons/";

    private static string Icon(string fileName) => IconRoot + fileName;

    private readonly SettingsService _settings = new();
    private SectionViewModel _selectedSection;
    private double _overlayPositionX = 0.0;
    private double _overlayPositionY = 0.0;
    private double _overlayScale = 100.0;
    private bool _overlayBackgroundEnabled = true;
    private double _overlayBackgroundOpacity = 94.0;
    private bool _includeWindowedGames = false;
    private bool _suppressSettingsSave;
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _savePending;
    private bool _isBatchUpdating;

    public MainWindowViewModel()
    {
        Sections =
        [
            new SectionViewModel("Performance", Icon("Pulse.svg"),
                "Select which performance information you want to display in-game.",
            [
                new OptionViewModel("FPS", Icon("Monitor.svg"), isOn: true),
                new OptionViewModel("Avg FPS", Icon("Pulse.svg")),
                new OptionViewModel("1% Low FPS", Icon("Pulse.svg")),
                new OptionViewModel("0.1% Low FPS", Icon("Pulse.svg")),
                new OptionViewModel("Frame Time", Icon("Clock.svg")),
                new OptionViewModel("Dropped Frames", Icon("Pulse.svg")),
                new OptionViewModel("Presented FPS", Icon("Monitor.svg")),
                new OptionViewModel("Displayed FPS", Icon("Monitor.svg")),
                new OptionViewModel("Application FPS", Icon("Monitor.svg")),
            ]),

            new SectionViewModel("CPU", Icon("CPU.svg"),
                "Select which CPU information you want to display in-game.",
            [
                new OptionViewModel("CPU Usage", Icon("Pulse.svg")),
                new OptionViewModel("CPU Busy", Icon("Clock.svg")),
                new OptionViewModel("CPU Wait", Icon("Clock.svg")),
                new OptionViewModel("CPU Frame Time", Icon("Clock.svg")),
            ]),

            new SectionViewModel("GPU", Icon("GPU.svg"),
                "Select which GPU information you want to display in-game.",
            [
                new OptionViewModel("GPU Temperature", Icon("Thermometer.svg")),
                new OptionViewModel("GPU Core Clock Frequency", Icon("Gauge.svg")),
                new OptionViewModel("GPU Memory Clock Frequency", Icon("Gauge.svg")),
                new OptionViewModel("GPU VRAM Usage", Icon("RAM.svg")),
                new OptionViewModel("VRAM Usage (%)", Icon("Percent.svg")),
                new OptionViewModel("GPU Power", Icon("Plug.svg")),
                new OptionViewModel("GPU Usage", Icon("Pulse.svg")),
                new OptionViewModel("GPU Render/Compute Utilization", Icon("Pulse.svg")),
                new OptionViewModel("GPU Power Limited", Icon("Pulse.svg")),
                new OptionViewModel("GPU Temperature Limited", Icon("Thermometer.svg")),
                new OptionViewModel("GPU Current Limited", Icon("Pulse.svg")),
                new OptionViewModel("GPU Voltage Limited", Icon("Pulse.svg")),
                new OptionViewModel("GPU Utilization Limited", Icon("Pulse.svg")),
                new OptionViewModel("GPU Busy", Icon("Clock.svg")),
                new OptionViewModel("GPU Wait", Icon("Clock.svg")),
                new OptionViewModel("GPU Time", Icon("Clock.svg")),
            ]),

            new SectionViewModel("RAM", Icon("RAM.svg"),
                "Select which RAM information you want to display in-game.",
            [
                new OptionViewModel("RAM Usage", Icon("RAM.svg")),
                new OptionViewModel("RAM Usage (%)", Icon("Percent.svg")),
                new OptionViewModel("Process/Game RAM Usage", Icon("Monitor.svg")),
            ]),

            new SectionViewModel("Latency", Icon("Clock.svg"),
                "Select which latency information you want to display in-game.",
            [
                new OptionViewModel("GPU Latency", Icon("Clock.svg")),
                new OptionViewModel("Display Latency", Icon("Clock.svg")),
                new OptionViewModel("Render/Present Latency", Icon("Clock.svg")),
                new OptionViewModel("Time Until Displayed", Icon("Clock.svg")),
                new OptionViewModel("Between Presents", Icon("Clock.svg")),
                new OptionViewModel("Between Display Changes", Icon("Clock.svg")),
                new OptionViewModel("Click-to-Photon Latency", Icon("Clock.svg")),
                new OptionViewModel("All Input-to-Photon Latency", Icon("Clock.svg")),
            ]),

            new SectionViewModel("More", Icon("More.svg"),
                "Select which additional information you want to display in-game.",
            [
                new OptionViewModel("System Time", Icon("Clock.svg")),
                new OptionViewModel("Session Playtime", Icon("Timer.svg")),
            ]),

            new SectionViewModel("Overlay", Icon("Target.svg"),
                "Choose where the overlay appears and how large it should be.",
                [],
                isOverlaySettings: true),
        ];

        _selectedSection = Sections[0];

        var loaded = _settings.Load();
        _overlayPositionX = loaded.OverlayPositionX;
        _overlayPositionY = loaded.OverlayPositionY;
        _overlayScale = loaded.OverlayScale;
        _overlayBackgroundEnabled = loaded.OverlayBackgroundEnabled;
        _overlayBackgroundOpacity = loaded.OverlayBackgroundOpacity;
        _includeWindowedGames = loaded.IncludeWindowedGames;

        foreach (var section in Sections)
        {
            foreach (var option in section.Options)
            {
                if (loaded.OptionStates.TryGetValue(GetOptionKey(section, option), out var isOn))
                    option.IsOn = isOn;
            }
        }

        SubscribeToOptionChanges();
        _settingsSaveTimer.Tick += OnSettingsSaveTimerTick;
    }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    public bool IsBatchUpdating => _isBatchUpdating;

    public SectionViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !SetField(ref _selectedSection, value))
                return;

            OnPropertyChanged(nameof(IsOptionsSectionVisible));
        }
    }

    public bool IsOptionsSectionVisible => !SelectedSection.IsOverlaySettings;

    public double OverlayPositionX
    {
        get => _overlayPositionX;
        set
        {
            var clamped = ClampPosition(value);
            if (!SetField(ref _overlayPositionX, clamped))
                return;

            RequestSettingsSave();
        }
    }

    public double OverlayPositionY
    {
        get => _overlayPositionY;
        set
        {
            var clamped = ClampPosition(value);
            if (!SetField(ref _overlayPositionY, clamped))
                return;

            RequestSettingsSave();
        }
    }

    public double OverlayScale
    {
        get => _overlayScale;
        set
        {
            var clamped = ClampScale(value);
            if (!SetField(ref _overlayScale, clamped))
                return;

            RequestSettingsSave();
        }
    }

    public bool OverlayBackgroundEnabled
    {
        get => _overlayBackgroundEnabled;
        set
        {
            if (!SetField(ref _overlayBackgroundEnabled, value))
                return;

            RequestSettingsSave();
        }
    }

    public double OverlayBackgroundOpacity
    {
        get => _overlayBackgroundOpacity;
        set
        {
            var clamped = ClampOpacity(value);
            if (!SetField(ref _overlayBackgroundOpacity, clamped))
                return;

            RequestSettingsSave();
        }
    }

    public bool IncludeWindowedGames
    {
        get => _includeWindowedGames;
        set
        {
            if (!SetField(ref _includeWindowedGames, value))
                return;

            RequestSettingsSave();
        }
    }

    private void SubscribeToOptionChanges()
    {
        foreach (var section in Sections)
        {
            foreach (var option in section.Options)
                option.PropertyChanged += OnOptionPropertyChanged;
        }
    }

    private void OnOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not OptionViewModel || e.PropertyName != nameof(OptionViewModel.IsOn))
            return;

        if (_suppressSettingsSave || _isBatchUpdating)
            return;

        RequestSettingsSave();
    }

    public void ResetStatisticsToDefault()
    {
        _suppressSettingsSave = true;
        _isBatchUpdating = true;
        try
        {
            foreach (var section in Sections)
            {
                foreach (var option in section.Options)
                    option.IsOn = section.Name == "Performance" && option.Name == "FPS";
            }
        }
        finally
        {
            _isBatchUpdating = false;
            _suppressSettingsSave = false;
        }

        OnPropertyChanged(nameof(IsBatchUpdating));
        RequestSettingsSave();
    }

    private void RequestSettingsSave()
    {
        _savePending = true;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void OnSettingsSaveTimerTick(object? sender, EventArgs e)
    {
        _settingsSaveTimer.Stop();
        SaveSettingsNow();
    }

    public void FlushPendingSave()
    {
        _settingsSaveTimer.Stop();
        SaveSettingsNow();
    }

    private void SaveSettingsNow()
    {
        if (!_savePending)
            return;

        _settings.Save(
            Sections.SelectMany(section => section.Options.Select(option =>
                (GetOptionKey(section, option), option.IsOn))),
            OverlayPositionX,
            OverlayPositionY,
            OverlayScale,
            OverlayBackgroundEnabled,
            OverlayBackgroundOpacity,
            IncludeWindowedGames);
        _savePending = false;
    }

    private static string GetOptionKey(SectionViewModel section, OptionViewModel option) =>
        $"{section.Name}/{option.Name}";

    private static double ClampPosition(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 0.0;

    private static double ClampScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 50.0, 200.0) : 100.0;

    private static double ClampOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 94.0;
}