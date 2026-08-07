using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Whispdows;

public interface IConfigurationValidator
{
    void ValidateConfiguration();
}

public sealed class CloudProviderDefinition
{
    private CloudProviderDefinition(
        string name,
        string apiKeyName,
        string apiKey,
        Uri transcriptionEndpoint,
        Uri chatCompletionsEndpoint)
    {
        Name = name;
        ApiKeyName = apiKeyName;
        ApiKey = apiKey;
        TranscriptionEndpoint = transcriptionEndpoint;
        ChatCompletionsEndpoint = chatCompletionsEndpoint;
    }

    public string Name { get; }

    public string ApiKeyName { get; }

    public string ApiKey { get; }

    public Uri TranscriptionEndpoint { get; }

    public Uri ChatCompletionsEndpoint { get; }

    public static CloudProviderDefinition Create(
        string provider,
        ProviderSecrets secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(secrets);

        return provider.ToLowerInvariant() switch
        {
            "openai" => new CloudProviderDefinition(
                "OpenAI",
                "OPENAI_API_KEY",
                secrets.Get("OPENAI_API_KEY"),
                new Uri("https://api.openai.com/v1/audio/transcriptions"),
                new Uri("https://api.openai.com/v1/chat/completions")),
            "groq" => new CloudProviderDefinition(
                "Groq",
                "GROQ_API_KEY",
                secrets.Get("GROQ_API_KEY"),
                new Uri("https://api.groq.com/openai/v1/audio/transcriptions"),
                new Uri("https://api.groq.com/openai/v1/chat/completions")),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unknown cloud provider.")
        };
    }

    public override string ToString() => Name;
}

public sealed class OpenAiCompatibleTranscriber : ITranscriber, IProviderComponent
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly CloudProviderDefinition _provider;
    private readonly string _model;
    private readonly string _language;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public OpenAiCompatibleTranscriber(
        CloudProviderDefinition provider,
        string model,
        string language,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _provider = provider;
        _model = model;
        _language = language;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string ProviderName => _provider.Name.ToLowerInvariant();

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloudValidation.Validate(_provider, _model);
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);
        ValidateConfiguration();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            var audioBytes = await ReadAudioAsync(wavAudio, timeout.Token);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _provider.TranscriptionEndpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _provider.ApiKey);

            using var form = new MultipartFormDataContent();
            using var audio = new ByteArrayContent(audioBytes);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "dictation.wav");
            form.Add(new StringContent(_model), "model");
            form.Add(new StringContent("json"), "response_format");
            if (!string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                form.Add(new StringContent(_language), "language");
            }

            request.Content = form;
            using var response = await SendAsync(request, timeout.Token);
            return await ReadTranscriptionAsync(response, timeout.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CloudRequestException.Timeout(_provider.Name, exception);
        }
        catch (HttpRequestException exception)
        {
            throw CloudRequestException.Transport(_provider.Name, exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static async Task<byte[]> ReadAudioAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        if (wavAudio.CanSeek)
        {
            wavAudio.Position = 0;
        }

        using var buffer = new MemoryStream();
        await wavAudio.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            response.Dispose();
            throw CloudRequestException.ApiError(_provider.Name, statusCode);
        }

        return response;
    }

    private async Task<string> ReadTranscriptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                throw CloudRequestException.Malformed(_provider.Name);
            }

            return (textElement.GetString() ?? string.Empty).Trim();
        }
        catch (JsonException exception)
        {
            throw CloudRequestException.Malformed(_provider.Name, exception);
        }
    }
}

public sealed class FallbackTranscriber : ITranscriber, IProviderComponent
{
    private readonly ITranscriber _primary;
    private readonly ITranscriber _fallback;
    private readonly bool _allowMissingPrimaryConfiguration;
    private readonly bool _allowMissingFallbackConfiguration;
    private bool _disposed;

    public FallbackTranscriber(
        ITranscriber primary,
        ITranscriber fallback,
        bool allowMissingPrimaryConfiguration = true,
        bool allowMissingFallbackConfiguration = false)
    {
        _primary = primary;
        _fallback = fallback;
        _allowMissingPrimaryConfiguration = allowMissingPrimaryConfiguration;
        _allowMissingFallbackConfiguration = allowMissingFallbackConfiguration;
    }

    public string ProviderName =>
        (_primary as IProviderComponent)?.ProviderName ?? "cloud";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_allowMissingPrimaryConfiguration)
        {
            try
            {
                _primary.ValidateConfiguration();
            }
            catch (MissingApiKeyException)
            {
                // An explicitly configured fallback can operate without the primary key.
            }
        }
        else
        {
            _primary.ValidateConfiguration();
        }

        if (_allowMissingFallbackConfiguration)
        {
            try
            {
                _fallback.ValidateConfiguration();
            }
            catch (MissingApiKeyException)
            {
                // A local primary can operate when its optional online fallback is unconfigured.
            }
        }
        else
        {
            _fallback.ValidateConfiguration();
        }
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);

        if (wavAudio.CanSeek)
        {
            wavAudio.Position = 0;
            try
            {
                return await _primary.TranscribeAsync(wavAudio, cancellationToken);
            }
            catch (Exception exception)
                when (IsFallbackFailure(exception, cancellationToken))
            {
                wavAudio.Position = 0;
                return await _fallback.TranscribeAsync(wavAudio, cancellationToken);
            }
        }

        using var audioBuffer = new MemoryStream();
        await wavAudio.CopyToAsync(audioBuffer, cancellationToken);
        var audioBytes = audioBuffer.ToArray();
        try
        {
            using var primaryAudio = new MemoryStream(audioBytes, writable: false);
            return await _primary.TranscribeAsync(primaryAudio, cancellationToken);
        }
        catch (Exception exception)
            when (IsFallbackFailure(exception, cancellationToken))
        {
            using var fallbackAudio = new MemoryStream(audioBytes, writable: false);
            return await _fallback.TranscribeAsync(fallbackAudio, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _primary.Dispose();
        _fallback.Dispose();
    }

    private static bool IsFallbackFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && exception is CloudProviderException
                or WindowsMlUnavailableException
                or LocalModelNotFoundException;
    }
}

internal static class TextCleanupPrompt
{
    internal const string System = """
        You clean voice dictation transcripts.

        Return only the corrected text.
        - Remove filler words and abandoned false starts only when meaning is unchanged.
        - Fix punctuation, spacing, and obvious transcription mistakes.
        - Preserve the speaker's wording, intent, names, numbers, URLs, and technical terms.
        - When the speaker clearly corrects, retracts, or replaces something, keep the correction and remove the superseded wording only when the intended change is clear.
        - Do not summarise, answer, explain, or add information.
        - Match casing to the apparent dictation style. Use normal sentence case for prose, preserve intentional capitals, and keep short casual fragments natural.
        """;
}

internal sealed record ChatCompletionProviderDefinition(
    string DisplayName,
    string ProviderName,
    Uri Endpoint,
    string ApiKeyName,
    string ApiKey,
    bool UseMaxTokens)
{
    public static ChatCompletionProviderDefinition FromCloud(
        CloudProviderDefinition provider)
    {
        return new ChatCompletionProviderDefinition(
            provider.Name,
            provider.Name.ToLowerInvariant(),
            provider.ChatCompletionsEndpoint,
            provider.ApiKeyName,
            provider.ApiKey,
            UseMaxTokens: false);
    }

    public static ChatCompletionProviderDefinition ForLocal(
        string displayName,
        string providerName,
        Uri endpoint)
    {
        return new ChatCompletionProviderDefinition(
            displayName,
            providerName,
            endpoint,
            string.Empty,
            string.Empty,
            UseMaxTokens: true);
    }
}

public sealed class LlmTextCleaner : ITextCleaner, IConfigurationValidator, IProviderComponent, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly ChatCompletionProviderDefinition _provider;
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public LlmTextCleaner(
        CloudProviderDefinition provider,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
        : this(
            ChatCompletionProviderDefinition.FromCloud(provider),
            model,
            httpClient,
            timeout)
    {
    }

    internal LlmTextCleaner(
        ChatCompletionProviderDefinition provider,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _provider = provider;
        _model = model;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string ProviderName => _provider.ProviderName;

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.IsNullOrWhiteSpace(_provider.ApiKeyName)
            && string.IsNullOrWhiteSpace(_provider.ApiKey))
        {
            throw new MissingApiKeyException(
                _provider.DisplayName,
                _provider.ApiKeyName);
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new InvalidOperationException(
                $"A model must be configured for the {_provider.DisplayName} provider.");
        }
    }

    public async Task<string> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transcript);
        ValidateConfiguration();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = new[]
            {
                new { role = "system", content = TextCleanupPrompt.System },
                new { role = "user", content = transcript }
            },
            ["temperature"] = 0,
            [_provider.UseMaxTokens ? "max_tokens" : "max_completion_tokens"] =
                CalculateOutputLimit(transcript),
            ["stream"] = false
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _provider.Endpoint);
            if (!string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _provider.ApiKey);
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var response = await SendAsync(request, timeout.Token);
            return await ReadCleanedTextAsync(response, timeout.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CloudRequestException.Timeout(
                _provider.DisplayName,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw CloudRequestException.Transport(
                _provider.DisplayName,
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static int CalculateOutputLimit(string transcript)
    {
        return Math.Clamp((transcript.Length / 3) + 64, 64, 2048);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            response.Dispose();
            throw CloudRequestException.ApiError(
                _provider.DisplayName,
                statusCode);
        }

        return response;
    }

    private async Task<string> ReadCleanedTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(contentElement.GetString()))
            {
                throw CloudRequestException.Malformed(_provider.DisplayName);
            }

            return contentElement.GetString()!.Trim();
        }
        catch (JsonException exception)
        {
            throw CloudRequestException.Malformed(
                _provider.DisplayName,
                exception);
        }
    }
}

public sealed class AzureOpenAiTextCleaner : ITextCleaner, IConfigurationValidator, IProviderComponent, IDisposable
{
    private const string ProviderDisplayName = "Azure OpenAI";
    private const string ApiKeyName = "AZURE_SPEECH_KEY";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public AzureOpenAiTextCleaner(
        string apiKey,
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _apiKey = apiKey;
        _endpoint = endpoint;
        _model = model;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string ProviderName => "azure-openai";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new MissingApiKeyException(ProviderDisplayName, ApiKeyName);
        }

        if (_apiKey.Any(character => character is '\r' or '\n'))
        {
            throw new InvalidOperationException(
                $"{ApiKeyName} contains invalid control characters.");
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new InvalidOperationException(
                $"A model must be configured for the {ProviderDisplayName} provider.");
        }

        _ = AzureOpenAiConfiguration.CreateResponsesEndpoint(_endpoint);
    }

    public async Task<string> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transcript);
        ValidateConfiguration();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var requestBody = new
        {
            model = _model,
            instructions = TextCleanupPrompt.System,
            input = transcript,
            store = false,
            max_output_tokens = CalculateOutputLimit(transcript)
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                AzureOpenAiConfiguration.CreateResponsesEndpoint(_endpoint));
            request.Headers.Add("api-key", _apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var response = await SendAsync(request, timeout.Token);
            return await ReadCleanedTextAsync(response, timeout.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CloudRequestException.Timeout(ProviderDisplayName, exception);
        }
        catch (HttpRequestException exception)
        {
            throw CloudRequestException.Transport(ProviderDisplayName, exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static int CalculateOutputLimit(string transcript)
    {
        return Math.Clamp((transcript.Length / 3) + 64, 64, 2048);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            response.Dispose();
            throw CloudRequestException.ApiError(ProviderDisplayName, statusCode);
        }

        return response;
    }

    private static async Task<string> ReadCleanedTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("output_text", out var outputText)
                && outputText.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(outputText.GetString()))
            {
                return outputText.GetString()!.Trim();
            }

            if (!root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
            {
                throw CloudRequestException.Malformed(ProviderDisplayName);
            }

            var textParts = new List<string>();
            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "message", StringComparison.Ordinal)
                    || !outputItem.TryGetProperty("content", out var contentItems)
                    || contentItems.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in contentItems.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("type", out var contentType)
                        && string.Equals(
                            contentType.GetString(),
                            "output_text",
                            StringComparison.Ordinal)
                        && contentItem.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(text.GetString()))
                    {
                        textParts.Add(text.GetString()!.Trim());
                    }
                }
            }

            if (textParts.Count == 0)
            {
                throw CloudRequestException.Malformed(ProviderDisplayName);
            }

            return string.Join(Environment.NewLine, textParts).Trim();
        }
        catch (JsonException exception)
        {
            throw CloudRequestException.Malformed(ProviderDisplayName, exception);
        }
    }
}

internal static class AzureOpenAiConfiguration
{
    private const string RequiredPath = "/openai/v1";

    public static bool IsValidEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath.TrimEnd('/').EndsWith(
                RequiredPath,
                StringComparison.OrdinalIgnoreCase);
    }

    public static Uri CreateResponsesEndpoint(string endpoint)
    {
        if (!IsValidEndpoint(endpoint))
        {
            throw new InvalidOperationException(
                "cleanup.azureEndpoint must be an HTTPS Azure OpenAI v1 endpoint ending in /openai/v1.");
        }

        return new Uri(
            endpoint.Trim().TrimEnd('/') + "/responses",
            UriKind.Absolute);
    }
}

public sealed class FallbackTextCleaner : ITextCleaner, IConfigurationValidator, IProviderComponent, IDisposable
{
    private readonly ITextCleaner _primary;
    private readonly ITextCleaner _fallback;
    private readonly bool _allowMissingPrimaryConfiguration;
    private readonly bool _allowMissingFallbackConfiguration;
    private bool _disposed;

    public FallbackTextCleaner(
        ITextCleaner primary,
        ITextCleaner fallback,
        bool allowMissingPrimaryConfiguration = true,
        bool allowMissingFallbackConfiguration = false)
    {
        _primary = primary;
        _fallback = fallback;
        _allowMissingPrimaryConfiguration = allowMissingPrimaryConfiguration;
        _allowMissingFallbackConfiguration = allowMissingFallbackConfiguration;
    }

    public string ProviderName =>
        (_primary as IProviderComponent)?.ProviderName ?? "cloud";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_primary is IConfigurationValidator primaryValidator)
        {
            if (_allowMissingPrimaryConfiguration)
            {
                try
                {
                    primaryValidator.ValidateConfiguration();
                }
                catch (MissingApiKeyException)
                {
                    // An explicitly configured fallback can operate without the primary key.
                }
            }
            else
            {
                primaryValidator.ValidateConfiguration();
            }
        }

        if (_fallback is IConfigurationValidator fallbackValidator)
        {
            if (_allowMissingFallbackConfiguration)
            {
                try
                {
                    fallbackValidator.ValidateConfiguration();
                }
                catch (MissingApiKeyException)
                {
                    // A local primary can operate when its optional online fallback is unconfigured.
                }
            }
            else
            {
                fallbackValidator.ValidateConfiguration();
            }
        }
    }

    public async Task<string> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return await _primary.CleanAsync(transcript, cancellationToken);
        }
        catch (Exception exception)
            when (IsFallbackFailure(exception, cancellationToken))
        {
            return await _fallback.CleanAsync(transcript, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_primary is IDisposable primaryDisposable)
        {
            primaryDisposable.Dispose();
        }

        if (_fallback is IDisposable fallbackDisposable)
        {
            fallbackDisposable.Dispose();
        }
    }

    private static bool IsFallbackFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && exception is CloudProviderException
                or WindowsMlUnavailableException
                or LocalModelNotFoundException;
    }
}

public abstract class CloudProviderException : Exception
{
    protected CloudProviderException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class MissingApiKeyException : CloudProviderException
{
    public MissingApiKeyException(string provider, string apiKeyName)
        : base($"{apiKeyName} is required when the {provider} provider is selected.")
    {
        Provider = provider;
        ApiKeyName = apiKeyName;
    }

    public string Provider { get; }

    public string ApiKeyName { get; }
}

public sealed class CloudRequestException : CloudProviderException
{
    private CloudRequestException(
        string provider,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        StatusCode = statusCode;
    }

    public string Provider { get; }

    public HttpStatusCode? StatusCode { get; }

    public static CloudRequestException ApiError(
        string provider,
        HttpStatusCode statusCode)
    {
        return new CloudRequestException(
            provider,
            $"{provider} returned HTTP {(int)statusCode}.",
            statusCode);
    }

    public static CloudRequestException Timeout(
        string provider,
        Exception innerException)
    {
        return new CloudRequestException(
            provider,
            $"{provider} timed out.",
            innerException: innerException);
    }

    public static CloudRequestException Transport(
        string provider,
        Exception innerException)
    {
        return new CloudRequestException(
            provider,
            $"{provider} could not be reached.",
            innerException: innerException);
    }

    public static CloudRequestException Malformed(
        string provider,
        Exception? innerException = null)
    {
        return new CloudRequestException(
            provider,
            $"{provider} returned a malformed response.",
            innerException: innerException);
    }
}

internal static class CloudValidation
{
    public static void Validate(CloudProviderDefinition provider, string model)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new MissingApiKeyException(provider.Name, provider.ApiKeyName);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                $"A model must be configured for the {provider.Name} provider.");
        }
    }
}
