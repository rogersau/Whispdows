using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Dictate;

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

public sealed class OpenAiCompatibleTranscriber : ITranscriber
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

public sealed class FallbackTranscriber : ITranscriber
{
    private readonly ITranscriber _primary;
    private readonly ITranscriber _fallback;
    private bool _disposed;

    public FallbackTranscriber(ITranscriber primary, ITranscriber fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fallback.ValidateConfiguration();
        try
        {
            _primary.ValidateConfiguration();
        }
        catch (MissingApiKeyException)
        {
            // An explicitly configured local fallback can operate without the cloud key.
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
            catch (CloudProviderException) when (!cancellationToken.IsCancellationRequested)
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
        catch (CloudProviderException) when (!cancellationToken.IsCancellationRequested)
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
}

public sealed class LlmTextCleaner : ITextCleaner, IConfigurationValidator, IDisposable
{
    private const string SystemPrompt = """
        You clean voice dictation transcripts.

        Return only the corrected text.
        - Remove filler words and abandoned false starts only when meaning is unchanged.
        - Fix punctuation, spacing, and obvious transcription mistakes.
        - Preserve the speaker's wording, intent, names, numbers, URLs, and technical terms.
        - Do not summarise, answer, explain, or add information.
        - Match casing to the apparent dictation style. Use normal sentence case for prose, preserve intentional capitals, and keep short casual fragments natural.
        """;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly CloudProviderDefinition _provider;
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
    {
        _provider = provider;
        _model = model;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
    }

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloudValidation.Validate(_provider, _model);
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
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = transcript }
            },
            temperature = 0,
            max_completion_tokens = CalculateOutputLimit(transcript)
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _provider.ChatCompletionsEndpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _provider.ApiKey);
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
            throw CloudRequestException.ApiError(_provider.Name, statusCode);
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
                throw CloudRequestException.Malformed(_provider.Name);
            }

            return contentElement.GetString()!.Trim();
        }
        catch (JsonException exception)
        {
            throw CloudRequestException.Malformed(_provider.Name, exception);
        }
    }
}

public sealed class FallbackTextCleaner : ITextCleaner, IConfigurationValidator, IDisposable
{
    private readonly ITextCleaner _primary;
    private readonly ITextCleaner _fallback;
    private bool _disposed;

    public FallbackTextCleaner(ITextCleaner primary, ITextCleaner fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_primary is IConfigurationValidator primaryValidator)
        {
            try
            {
                primaryValidator.ValidateConfiguration();
            }
            catch (MissingApiKeyException)
            {
                // An explicitly configured basic fallback can operate without the cloud key.
            }
        }

        if (_fallback is IConfigurationValidator fallbackValidator)
        {
            fallbackValidator.ValidateConfiguration();
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
        catch (CloudProviderException) when (!cancellationToken.IsCancellationRequested)
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
