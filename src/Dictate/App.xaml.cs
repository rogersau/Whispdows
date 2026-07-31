using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Dictate;

public partial class App : System.Windows.Application
{
    private readonly SettingsPaths _paths = SettingsPaths.CreateDefault();
    private readonly SettingsLoader _settingsLoader;
    private readonly StartupRegistration _startupRegistration;
    private AppSettings _settings = AppSettings.CreateDefault();
    private TrayMenu? _trayMenu;
    private PillWindow? _pillWindow;

    public App()
    {
        _settingsLoader = new SettingsLoader(_paths);
        _startupRegistration = new StartupRegistration("Dictate");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _settings = _settingsLoader.LoadOrCreate();
            ReconcileLaunchAtLogin();

            _pillWindow = new PillWindow();
            _trayMenu = new TrayMenu(
                enabled: _settings.Enabled,
                launchAtLogin: _startupRegistration.IsEnabled,
                onEnabledChanged: SetEnabled,
                onLaunchAtLoginChanged: SetLaunchAtLogin,
                onReloadRequested: ReloadSettings,
                onOpenSettingsRequested: OpenSettingsFolder,
                onOpenReadmeRequested: OpenReadme,
                onExitRequested: Shutdown);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                BuildStartupErrorMessage(exception),
                "Dictate could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayMenu?.Dispose();
        _pillWindow?.Close();
        base.OnExit(e);
    }

    private void SetEnabled(bool enabled)
    {
        var previous = _settings.Enabled;
        _settings.Enabled = enabled;

        try
        {
            _settingsLoader.Save(_settings);
            _trayMenu?.ShowInfo(enabled ? "Dictation enabled" : "Dictation disabled");
        }
        catch (Exception exception)
        {
            _settings.Enabled = previous;
            _trayMenu?.SetEnabled(previous);
            _trayMenu?.ShowError($"Could not save settings: {exception.Message}");
        }
    }

    private void SetLaunchAtLogin(bool enabled)
    {
        var previous = _settings.LaunchAtLogin;

        try
        {
            _startupRegistration.SetEnabled(enabled);
            _settings.LaunchAtLogin = enabled;
            _settingsLoader.Save(_settings);
            _trayMenu?.ShowInfo(enabled ? "Launch at login enabled" : "Launch at login disabled");
        }
        catch (Exception exception)
        {
            _settings.LaunchAtLogin = previous;
            _trayMenu?.SetLaunchAtLogin(previous);
            _trayMenu?.ShowError($"Could not update launch at login: {exception.Message}");
        }
    }

    private void ReloadSettings()
    {
        try
        {
            var loaded = _settingsLoader.LoadOrCreate();
            _settings = loaded;
            ReconcileLaunchAtLogin();
            _trayMenu?.ApplySettings(_settings.Enabled, _startupRegistration.IsEnabled);
            _trayMenu?.ShowInfo("Settings reloaded");
        }
        catch (Exception exception)
        {
            _trayMenu?.ShowError($"Settings were not reloaded: {exception.Message}");
        }
    }

    private void ReconcileLaunchAtLogin()
    {
        _startupRegistration.SetEnabled(_settings.LaunchAtLogin);
    }

    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.SettingsDirectory);
            OpenPath(_paths.SettingsDirectory);
        }
        catch (Exception exception)
        {
            _trayMenu?.ShowError($"Could not open settings folder: {exception.Message}");
        }
    }

    private void OpenReadme()
    {
        try
        {
            if (!File.Exists(_paths.ReadmePath))
            {
                _trayMenu?.ShowError("README.md is not available beside the application.");
                return;
            }

            OpenPath(_paths.ReadmePath);
        }
        catch (Exception exception)
        {
            _trayMenu?.ShowError($"Could not open README: {exception.Message}");
        }
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static string BuildStartupErrorMessage(Exception exception)
    {
        return exception is SettingsValidationException validationException
            ? $"Settings are invalid:\n\n{string.Join("\n", validationException.Errors)}"
            : exception.Message;
    }
}
