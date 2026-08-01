using System.IO;
using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class InferenceRoutingTests
{
    [Fact]
    public async Task Priority_transcriber_uses_the_first_successful_device()
    {
        var attempts = new List<string>();
        using var transcriber = new PriorityTranscriber(
        [
            new StubTranscriber("openvino-npu", attempts, failure: new OnDeviceInferenceUnavailableException("no npu")),
            new StubTranscriber("openvino-gpu", attempts, result: "gpu transcript"),
            new StubTranscriber("local-cpu", attempts, result: "cpu transcript")
        ]);

        using var audio = new MemoryStream([1, 2, 3]);
        var result = await transcriber.TranscribeAsync(audio, CancellationToken.None);

        Assert.Equal("gpu transcript", result);
        Assert.Equal(["openvino-npu", "openvino-gpu"], attempts);
        Assert.Equal("openvino-gpu", transcriber.ProviderName);
    }

    [Fact]
    public async Task Priority_transcriber_does_not_fallback_after_cancellation()
    {
        var attempts = new List<string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var transcriber = new PriorityTranscriber(
        [
            new StubTranscriber("openvino-npu", attempts, failure: new OperationCanceledException()),
            new StubTranscriber("local-cpu", attempts, result: "cpu transcript")
        ]);

        using var audio = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcriber.TranscribeAsync(audio, cancellation.Token));

        Assert.Empty(attempts);
    }

    [Fact]
    public async Task Priority_cleaner_tracks_npu_gpu_cpu_fallback_order()
    {
        var attempts = new List<string>();
        using var cleaner = new PriorityTextCleaner(
        [
            new StubCleaner("windowsml-npu", attempts, failure: new WindowsMlUnavailableException("no npu model")),
            new StubCleaner("windowsml-gpu", attempts, failure: new WindowsMlUnavailableException("no gpu model")),
            new StubCleaner("windowsml-cpu", attempts, result: "cleaned")
        ]);

        var result = await cleaner.CleanAsync("raw", CancellationToken.None);

        Assert.Equal("cleaned", result);
        Assert.Equal(
            ["windowsml-npu", "windowsml-gpu", "windowsml-cpu"],
            attempts);
        Assert.Equal("windowsml-cpu", cleaner.ProviderName);
    }

    [Fact]
    public void OpenVino_provider_name_includes_the_selected_device()
    {
        using var transcriber = new OpenVinoWhisperTranscriber(
            Path.Combine("models", "whisper-base.en-int8-ov"),
            "en",
            InferenceDevice.Npu,
            Path.GetTempPath());

        Assert.Equal("openvino-genai-npu", transcriber.ProviderName);
    }

    [Fact]
    public async Task Background_initialization_falls_through_until_ready()
    {
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var initialization = new BackgroundInferenceInitialization<string>(
            _ => completion.Task);

        await Assert.ThrowsAsync<OnDeviceInferenceUnavailableException>(
            () => initialization.GetIfReadyAsync(
                "windowsml-npu",
                CancellationToken.None));

        completion.SetResult("ready");
        await initialization.WarmUpAsync(CancellationToken.None);

        Assert.Equal(
            "ready",
            await initialization.GetIfReadyAsync(
                "windowsml-npu",
                CancellationToken.None));
    }

    private sealed class StubTranscriber : ITranscriber, IProviderComponent
    {
        private readonly ICollection<string> _attempts;
        private readonly string _result;
        private readonly Exception? _failure;

        public StubTranscriber(
            string providerName,
            ICollection<string> attempts,
            string result = "",
            Exception? failure = null)
        {
            ProviderName = providerName;
            _attempts = attempts;
            _result = result;
            _failure = failure;
        }

        public string ProviderName { get; }

        public void ValidateConfiguration()
        {
        }

        public Task<string> TranscribeAsync(
            Stream wavAudio,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _attempts.Add(ProviderName);
            return _failure is null
                ? Task.FromResult(_result)
                : Task.FromException<string>(_failure);
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubCleaner :
        ITextCleaner,
        IConfigurationValidator,
        IProviderComponent,
        IDisposable
    {
        private readonly ICollection<string> _attempts;
        private readonly string _result;
        private readonly Exception? _failure;

        public StubCleaner(
            string providerName,
            ICollection<string> attempts,
            string result = "",
            Exception? failure = null)
        {
            ProviderName = providerName;
            _attempts = attempts;
            _result = result;
            _failure = failure;
        }

        public string ProviderName { get; }

        public void ValidateConfiguration()
        {
        }

        public Task<string> CleanAsync(
            string transcript,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _attempts.Add(ProviderName);
            return _failure is null
                ? Task.FromResult(_result)
                : Task.FromException<string>(_failure);
        }

        public void Dispose()
        {
        }
    }
}
