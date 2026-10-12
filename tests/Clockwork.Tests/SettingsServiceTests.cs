using System.Text.Json;
using Clockwork.Services;
using Xunit;

namespace Clockwork.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _testSettingsFile;

    public SettingsServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ClockworkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _testSettingsFile = Path.Combine(_tempDirectory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEmptyDefaults()
    {
        var service = new SettingsService(_testSettingsFile);

        var loaded = service.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.OptionStates);
        Assert.Equal(0.0, loaded.OverlayPositionX);
        Assert.Equal(0.0, loaded.OverlayPositionY);
        Assert.Equal(100.0, loaded.OverlayScale);
        Assert.True(loaded.OverlayBackgroundEnabled);
        Assert.Equal(94.0, loaded.OverlayBackgroundOpacity);
        Assert.False(loaded.IncludeWindowedGames);
        Assert.False(loaded.StartWithWindows);
        Assert.False(loaded.StartMinimized);
        Assert.False(loaded.FirstTimeCloseNoticeShown);
        Assert.Empty(loaded.CustomIgnoredApplications ?? []);
        Assert.Equal("Ctrl + Shift + O", loaded.ToggleHotkey);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllConfiguredValues()
    {
        var service = new SettingsService(_testSettingsFile);
        var options = new List<(string Key, bool IsOn)>
        {
            ("fps", true),
            ("gpu-temp", false),
            ("frame-time", true),
        };
        var customIgnored = new[] { "discord", "spotify", "customgame" };

        service.Save(
            optionStates: options,
            overlayPositionX: 45.5,
            overlayPositionY: 90.0,
            overlayScale: 150.0,
            overlayBackgroundEnabled: false,
            overlayBackgroundOpacity: 70.0,
            includeWindowedGames: true,
            startWithWindows: true,
            startMinimized: true,
            firstTimeCloseNoticeShown: true,
            customIgnoredApplications: customIgnored,
            toggleHotkey: "Ctrl+Shift+O");

        var loaded = service.Load();

        Assert.Equal(3, loaded.OptionStates.Count);
        Assert.True(loaded.OptionStates["fps"]);
        Assert.False(loaded.OptionStates["gpu-temp"]);
        Assert.True(loaded.OptionStates["frame-time"]);
        Assert.Equal(45.5, loaded.OverlayPositionX);
        Assert.Equal(90.0, loaded.OverlayPositionY);
        Assert.Equal(150.0, loaded.OverlayScale);
        Assert.False(loaded.OverlayBackgroundEnabled);
        Assert.Equal(70.0, loaded.OverlayBackgroundOpacity);
        Assert.True(loaded.IncludeWindowedGames);
        Assert.True(loaded.StartWithWindows);
        Assert.True(loaded.StartMinimized);
        Assert.True(loaded.FirstTimeCloseNoticeShown);
        Assert.Equal("Ctrl+Shift+O", loaded.ToggleHotkey);
        Assert.Equal(customIgnored, loaded.CustomIgnoredApplications);
    }

    [Fact]
    public void Save_ClampsValuesWithinBounds()
    {
        var service = new SettingsService(_testSettingsFile);

        service.Save(
            optionStates: [],
            overlayPositionX: -50.0,
            overlayPositionY: 150.0,
            overlayScale: 500.0,
            overlayBackgroundEnabled: true,
            overlayBackgroundOpacity: -10.0,
            includeWindowedGames: false);

        var loaded = service.Load();

        Assert.Equal(0.0, loaded.OverlayPositionX);
        Assert.Equal(100.0, loaded.OverlayPositionY);
        Assert.Equal(200.0, loaded.OverlayScale);
        Assert.Equal(0.0, loaded.OverlayBackgroundOpacity);
    }

    [Fact]
    public void Load_MigratesLegacyDictionaryFormat()
    {
        var legacyJson = JsonSerializer.Serialize(new Dictionary<string, bool>
        {
            ["Performance/FPS"] = true,
            ["GPU/GPU Temperature"] = false
        });

        File.WriteAllText(_testSettingsFile, legacyJson);

        var service = new SettingsService(_testSettingsFile);
        var loaded = service.Load();

        Assert.Equal(2, loaded.OptionStates.Count);
        Assert.True(loaded.OptionStates["Performance/FPS"]);
        Assert.False(loaded.OptionStates["GPU/GPU Temperature"]);
        Assert.Equal(100.0, loaded.OverlayScale);
        Assert.Equal(0.0, loaded.OverlayPositionX);
    }

    [Fact]
    public void Save_CreatesBackupOfPreviousSettingsFile()
    {
        var service = new SettingsService(_testSettingsFile);

        service.Save([("fps", true)], 10.0, 10.0, 100.0, true, 94.0, false);
        Assert.True(File.Exists(_testSettingsFile));

        service.Save([("fps", false)], 20.0, 20.0, 120.0, true, 94.0, false);
        Assert.True(File.Exists(service.BackupPath));

        var backupContent = File.ReadAllText(service.BackupPath);
        Assert.Contains("\"PositionX\": 10", backupContent);
    }

    [Fact]
    public void Load_WhenSettingsFileIsCorrupt_RecoversFromBackup()
    {
        var service = new SettingsService(_testSettingsFile);

        // Step 1: Write initial valid settings
        service.Save([("fps", true)], 25.0, 30.0, 125.0, true, 80.0, false);

        // Step 2: Trigger backup creation by saving a second version
        service.Save([("fps", true)], 50.0, 50.0, 150.0, true, 80.0, false);

        // Step 3: Corrupt the main settings file
        File.WriteAllText(_testSettingsFile, "{ malformed json file [[");

        // Step 4: Load should catch the corruption and restore from the backup file
        var loaded = service.Load();

        Assert.Equal(25.0, loaded.OverlayPositionX);
        Assert.Equal(30.0, loaded.OverlayPositionY);
        Assert.Equal(125.0, loaded.OverlayScale);
        Assert.True(File.Exists(_testSettingsFile + ".corrupt"));
    }

    [Fact]
    public void CustomIgnoredApplications_DeduplicatesAndTrimsEntries()
    {
        var service = new SettingsService(_testSettingsFile);
        var inputs = new[] { "discord", "  DISCORD  ", "spotify.exe", "", "   " };

        service.Save(
            optionStates: [],
            overlayPositionX: 0.0,
            overlayPositionY: 0.0,
            overlayScale: 100.0,
            overlayBackgroundEnabled: true,
            overlayBackgroundOpacity: 94.0,
            includeWindowedGames: false,
            customIgnoredApplications: inputs);

        var loaded = service.Load();

        Assert.NotNull(loaded.CustomIgnoredApplications);
        Assert.Equal(2, loaded.CustomIgnoredApplications.Count);
        Assert.Contains("discord", loaded.CustomIgnoredApplications, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("spotify.exe", loaded.CustomIgnoredApplications, StringComparer.OrdinalIgnoreCase);
    }
}