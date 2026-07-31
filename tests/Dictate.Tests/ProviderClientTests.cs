using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class ProviderClientTests
{
    [Theory]
    [InlineData(
        "openai",
        "OPENAI_API_KEY",
        "https://api.openai.com/v1/audio/transcriptions",
        "https://api.openai.com/v1/chat/completions")]
    [InlineData(
        "groq",
        "GROQ_API_KEY",
        "https://api.groq.com/openai/v1/audio/transcriptions",
        "https://api.groq.com/openai/v1/chat/completions")]
    public void Provider_definition_selects_the_expected_key_and_endpoints(
        string providerName,
        string keyName,
        string transcriptionEndpoint,
        string chatEndpoint)
    {
        var provider = CloudProviderDefinition.Create(
            providerName,
            Secrets((keyName, "test-key")));

        Assert.Equal("test-key", provider.ApiKey);
        Assert.DoesNotContain("test-key", provider.ToString());
        Assert.Equal(transcriptionEndpoint, provider.TranscriptionEndpoint.AbsoluteUri);
        Assert.Equal(chatEndpoint, provider.ChatCompletionsEndpoint.AbsoluteUri);
    }

    [Fact]
    public async Task Transcriber_sends_one_multipart_request_and_reads_plain_text()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"text":"  hello world  "}"""));
        using var client = new HttpClient(handler);
        using var transcriber = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create(
                "openai",
                Secrets(("OPENAI_API_KEY", "openai-secret"))),
            "gpt-4o-transcribe",
            "en",
            client);

        using var audio = new MemoryStream(Encoding.ASCII.GetBytes("RIFF-wave"));
        var result = await transcriber.TranscribeAsync(audio, CancellationToken.None);

        Assert.Equal("hello world", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.openai.com/v1/audio/transcriptions",
            request.Uri.AbsoluteUri);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("openai-secret", request.Authorization?.Parameter);
        Assert.StartsWith("multipart/form-data", request.ContentType);
        Assert.Contains("name=model", request.Body);
        Assert.Contains("gpt-4o-transcribe", request.Body);
        Assert.Contains("name=language", request.Body);
        Assert.Contains("dictation.wav", request.Body);
        Assert.DoesNotContain("openai-secret", request.Body);
    }

    [Fact]
    public async Task Transcriber_does_not_send_a_request_without_the_required_key()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"text":"unused"}"""));
        using var client = new HttpClient(handler);
        using var transcriber = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create("groq", ProviderSecrets.Empty),
            "whisper-large-v3-turbo",
            "en",
            client);

        var exception = await Assert.ThrowsAsync<MissingApiKeyException>(
            () => transcriber.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                CancellationToken.None));

        Assert.Equal("GROQ_API_KEY", exception.ApiKeyName);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Azure_transcriber_uses_the_regional_fast_transcription_endpoint()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse(
                """{"combinedPhrases":[{"text":"  hello from Azure  "}],"phrases":[]}"""));
        using var client = new HttpClient(handler);
        using var transcriber = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast",
            "en-AU",
            client);

        var result = await transcriber.TranscribeAsync(
            new MemoryStream(Encoding.ASCII.GetBytes("RIFF-wave")),
            CancellationToken.None);

        Assert.Equal("hello from Azure", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://australiaeast.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
            request.Uri.AbsoluteUri);
        Assert.Null(request.Authorization);
        Assert.Equal("azure-secret", request.SubscriptionKey);
        Assert.StartsWith("multipart/form-data", request.ContentType);
        Assert.Contains("name=audio", request.Body);
        Assert.Contains("dictation.wav", request.Body);
        Assert.Contains("name=definition", request.Body);
        Assert.Contains("en-AU", request.Body);
        Assert.DoesNotContain("azure-secret", request.Body);
    }

    [Fact]
    public async Task Azure_transcriber_does_not_request_without_its_key()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse("""{"combinedPhrases":[]}"""));
        using var client = new HttpClient(handler);
        using var transcriber = new AzureSpeechTranscriber(
            string.Empty,
            "australiaeast",
            "en-AU",
            client);

        var exception = await Assert.ThrowsAsync<MissingApiKeyException>(
            () => transcriber.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                CancellationToken.None));

        Assert.Equal("AZURE_SPEECH_KEY", exception.ApiKeyName);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Azure_api_failure_uses_local_fallback_without_retrying()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var azure = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast",
            "en-AU",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(azure, local);

        var result = await transcriber.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);

        Assert.Equal("local result", result);
        Assert.Single(handler.Requests);
        Assert.Equal(1, local.CallCount);
    }

    [Fact]
    public async Task Azure_missing_key_uses_explicit_local_fallback()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse("""{"combinedPhrases":[]}"""));
        using var client = new HttpClient(handler);
        using var azure = new AzureSpeechTranscriber(
            string.Empty,
            "australiaeast",
            "en-AU",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(azure, local);

        transcriber.ValidateConfiguration();
        var result = await transcriber.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);

        Assert.Equal("local result", result);
        Assert.Empty(handler.Requests);
        Assert.Equal(1, local.CallCount);
    }

    [Fact]
    public async Task Azure_timeout_is_a_cloud_failure()
    {
        var handler = new RecordingHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""{"combinedPhrases":[]}""");
        });
        using var client = new HttpClient(handler);
        using var transcriber = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast",
            "en-AU",
            client,
            TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsAsync<CloudRequestException>(
            () => transcriber.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                CancellationToken.None));

        Assert.Equal("Azure Speech", exception.Provider);
        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task Azure_user_cancellation_does_not_use_local_fallback()
    {
        var handler = new RecordingHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""{"combinedPhrases":[]}""");
        });
        using var client = new HttpClient(handler);
        using var azure = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast",
            "en-AU",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(azure, local);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcriber.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                cancellation.Token));

        Assert.Equal(0, local.CallCount);
    }

    [Fact]
    public async Task Azure_malformed_response_is_a_cloud_failure()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse("""{"text":"wrong response shape"}"""));
        using var client = new HttpClient(handler);
        using var transcriber = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast",
            "en-AU",
            client);

        var exception = await Assert.ThrowsAsync<CloudRequestException>(
            () => transcriber.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                CancellationToken.None));

        Assert.Contains("malformed", exception.Message);
    }

    [Fact]
    public void Azure_key_rejects_header_control_characters()
    {
        using var transcriber = new AzureSpeechTranscriber(
            "azure-secret\r\nInjected: value",
            "australiaeast",
            "en-AU");

        var exception = Assert.Throws<InvalidOperationException>(
            transcriber.ValidateConfiguration);

        Assert.DoesNotContain("azure-secret", exception.Message);
    }

    [Fact]
    public void Azure_transcriber_rejects_use_after_disposal()
    {
        var transcriber = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast",
            "en-AU");
        transcriber.Dispose();

        Assert.Throws<ObjectDisposedException>(
            transcriber.ValidateConfiguration);
    }

    [Fact]
    public void Azure_endpoint_rejects_non_region_host_input()
    {
        using var transcriber = new AzureSpeechTranscriber(
            "azure-secret",
            "australiaeast.example.com/path",
            "en-AU");

        var exception = Assert.Throws<InvalidOperationException>(
            transcriber.ValidateConfiguration);

        Assert.Contains("Azure region identifier", exception.Message);
    }

    [Fact]
    public async Task Transcriber_missing_key_uses_explicit_local_fallback()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"text":"unused"}"""));
        using var client = new HttpClient(handler);
        using var cloud = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create("openai", ProviderSecrets.Empty),
            "gpt-4o-transcribe",
            "en",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(cloud, local);

        transcriber.ValidateConfiguration();
        var result = await transcriber.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);

        Assert.Equal("local result", result);
        Assert.Empty(handler.Requests);
        Assert.Equal(1, local.CallCount);
    }

    [Fact]
    public async Task Transcriber_api_failure_uses_local_fallback_without_retrying()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("response body must stay private")
            });
        using var client = new HttpClient(handler);
        using var cloud = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create(
                "groq",
                Secrets(("GROQ_API_KEY", "groq-secret"))),
            "whisper-large-v3-turbo",
            "en",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(cloud, local);

        var result = await transcriber.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);

        Assert.Equal("local result", result);
        Assert.Single(handler.Requests);
        Assert.Equal(1, local.CallCount);
    }

    [Fact]
    public async Task Transcriber_timeout_uses_local_fallback()
    {
        var handler = new RecordingHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""{"text":"unused"}""");
        });
        using var client = new HttpClient(handler);
        using var cloud = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create(
                "openai",
                Secrets(("OPENAI_API_KEY", "openai-secret"))),
            "gpt-4o-transcribe",
            "en",
            client,
            TimeSpan.FromMilliseconds(25));
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(cloud, local);

        var result = await transcriber.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            CancellationToken.None);

        Assert.Equal("local result", result);
        Assert.Equal(1, local.CallCount);
    }

    [Fact]
    public async Task Transcriber_buffers_a_non_seekable_stream_for_local_fallback()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var cloud = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create(
                "groq",
                Secrets(("GROQ_API_KEY", "groq-secret"))),
            "whisper-large-v3-turbo",
            "en",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(cloud, local);
        using var audio = new NonSeekableReadStream([4, 5, 6, 7]);

        var result = await transcriber.TranscribeAsync(
            audio,
            CancellationToken.None);

        Assert.Equal("local result", result);
        Assert.Equal<byte>([4, 5, 6, 7], local.LastAudio);
    }

    [Fact]
    public async Task Transcriber_does_not_fallback_after_user_cancellation()
    {
        var handler = new RecordingHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""{"text":"unused"}""");
        });
        using var client = new HttpClient(handler);
        using var cloud = new OpenAiCompatibleTranscriber(
            CloudProviderDefinition.Create(
                "openai",
                Secrets(("OPENAI_API_KEY", "openai-secret"))),
            "gpt-4o-transcribe",
            "en",
            client);
        var local = new StubTranscriber("local result");
        using var transcriber = new FallbackTranscriber(cloud, local);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcriber.TranscribeAsync(
                new MemoryStream([1, 2, 3]),
                cancellation.Token));

        Assert.Equal(0, local.CallCount);
    }

    [Fact]
    public async Task Llm_cleaner_sends_only_fixed_instructions_and_the_transcript()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse(
                """{"choices":[{"message":{"content":"Cleaned text."}}]}"""));
        using var client = new HttpClient(handler);
        using var cleaner = new LlmTextCleaner(
            CloudProviderDefinition.Create(
                "groq",
                Secrets(("GROQ_API_KEY", "groq-secret"))),
            "llama-3.3-70b-versatile",
            client);

        var result = await cleaner.CleanAsync(
            "um raw transcript",
            CancellationToken.None);

        Assert.Equal("Cleaned text.", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://api.groq.com/openai/v1/chat/completions",
            request.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("llama-3.3-70b-versatile", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.InRange(root.GetProperty("max_completion_tokens").GetInt32(), 64, 2048);
        Assert.False(root.TryGetProperty("tools", out _));

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains(
            "Return only the corrected text",
            messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(
            "um raw transcript",
            messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Llm_cleaner_malformed_response_falls_back_to_basic_cleanup()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"choices":[]}"""));
        using var client = new HttpClient(handler);
        using var cloud = new LlmTextCleaner(
            CloudProviderDefinition.Create(
                "openai",
                Secrets(("OPENAI_API_KEY", "openai-secret"))),
            "gpt-4.1-mini",
            client);
        using var cleaner = new FallbackTextCleaner(
            cloud,
            new BasicTextCleaner("sentence"));

        var result = await cleaner.CleanAsync(
            "um hello there",
            CancellationToken.None);

        Assert.Equal("Hello there.", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Llm_cleaner_missing_key_falls_back_without_a_request()
    {
        var handler = new RecordingHandler(
            _ => JsonResponse(
                """{"choices":[{"message":{"content":"unused"}}]}"""));
        using var client = new HttpClient(handler);
        using var cloud = new LlmTextCleaner(
            CloudProviderDefinition.Create("openai", ProviderSecrets.Empty),
            "gpt-4.1-mini",
            client);
        using var cleaner = new FallbackTextCleaner(
            cloud,
            new BasicTextCleaner("sentence"));

        cleaner.ValidateConfiguration();
        var result = await cleaner.CleanAsync(
            "uh keep this",
            CancellationToken.None);

        Assert.Equal("Keep this.", result);
        Assert.Empty(handler.Requests);
    }

    private static ProviderSecrets Secrets(params (string Name, string Value)[] values)
    {
        return new ProviderSecrets(values.ToDictionary(
            item => item.Name,
            item => item.Value,
            StringComparer.Ordinal));
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

        public RecordingHandler(Func<RequestSnapshot, HttpResponseMessage> respond)
        {
            _respond = async cancellationToken =>
            {
                await Task.Yield();
                return respond(_currentRequest!);
            };
        }

        public RecordingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        private RequestSnapshot? _currentRequest;

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var snapshot = new RequestSnapshot(
                request.RequestUri!,
                request.Headers.Authorization,
                request.Headers.TryGetValues(
                    "Ocp-Apim-Subscription-Key",
                    out var subscriptionKeys)
                    ? subscriptionKeys.Single()
                    : null,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(snapshot);
            _currentRequest = snapshot;
            return await _respond(cancellationToken);
        }
    }

    private sealed record RequestSnapshot(
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string? SubscriptionKey,
        string ContentType,
        string Body);

    private sealed class StubTranscriber : ITranscriber
    {
        private readonly string _result;

        public StubTranscriber(string result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public byte[] LastAudio { get; private set; } = [];

        public void ValidateConfiguration()
        {
        }

        public Task<string> TranscribeAsync(
            Stream wavAudio,
            CancellationToken cancellationToken)
        {
            CallCount++;
            using var buffer = new MemoryStream();
            wavAudio.CopyTo(buffer);
            LastAudio = buffer.ToArray();
            return Task.FromResult(_result);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableReadStream(byte[] bytes)
        {
            _inner = new MemoryStream(bytes, writable: false);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
