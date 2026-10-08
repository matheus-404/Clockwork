using System.Text.Json;

namespace Clockwork.Services;

/// <summary>Persists user settings between application launches.</summary>
public sealed class SettingsService
{
    private sealed class SettingsFile
    {
        public Dictionary<string, bool> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public OverlaySettingsFile Overlay { get; set; } = new();
    }

    private sealed class OverlaySettingsFile
    {
        public double PositionX { get; set; } = 0.0;
        public double PositionY { get; set; } = 0.0;
        public double Scale { get; set; } = 100.0;
        public bool BackgroundEnabled { get; set; } = true;
        public double BackgroundOpacity { get; set; } = 94.0;
        public bool IncludeWindowedGames { get; set; } = false;
    }

    public sealed record LoadedSettings(
        Dictionary<string, bool> OptionStates,
        double OverlayPositionX,
        double OverlayPositionY,
        double OverlayScale,
        bool OverlayBackgroundEnabled,
        double OverlayBackgroundOpacity,
        bool IncludeWindowedGames);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clockwork",
        "settings.json");

    public LoadedSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return Empty();

            var json = File.ReadAllText(_filePath);

            using var document = JsonDocument.Parse(json);
            bool isCurrentFormat = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Options", out _);

            if (isCurrentFormat)
            {
                var file = JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions);
                if (file is not null)
                {
                    return new LoadedSettings(
                        new Dictionary<string, bool>(file.Options, StringComparer.OrdinalIgnoreCase),
                        ClampPosition(file.Overlay?.PositionX ?? 0.0),
                        ClampPosition(file.Overlay?.PositionY ?? 0.0),
                        ClampScale(file.Overlay?.Scale ?? 100.0),
                        file.Overlay?.BackgroundEnabled ?? true,
                        ClampOpacity(file.Overlay?.BackgroundOpacity ?? 94.0),
                        file.Overlay?.IncludeWindowedGames ?? false);
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
        catch
        {
            return Empty();
        }
    }

    public void Save(
        IEnumerable<(string Key, bool IsOn)> optionStates,
        double overlayPositionX,
        double overlayPositionY,
        double overlayScale,
        bool overlayBackgroundEnabled,
        double overlayBackgroundOpacity,
        bool includeWindowedGames)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
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
                },
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            var tempPath = _filePath + ".tmp";

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
        }
    }

    private static LoadedSettings Empty() => new(
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        0.0,
        0.0,
        100.0,
        true,
        94.0,
        false);

    private static double ClampPosition(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 0.0;
    private static double ClampScale(double value) => double.IsFinite(value) ? Math.Clamp(value, 50.0, 200.0) : 100.0;
    private static double ClampOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.0, 100.0) : 94.0;
}