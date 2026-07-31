using System.Net.Http;

namespace Whispdows;

/// <summary>
/// Configuration helpers for a local Ollama (or other OpenAI-compatible) server.
/// </summary>
internal static class LocalOllamaConfiguration
{
    private const string ChatCompletionsSuffix = "/chat/completions";

    public static bool IsValidEndpoint(string? endpoint)
    {
        return TryNormalizeEndpoint(endpoint, out _);
    }

    public static Uri NormalizeEndpoint(string endpoint)
    {
        if (!TryNormalizeEndpoint(endpoint, out var normalized))
        {
            throw new InvalidOperationException(
                "cleanup.localEndpoint must be an HTTP(S) loopback OpenAI-compatible endpoint.");
        }

        return normalized;
    }

    private static bool TryNormalizeEndpoint(string? endpoint, out Uri normalized)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
        {
            path = "/v1";
        }

        if (!path.EndsWith(ChatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            path += ChatCompletionsSuffix;
        }

        normalized = new UriBuilder(uri) { Path = path }.Uri;
        return true;
    }
}

/// <summary>
/// Sends conservative transcript cleanup requests to a local Ollama server.
/// No API key or authorization header is sent.
/// </summary>
public sealed class OllamaTextCleaner : ITextCleaner, IConfigurationValidator, IProviderComponent, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
    private readonly LlmTextCleaner _inner;

    public OllamaTextCleaner(
        string model,
        string endpoint,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        var provider = ChatCompletionProviderDefinition.ForLocal(
            "Ollama",
            "ollama",
            LocalOllamaConfiguration.NormalizeEndpoint(endpoint));
        _inner = new LlmTextCleaner(
            provider,
            model,
            httpClient,
            timeout ?? DefaultTimeout);
    }

    public string ProviderName => "ollama";

    public void ValidateConfiguration()
    {
        _inner.ValidateConfiguration();
    }

    public async Task<string> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        return await _inner.CleanAsync(transcript, cancellationToken);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }
}
