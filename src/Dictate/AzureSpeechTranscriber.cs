using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dictate;

public sealed class AzureSpeechTranscriber : ITranscriber, IProviderComponent
{
    private const string ApiVersion = "2025-10-15";
    private const string ProviderDisplayName = "Azure Speech";
    private const string SubscriptionKeyHeader = "Ocp-Apim-Subscription-Key";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly string _apiKey;
    private readonly string _region;
    private readonly string _locale;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public AzureSpeechTranscriber(
        string apiKey,
        string region,
        string locale,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _apiKey = apiKey;
        _region = region;
        _locale = locale;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string ProviderName => "azure";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new MissingApiKeyException(
                ProviderDisplayName,
                "AZURE_SPEECH_KEY");
        }

        if (_apiKey.Any(character => character is '\r' or '\n'))
        {
            throw new InvalidOperationException(
                "AZURE_SPEECH_KEY contains invalid control characters.");
        }

        AzureSpeechConfiguration.Validate(_region, _locale);
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);
        ValidateConfiguration();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            var audioBytes = await ReadAudioAsync(wavAudio, timeout.Token);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                CreateEndpoint(_region));
            request.Headers.Add(
                SubscriptionKeyHeader,
                _apiKey);

            using var form = new MultipartFormDataContent();
            using var audio = new ByteArrayContent(audioBytes);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "audio", "dictation.wav");

            var definitionJson = JsonSerializer.Serialize(new
            {
                locales = new[] { _locale.Trim() }
            });
            using var definition = new StringContent(
                definitionJson,
                Encoding.UTF8,
                "application/json");
            form.Add(definition, "definition");
            request.Content = form;

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw CloudRequestException.ApiError(
                    ProviderDisplayName,
                    response.StatusCode);
            }

            return await ReadTranscriptionAsync(response, timeout.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CloudRequestException.Timeout(
                ProviderDisplayName,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw CloudRequestException.Transport(
                ProviderDisplayName,
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

    private static Uri CreateEndpoint(string region)
    {
        var normalizedRegion = region.Trim().ToLowerInvariant();
        return new Uri(
            $"https://{normalizedRegion}.api.cognitive.microsoft.com/" +
            $"speechtotext/transcriptions:transcribe?api-version={ApiVersion}");
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

    private static async Task<string> ReadTranscriptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(
                    "combinedPhrases",
                    out var combinedPhrases)
                || combinedPhrases.ValueKind != JsonValueKind.Array)
            {
                throw CloudRequestException.Malformed(ProviderDisplayName);
            }

            var text = combinedPhrases
                .EnumerateArray()
                .Select(phrase =>
                    phrase.ValueKind == JsonValueKind.Object
                    && phrase.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString()?.Trim()
                        : null)
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return string.Join(Environment.NewLine, text);
        }
        catch (JsonException exception)
        {
            throw CloudRequestException.Malformed(
                ProviderDisplayName,
                exception);
        }
    }
}

internal static class AzureSpeechConfiguration
{
    public static bool IsValidRegion(string? region)
    {
        return IsValidIdentifier(region, maximumLength: 64);
    }

    public static bool IsValidLocale(string? locale)
    {
        return IsValidIdentifier(locale, maximumLength: 32);
    }

    public static void Validate(string region, string locale)
    {
        if (!IsValidRegion(region))
        {
            throw new InvalidOperationException(
                "transcription.azureRegion must be an Azure region identifier such as australiaeast.");
        }

        if (!IsValidLocale(locale))
        {
            throw new InvalidOperationException(
                "transcription.azureLocale must be a locale such as en-AU.");
        }
    }

    private static bool IsValidIdentifier(
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            && trimmed[0] != '-'
            && trimmed[^1] != '-'
            && trimmed.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-');
    }
}
