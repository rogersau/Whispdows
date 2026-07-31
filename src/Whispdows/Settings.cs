using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whispdows;

public sealed class SettingsPaths
{
    public SettingsPaths(string settingsDirectory, string applicationDirectory)
    {
        SettingsDirectory = Path.GetFullPath(settingsDirectory);
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
        SecretsFile = Path.Combine(SettingsDirectory, "secrets.dat");
        EnvironmentFile = Path.Combine(SettingsDirectory, ".env");
        LogDirectory = Path.Combine(SettingsDirectory, "logs");
        ReadmePath = Path.Combine(ApplicationDirectory, "README.md");
    }

    public string SettingsDirectory { get; }

    public string SettingsFile { get; }

    public string SecretsFile { get; }

    public string EnvironmentFile { get; }

    public string LogDirectory { get; }

    public string ApplicationDirectory { get; }

    public string ReadmePath { get; }

    public static SettingsPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new SettingsPaths(
            Path.Combine(localAppData, "Whispdows"),
            AppContext.BaseDirectory);
    }
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    public FeatureSettings Features { get; set; } = new();

    public HotkeySettings Hotkey { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public TranscriptionSettings Transcription { get; set; } = new();

    public CleanupSettings Cleanup { get; set; } = new();

    public PasteSettings Paste { get; set; } = new();

    public MeetingNotesSettings MeetingNotes { get; set; } = new();

    public bool LaunchAtLogin { get; set; }

    public static AppSettings CreateDefault() => new();
}

internal static class SettingsSnapshot
{
    public static AppSettings Clone(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AppSettings
        {
            Enabled = source.Enabled,
            Features = new FeatureSettings
            {
                Transcribe = source.Features.Transcribe,
                MeetingNotes = source.Features.MeetingNotes
            },
            Hotkey = new HotkeySettings
            {
                Shortcut = source.Hotkey.Shortcut,
                Suppress = source.Hotkey.Suppress
            },
            Audio = new AudioSettings
            {
                DeviceId = source.Audio.DeviceId,
                MaxSeconds = source.Audio.MaxSeconds
            },
            Transcription = new TranscriptionSettings
            {
                Provider = source.Transcription.Provider,
                Language = source.Transcription.Language,
                FallbackToLocal = source.Transcription.FallbackToLocal,
                LocalModelPath = source.Transcription.LocalModelPath,
                LocalThreads = source.Transcription.LocalThreads,
                OpenaiModel = source.Transcription.OpenaiModel,
                GroqModel = source.Transcription.GroqModel,
                AzureRegion = source.Transcription.AzureRegion,
                AzureLocale = source.Transcription.AzureLocale
            },
            Cleanup = new CleanupSettings
            {
                Provider = source.Cleanup.Provider,
                Model = source.Cleanup.Model,
                AzureEndpoint = source.Cleanup.AzureEndpoint,
                Style = source.Cleanup.Style,
                FallbackToBasic = source.Cleanup.FallbackToBasic
            },
            Paste = new PasteSettings
            {
                RestoreClipboard = source.Paste.RestoreClipboard,
                RestoreDelayMs = source.Paste.RestoreDelayMs
            },
            MeetingNotes = new MeetingNotesSettings
            {
                OutputDirectory = source.MeetingNotes.OutputDirectory,
                TranscriptionProvider = source.MeetingNotes.TranscriptionProvider,
                Language = source.MeetingNotes.Language,
                LocalModelPath = source.MeetingNotes.LocalModelPath,
                LocalThreads = source.MeetingNotes.LocalThreads,
                OpenaiTranscriptionModel = source.MeetingNotes.OpenaiTranscriptionModel,
                GroqTranscriptionModel = source.MeetingNotes.GroqTranscriptionModel,
                NotesProvider = source.MeetingNotes.NotesProvider,
                OpenaiNotesModel = source.MeetingNotes.OpenaiNotesModel,
                GroqNotesModel = source.MeetingNotes.GroqNotesModel,
                OllamaEndpoint = source.MeetingNotes.OllamaEndpoint,
                OllamaModel = source.MeetingNotes.OllamaModel
            },
            LaunchAtLogin = source.LaunchAtLogin
        };
    }
}

public sealed class FeatureSettings
{
    public bool Transcribe { get; set; } = true;

    public bool MeetingNotes { get; set; } = true;
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

    public string OpenaiModel { get; set; } = "gpt-4o-transcribe";

    public string GroqModel { get; set; } = "whisper-large-v3-turbo";

    public string AzureRegion { get; set; } = string.Empty;

    public string AzureLocale { get; set; } = "en-US";
}

public sealed class CleanupSettings
{
    public string Provider { get; set; } = "basic";

    public string Model { get; set; } = string.Empty;

    public string AzureEndpoint { get; set; } = string.Empty;

    public string Style { get; set; } = "auto";

    public bool FallbackToBasic { get; set; } = true;
}

public sealed class PasteSettings
{
    public bool RestoreClipboard { get; set; } = true;

    public int RestoreDelayMs { get; set; } = 175;
}

public sealed class MeetingNotesSettings
{
    public string OutputDirectory { get; set; } = "~/MeetingNotes";

    public string TranscriptionProvider { get; set; } = "auto";

    public string Language { get; set; } = "en";

    public string LocalModelPath { get; set; } = "models/ggml-medium.en.bin";

    public int LocalThreads { get; set; }

    public string OpenaiTranscriptionModel { get; set; } = "gpt-4o-transcribe";

    public string GroqTranscriptionModel { get; set; } = "whisper-large-v3-turbo";

    public string NotesProvider { get; set; } = "auto";

    public string OpenaiNotesModel { get; set; } = "gpt-4.1-mini";

    public string GroqNotesModel { get; set; } = "openai/gpt-oss-120b";

    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";

    public string OllamaModel { get; set; } = "llama3.2:3b";
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
        "local", "openai", "groq", "azure"
    };

    private static readonly HashSet<string> CleanupProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "basic", "openai", "groq", "azure-openai", "none"
    };

    private static readonly HashSet<string> CleanupStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "sentence", "fragment"
    };

    private static readonly HashSet<string> MeetingTranscriptionProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "local", "openai", "groq"
    };

    private static readonly HashSet<string> MeetingNotesProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "openai", "groq", "ollama"
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
        else if (!HotkeyParser.TryParse(settings.Hotkey.Shortcut, out _, out var hotkeyError))
        {
            errors.Add(hotkeyError!);
        }

        if (settings.Features is null)
        {
            errors.Add("features must be an object.");
        }
        else if (!settings.Features.Transcribe && !settings.Features.MeetingNotes)
        {
            errors.Add("At least one of features.transcribe or features.meetingNotes must be enabled.");
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

            if (string.Equals(settings.Transcription.Provider, "openai", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(settings.Transcription.OpenaiModel))
            {
                errors.Add("transcription.openaiModel must not be empty when using OpenAI.");
            }

            if (string.Equals(settings.Transcription.Provider, "groq", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(settings.Transcription.GroqModel))
            {
                errors.Add("transcription.groqModel must not be empty when using Groq.");
            }

            if (string.Equals(settings.Transcription.Provider, "azure", StringComparison.OrdinalIgnoreCase))
            {
                if (!AzureSpeechConfiguration.IsValidRegion(
                    settings.Transcription.AzureRegion))
                {
                    errors.Add("transcription.azureRegion must be an Azure region identifier such as australiaeast.");
                }

                if (!AzureSpeechConfiguration.IsValidLocale(
                    settings.Transcription.AzureLocale))
                {
                    errors.Add("transcription.azureLocale must be a locale such as en-AU.");
                }
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
            var isAzureOpenAi = string.Equals(
                settings.Cleanup.Provider,
                "azure-openai",
                StringComparison.OrdinalIgnoreCase);
            if ((string.Equals(settings.Cleanup.Provider, "openai", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(settings.Cleanup.Provider, "groq", StringComparison.OrdinalIgnoreCase)
                    || isAzureOpenAi)
                && string.IsNullOrWhiteSpace(settings.Cleanup.Model))
            {
                errors.Add("cleanup.model must not be empty when using a cloud cleanup provider.");
            }

            if (isAzureOpenAi
                && !AzureOpenAiConfiguration.IsValidEndpoint(settings.Cleanup.AzureEndpoint))
            {
                errors.Add(
                    "cleanup.azureEndpoint must be an HTTPS Azure OpenAI v1 endpoint ending in /openai/v1.");
            }
        }

        if (settings.Paste is null)
        {
            errors.Add("paste must be an object.");
        }
        else if (settings.Paste.RestoreDelayMs is < 0 or > 5000)
        {
            errors.Add("paste.restoreDelayMs must be between 0 and 5000.");
        }

        if (settings.MeetingNotes is null)
        {
            errors.Add("meetingNotes must be an object.");
        }
        else
        {
            AddAllowedValueError(
                errors,
                "meetingNotes.transcriptionProvider",
                settings.MeetingNotes.TranscriptionProvider,
                MeetingTranscriptionProviders);
            AddAllowedValueError(
                errors,
                "meetingNotes.notesProvider",
                settings.MeetingNotes.NotesProvider,
                MeetingNotesProviders);

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.OutputDirectory))
            {
                errors.Add("meetingNotes.outputDirectory must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.Language))
            {
                errors.Add("meetingNotes.language must not be empty.");
            }

            if (settings.MeetingNotes.LocalThreads < 0)
            {
                errors.Add("meetingNotes.localThreads cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.LocalModelPath))
            {
                errors.Add("meetingNotes.localModelPath must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.OpenaiTranscriptionModel))
            {
                errors.Add("meetingNotes.openaiTranscriptionModel must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.GroqTranscriptionModel))
            {
                errors.Add("meetingNotes.groqTranscriptionModel must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.OpenaiNotesModel))
            {
                errors.Add("meetingNotes.openaiNotesModel must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.GroqNotesModel))
            {
                errors.Add("meetingNotes.groqNotesModel must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(settings.MeetingNotes.OllamaModel))
            {
                errors.Add("meetingNotes.ollamaModel must not be empty.");
            }

            if (!IsLoopbackHttpEndpoint(settings.MeetingNotes.OllamaEndpoint))
            {
                errors.Add(
                    "meetingNotes.ollamaEndpoint must be an HTTP or HTTPS loopback endpoint.");
            }
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

    private static bool IsLoopbackHttpEndpoint(string? endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.IsLoopback
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
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
