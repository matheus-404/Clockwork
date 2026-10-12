using System.Text.Json;

namespace Clockwork.Services;

/// <summary>Persists user settings between application launches.</summary>
public sealed class SettingsService
{
    private sealed class SettingsFile
    {
        public Dictionary<string, bool> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public OverlaySettingsFile Overlay { get; set; } = new();
        public GeneralSettingsFile General { get; set; } = new();
    }

    private sealed class OverlaySettingsFile
    {
        public double PositionX { get; set; } = 0.0;
        public double PositionY { get; set; } = 0.0;
        public double Scale { get; set; } = 100.0;
        public bool BackgroundEnabled { get; set; } = true;
        public double BackgroundOpacity { get; set; } = 94.0;
        public bool IncludeWindowedGames { get; set; } = false;
        public string? ToggleHotkey { get; set; } = "F11";
    }

    private sealed class GeneralSettingsFile
    {
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool FirstTimeCloseNoticeShown { get; set; } = false;
        public List<string> CustomIgnoredApplications { get; set; } = [];
    }

    public sealed record LoadedSettings(
        Dictionary<string, bool> OptionStates,
        double OverlayPositionX,
        double OverlayPositionY,
        double OverlayScale,
        bool OverlayBackgroundEnabled,
        double OverlayBackgroundOpacity,
        bool IncludeWindowedGames,
        bool StartWithWindows = false,
        bool StartMinimized = false,
        bool FirstTimeCloseNoticeShown = false,
        IReadOnlyList<string>? CustomIgnoredApplications = null,
        string? ToggleHotkey = "F11");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly string _backupPath;

    public SettingsService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clockwork",
            "settings.json");
        _backupPath = _filePath + ".bak";
    }

    public string FilePath => _filePath;
    public string BackupPath => _backupPath;

    public LoadedSettings Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                return ParseFile(_filePath);
            }
            catch (Exception ex)
            {
                TelemetryDiagnostics.Write($"Settings file '{_filePath}' is corrupt: {ex.Message}. Attempting to restore from backup.");

                try
                {
                    var corruptCopy = _filePath + ".corrupt";
                    File.Copy(_filePath, corruptCopy, overwrite: true);
                }
                catch
                {
                }

                if (File.Exists(_backupPath))
                {
                    try
                    {
                        var backupSettings = ParseFile(_backupPath);
                        TelemetryDiagnostics.Write($"Successfully recovered settings from backup '{_backupPath}'.");

                        try
                        {
                            File.Copy(_backupPath, _filePath, overwrite: true);
                        }
                        catch
                        {
                        }

                        return backupSettings;
                    }
                    catch (Exception backupEx)
                    {
                        TelemetryDiagnostics.Write($"Backup settings file '{_backupPath}' is also corrupt: {backupEx.Message}.");
                    }
                }

                return Empty();
            }
        }

        if (File.Exists(_backupPath))
        {
            try
            {
                var backupSettings = ParseFile(_backupPath);
                TelemetryDiagnostics.Write($"Restored settings from backup '{_backupPath}'.");

                try
                {
                    File.Copy(_backupPath, _filePath, overwrite: true);
                }
                catch
                {
                }

                return backupSettings;
            }
            catch (Exception ex)
            {
                TelemetryDiagnostics.Write($"Backup file '{_backupPath}' could not be loaded: {ex.Message}.");
            }
        }

        return Empty();
    }

    public void Save(
        IEnumerable<(string Key, bool IsOn)> optionStates,
        double overlayPositionX,
        double overlayPositionY,
        double overlayScale,
        bool overlayBackgroundEnabled,
        double overlayBackgroundOpacity,
        bool includeWindowedGames,
        bool startWithWindows = false,
        bool startMinimized = false,
        bool firstTimeCloseNoticeShown = false,
        IEnumerable<string>? customIgnoredApplications = null,
        string? toggleHotkey = "F11")
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var data = new SettingsFile
            {
                Options = optionStates.ToDictionary(
                    x => x.Key,
                    x => x.IsOn,
                    StringComparer.OrdinalIgnoreCase),
                Overlay = new OverlaySettingsFile
                {
                    PositionX = ClampPosition(overlayPositionX),
                    PositionY = ClampPosition(overlayPositionY),
                    Scale = ClampScale(overlayScale),
                    BackgroundEnabled = overlayBackgroundEnabled,
                    BackgroundOpacity = ClampOpacity(overlayBackgroundOpacity),
                    IncludeWindowedGames = includeWindowedGames,
                    ToggleHotkey = toggleHotkey,
                },
                General = new GeneralSettingsFile
                {
                    StartWithWindows = startWithWindows,
                    StartMinimized = startMinimized,
                    FirstTimeCloseNoticeShown = firstTimeCloseNoticeShown,
                    CustomIgnoredApplications = customIgnoredApplications?
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? [],
                },
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            var tempPath = _filePath + ".tmp";

            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                try
                {
                    File.Copy(_filePath, _backupPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    TelemetryDiagnostics.Write($"Failed to update settings backup: {ex.Message}");
                }
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            TelemetryDiagnostics.Write($"Failed to save settings to '{_filePath}': {ex.Message}");
        }
    }

    private static LoadedSettings ParseFile(string path)
    {
        var json = File.ReadAllText(path);

        using var document = JsonDocument.Parse(json);
        bool isCurrentFormat = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Options", out _);

        if (isCurrentFormat)
        {
            var file = JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions);
            if (file is not null)
            {
                return new LoadedSettings(
                    new Dictionary<string, bool>(file.Options ?? new(), StringComparer.OrdinalIgnoreCase),
                    ClampPosition(file.Overlay?.PositionX ?? 0.0),
                    ClampPosition(file.Overlay?.PositionY ?? 0.0),
                    ClampScale(file.Overlay?.Scale ?? 100.0),
                    file.Overlay?.BackgroundEnabled ?? true,
                    ClampOpacity(file.Overlay?.BackgroundOpacity ?? 94.0),
                    file.Overlay?.IncludeWindowedGames ?? false,
                    file.General?.StartWithWindows ?? false,
                    file.General?.StartMinimized ?? false,
                    file.General?.FirstTimeCloseNoticeShown ?? false,
                    file.General?.CustomIgnoredApplications ?? [],
                    file.Overlay?.ToggleHotkey ?? "F11");
            }
        }

        var oldOptions = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, JsonOptions);
        return oldOptions is null
            ? Empty()
            : new LoadedSettings(
                new Dictionary<string, bool>(oldOptions, StringComparer.OrdinalIgnoreCase),
                0.0,
                0.0,
                100.0,
                true,
                94.0,
                false);
    }

    private static LoadedSettings Empty() => new(
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        0.0,
        0.0,
        100.0,
        true,
        94.0,
        false,
        false,
        false,
        false,
        [],
        "F11");

    private static double ClampPosition(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 0.0;
    private static double ClampScale(double value) => double.IsFinite(value) ? Math.Clamp(value, 50.0, 200.0) : 100.0;
    private static double ClampOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 94.0;
}