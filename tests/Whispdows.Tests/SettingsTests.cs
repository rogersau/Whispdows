using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void LoadOrCreate_writes_the_default_settings_shape()
    {
        using var sandbox = new TestSandbox();
        var settings = sandbox.Loader.LoadOrCreate();

        Assert.True(settings.Enabled);
        Assert.True(settings.Features.Transcribe);
        Assert.True(settings.Features.MeetingNotes);
        Assert.Equal("RightCtrl", settings.Hotkey.Shortcut);
        Assert.Equal(90, settings.Audio.MaxSeconds);
        Assert.Equal("local", settings.Transcription.Provider);
        Assert.Equal("gpt-4o-transcribe", settings.Transcription.OpenaiModel);
        Assert.Equal("en-US", settings.Transcription.AzureLocale);
        Assert.Equal("basic", settings.Cleanup.Provider);
        Assert.Equal("auto", settings.MeetingNotes.TranscriptionProvider);
        Assert.Equal("models/ggml-medium.en.bin", settings.MeetingNotes.LocalModelPath);
        Assert.Equal("auto", settings.MeetingNotes.NotesProvider);
        Assert.Equal("~/MeetingNotes", settings.MeetingNotes.OutputDirectory);
        Assert.True(File.Exists(sandbox.Paths.SettingsFile));

        var json = File.ReadAllText(sandbox.Paths.SettingsFile);
        Assert.Contains("\"launchAtLogin\": false", json);
        Assert.Contains("\"restoreDelayMs\": 175", json);
        Assert.Contains("\"meetingNotes\": true", json);
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
    public void Save_requires_a_model_for_cloud_cleanup()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.Cleanup.Provider = "openai";

        var exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("cleanup.model", exception.Message);
    }

    [Fact]
    public void Save_requires_an_endpoint_for_azure_openai_cleanup()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.Cleanup.Provider = "azure-openai";
        settings.Cleanup.Model = "gpt-5.4-nano";

        var exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("cleanup.azureEndpoint", exception.Message);

        settings.Cleanup.AzureEndpoint =
            "https://resource.services.ai.azure.com/openai/v1";
        sandbox.Loader.Save(settings);

        var loaded = sandbox.Loader.LoadOrCreate();
        Assert.Equal("azure-openai", loaded.Cleanup.Provider);
        Assert.Equal("gpt-5.4-nano", loaded.Cleanup.Model);
        Assert.Equal(settings.Cleanup.AzureEndpoint, loaded.Cleanup.AzureEndpoint);
    }

    [Fact]
    public void Settings_snapshot_is_deep_and_does_not_mutate_live_settings()
    {
        var settings = AppSettings.CreateDefault();
        settings.Hotkey.Shortcut = "F13";
        settings.Cleanup.Provider = "azure-openai";
        settings.Cleanup.Model = "gpt-5.4-nano";
        settings.Cleanup.AzureEndpoint =
            "https://resource.services.ai.azure.com/openai/v1";
        settings.Features.Transcribe = false;
        settings.MeetingNotes.OutputDirectory = "D:\\PrivateMeetings";

        var snapshot = SettingsSnapshot.Clone(settings);
        snapshot.Hotkey.Shortcut = "RightCtrl";
        snapshot.Cleanup.Model = "other-model";
        snapshot.Cleanup.AzureEndpoint = "https://other.example/openai/v1";
        snapshot.Features.Transcribe = true;
        snapshot.MeetingNotes.OutputDirectory = "~/Elsewhere";

        Assert.Equal("F13", settings.Hotkey.Shortcut);
        Assert.Equal("gpt-5.4-nano", settings.Cleanup.Model);
        Assert.Equal(
            "https://resource.services.ai.azure.com/openai/v1",
            settings.Cleanup.AzureEndpoint);
        Assert.False(settings.Features.Transcribe);
        Assert.Equal("D:\\PrivateMeetings", settings.MeetingNotes.OutputDirectory);
    }

    [Fact]
    public void Save_requires_at_least_one_installed_feature()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.Features.Transcribe = false;
        settings.Features.MeetingNotes = false;

        var exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("At least one", exception.Message);
    }

    [Fact]
    public void Save_requires_a_loopback_endpoint_for_local_meeting_notes()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.MeetingNotes.OllamaEndpoint = "https://example.com";

        var exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("loopback", exception.Message);
    }

    [Fact]
    public void Save_requires_a_region_and_locale_for_azure_speech()
    {
        using var sandbox = new TestSandbox();
        var settings = AppSettings.CreateDefault();
        settings.Transcription.Provider = "azure";

        var exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("transcription.azureRegion", exception.Message);

        settings.Transcription.AzureRegion = "australiaeast";
        settings.Transcription.AzureLocale = string.Empty;
        exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("transcription.azureLocale", exception.Message);

        settings.Transcription.AzureRegion = "australiaeast.example.com/path";
        settings.Transcription.AzureLocale = "en/AU";
        exception = Assert.Throws<SettingsValidationException>(
            () => sandbox.Loader.Save(settings));

        Assert.Contains("transcription.azureRegion", exception.Message);
        Assert.Contains("transcription.azureLocale", exception.Message);
    }

    [Fact]
    public void Settings_paths_keep_application_and_user_data_separate()
    {
        using var sandbox = new TestSandbox();

        Assert.NotEqual(sandbox.Paths.SettingsDirectory, sandbox.Paths.ApplicationDirectory);
        Assert.EndsWith(Path.Combine("user", "Whispdows", "settings.json"), sandbox.Paths.SettingsFile);
        Assert.EndsWith(Path.Combine("user", "Whispdows", "secrets.dat"), sandbox.Paths.SecretsFile);
        Assert.EndsWith(Path.Combine("user", "Whispdows", "logs"), sandbox.Paths.LogDirectory);
        Assert.EndsWith(Path.Combine("application", "README.md"), sandbox.Paths.ReadmePath);
    }

    private sealed class TestSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WhispdowsTests", Guid.NewGuid().ToString("N"));

        public TestSandbox()
        {
            Paths = new SettingsPaths(
                Path.Combine(_root, "user", "Whispdows"),
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
