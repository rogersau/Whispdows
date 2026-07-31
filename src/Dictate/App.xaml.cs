using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Dictate;

public partial class App : System.Windows.Application
{
    private readonly SettingsPaths _paths = SettingsPaths.CreateDefault();
    private readonly SettingsLoader _settingsLoader;
    private readonly EnvironmentFileLoader _environmentFileLoader;
    private readonly StartupRegistration _startupRegistration;
    private AppSettings _settings = AppSettings.CreateDefault();
    private ProviderSecrets _secrets = ProviderSecrets.Empty;
    private IAppLogger _logger = NullAppLogger.Instance;
    private TrayMenu? _trayMenu;
    private PillWindow? _pillWindow;
    private DictationController? _controller;
    private HotkeyHook? _hotkeyHook;

    public App()
    {
        _settingsLoader = new SettingsLoader(_paths);
        _environmentFileLoader = new EnvironmentFileLoader(_paths.EnvironmentFile);
        _startupRegistration = new StartupRegistration("Dictate");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _logger = CreateLogger();

        if (StartupConfiguration.IsEnableCommand(e.Args))
        {
            try
            {
                StartupConfiguration.Enable(_settingsLoader, _startupRegistration);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                _logger.LogException("startup-install", exception);
                Shutdown(1);
            }

            return;
        }

        try
        {
            _settings = _settingsLoader.LoadOrCreate();
            _secrets = _environmentFileLoader.LoadOrCreate();
            ReconcileLaunchAtLogin();

            _pillWindow = new PillWindow();
            _controller = new DictationController(
                new AudioRecorder(),
                _pillWindow,
                NativeWindow.GetForegroundWindow,
                _settings.Audio,
                CreatePipeline(_settings, _secrets),
                _logger);
            _controller.StateChanged += ControllerOnStateChanged;
            _controller.ErrorOccurred += ControllerOnError;

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
            _logger.LogException("startup", exception);
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
            _controller.ErrorOccurred -= ControllerOnError;
            _controller.Dispose();
        }

        _trayMenu?.Dispose();
        _pillWindow?.Close();
        _logger.Dispose();
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
            _logger.LogException("startup-enable", exception);
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
            _logger.LogException("enabled-toggle", exception);
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
            _logger.LogException("startup-toggle", exception);
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
        var previousSecrets = _secrets;
        var previousRegistration = _startupRegistration.IsEnabled;
        DictationPipeline? candidatePipeline = null;
        var pipelineReplaced = false;

        try
        {
            var loaded = _settingsLoader.LoadOrCreate();
            var loadedSecrets = _environmentFileLoader.LoadOrCreate();
            candidatePipeline = CreatePipeline(loaded, loadedSecrets);
            await ApplyRuntimeEnabledAsync(false, previousSettings);
            _startupRegistration.SetEnabled(loaded.LaunchAtLogin);
            _controller.UpdateAudioSettings(loaded.Audio);
            _controller.UpdatePipeline(candidatePipeline);
            candidatePipeline = null;
            pipelineReplaced = true;

            if (loaded.Enabled)
            {
                EnableRuntime(loaded);
            }

            _settings = loaded;
            _secrets = loadedSecrets;
            _trayMenu?.ApplySettings(_settings.Enabled, _startupRegistration.IsEnabled);
            _trayMenu?.ShowInfo("Settings reloaded");
        }
        catch (Exception exception)
        {
            _logger.LogException("settings-reload", exception);
            candidatePipeline?.Dispose();
            Exception? rollbackException = null;
            DictationPipeline? rollbackPipeline = null;
            try
            {
                await ApplyRuntimeEnabledAsync(false, previousSettings);
                _startupRegistration.SetEnabled(previousRegistration);
                _controller.UpdateAudioSettings(previousSettings.Audio);
                if (pipelineReplaced)
                {
                    rollbackPipeline = CreatePipeline(previousSettings, previousSecrets);
                    _controller.UpdatePipeline(rollbackPipeline);
                    rollbackPipeline = null;
                }

                await ApplyRuntimeEnabledAsync(previousSettings.Enabled, previousSettings);
            }
            catch (Exception rollbackFailure)
            {
                _logger.LogException("settings-rollback", rollbackFailure);
                rollbackPipeline?.Dispose();
                rollbackException = rollbackFailure;
                try
                {
                    await ApplyRuntimeEnabledAsync(false, previousSettings);
                }
                catch
                {
                    // The runtime is already being treated as disabled.
                }
            }

            _settings = previousSettings;
            _secrets = previousSecrets;
            if (rollbackException is null)
            {
                _trayMenu?.ApplySettings(previousSettings.Enabled, previousRegistration);
                _trayMenu?.ShowError($"Settings were not reloaded: {exception.Message}");
            }
            else
            {
                _settings.Enabled = false;
                _trayMenu?.ApplySettings(false, _startupRegistration.IsEnabled);
                _trayMenu?.ShowError(
                    $"Settings reload and rollback failed. Dictation remains disabled. " +
                    $"{exception.Message} ({rollbackException.GetType().Name})");
            }
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
        _controller.ValidateConfiguration();
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
            _logger.LogException("hotkey-event", exception);
            _trayMenu?.ShowError($"Dictation failed: {exception.Message}");
        }
    }

    private void ControllerOnStateChanged(DictationState state)
    {
        _hotkeyHook?.SetRecordingActive(state == DictationState.Recording);
    }

    private void ControllerOnError(string message)
    {
        _trayMenu?.ShowError(message);
    }

    private DictationPipeline CreatePipeline(
        AppSettings settings,
        ProviderSecrets secrets)
    {
        var transcriber = CreateTranscriber(settings, secrets);

        try
        {
            var cleaner = CreateTextCleaner(settings, secrets);
            return new DictationPipeline(
                transcriber,
                cleaner,
                new TextInserter(settings.Paste));
        }
        catch
        {
            transcriber.Dispose();
            throw;
        }
    }

    private ITranscriber CreateTranscriber(
        AppSettings settings,
        ProviderSecrets secrets)
    {
        var providerName = settings.Transcription.Provider.ToLowerInvariant();
        if (providerName == "local")
        {
            return CreateLocalTranscriber(settings.Transcription);
        }

        ITranscriber cloud;
        if (providerName == "azure")
        {
            cloud = new AzureSpeechTranscriber(
                secrets.Get("AZURE_SPEECH_KEY"),
                settings.Transcription.AzureRegion,
                settings.Transcription.AzureLocale);
        }
        else
        {
            var provider = CloudProviderDefinition.Create(providerName, secrets);
            cloud = new OpenAiCompatibleTranscriber(
                provider,
                providerName == "openai"
                    ? settings.Transcription.OpenaiModel
                    : settings.Transcription.GroqModel,
                settings.Transcription.Language);
        }

        if (!settings.Transcription.FallbackToLocal)
        {
            return cloud;
        }

        try
        {
            return new FallbackTranscriber(
                cloud,
                CreateLocalTranscriber(settings.Transcription));
        }
        catch
        {
            cloud.Dispose();
            throw;
        }
    }

    private ITextCleaner CreateTextCleaner(
        AppSettings settings,
        ProviderSecrets secrets)
    {
        var providerName = settings.Cleanup.Provider.ToLowerInvariant();
        if (providerName == "basic")
        {
            return new BasicTextCleaner(settings.Cleanup.Style.ToLowerInvariant());
        }

        if (providerName == "none")
        {
            return new NoOpTextCleaner();
        }

        ITextCleaner cloud = new LlmTextCleaner(
            CloudProviderDefinition.Create(providerName, secrets),
            settings.Cleanup.Model);
        if (!settings.Cleanup.FallbackToBasic)
        {
            return cloud;
        }

        try
        {
            return new FallbackTextCleaner(
                cloud,
                new BasicTextCleaner(settings.Cleanup.Style.ToLowerInvariant()));
        }
        catch
        {
            if (cloud is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    private ITranscriber CreateLocalTranscriber(TranscriptionSettings settings)
    {
        return new WhisperCppTranscriber(
            ResolveApplicationPath(settings.LocalModelPath),
            settings.Language,
            settings.LocalThreads);
    }

    private string ResolveApplicationPath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(
                _paths.ApplicationDirectory,
                configuredPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private IAppLogger CreateLogger()
    {
        try
        {
            return new SafeAppLogger(new RollingFileLogger(_paths.LogDirectory));
        }
        catch
        {
            return NullAppLogger.Instance;
        }
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
            _logger.LogException("open-settings", exception);
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
            _logger.LogException("open-readme", exception);
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
