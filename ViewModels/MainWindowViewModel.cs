using Avalonia.Threading;
using Clockwork.Overlay;
using Clockwork.Services;
using Microsoft.Win32;

namespace Clockwork.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public const string DefaultToggleHotkey = "Ctrl + Shift + O";

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
    private bool _startWithWindows = false;
    private bool _startMinimized = false;
    private bool _firstTimeCloseNoticeShown = false;
    private string _toggleHotkey = DefaultToggleHotkey;
    private List<string> _customIgnoredApplications = [];
    private string _customIgnoredApplicationsText = string.Empty;

    private bool _suppressSettingsSave;
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _savePending;
    private bool _isBatchUpdating;

    public MainWindowViewModel()
    {
        var sections = new List<SectionViewModel>();
        foreach (var definition in StatRegistry.Sections)
        {
            var options = definition.Stats
                .Select(stat => new OptionViewModel(stat, Icon(stat.IconFile), stat.DefaultOn))
                .ToArray();
            sections.Add(new SectionViewModel(definition.Name, Icon(definition.IconFile), definition.Description, options));
        }

        sections.Add(new SectionViewModel("Overlay", Icon("Target.svg"),
            "Choose where the overlay appears, how it looks, and general application preferences.",
            [],
            isOverlaySettings: true));

        Sections = sections;
        _selectedSection = Sections[0];

        var loaded = _settings.Load();
        _overlayPositionX = loaded.OverlayPositionX;
        _overlayPositionY = loaded.OverlayPositionY;
        _overlayScale = loaded.OverlayScale;
        _overlayBackgroundEnabled = loaded.OverlayBackgroundEnabled;
        _overlayBackgroundOpacity = loaded.OverlayBackgroundOpacity;
        _includeWindowedGames = loaded.IncludeWindowedGames;
        _startWithWindows = loaded.StartWithWindows;
        _startMinimized = loaded.StartMinimized;
        _firstTimeCloseNoticeShown = loaded.FirstTimeCloseNoticeShown;
        _toggleHotkey = string.IsNullOrWhiteSpace(loaded.ToggleHotkey) ? DefaultToggleHotkey : loaded.ToggleHotkey;

        _customIgnoredApplications = loaded.CustomIgnoredApplications?.ToList() ?? [];
        _customIgnoredApplicationsText = string.Join(Environment.NewLine, _customIgnoredApplications);

        IgnoredApplications.SetCustom(_customIgnoredApplications);

        if (_startWithWindows)
            SetStartWithWindowsRegistry(true);

        foreach (var section in Sections)
        {
            foreach (var option in section.Options)
            {
                // Prefers the stable key and falls back to the "Section/Label" key older versions
                // wrote, so existing settings carry over. The next save writes stable keys only.
                if (StatRegistry.TryResolveSavedState(loaded.OptionStates, option.Stat, out var isOn))
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

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetField(ref _startWithWindows, value))
                return;

            SetStartWithWindowsRegistry(value);
            RequestSettingsSave();
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!SetField(ref _startMinimized, value))
                return;

            RequestSettingsSave();
        }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? DefaultToggleHotkey : value.Trim();
            if (!SetField(ref _toggleHotkey, trimmed))
                return;

            RequestSettingsSave();
        }
    }

    public string CustomIgnoredApplicationsText
    {
        get => _customIgnoredApplicationsText;
        set
        {
            if (!SetField(ref _customIgnoredApplicationsText, value))
                return;

            var list = value
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _customIgnoredApplications = list;
            IgnoredApplications.SetCustom(list);
            RequestSettingsSave();
        }
    }

    public bool FirstTimeCloseNoticeShown
    {
        get => _firstTimeCloseNoticeShown;
        set
        {
            if (!SetField(ref _firstTimeCloseNoticeShown, value))
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
                    option.IsOn = option.Stat.DefaultOn;
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
                (option.Stat.Key, option.IsOn))),
            OverlayPositionX,
            OverlayPositionY,
            OverlayScale,
            OverlayBackgroundEnabled,
            OverlayBackgroundOpacity,
            IncludeWindowedGames,
            StartWithWindows,
            StartMinimized,
            FirstTimeCloseNoticeShown,
            _customIgnoredApplications,
            ToggleHotkey);
        _savePending = false;
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Clockwork";

    private static void SetStartWithWindowsRegistry(bool enable)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (enable && string.IsNullOrEmpty(processPath))
                return;

            var desired = enable ? $"\"{processPath}\" --minimized" : null;

            // Read with read-only access first. Most launches find the value already correct (or
            // already absent), so the key is only opened for writing when something must change.
            using (var readKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (readKey is null)
                    return;

                var current = readKey.GetValue(RunValueName) as string;
                if (string.Equals(current, desired, StringComparison.Ordinal))
                    return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key is null)
                return;

            if (desired is null)
                key.DeleteValue(RunValueName, false);
            else
                key.SetValue(RunValueName, desired);
        }
        catch (Exception ex)
        {
            TelemetryDiagnostics.Write($"Failed to update Start With Windows registry setting: {ex.Message}");
        }
    }

    private static double ClampPosition(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 0.0;

    private static double ClampScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 50.0, 200.0) : 100.0;

    private static double ClampOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 94.0;
}