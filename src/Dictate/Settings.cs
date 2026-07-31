using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictate;

public sealed class SettingsPaths
{
    public SettingsPaths(string settingsDirectory, string applicationDirectory)
    {
        SettingsDirectory = Path.GetFullPath(settingsDirectory);
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
        EnvironmentFile = Path.Combine(SettingsDirectory, ".env");
        ReadmePath = Path.Combine(ApplicationDirectory, "README.md");
    }

    public string SettingsDirectory { get; }

    public string SettingsFile { get; }

    public string EnvironmentFile { get; }

    public string ApplicationDirectory { get; }

    public string ReadmePath { get; }

    public static SettingsPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new SettingsPaths(
            Path.Combine(localAppData, "Dictate"),
            AppContext.BaseDirectory);
    }
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    public HotkeySettings Hotkey { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public TranscriptionSettings Transcription { get; set; } = new();

    public CleanupSettings Cleanup { get; set; } = new();

    public PasteSettings Paste { get; set; } = new();

    public bool LaunchAtLogin { get; set; }

    public static AppSettings CreateDefault() => new();
}

public sealed class HotkeySettings
{
    public string Shortcut { get; set; } = "RightCtrl";

    public bool Suppress { get; set; } = true;
}

public sealed class AudioSettings
{
    public string DeviceId { get; set; } = "default";

    public int MaxSeconds { get; set; } = 90;
}

public sealed class TranscriptionSettings
{
    public string Provider { get; set; } = "local";

    public string Language { get; set; } = "en";

    public bool FallbackToLocal { get; set; } = true;

    public string LocalModelPath { get; set; } = "models/ggml-small.en.bin";

    public int LocalThreads { get; set; }

    public string OpenaiModel { get; set; } = "gpt-transcribe";

    public string GroqModel { get; set; } = "whisper-large-v3-turbo";
}

public sealed class CleanupSettings
{
    public string Provider { get; set; } = "basic";

    public string Model { get; set; } = string.Empty;

    public string Style { get; set; } = "auto";

    public bool FallbackToBasic { get; set; } = true;
}

public sealed class PasteSettings
{
    public bool RestoreClipboard { get; set; } = true;

    public int RestoreDelayMs { get; set; } = 175;
}

public sealed class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly SettingsPaths _paths;

    public SettingsLoader(SettingsPaths paths)
    {
        _paths = paths;
    }

    public AppSettings LoadOrCreate()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);

        if (!File.Exists(_paths.SettingsFile))
        {
            var defaults = AppSettings.CreateDefault();
            Save(defaults);
            return defaults;
        }

        AppSettings? settings;
        try
        {
            var json = File.ReadAllText(_paths.SettingsFile);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new SettingsValidationException(
                new[] { $"settings.json is not valid JSON: {exception.Message}" },
                exception);
        }

        if (settings is null)
        {
            throw new SettingsValidationException(new[] { "settings.json does not contain a settings object." });
        }

        SettingsValidator.ThrowIfInvalid(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        SettingsValidator.ThrowIfInvalid(settings);
        Directory.CreateDirectory(_paths.SettingsDirectory);

        var json = JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine;
        var temporaryFile = _paths.SettingsFile + ".tmp";

        try
        {
            File.WriteAllText(temporaryFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryFile, _paths.SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }
}

public static class SettingsValidator
{
    private static readonly HashSet<string> TranscriptionProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "local", "openai", "groq"
    };

    private static readonly HashSet<string> CleanupProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "basic", "openai", "groq", "none"
    };

    private static readonly HashSet<string> CleanupStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "sentence", "fragment"
    };

    public static IReadOnlyList<string> Validate(AppSettings? settings)
    {
        var errors = new List<string>();

        if (settings is null)
        {
            errors.Add("Settings cannot be null.");
            return errors;
        }

        if (settings.Hotkey is null)
        {
            errors.Add("hotkey must be an object.");
        }
        else if (string.IsNullOrWhiteSpace(settings.Hotkey.Shortcut))
        {
            errors.Add("hotkey.shortcut must not be empty.");
        }

        if (settings.Audio is null)
        {
            errors.Add("audio must be an object.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Audio.DeviceId))
            {
                errors.Add("audio.deviceId must not be empty.");
            }

            if (settings.Audio.MaxSeconds is < 1 or > 3600)
            {
                errors.Add("audio.maxSeconds must be between 1 and 3600.");
            }
        }

        if (settings.Transcription is null)
        {
            errors.Add("transcription must be an object.");
        }
        else
        {
            AddAllowedValueError(errors, "transcription.provider", settings.Transcription.Provider, TranscriptionProviders);
            if (string.IsNullOrWhiteSpace(settings.Transcription.Language))
            {
                errors.Add("transcription.language must not be empty.");
            }

            if (settings.Transcription.LocalThreads < 0)
            {
                errors.Add("transcription.localThreads cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(settings.Transcription.LocalModelPath))
            {
                errors.Add("transcription.localModelPath must not be empty.");
            }
        }

        if (settings.Cleanup is null)
        {
            errors.Add("cleanup must be an object.");
        }
        else
        {
            AddAllowedValueError(errors, "cleanup.provider", settings.Cleanup.Provider, CleanupProviders);
            AddAllowedValueError(errors, "cleanup.style", settings.Cleanup.Style, CleanupStyles);
        }

        if (settings.Paste is null)
        {
            errors.Add("paste must be an object.");
        }
        else if (settings.Paste.RestoreDelayMs is < 0 or > 5000)
        {
            errors.Add("paste.restoreDelayMs must be between 0 and 5000.");
        }

        return errors;
    }

    public static void ThrowIfInvalid(AppSettings? settings)
    {
        var errors = Validate(settings);
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }
    }

    private static void AddAllowedValueError(
        ICollection<string> errors,
        string propertyName,
        string? value,
        IReadOnlySet<string> allowedValues)
    {
        if (string.IsNullOrWhiteSpace(value) || !allowedValues.Contains(value))
        {
            errors.Add($"{propertyName} must be one of: {string.Join(", ", allowedValues.Order())}.");
        }
    }
}

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(IEnumerable<string> errors, Exception? innerException = null)
        : base(string.Join(Environment.NewLine, errors), innerException)
    {
        Errors = errors.ToArray();
    }

    public IReadOnlyList<string> Errors { get; }
}
