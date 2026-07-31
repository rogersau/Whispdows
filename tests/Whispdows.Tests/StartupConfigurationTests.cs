using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class StartupConfigurationTests
{
    [Fact]
    public void Installer_command_updates_minified_settings_and_registration_together()
    {
        using var sandbox = new SettingsSandbox();
        Directory.CreateDirectory(sandbox.Paths.SettingsDirectory);
        File.WriteAllText(
            sandbox.Paths.SettingsFile,
            """{"enabled":false,"launchAtLogin":false}""");
        var registration = new FakeStartupRegistration();

        StartupConfiguration.Enable(
            new SettingsLoader(sandbox.Paths),
            registration);

        var settings = new SettingsLoader(sandbox.Paths).LoadOrCreate();
        Assert.False(settings.Enabled);
        Assert.True(settings.LaunchAtLogin);
        Assert.True(registration.IsEnabled);
        Assert.True(registration.SetEnabledCalled);
    }

    [Theory]
    [InlineData("--enable-startup", true)]
    [InlineData("--ENABLE-STARTUP", true)]
    [InlineData("--other", false)]
    public void Installer_command_is_recognized_explicitly(
        string argument,
        bool expected)
    {
        Assert.Equal(
            expected,
            StartupConfiguration.IsEnableCommand([argument]));
    }

    [Theory]
    [InlineData("--configure-features=transcribe", FeatureSelection.Transcribe)]
    [InlineData("--configure-features=meeting-notes", FeatureSelection.MeetingNotes)]
    [InlineData("--CONFIGURE-FEATURES=BOTH", FeatureSelection.Both)]
    public void Installer_feature_command_is_parsed_explicitly(
        string argument,
        FeatureSelection expected)
    {
        Assert.True(FeatureConfiguration.TryParse([argument], out var selection));
        Assert.Equal(expected, selection);
    }

    [Theory]
    [InlineData(FeatureSelection.Transcribe, true, false)]
    [InlineData(FeatureSelection.MeetingNotes, false, true)]
    [InlineData(FeatureSelection.Both, true, true)]
    public void Installer_feature_command_persists_the_selection(
        FeatureSelection selection,
        bool transcribe,
        bool meetingNotes)
    {
        using var sandbox = new SettingsSandbox();
        var loader = new SettingsLoader(sandbox.Paths);

        FeatureConfiguration.Apply(loader, selection);

        var settings = loader.LoadOrCreate();
        Assert.Equal(transcribe, settings.Features.Transcribe);
        Assert.Equal(meetingNotes, settings.Features.MeetingNotes);
    }

    private sealed class FakeStartupRegistration : IStartupRegistration
    {
        public bool IsEnabled { get; private set; }

        public bool SetEnabledCalled { get; private set; }

        public void SetEnabled(bool enabled)
        {
            SetEnabledCalled = true;
            IsEnabled = enabled;
        }
    }

    private sealed class SettingsSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WhispdowsStartupTests",
            Guid.NewGuid().ToString("N"));

        public SettingsSandbox()
        {
            Paths = new SettingsPaths(
                Path.Combine(_root, "settings"),
                Path.Combine(_root, "app"));
        }

        public SettingsPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
