using System.Net;
using System.Text;
using System.Text.Json;
using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class OllamaTextCleanerTests
{
    [Fact]
    public async Task Sends_openai_chat_request_without_auth_and_normalizes_endpoint()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "{\"choices\":[{\"message\":{\"content\":\" Cleaned text. \"}}]}"));
        using var client = new HttpClient(handler);
        using var cleaner = new OllamaTextCleaner("qwen2.5:1.5b", "http://localhost:11434/v1/", client);

        var result = await cleaner.CleanAsync("um raw transcript", CancellationToken.None);

        Assert.Equal("Cleaned text.", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("qwen2.5:1.5b", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.InRange(root.GetProperty("max_tokens").GetInt32(), 64, 2048);
        Assert.Equal("um raw transcript", root.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:1234/openai/v1", "http://localhost:1234/openai/v1/chat/completions")]
    public void Normalizes_common_openai_compatible_base_endpoints(
        string configured,
        string expected)
    {
        Assert.Equal(
            expected,
            LocalOllamaConfiguration.NormalizeEndpoint(configured).AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/v1")]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("http://127.0.0.1:11434/v1?token=bad")]
    public void Rejects_non_loopback_or_malformed_endpoints(string endpoint)
    {
        Assert.False(LocalOllamaConfiguration.IsValidEndpoint(endpoint));
        Assert.Throws<InvalidOperationException>(
            () => new OllamaTextCleaner("gemma3:1b", endpoint));
    }

    [Fact]
    public async Task Malformed_response_maps_to_provider_exception_and_fallback()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"choices\":[]}"));
        using var client = new HttpClient(handler);
        using var primary = new OllamaTextCleaner("gemma3:1b", "http://127.0.0.1:11434/v1", client);
        using var cleaner = new FallbackTextCleaner(primary, new BasicTextCleaner("sentence"));

        var result = await cleaner.CleanAsync("um hello there", CancellationToken.None);

        Assert.Equal("Hello there.", result);
        Assert.Equal("ollama", cleaner.ProviderName);
    }

    [Fact]
    public async Task Transport_failure_falls_back_to_basic_cleanup()
    {
        var handler = new RecordingHandler(
            _ => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Ollama is not running.")));
        using var client = new HttpClient(handler);
        using var primary = new OllamaTextCleaner(
            "gemma3:1b",
            "http://127.0.0.1:11434/v1",
            client);
        using var cleaner = new FallbackTextCleaner(
            primary,
            new BasicTextCleaner("sentence"));

        var result = await cleaner.CleanAsync(
            "uh keep this safe",
            CancellationToken.None);

        Assert.Equal("Keep this safe.", result);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var client = new HttpClient(new RecordingHandler(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("{}");
        }));
        using var cleaner = new OllamaTextCleaner(
            "gemma3:1b",
            "http://127.0.0.1:11434/v1",
            client,
            TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cleaner.CleanAsync("hello", cancellation.Token));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<CancellationToken, HttpResponseMessage> respond)
        {
            _respond = token => Task.FromResult(respond(token));
        }

        public RecordingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.RequestUri,
                request.Headers.Authorization,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await _respond(cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        Uri? RequestUri,
        System.Net.Http.Headers.AuthenticationHeaderValue? Authorization,
        string Body);
}
