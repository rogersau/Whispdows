using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

namespace Whispdows;

public partial class SettingsWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly AppSettings _template;
    private readonly ProviderSecrets _templateSecrets;
    private readonly Func<AppSettings, ProviderSecrets, Task<string?>> _applySettings;
    private readonly HashSet<Key> _recordingModifierKeys = [];
    private bool _isRecordingHotkey;
    private Key? _firstRecordingModifier;
    private bool _recordingSawMultipleModifiers;

    public SettingsWindow(
        AppSettings settings,
        ProviderSecrets secrets,
        Func<AppSettings, ProviderSecrets, Task<string?>> applySettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(applySettings);

        _template = SettingsSnapshot.Clone(settings);
        _templateSecrets = secrets;
        _applySettings = applySettings;
        InitializeComponent();
        LoadSettings(_template);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var darkMode = 1;
        var borderColor = ColorRef(46, 58, 80);
        var captionColor = ColorRef(14, 17, 24);
        var textColor = ColorRef(236, 242, 255);
        SetDwmWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode);
        SetDwmWindowAttribute(handle, DwmwaBorderColor, ref borderColor);
        SetDwmWindowAttribute(handle, DwmwaCaptionColor, ref captionColor);
        SetDwmWindowAttribute(handle, DwmwaTextColor, ref textColor);
        ConstrainToWorkingArea(handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopHotkeyRecording();
        ClearApiKeyInputs();
        base.OnClosed(e);
    }

    private void LoadSettings(AppSettings settings)
    {
        EnabledBox.IsChecked = settings.Enabled;
        LaunchAtLoginBox.IsChecked = settings.LaunchAtLogin;
        ShortcutBox.Text = settings.Hotkey.Shortcut;
        SuppressBox.IsChecked = settings.Hotkey.Suppress;

        LoadAudioDevices(settings.Audio.DeviceId);
        MaxSecondsBox.Text = settings.Audio.MaxSeconds.ToString(CultureInfo.InvariantCulture);

        SelectValue(TranscriptionProviderBox, settings.Transcription.Provider);
        LanguageBox.Text = settings.Transcription.Language;
        FallbackLocalBox.IsChecked = settings.Transcription.FallbackToLocal;
        LocalModelPathBox.Text = settings.Transcription.LocalModelPath;
        LocalThreadsBox.Text = settings.Transcription.LocalThreads.ToString(CultureInfo.InvariantCulture);
        OpenAiModelBox.Text = settings.Transcription.OpenaiModel;
        GroqModelBox.Text = settings.Transcription.GroqModel;
        AzureRegionBox.Text = settings.Transcription.AzureRegion;
        AzureLocaleBox.Text = settings.Transcription.AzureLocale;

        SelectValue(CleanupProviderBox, settings.Cleanup.Provider);
        SelectValue(CleanupStyleBox, settings.Cleanup.Style);
        CleanupModelBox.Text = settings.Cleanup.Model;
        AzureEndpointBox.Text = settings.Cleanup.AzureEndpoint;
        LocalCleanupModelBox.Text = settings.Cleanup.LocalModel;
        LocalCleanupEndpointBox.Text = settings.Cleanup.LocalEndpoint;
        FallbackBasicBox.IsChecked = settings.Cleanup.FallbackToBasic;

        RestoreClipboardBox.IsChecked = settings.Paste.RestoreClipboard;
        RestoreDelayBox.Text = settings.Paste.RestoreDelayMs.ToString(CultureInfo.InvariantCulture);
        OpenAiApiKeyStatus.Text = SecretStatus("OPENAI_API_KEY");
        GroqApiKeyStatus.Text = SecretStatus("GROQ_API_KEY");
        AzureApiKeyStatus.Text = SecretStatus("AZURE_SPEECH_KEY");
        UpdateProviderPanels();
    }

    private void LoadAudioDevices(string selectedDeviceId)
    {
        var devices = new List<AudioDeviceOption>();
        try
        {
            devices.AddRange(AudioDeviceCatalog.GetCaptureDevices());
        }
        catch
        {
            devices.Add(new AudioDeviceOption("default", "Default microphone"));
        }

        if (!devices.Any(device => string.Equals(
                device.Id,
                selectedDeviceId,
                StringComparison.OrdinalIgnoreCase)))
        {
            devices.Add(new AudioDeviceOption(
                selectedDeviceId,
                $"Unavailable device ({selectedDeviceId})"));
        }

        AudioDeviceBox.Items.Clear();
        foreach (var device in devices)
        {
            AudioDeviceBox.Items.Add(new WpfControls.ComboBoxItem
            {
                Content = device.Name,
                Tag = device.Id
            });
        }

        AudioDeviceBox.SelectedItem = AudioDeviceBox.Items
            .OfType<WpfControls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                selectedDeviceId,
                StringComparison.OrdinalIgnoreCase));
        AudioDeviceBox.SelectedIndex = AudioDeviceBox.SelectedIndex < 0 ? 0 : AudioDeviceBox.SelectedIndex;
    }

    private string SecretStatus(string name)
    {
        return _templateSecrets.Has(name)
            ? "A key is stored securely. Enter a new value to replace it."
            : "No key is configured.";
    }

    private void TranscriptionProviderBox_OnSelectionChanged(
        object sender,
        WpfControls.SelectionChangedEventArgs e)
    {
        UpdateProviderPanels();
    }

    private void CleanupProviderBox_OnSelectionChanged(
        object sender,
        WpfControls.SelectionChangedEventArgs e)
    {
        UpdateProviderPanels();
    }

    private void FallbackBasicBox_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateProviderPanels();
    }

    private void LocalCleanupModelBox_OnTextChanged(
        object sender,
        WpfControls.TextChangedEventArgs e)
    {
        UpdateLocalSetupHint();
    }

    private void LocalModelPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfControls.Button { Tag: string model })
        {
            LocalCleanupModelBox.Text = model;
            LocalCleanupModelBox.Focus();
            LocalCleanupModelBox.CaretIndex = LocalCleanupModelBox.Text.Length;
        }
    }

    private void UpdateProviderPanels()
    {
        var transcriptionProvider = SelectedValue(TranscriptionProviderBox);
        LocalTranscriptionPanel.Visibility = transcriptionProvider == "local"
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenAiTranscriptionPanel.Visibility = transcriptionProvider == "openai"
            ? Visibility.Visible
            : Visibility.Collapsed;
        GroqTranscriptionPanel.Visibility = transcriptionProvider == "groq"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AzureTranscriptionPanel.Visibility = transcriptionProvider == "azure"
            ? Visibility.Visible
            : Visibility.Collapsed;
        FallbackLocalBox.Visibility = transcriptionProvider == "local"
            ? Visibility.Collapsed
            : Visibility.Visible;

        var cleanupProvider = SelectedValue(CleanupProviderBox);
        var isCloudCleanup = cleanupProvider is "azure-openai" or "openai" or "groq";
        var isLocalAiCleanup = cleanupProvider == "ollama";
        var hasAiCleanup = isCloudCleanup || isLocalAiCleanup;
        LocalCleanupPanel.Visibility = isLocalAiCleanup
            ? Visibility.Visible
            : Visibility.Collapsed;
        CloudCleanupPanel.Visibility = isCloudCleanup
            ? Visibility.Visible
            : Visibility.Collapsed;
        AzureCleanupPanel.Visibility = cleanupProvider == "azure-openai"
            ? Visibility.Visible
            : Visibility.Collapsed;
        FallbackBasicBox.Visibility = hasAiCleanup
            ? Visibility.Visible
            : Visibility.Collapsed;

        var usesBasicCleanup = cleanupProvider == "basic"
            || (hasAiCleanup && FallbackBasicBox.IsChecked == true);
        CleanupStyleLabel.IsEnabled = usesBasicCleanup;
        CleanupStyleBox.IsEnabled = usesBasicCleanup;

        CleanupPrivacyText.Text = cleanupProvider switch
        {
            "ollama" =>
                "Cleanup runs through the local endpoint on this PC. Whispdows does not send the transcript to a cloud cleanup service.",
            "azure-openai" or "openai" or "groq" =>
                "Cloud cleanup sends the transcript to the selected provider. API keys are stored securely for this Windows user.",
            "none" => "The raw transcript is pasted without any cleanup.",
            _ => "Basic cleanup is deterministic and stays entirely on this PC."
        };
        UpdateLocalSetupHint();
    }

    private void UpdateLocalSetupHint()
    {
        if (LocalSetupHint is null || LocalCleanupModelBox is null)
        {
            return;
        }

        var model = LocalCleanupModelBox.Text.Trim();
        LocalSetupHint.Text = string.IsNullOrWhiteSpace(model)
            ? "Install Ollama, choose a model above, then pull it before enabling local AI cleanup."
            : $"Setup command:  ollama pull {model}";
    }

    private async void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = TryBuildSettings();
        if (settings is null)
        {
            return;
        }

        var secrets = BuildSecrets();
        SetBusy(true);
        try
        {
            var error = await _applySettings(settings, secrets);
            if (string.IsNullOrWhiteSpace(error))
            {
                Close();
                return;
            }

            ShowError(error);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            ClearApiKeyInputs();
            SetBusy(false);
        }
    }

    private void RecordHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            StopHotkeyRecording();
            return;
        }

        _isRecordingHotkey = true;
        _recordingModifierKeys.Clear();
        _firstRecordingModifier = null;
        _recordingSawMultipleModifiers = false;
        ShortcutBox.IsEnabled = false;
        RecordHotkeyButton.Content = "Cancel recording";
        RecordHotkeyButton.Focus();
        ErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void Window_OnPreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat)
        {
            return;
        }

        var key = EffectiveKey(e);
        if (key == Key.Escape)
        {
            StopHotkeyRecording();
            return;
        }

        if (IsModifierKey(key))
        {
            if (_recordingModifierKeys.Add(key))
            {
                if (_firstRecordingModifier is null)
                {
                    _firstRecordingModifier = key;
                }
                else
                {
                    _recordingSawMultipleModifiers = true;
                }
            }

            return;
        }

        if (!TryGetTriggerToken(key, out var triggerToken))
        {
            ShowError($"'{key}' cannot be used as a Whispdows hotkey.");
            StopHotkeyRecording();
            return;
        }

        CaptureHotkey(BuildShortcut(_recordingModifierKeys, triggerToken));
    }

    private void Window_OnPreviewKeyUp(object sender, WpfInput.KeyEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        var key = EffectiveKey(e);
        if (!IsModifierKey(key))
        {
            return;
        }

        e.Handled = true;
        _recordingModifierKeys.Remove(key);
        if (_recordingModifierKeys.Count == 0
            && !_recordingSawMultipleModifiers
            && _firstRecordingModifier == key)
        {
            CaptureHotkey(GetModifierToken(key));
        }
    }

    private void CaptureHotkey(string shortcut)
    {
        if (!HotkeyParser.TryParse(shortcut, out _, out var error))
        {
            ShowError(error ?? "That hotkey is not supported.");
            StopHotkeyRecording();
            return;
        }

        ShortcutBox.Text = shortcut;
        StopHotkeyRecording();
    }

    private void StopHotkeyRecording()
    {
        _isRecordingHotkey = false;
        _recordingModifierKeys.Clear();
        _firstRecordingModifier = null;
        _recordingSawMultipleModifiers = false;
        if (ShortcutBox is not null)
        {
            ShortcutBox.IsEnabled = true;
        }

        if (RecordHotkeyButton is not null)
        {
            RecordHotkeyButton.Content = "Record hotkey";
        }
    }

    private static Key EffectiveKey(WpfInput.KeyEventArgs e)
    {
        return e.Key == Key.System ? e.SystemKey : e.Key;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;
    }

    private static string GetModifierToken(Key key)
    {
        return key switch
        {
            Key.LeftCtrl => "LeftCtrl",
            Key.RightCtrl => "RightCtrl",
            Key.LeftShift => "LeftShift",
            Key.RightShift => "RightShift",
            Key.LeftAlt => "LeftAlt",
            Key.RightAlt => "RightAlt",
            Key.LWin => "LeftWin",
            Key.RWin => "RightWin",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
        };
    }

    private static string BuildShortcut(IEnumerable<Key> modifiers, string triggerToken)
    {
        var modifierTokens = modifiers
            .Select(GetModifierCategory)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(token => token switch
            {
                "Ctrl" => 0,
                "Shift" => 1,
                "Alt" => 2,
                "Win" => 3,
                _ => 4
            })
            .ToList();
        modifierTokens.Add(triggerToken);
        return string.Join('+', modifierTokens);
    }

    private static string GetModifierCategory(Key key)
    {
        return key switch
        {
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LWin or Key.RWin => "Win",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
        };
    }

    private static bool TryGetTriggerToken(Key key, out string token)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            token = key.ToString();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            token = ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            token = key.ToString();
            return true;
        }

        if (key == Key.Space)
        {
            token = "Space";
            return true;
        }

        token = string.Empty;
        return false;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private AppSettings? TryBuildSettings()
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
        var settings = SettingsSnapshot.Clone(_template);
        var errors = new List<string>();

        settings.Enabled = EnabledBox.IsChecked == true;
        settings.LaunchAtLogin = LaunchAtLoginBox.IsChecked == true;
        settings.Hotkey.Shortcut = ShortcutBox.Text.Trim();
        settings.Hotkey.Suppress = SuppressBox.IsChecked == true;

        settings.Audio.DeviceId = SelectedDeviceId();
        settings.Audio.MaxSeconds = ReadInteger(
            MaxSecondsBox,
            "audio.maxSeconds",
            settings.Audio.MaxSeconds,
            errors);

        settings.Transcription.Provider = ReadSelectedValue(
            TranscriptionProviderBox,
            "transcription.provider",
            errors);
        settings.Transcription.Language = LanguageBox.Text.Trim();
        settings.Transcription.FallbackToLocal = FallbackLocalBox.IsChecked == true;
        settings.Transcription.LocalModelPath = LocalModelPathBox.Text.Trim();
        settings.Transcription.LocalThreads = ReadInteger(
            LocalThreadsBox,
            "transcription.localThreads",
            settings.Transcription.LocalThreads,
            errors);
        settings.Transcription.OpenaiModel = OpenAiModelBox.Text.Trim();
        settings.Transcription.GroqModel = GroqModelBox.Text.Trim();
        settings.Transcription.AzureRegion = AzureRegionBox.Text.Trim();
        settings.Transcription.AzureLocale = AzureLocaleBox.Text.Trim();

        settings.Cleanup.Provider = ReadSelectedValue(
            CleanupProviderBox,
            "cleanup.provider",
            errors);
        settings.Cleanup.Style = ReadSelectedValue(
            CleanupStyleBox,
            "cleanup.style",
            errors);
        settings.Cleanup.Model = CleanupModelBox.Text.Trim();
        settings.Cleanup.AzureEndpoint = AzureEndpointBox.Text.Trim();
        settings.Cleanup.LocalModel = LocalCleanupModelBox.Text.Trim();
        settings.Cleanup.LocalEndpoint = LocalCleanupEndpointBox.Text.Trim();
        settings.Cleanup.FallbackToBasic = FallbackBasicBox.IsChecked == true;

        settings.Paste.RestoreClipboard = RestoreClipboardBox.IsChecked == true;
        settings.Paste.RestoreDelayMs = ReadInteger(
            RestoreDelayBox,
            "paste.restoreDelayMs",
            settings.Paste.RestoreDelayMs,
            errors);

        errors.AddRange(SettingsValidator.Validate(settings));
        if (errors.Count > 0)
        {
            FocusFirstInvalidField(errors);
            ShowError(string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal)));
            return null;
        }

        ErrorBorder.Visibility = Visibility.Collapsed;
        return settings;
    }

    private ProviderSecrets BuildSecrets()
    {
        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        AddSecretUpdate(
            updates,
            "OPENAI_API_KEY",
            OpenAiApiKeyBox,
            ClearOpenAiApiKeyBox);
        AddSecretUpdate(
            updates,
            "GROQ_API_KEY",
            GroqApiKeyBox,
            ClearGroqApiKeyBox);
        AddSecretUpdate(
            updates,
            "AZURE_SPEECH_KEY",
            AzureApiKeyBox,
            ClearAzureApiKeyBox);
        return _templateSecrets.WithUpdates(updates);
    }

    private static void AddSecretUpdate(
        IDictionary<string, string?> updates,
        string name,
        WpfControls.PasswordBox passwordBox,
        WpfControls.CheckBox clearBox)
    {
        if (clearBox.IsChecked == true)
        {
            updates[name] = string.Empty;
        }
        else if (passwordBox.SecurePassword.Length > 0)
        {
            updates[name] = passwordBox.Password;
        }
    }

    private void ClearApiKeyInputs()
    {
        if (OpenAiApiKeyBox is null)
        {
            return;
        }

        OpenAiApiKeyBox.Clear();
        GroqApiKeyBox.Clear();
        AzureApiKeyBox.Clear();
    }

    private string SelectedDeviceId()
    {
        return (AudioDeviceBox.SelectedItem as WpfControls.ComboBoxItem)?.Tag?.ToString()
            ?? "default";
    }

    private static int ReadInteger(
        WpfControls.TextBox textBox,
        string propertyName,
        int fallback,
        ICollection<string> errors)
    {
        if (int.TryParse(
            textBox.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value))
        {
            return value;
        }

        errors.Add($"{propertyName} must be a whole number.");
        return fallback;
    }

    private static string ReadSelectedValue(
        WpfControls.ComboBox comboBox,
        string propertyName,
        ICollection<string> errors)
    {
        var value = SelectedValue(comboBox);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} must be selected.");
        }

        return value;
    }

    private static void SelectValue(WpfControls.ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<WpfControls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                ItemValue(item),
                value,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectedValue(WpfControls.ComboBox comboBox)
    {
        return comboBox.SelectedItem is WpfControls.ComboBoxItem item
            ? ItemValue(item)
            : string.Empty;
    }

    private static string ItemValue(WpfControls.ComboBoxItem item)
    {
        return item.Tag?.ToString() ?? item.Content?.ToString() ?? string.Empty;
    }

    private void FocusFirstInvalidField(IReadOnlyCollection<string> errors)
    {
        WpfControls.Control? control = errors
            .Select(ResolveInvalidControl)
            .FirstOrDefault(candidate => candidate is not null);
        control?.Focus();
    }

    private WpfControls.Control? ResolveInvalidControl(string error)
    {
        if (error.StartsWith("hotkey", StringComparison.OrdinalIgnoreCase)
            || error.Contains("shortcut", StringComparison.OrdinalIgnoreCase))
        {
            return ShortcutBox;
        }

        if (error.StartsWith("audio.deviceId", StringComparison.OrdinalIgnoreCase))
        {
            return AudioDeviceBox;
        }

        if (error.StartsWith("audio.maxSeconds", StringComparison.OrdinalIgnoreCase))
        {
            return MaxSecondsBox;
        }

        if (error.StartsWith("transcription.provider", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptionProviderBox;
        }

        if (error.StartsWith("transcription.language", StringComparison.OrdinalIgnoreCase))
        {
            return LanguageBox;
        }

        if (error.StartsWith("transcription.localModelPath", StringComparison.OrdinalIgnoreCase))
        {
            return LocalModelPathBox;
        }

        if (error.StartsWith("transcription.localThreads", StringComparison.OrdinalIgnoreCase))
        {
            return LocalThreadsBox;
        }

        if (error.StartsWith("cleanup.provider", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupProviderBox;
        }

        if (error.StartsWith("cleanup.localModel", StringComparison.OrdinalIgnoreCase))
        {
            return LocalCleanupModelBox;
        }

        if (error.StartsWith("cleanup.localEndpoint", StringComparison.OrdinalIgnoreCase))
        {
            return LocalCleanupEndpointBox;
        }

        if (error.StartsWith("cleanup.azureEndpoint", StringComparison.OrdinalIgnoreCase))
        {
            return AzureEndpointBox;
        }

        if (error.StartsWith("cleanup.model", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupModelBox;
        }

        if (error.StartsWith("cleanup.style", StringComparison.OrdinalIgnoreCase))
        {
            return CleanupStyleBox;
        }

        if (error.StartsWith("paste.restoreDelayMs", StringComparison.OrdinalIgnoreCase))
        {
            return RestoreDelayBox;
        }

        return null;
    }

    private void SetBusy(bool busy)
    {
        ApplyButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        SettingsScroll.IsEnabled = !busy;
        SaveProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ErrorBorder.Visibility = Visibility.Collapsed;
        }

        ApplyButton.Content = busy ? "Applying…" : "Save & Apply";
    }

    private void ShowError(string message)
    {
        SaveProgress.Visibility = Visibility.Collapsed;
        ErrorText.Text = FriendlyError(message);
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private static string FriendlyError(string message)
    {
        return message
            .Replace("hotkey.shortcut", "Shortcut", StringComparison.Ordinal)
            .Replace("audio.deviceId", "Microphone / device ID", StringComparison.Ordinal)
            .Replace("audio.maxSeconds", "Maximum recording length", StringComparison.Ordinal)
            .Replace("transcription.provider", "Transcription provider", StringComparison.Ordinal)
            .Replace("transcription.language", "Language code", StringComparison.Ordinal)
            .Replace("transcription.localModelPath", "Whisper model path", StringComparison.Ordinal)
            .Replace("transcription.localThreads", "CPU threads", StringComparison.Ordinal)
            .Replace("cleanup.provider", "Cleanup provider", StringComparison.Ordinal)
            .Replace("cleanup.localModel", "Local cleanup model", StringComparison.Ordinal)
            .Replace("cleanup.localEndpoint", "Local cleanup endpoint", StringComparison.Ordinal)
            .Replace("cleanup.azureEndpoint", "Azure OpenAI endpoint", StringComparison.Ordinal)
            .Replace("cleanup.model", "Cleanup model / deployment", StringComparison.Ordinal)
            .Replace("cleanup.style", "Basic cleanup style", StringComparison.Ordinal)
            .Replace("paste.restoreDelayMs", "Clipboard restore delay", StringComparison.Ordinal);
    }

    private static int ColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    private void ConstrainToWorkingArea(nint handle)
    {
        var screen = System.Windows.Forms.Screen.FromHandle((IntPtr)handle);
        if (HwndSource.FromHwnd(handle) is not HwndSource source
            || source.CompositionTarget is null)
        {
            return;
        }

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new System.Windows.Point(
            screen.WorkingArea.Left,
            screen.WorkingArea.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(
            screen.WorkingArea.Right,
            screen.WorkingArea.Bottom));
        const double margin = 16;
        var availableWidth = Math.Max(
            320,
            bottomRight.X - topLeft.X - (margin * 2));
        var availableHeight = Math.Max(
            420,
            bottomRight.Y - topLeft.Y - (margin * 2));

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Left = topLeft.X + ((bottomRight.X - topLeft.X - Width) / 2);
        Top = topLeft.Y + ((bottomRight.Y - topLeft.Y - Height) / 2);
    }

    private static void SetDwmWindowAttribute(
        nint handle,
        int attribute,
        ref int value)
    {
        _ = DwmSetWindowAttribute(
            handle,
            attribute,
            ref value,
            Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int valueSize);
}
