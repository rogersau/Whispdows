using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void LoadOrCreate_writes_the_default_settings_shape()
    {
        using var sandbox = new TestSandbox();
        var settings = sandbox.Loader.LoadOrCreate();

        Assert.True(settings.Enabled);
        Assert.Equal("RightCtrl", settings.Hotkey.Shortcut);
        Assert.Equal(90, settings.Audio.MaxSeconds);
        Assert.Equal("local", settings.Transcription.Provider);
        Assert.Equal("basic", settings.Cleanup.Provider);
        Assert.True(File.Exists(sandbox.Paths.SettingsFile));

        var json = File.ReadAllText(sandbox.Paths.SettingsFile);
        Assert.Contains("\"launchAtLogin\": false", json);
        Assert.Contains("\"restoreDelayMs\": 175", json);
    }

    [Fact]
    public void LoadOrCreate_rejects_unknown_providers()
    {
        using var sandbox = new TestSandbox();
        sandbox.Loader.LoadOrCreate();
        File.WriteAllText(
            sandbox.Paths.SettingsFile,
            "{ \"transcription\": { \"provider\": \"mars\" } }");

        var exception = Assert.Throws<SettingsValidationException>(() => sandbox.Loader.LoadOrCreate());

        Assert.Contains("transcription.provider", exception.Message);
    }

    [Fact]
    public void Save_rejects_invalid_cleanup_style()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.Cleanup.Style = "rewrite-everything";

        var exception = Assert.Throws<SettingsValidationException>(() => sandbox.Loader.Save(settings));

        Assert.Contains("cleanup.style", exception.Message);
    }

    [Fact]
    public void Save_rejects_an_unparseable_hotkey()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.Hotkey.Shortcut = "Ctrl++Space";

        var exception = Assert.Throws<SettingsValidationException>(() => sandbox.Loader.Save(settings));

        Assert.Contains("empty key", exception.Message);
    }

    [Fact]
    public void Settings_paths_keep_application_and_user_data_separate()
    {
        using var sandbox = new TestSandbox();

        Assert.NotEqual(sandbox.Paths.SettingsDirectory, sandbox.Paths.ApplicationDirectory);
        Assert.EndsWith(Path.Combine("user", "Dictate", "settings.json"), sandbox.Paths.SettingsFile);
        Assert.EndsWith(Path.Combine("application", "README.md"), sandbox.Paths.ReadmePath);
    }

    private sealed class TestSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DictateTests", Guid.NewGuid().ToString("N"));

        public TestSandbox()
        {
            Paths = new SettingsPaths(
                Path.Combine(_root, "user", "Dictate"),
                Path.Combine(_root, "application"));
            Loader = new SettingsLoader(Paths);
        }

        public SettingsPaths Paths { get; }

        public SettingsLoader Loader { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
