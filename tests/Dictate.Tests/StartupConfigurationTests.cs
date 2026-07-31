using Dictate;
using Xunit;

namespace Dictate.Tests;

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
            "DictateStartupTests",
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
