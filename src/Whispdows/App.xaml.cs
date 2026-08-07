using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Whispdows;

public partial class App : System.Windows.Application
{
    private readonly SettingsPaths _paths = SettingsPaths.CreateDefault();
    private readonly SettingsLoader _settingsLoader;
    private readonly SecureSecretsStore _secretsStore;
    private readonly StartupRegistration _startupRegistration;
    private readonly SemaphoreSlim _meetingToggleGate = new(1, 1);
    private AppSettings _settings = AppSettings.CreateDefault();
    private ProviderSecrets _secrets = ProviderSecrets.Empty;
    private IAppLogger _logger = NullAppLogger.Instance;
    private WindowsMlRuntime? _windowsMlRuntime;
    private TrayMenu? _trayMenu;
    private PillWindow? _pillWindow;
    private SettingsWindow? _settingsWindow;
    private DictationController? _controller;
    private MeetingNotesController? _meetingController;
    private HotkeyHook? _hotkeyHook;
    private bool _dictationPausedForMeeting;

    public App()
    {
        _settingsLoader = new SettingsLoader(_paths);
        _secretsStore = new SecureSecretsStore(_paths.SecretsFile, _paths.EnvironmentFile);
        _startupRegistration = new StartupRegistration("Whispdows");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _logger = CreateLogger();
        _windowsMlRuntime = new WindowsMlRuntime(
            Path.Combine(_paths.SettingsDirectory, "windowsml"));

        if (FeatureConfiguration.TryParse(e.Args, out var featureSelection))
        {
            try
            {
                FeatureConfiguration.Apply(_settingsLoader, featureSelection);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                _logger.LogException("feature-install", exception);
                Shutdown(1);
            }

            return;
        }

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
            _secrets = _secretsStore.LoadOrCreate();
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
                dictationAvailable: _settings.Features.Transcribe,
                meetingNotesAvailable: _settings.Features.MeetingNotes,
                launchAtLogin: _startupRegistration.IsEnabled,
                onEnabledChanged: SetEnabled,
                onLaunchAtLoginChanged: SetLaunchAtLogin,
                onMeetingRecordingRequested: ToggleMeetingRecording,
                onOpenSettingsEditorRequested: OpenSettingsEditor,
                onReloadRequested: ReloadSettings,
                onOpenSettingsRequested: OpenSettingsFolder,
                onOpenMeetingNotesRequested: OpenMeetingNotesFolder,
                onOpenReadmeRequested: OpenReadme,
                onExitRequested: Shutdown);

            if (_settings.Features.Transcribe && _settings.Enabled)
            {
                TryEnableAtStartup();
            }
        }
        catch (Exception exception)
        {
            _logger.LogException("startup", exception);
            System.Windows.MessageBox.Show(
                BuildStartupErrorMessage(exception),
                "Whispdows could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeMeetingController();
        _hotkeyHook?.Dispose();
        if (_controller is not null)
        {
            _controller.StateChanged -= ControllerOnStateChanged;
            _controller.ErrorOccurred -= ControllerOnError;
            _controller.Dispose();
        }

        _trayMenu?.Dispose();
        _settingsWindow?.Close();
        _pillWindow?.Close();
        _windowsMlRuntime?.Dispose();
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
        if (_controller is null
            || _hotkeyHook is null
            || !_settings.Features.Transcribe
            || enabled == _settings.Enabled)
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

        try
        {
            var loaded = _settingsLoader.LoadOrCreate();
            var loadedSecrets = _secretsStore.LoadOrCreate();
            await ApplySettingsAsync(loaded, loadedSecrets, "Settings reloaded");
        }
        catch (Exception exception)
        {
            _logger.LogException("settings-reload", exception);
            _trayMenu?.ShowError($"Settings were not reloaded: {exception.Message}");
        }
    }

    private async Task<string?> ApplySettingsFromEditorAsync(
        AppSettings settings,
        ProviderSecrets secrets)
    {
        try
        {
            return await ApplySettingsAsync(settings, secrets, "Settings applied");
        }
        catch (Exception exception)
        {
            _logger.LogException("settings-editor", exception);
            _trayMenu?.ShowError($"Settings were not applied: {exception.Message}");
            return exception.Message;
        }
    }

    private async Task<string?> ApplySettingsAsync(
        AppSettings loaded,
        ProviderSecrets loadedSecrets,
        string successMessage)
    {
        if (_controller is null || _hotkeyHook is null)
        {
            return "The dictation runtime is not initialized.";
        }

        if (_meetingController is { State: not (MeetingNotesState.Idle or MeetingNotesState.Error) })
        {
            return "Stop the active meeting recording before applying settings.";
        }

        var previousSettings = _settings;
        var previousSecrets = _secrets;
        var previousRegistration = _startupRegistration.IsEnabled;
        DictationPipeline? candidatePipeline = null;
        var pipelineReplaced = false;
        var secretsPersisted = false;
        var settingsPersisted = false;

        try
        {
            SettingsValidator.ThrowIfInvalid(loaded);
            candidatePipeline = CreatePipeline(loaded, loadedSecrets);
            await ApplyRuntimeEnabledAsync(false, previousSettings);
            _startupRegistration.SetEnabled(loaded.LaunchAtLogin);
            _controller.UpdateAudioSettings(loaded.Audio);
            _controller.UpdatePipeline(candidatePipeline);
            candidatePipeline = null;
            pipelineReplaced = true;

            if (loaded.Features.Transcribe && loaded.Enabled)
            {
                EnableRuntime(loaded);
            }

            _secretsStore.Save(loadedSecrets);
            secretsPersisted = true;
            _settingsLoader.Save(loaded);
            settingsPersisted = true;
            _settings = loaded;
            _secrets = loadedSecrets;
            _trayMenu?.ApplySettings(
                _settings.Enabled,
                _startupRegistration.IsEnabled,
                _settings.Features.Transcribe,
                _settings.Features.MeetingNotes);
            _trayMenu?.ShowInfo(successMessage);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogException("settings-apply", exception);
            if (settingsPersisted)
            {
                try
                {
                    _settingsLoader.Save(previousSettings);
                }
                catch (Exception restoreSettingsFailure)
                {
                    _logger.LogException("settings-disk-rollback", restoreSettingsFailure);
                }
            }

            if (secretsPersisted)
            {
                try
                {
                    _secretsStore.Save(previousSecrets);
                }
                catch (Exception restoreSecretsFailure)
                {
                    _logger.LogException("secrets-disk-rollback", restoreSecretsFailure);
                }
            }

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

                await ApplyRuntimeEnabledAsync(
                    previousSettings.Features.Transcribe && previousSettings.Enabled,
                    previousSettings);
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
                _trayMenu?.ApplySettings(
                    previousSettings.Enabled,
                    previousRegistration,
                    previousSettings.Features.Transcribe,
                    previousSettings.Features.MeetingNotes);
                var message = $"Settings were not applied: {exception.Message}";
                _trayMenu?.ShowError(message);
                return message;
            }

            _settings.Enabled = false;
            _trayMenu?.ApplySettings(
                false,
                _startupRegistration.IsEnabled,
                previousSettings.Features.Transcribe,
                previousSettings.Features.MeetingNotes);
            var rollbackMessage =
                "Settings apply and rollback failed. Dictation remains disabled. " +
                $"{exception.Message} ({rollbackException.GetType().Name})";
            _trayMenu?.ShowError(rollbackMessage);
            return rollbackMessage;
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

        if (!settings.Features.Transcribe)
        {
            throw new InvalidOperationException(
                "Dictation was not selected during installation.");
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
        _trayMenu?.SetState(state);
    }

    private void ControllerOnError(string message)
    {
        _trayMenu?.ShowError(message);
    }

    private void ToggleMeetingRecording()
    {
        _ = ToggleMeetingRecordingSafelyAsync();
    }

    private async Task ToggleMeetingRecordingSafelyAsync()
    {
        if (!await _meetingToggleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_meetingController?.State == MeetingNotesState.Recording)
            {
                await StopMeetingRecordingAsync();
                return;
            }

            if (_meetingController is not null)
            {
                return;
            }

            await StartMeetingRecordingAsync();
        }
        catch (Exception exception)
        {
            _logger.LogException("meeting-toggle", exception);
            _trayMenu?.SetMeetingState(MeetingNotesState.Error);
            _trayMenu?.ShowError($"Meeting recording failed: {exception.Message}");
        }
        finally
        {
            _meetingToggleGate.Release();
        }
    }

    private async Task StartMeetingRecordingAsync()
    {
        if (!_settings.Features.MeetingNotes)
        {
            throw new InvalidOperationException(
                "Meeting Notes was not selected during installation.");
        }

        if (_controller?.State is not (DictationState.Idle or DictationState.Disabled))
        {
            throw new InvalidOperationException(
                "Wait for the current dictation to finish before recording a meeting.");
        }

        _dictationPausedForMeeting =
            _settings.Features.Transcribe && _settings.Enabled;
        if (_dictationPausedForMeeting)
        {
            await ApplyRuntimeEnabledAsync(false, _settings);
        }

        try
        {
            _meetingController = CreateMeetingController(_settings, _secrets);
            _meetingController.StateChanged += MeetingControllerOnStateChanged;
            _meetingController.Completed += MeetingControllerOnCompleted;
            _meetingController.ErrorOccurred += MeetingControllerOnError;
            _meetingController.Start();
            _trayMenu?.ShowInfo(
                "Meeting recording started (system audio + microphone)");
        }
        catch
        {
            DisposeMeetingController();
            await RestoreDictationAfterMeetingAsync();
            throw;
        }
    }

    private async Task StopMeetingRecordingAsync()
    {
        var controller = _meetingController
            ?? throw new InvalidOperationException(
                "Meeting audio capture is not active.");
        try
        {
            await controller.StopAsync();
        }
        finally
        {
            DisposeMeetingController();
            await RestoreDictationAfterMeetingAsync();
        }
    }

    private async Task RestoreDictationAfterMeetingAsync()
    {
        var shouldRestore = _dictationPausedForMeeting;
        _dictationPausedForMeeting = false;
        if (shouldRestore && _settings.Features.Transcribe && _settings.Enabled)
        {
            try
            {
                await ApplyRuntimeEnabledAsync(true, _settings);
                _trayMenu?.SetEnabled(true);
            }
            catch (Exception exception)
            {
                _logger.LogException("meeting-dictation-restore", exception);
                _trayMenu?.SetEnabled(false);
                _trayMenu?.ShowError(
                    $"Meeting finished, but dictation could not restart: {exception.Message}");
            }
        }
    }

    private void MeetingControllerOnStateChanged(MeetingNotesState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => MeetingControllerOnStateChanged(state));
            return;
        }

        _trayMenu?.SetMeetingState(state);
    }

    private void MeetingControllerOnCompleted(MeetingNotesArchiveResult result)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => MeetingControllerOnCompleted(result));
            return;
        }

        _trayMenu?.ShowInfo($"Meeting notes saved to {result.MarkdownPath}");
    }

    private void MeetingControllerOnError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => MeetingControllerOnError(message));
            return;
        }

        _trayMenu?.ShowError(message);
    }

    private void DisposeMeetingController()
    {
        var controller = _meetingController;
        _meetingController = null;
        if (controller is null)
        {
            return;
        }

        controller.StateChanged -= MeetingControllerOnStateChanged;
        controller.Completed -= MeetingControllerOnCompleted;
        controller.ErrorOccurred -= MeetingControllerOnError;
        controller.Dispose();
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
        if (providerName == "windowsml")
        {
            var local = CreateWindowsMlTranscriber(settings.Transcription);
            if (!settings.Transcription.FallbackToOnline
                || string.Equals(
                    settings.Transcription.OnlineProvider,
                    "none",
                    StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            try
            {
                return new FallbackTranscriber(
                    local,
                    CreateOnlineTranscriber(
                        settings.Transcription,
                        secrets,
                        settings.Transcription.OnlineProvider),
                    allowMissingPrimaryConfiguration: false,
                    allowMissingFallbackConfiguration: true);
            }
            catch
            {
                local.Dispose();
                throw;
            }
        }

        if (providerName == "local")
        {
            var local = CreateLocalTranscriber(settings.Transcription);
            if (!settings.Transcription.FallbackToOnline
                || string.Equals(
                    settings.Transcription.OnlineProvider,
                    "none",
                    StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            try
            {
                return new FallbackTranscriber(
                    local,
                    CreateOnlineTranscriber(
                        settings.Transcription,
                        secrets,
                        settings.Transcription.OnlineProvider),
                    allowMissingPrimaryConfiguration: false,
                    allowMissingFallbackConfiguration: true);
            }
            catch
            {
                local.Dispose();
                throw;
            }
        }

        var cloud = CreateOnlineTranscriber(
            settings.Transcription,
            secrets,
            providerName);

        if (!settings.Transcription.FallbackToLocal)
        {
            return cloud;
        }

        try
        {
            return new FallbackTranscriber(
                cloud,
                CreateLocalFallbackTranscriber(settings.Transcription));
        }
        catch
        {
            cloud.Dispose();
            throw;
        }
    }

    private MeetingNotesController CreateMeetingController(
        AppSettings settings,
        ProviderSecrets secrets)
    {
        var transcriber = CreateMeetingTranscriber(
            settings.MeetingNotes,
            secrets);
        try
        {
            var notesGenerator = CreateMeetingNotesGenerator(
                settings.MeetingNotes,
                secrets);
            try
            {
                return new MeetingNotesController(
                    new MeetingAudioRecorder(),
                    new AudioSettings
                    {
                        DeviceId = settings.Audio.DeviceId,
                        MaxSeconds = settings.Audio.MaxSeconds
                    },
                    transcriber,
                    notesGenerator,
                    new MeetingNotesArchive(
                        settings.MeetingNotes.OutputDirectory),
                    _logger);
            }
            catch
            {
                notesGenerator.Dispose();
                throw;
            }
        }
        catch
        {
            transcriber.Dispose();
            throw;
        }
    }

    private ITranscriber CreateMeetingTranscriber(
        MeetingNotesSettings settings,
        ProviderSecrets secrets)
    {
        var providerName = settings.TranscriptionProvider.ToLowerInvariant();
        if (providerName == "auto")
        {
            providerName = secrets.Has("OPENAI_API_KEY")
                ? "openai"
                : secrets.Has("GROQ_API_KEY")
                    ? "groq"
                    : "local";
        }

        if (providerName == "local")
        {
            return CreateLocalMeetingTranscriber(settings);
        }

        var cloud = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create(providerName, secrets),
            providerName == "openai"
                ? settings.OpenaiTranscriptionModel
                : settings.GroqTranscriptionModel,
            settings.Language,
            timeout: TimeSpan.FromMinutes(3));
        ITranscriber chunked = new ChunkingTranscriber(
            cloud,
            TimeSpan.FromMinutes(10));

        var localModelPath = ResolveApplicationPath(settings.LocalModelPath);
        if (!File.Exists(localModelPath))
        {
            return chunked;
        }

        try
        {
            return new FallbackTranscriber(
                chunked,
                CreateLocalMeetingTranscriber(settings));
        }
        catch
        {
            chunked.Dispose();
            throw;
        }
    }

    private ITranscriber CreateLocalMeetingTranscriber(
        MeetingNotesSettings settings)
    {
        return new WhisperCppTranscriber(
            ResolveApplicationPath(settings.LocalModelPath),
            settings.Language,
            settings.LocalThreads);
    }

    private IMeetingNotesGenerator CreateMeetingNotesGenerator(
        MeetingNotesSettings settings,
        ProviderSecrets secrets)
    {
        var providerName = settings.NotesProvider.ToLowerInvariant();
        if (providerName == "auto")
        {
            providerName = secrets.Has("OPENAI_API_KEY")
                ? "openai"
                : secrets.Has("GROQ_API_KEY")
                    ? "groq"
                    : "ollama";
        }

        var local = new OllamaMeetingNotesGenerator(
            settings.OllamaEndpoint,
            settings.OllamaModel);
        if (providerName == "ollama")
        {
            return local;
        }

        try
        {
            var cloud = new OpenAiCompatibleMeetingNotesGenerator(
                CloudProviderDefinition.Create(providerName, secrets),
                providerName == "openai"
                    ? settings.OpenaiNotesModel
                    : settings.GroqNotesModel);
            return new FallbackMeetingNotesGenerator(cloud, local);
        }
        catch
        {
            local.Dispose();
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

        if (providerName == "windowsml")
        {
            var local = new WindowsMlTextCleaner(
                GetWindowsMlRuntime(),
                settings.Cleanup.WindowsMlModel);
            ITextCleaner? fallback = null;

            if (settings.Cleanup.FallbackToOnline
                && !string.Equals(
                    settings.Cleanup.OnlineProvider,
                    "none",
                    StringComparison.OrdinalIgnoreCase))
            {
                fallback = CreateOnlineTextCleaner(
                    settings.Cleanup,
                    secrets,
                    settings.Cleanup.OnlineProvider,
                    settings.Cleanup.OnlineModel);
            }

            if (settings.Cleanup.FallbackToBasic)
            {
                var basic = new BasicTextCleaner(settings.Cleanup.Style.ToLowerInvariant());
                fallback = fallback is null
                    ? basic
                    : new FallbackTextCleaner(
                        fallback,
                        basic,
                        allowMissingPrimaryConfiguration: true,
                        allowMissingFallbackConfiguration: false);
            }

            if (fallback is null)
            {
                return local;
            }

            try
            {
                return new FallbackTextCleaner(
                    local,
                    fallback,
                    allowMissingPrimaryConfiguration: false,
                    allowMissingFallbackConfiguration: false);
            }
            catch
            {
                local.Dispose();
                if (fallback is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                throw;
            }
        }

        ITextCleaner aiCleaner = CreateOnlineTextCleaner(
            settings.Cleanup,
            secrets,
            providerName);
        if (!settings.Cleanup.FallbackToBasic)
        {
            return aiCleaner;
        }

        try
        {
            return new FallbackTextCleaner(
                aiCleaner,
                new BasicTextCleaner(settings.Cleanup.Style.ToLowerInvariant()));
        }
        catch
        {
            if (aiCleaner is IDisposable disposable)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    private ITranscriber CreateOnlineTranscriber(
        TranscriptionSettings settings,
        ProviderSecrets secrets,
        string providerName)
    {
        providerName = providerName.ToLowerInvariant();
        if (providerName == "azure")
        {
            return new AzureSpeechTranscriber(
                secrets.Get("AZURE_SPEECH_KEY"),
                settings.AzureRegion,
                settings.AzureLocale);
        }

        var provider = CloudProviderDefinition.Create(providerName, secrets);
        return new OpenAiCompatibleTranscriber(
            provider,
            providerName == "openai"
                ? settings.OpenaiModel
                : settings.GroqModel,
            settings.Language);
    }

    private ITextCleaner CreateOnlineTextCleaner(
        CleanupSettings settings,
        ProviderSecrets secrets,
        string providerName,
        string? modelOverride = null)
    {
        providerName = providerName.ToLowerInvariant();
        var model = modelOverride ?? settings.Model;
        return providerName switch
        {
            "azure-openai" => new AzureOpenAiTextCleaner(
                secrets.Get("AZURE_SPEECH_KEY"),
                settings.AzureEndpoint,
                model),
            "ollama" => new OllamaTextCleaner(
                settings.LocalModel,
                settings.LocalEndpoint),
            _ => new LlmTextCleaner(
                CloudProviderDefinition.Create(providerName, secrets),
                model)
        };
    }

    private ITranscriber CreateLocalTranscriber(TranscriptionSettings settings)
    {
        return new WhisperCppTranscriber(
            ResolveApplicationPath(settings.LocalModelPath),
            settings.Language,
            settings.LocalThreads);
    }

    private ITranscriber CreateWindowsMlTranscriber(TranscriptionSettings settings)
    {
        return new WindowsMlTranscriber(
            GetWindowsMlRuntime(),
            settings.WindowsMlModel,
            settings.Language);
    }

    private ITranscriber CreateLocalFallbackTranscriber(TranscriptionSettings settings)
    {
        return new FallbackTranscriber(
            CreateWindowsMlTranscriber(settings),
            CreateLocalTranscriber(settings),
            allowMissingPrimaryConfiguration: false,
            allowMissingFallbackConfiguration: false);
    }

    private WindowsMlRuntime GetWindowsMlRuntime()
    {
        return _windowsMlRuntime
            ?? throw new InvalidOperationException(
                "The Windows ML runtime has not been initialized.");
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

    private void OpenSettingsEditor()
    {
        try
        {
            if (_settingsWindow is not null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(
                _settings,
                _secrets,
                ApplySettingsFromEditorAsync);
            _settingsWindow.Closed += SettingsWindowOnClosed;
            _settingsWindow.Show();
        }
        catch (Exception exception)
        {
            _logger.LogException("open-settings-editor", exception);
            _trayMenu?.ShowError($"Could not open settings: {exception.Message}");
        }
    }

    private void SettingsWindowOnClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _settingsWindow))
        {
            _settingsWindow = null;
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

    private void OpenMeetingNotesFolder()
    {
        try
        {
            var directory = MeetingNotesArchive.ResolveOutputDirectory(
                _settings.MeetingNotes.OutputDirectory);
            Directory.CreateDirectory(directory);
            OpenPath(directory);
        }
        catch (Exception exception)
        {
            _logger.LogException("open-meeting-notes", exception);
            _trayMenu?.ShowError(
                $"Could not open the MeetingNotes folder: {exception.Message}");
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
