using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Whispdows;

public sealed record MeetingActionItem(string Owner, string Task);

public sealed class MeetingNotesContent
{
    public MeetingNotesContent(
        IEnumerable<string> summary,
        IEnumerable<string> decisions,
        IEnumerable<MeetingActionItem> actionItems)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(actionItems);

        Summary = summary.Select(NormalizeRequired).ToArray();
        Decisions = decisions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        ActionItems = actionItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Task))
            .Select(item => new MeetingActionItem(
                string.IsNullOrWhiteSpace(item.Owner)
                    ? "Unassigned"
                    : item.Owner.Trim(),
                item.Task.Trim()))
            .ToArray();

        if (Summary.Count != 5)
        {
            throw new MeetingNotesGenerationException(
                "The notes model must return exactly five summary bullets.");
        }
    }

    public IReadOnlyList<string> Summary { get; }

    public IReadOnlyList<string> Decisions { get; }

    public IReadOnlyList<MeetingActionItem> ActionItems { get; }

    private static string NormalizeRequired(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MeetingNotesGenerationException(
                "The notes model returned an empty summary bullet.");
        }

        return value.Trim();
    }
}

public interface IMeetingNotesGenerator :
    IConfigurationValidator,
    IProviderComponent,
    IDisposable
{
    Task<MeetingNotesContent> GenerateAsync(
        string transcript,
        CancellationToken cancellationToken);
}

public static class MeetingNotesPrompt
{
    public const string System =
        """
        You turn a meeting transcript into concise, factual notes.
        Return only a JSON object with this exact shape:
        {
          "summary": ["bullet 1", "bullet 2", "bullet 3", "bullet 4", "bullet 5"],
          "decisions": ["decision"],
          "actionItems": [{"owner": "name or Unassigned", "task": "task"}]
        }
        The summary must contain exactly five non-empty bullets.
        Include only decisions actually made. Use an empty array when there were none.
        Include only concrete action items. Preserve explicit owners and use
        "Unassigned" when the transcript does not name one. Do not invent facts,
        owners, decisions, or commitments.
        """;

    public static string BuildUserMessage(string transcript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        return "Meeting transcript:\n\n" + transcript.Trim();
    }
}

public sealed class OpenAiCompatibleMeetingNotesGenerator
    : IMeetingNotesGenerator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly CloudProviderDefinition _provider;
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public OpenAiCompatibleMeetingNotesGenerator(
        CloudProviderDefinition provider,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model;
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

    public async Task<MeetingNotesContent> GenerateAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ValidateConfiguration();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _provider.ChatCompletionsEndpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _provider.ApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = _model,
                    temperature = 0.1,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = MeetingNotesPrompt.System },
                        new
                        {
                            role = "user",
                            content = MeetingNotesPrompt.BuildUserMessage(transcript)
                        }
                    }
                }),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw CloudRequestException.ApiError(
                    _provider.Name,
                    response.StatusCode);
            }

            await using var content = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: timeout.Token);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var generated)
                || generated.ValueKind != JsonValueKind.String)
            {
                throw CloudRequestException.Malformed(_provider.Name);
            }

            try
            {
                return MeetingNotesResponseParser.Parse(
                    generated.GetString() ?? string.Empty);
            }
            catch (MeetingNotesGenerationException exception)
            {
                throw CloudRequestException.Malformed(_provider.Name, exception);
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw CloudRequestException.Timeout(_provider.Name, exception);
        }
        catch (JsonException exception)
        {
            throw CloudRequestException.Malformed(_provider.Name, exception);
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
}

public sealed class OllamaMeetingNotesGenerator : IMeetingNotesGenerator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public OllamaMeetingNotesGenerator(
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _endpoint = CreateChatEndpoint(endpoint);
        _model = model;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string ProviderName => "ollama";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new MeetingNotesGenerationException(
                "An Ollama model must be configured for meeting notes.");
        }
    }

    public async Task<MeetingNotesContent> GenerateAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ValidateConfiguration();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = _model,
                        stream = false,
                        format = "json",
                        options = new { temperature = 0.1 },
                        messages = new object[]
                        {
                            new
                            {
                                role = "system",
                                content = MeetingNotesPrompt.System
                            },
                            new
                            {
                                role = "user",
                                content = MeetingNotesPrompt.BuildUserMessage(transcript)
                            }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new MeetingNotesGenerationException(
                    $"The local Ollama model returned HTTP {(int)response.StatusCode}.");
            }

            await using var content = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var generated)
                || generated.ValueKind != JsonValueKind.String)
            {
                throw new MeetingNotesGenerationException(
                    "The local Ollama model returned a malformed response.");
            }

            return MeetingNotesResponseParser.Parse(
                generated.GetString() ?? string.Empty);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new MeetingNotesGenerationException(
                "The local Ollama model timed out.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new MeetingNotesGenerationException(
                "The local Ollama model returned malformed JSON.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MeetingNotesGenerationException(
                "The local Ollama model is unavailable.",
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

    private static Uri CreateChatEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp
                && baseUri.Scheme != Uri.UriSchemeHttps)
            || !baseUri.IsLoopback)
        {
            throw new MeetingNotesGenerationException(
                "The Ollama endpoint must be an HTTP or HTTPS loopback address.");
        }

        return new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/api/chat");
    }
}

public sealed class FallbackMeetingNotesGenerator : IMeetingNotesGenerator
{
    private readonly IMeetingNotesGenerator _primary;
    private readonly IMeetingNotesGenerator _fallback;
    private bool _disposed;

    public FallbackMeetingNotesGenerator(
        IMeetingNotesGenerator primary,
        IMeetingNotesGenerator fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public string ProviderName => _primary.ProviderName;

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _primary.ValidateConfiguration();
        }
        catch (MissingApiKeyException)
        {
            // The configured local model remains usable without a cloud key.
        }

        _fallback.ValidateConfiguration();
    }

    public async Task<MeetingNotesContent> GenerateAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await _primary.GenerateAsync(transcript, cancellationToken);
        }
        catch (Exception exception)
            when (!cancellationToken.IsCancellationRequested
                && exception is CloudProviderException
                    or MeetingNotesGenerationException)
        {
            return await _fallback.GenerateAsync(transcript, cancellationToken);
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

public static class MeetingNotesResponseParser
{
    public static MeetingNotesContent Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var normalized = StripCodeFence(json.Trim());

        try
        {
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            var summary = ReadStringArray(root, "summary", required: true);
            var decisions = ReadStringArray(root, "decisions", required: false);
            var actions = ReadActionItems(root);
            return new MeetingNotesContent(summary, decisions, actions);
        }
        catch (JsonException exception)
        {
            throw new MeetingNotesGenerationException(
                "The notes model returned malformed JSON.",
                exception);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string name,
        bool required)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            if (!required)
            {
                return [];
            }

            throw new MeetingNotesGenerationException(
                $"The notes model did not return '{name}'.");
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new MeetingNotesGenerationException(
                $"The notes model returned invalid '{name}'.");
        }

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new MeetingNotesGenerationException(
                    $"The notes model returned invalid '{name}'.");
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static IReadOnlyList<MeetingActionItem> ReadActionItems(
        JsonElement root)
    {
        if (!root.TryGetProperty("actionItems", out var property))
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new MeetingNotesGenerationException(
                "The notes model returned invalid 'actionItems'.");
        }

        var actions = new List<MeetingActionItem>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("task", out var task)
                || task.ValueKind != JsonValueKind.String)
            {
                throw new MeetingNotesGenerationException(
                    "The notes model returned an invalid action item.");
            }

            var owner = item.TryGetProperty("owner", out var ownerElement)
                && ownerElement.ValueKind == JsonValueKind.String
                ? ownerElement.GetString() ?? string.Empty
                : "Unassigned";
            actions.Add(new MeetingActionItem(
                owner,
                task.GetString() ?? string.Empty));
        }

        return actions;
    }

    private static string StripCodeFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var firstNewLine = value.IndexOf('\n');
        var closingFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && closingFence > firstNewLine
            ? value[(firstNewLine + 1)..closingFence].Trim()
            : value;
    }
}

public sealed class MeetingNotesGenerationException : Exception
{
    public MeetingNotesGenerationException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
