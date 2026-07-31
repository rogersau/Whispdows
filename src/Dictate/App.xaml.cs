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
    private DictationController? _controller;
    private HotkeyHook? _hotkeyHook;

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
            _controller = new DictationController(
                new AudioRecorder(),
                _pillWindow,
                NativeWindow.GetForegroundWindow,
                _settings.Audio);
            _controller.StateChanged += ControllerOnStateChanged;

            _hotkeyHook = new HotkeyHook(Dispatcher, HandleHotkeyEvent);
            _trayMenu = new TrayMenu(
                enabled: false,
                launchAtLogin: _startupRegistration.IsEnabled,
                onEnabledChanged: SetEnabled,
                onLaunchAtLoginChanged: SetLaunchAtLogin,
                onReloadRequested: ReloadSettings,
                onOpenSettingsRequested: OpenSettingsFolder,
                onOpenReadmeRequested: OpenReadme,
                onExitRequested: Shutdown);

            if (_settings.Enabled)
            {
                TryEnableAtStartup();
            }
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
        _hotkeyHook?.Dispose();
        if (_controller is not null)
        {
            _controller.StateChanged -= ControllerOnStateChanged;
            _controller.Dispose();
        }

        _trayMenu?.Dispose();
        _pillWindow?.Close();
        base.OnExit(e);
    }

    private void TryEnableAtStartup()
    {
        try
        {
            EnableRuntime(_settings);
            _trayMenu?.SetEnabled(true);
        }
        catch (Exception exception)
        {
            _settings.Enabled = false;
            try
            {
                _settingsLoader.Save(_settings);
            }
            catch
            {
                // The hook failure is the useful error to show here.
            }

            _trayMenu?.SetEnabled(false);
            _trayMenu?.ShowError($"Dictation was disabled: {exception.Message}");
        }
    }

    private async void SetEnabled(bool enabled)
    {
        if (_controller is null || _hotkeyHook is null || enabled == _settings.Enabled)
        {
            return;
        }

        var previous = _settings.Enabled;
        try
        {
            await ApplyRuntimeEnabledAsync(enabled, _settings);
            _settings.Enabled = enabled;
            _settingsLoader.Save(_settings);
            _trayMenu?.ShowInfo(enabled ? "Dictation enabled" : "Dictation disabled");
        }
        catch (Exception exception)
        {
            _settings.Enabled = previous;
            try
            {
                await ApplyRuntimeEnabledAsync(previous, _settings);
            }
            catch
            {
                // The original operation error is more actionable.
            }

            _trayMenu?.SetEnabled(previous);
            _trayMenu?.ShowError($"Could not change dictation state: {exception.Message}");
        }
    }

    private void SetLaunchAtLogin(bool enabled)
    {
        var previousSetting = _settings.LaunchAtLogin;
        var previousRegistration = _startupRegistration.IsEnabled;

        try
        {
            _settings.LaunchAtLogin = enabled;
            _settingsLoader.Save(_settings);
            _startupRegistration.SetEnabled(enabled);
            _trayMenu?.ShowInfo(enabled ? "Launch at login enabled" : "Launch at login disabled");
        }
        catch (Exception exception)
        {
            _settings.LaunchAtLogin = previousSetting;
            try
            {
                _settingsLoader.Save(_settings);
                _startupRegistration.SetEnabled(previousRegistration);
            }
            catch
            {
                // Preserve the first error; it explains why the toggle failed.
            }

            _trayMenu?.SetLaunchAtLogin(previousRegistration);
            _trayMenu?.ShowError($"Could not update launch at login: {exception.Message}");
        }
    }

    private async void ReloadSettings()
    {
        if (_controller is null || _hotkeyHook is null)
        {
            return;
        }

        var previousSettings = _settings;
        var previousRegistration = _startupRegistration.IsEnabled;

        try
        {
            var loaded = _settingsLoader.LoadOrCreate();
            await ApplyRuntimeEnabledAsync(false, previousSettings);
            _startupRegistration.SetEnabled(loaded.LaunchAtLogin);
            _controller.UpdateAudioSettings(loaded.Audio);

            if (loaded.Enabled)
            {
                EnableRuntime(loaded);
            }

            _settings = loaded;
            _trayMenu?.ApplySettings(_settings.Enabled, _startupRegistration.IsEnabled);
            _trayMenu?.ShowInfo("Settings reloaded");
        }
        catch (Exception exception)
        {
            try
            {
                _startupRegistration.SetEnabled(previousRegistration);
                _controller.UpdateAudioSettings(previousSettings.Audio);
                await ApplyRuntimeEnabledAsync(previousSettings.Enabled, previousSettings);
            }
            catch
            {
                // Keep reporting the reload failure that initiated rollback.
            }

            _settings = previousSettings;
            _trayMenu?.ApplySettings(previousSettings.Enabled, previousRegistration);
            _trayMenu?.ShowError($"Settings were not reloaded: {exception.Message}");
        }
    }

    private void ReconcileLaunchAtLogin()
    {
        _startupRegistration.SetEnabled(_settings.LaunchAtLogin);
    }

    private void EnableRuntime(AppSettings settings)
    {
        if (_controller is null || _hotkeyHook is null)
        {
            throw new InvalidOperationException("The dictation runtime is not initialized.");
        }

        var shortcut = HotkeyParser.Parse(settings.Hotkey.Shortcut);
        _controller.UpdateAudioSettings(settings.Audio);
        _controller.Enable();

        try
        {
            _hotkeyHook.Install(new HotkeyBinding(shortcut, settings.Hotkey.Suppress));
        }
        catch
        {
            _controller.DisableAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    private async Task ApplyRuntimeEnabledAsync(bool enabled, AppSettings settings)
    {
        if (enabled)
        {
            EnableRuntime(settings);
            return;
        }

        _hotkeyHook?.Remove();
        if (_controller is not null)
        {
            await _controller.DisableAsync();
        }
    }

    private void HandleHotkeyEvent(HotkeyEvent hotkeyEvent)
    {
        _ = HandleHotkeyEventSafelyAsync(hotkeyEvent);
    }

    private async Task HandleHotkeyEventSafelyAsync(HotkeyEvent hotkeyEvent)
    {
        try
        {
            if (_controller is not null)
            {
                await _controller.HandleHotkeyEventAsync(hotkeyEvent);
            }
        }
        catch (Exception exception)
        {
            _trayMenu?.ShowError($"Dictation failed: {exception.Message}");
        }
    }

    private void ControllerOnStateChanged(DictationState state)
    {
        _hotkeyHook?.SetRecordingActive(state == DictationState.Recording);
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
