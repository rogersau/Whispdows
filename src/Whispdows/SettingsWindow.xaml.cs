using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WpfControls = System.Windows.Controls;

namespace Whispdows;

public partial class SettingsWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly AppSettings _template;
    private readonly Func<AppSettings, Task<string?>> _applySettings;

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings, Task<string?>> applySettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(applySettings);

        _template = SettingsSnapshot.Clone(settings);
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
    }

    private void LoadSettings(AppSettings settings)
    {
        EnabledBox.IsChecked = settings.Enabled;
        LaunchAtLoginBox.IsChecked = settings.LaunchAtLogin;
        ShortcutBox.Text = settings.Hotkey.Shortcut;
        SuppressBox.IsChecked = settings.Hotkey.Suppress;

        DeviceIdBox.Text = settings.Audio.DeviceId;
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
        FallbackBasicBox.IsChecked = settings.Cleanup.FallbackToBasic;

        RestoreClipboardBox.IsChecked = settings.Paste.RestoreClipboard;
        RestoreDelayBox.Text = settings.Paste.RestoreDelayMs.ToString(CultureInfo.InvariantCulture);
        UpdateProviderPanels();
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

        var cleanupProvider = SelectedValue(CleanupProviderBox);
        var isCloudCleanup = cleanupProvider is "azure-openai" or "openai" or "groq";
        CloudCleanupPanel.Visibility = isCloudCleanup
            ? Visibility.Visible
            : Visibility.Collapsed;
        AzureCleanupPanel.Visibility = cleanupProvider == "azure-openai"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = TryBuildSettings();
        if (settings is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var error = await _applySettings(settings);
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
            SetBusy(false);
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private AppSettings? TryBuildSettings()
    {
        var settings = SettingsSnapshot.Clone(_template);
        var errors = new List<string>();

        settings.Enabled = EnabledBox.IsChecked == true;
        settings.LaunchAtLogin = LaunchAtLoginBox.IsChecked == true;
        settings.Hotkey.Shortcut = ShortcutBox.Text.Trim();
        settings.Hotkey.Suppress = SuppressBox.IsChecked == true;

        settings.Audio.DeviceId = DeviceIdBox.Text.Trim();
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
            ShowError(string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal)));
            return null;
        }

        ErrorBorder.Visibility = Visibility.Collapsed;
        return settings;
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
                item.Content?.ToString(),
                value,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectedValue(WpfControls.ComboBox comboBox)
    {
        return (comboBox.SelectedItem as WpfControls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    }

    private void SetBusy(bool busy)
    {
        ApplyButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        ApplyButton.Content = busy ? "Applying…" : "Save & Apply";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private static int ColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
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
