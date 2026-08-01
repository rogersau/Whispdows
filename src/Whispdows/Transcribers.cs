using System.IO;
using System.Text;
using Whisper.net;

namespace Whispdows;

public interface ITranscriber : IConfigurationValidator, IDisposable
{
    Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken);
}

public sealed class DictationPipeline : IDisposable
{
    private bool _disposed;

    public DictationPipeline(
        ITranscriber transcriber,
        ITextCleaner textCleaner,
        ITextInserter textInserter)
    {
        Transcriber = transcriber;
        TextCleaner = textCleaner;
        TextInserter = textInserter;
    }

    public ITranscriber Transcriber { get; }

    public ITextCleaner TextCleaner { get; }

    public ITextInserter TextInserter { get; }

    public string TranscriptionProvider =>
        (Transcriber as IProviderComponent)?.ProviderName ?? "custom";

    public string CleanupProvider =>
        (TextCleaner as IProviderComponent)?.ProviderName ?? "custom";

    public void ValidateConfiguration()
    {
        Transcriber.ValidateConfiguration();
        if (TextCleaner is IConfigurationValidator validator)
        {
            validator.ValidateConfiguration();
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Transcriber is IInferenceWarmup transcriberWarmup)
        {
            await transcriberWarmup.WarmUpAsync(cancellationToken);
        }

        if (TextCleaner is IInferenceWarmup cleanerWarmup)
        {
            await cleanerWarmup.WarmUpAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Transcriber.Dispose();
        if (TextCleaner is IDisposable disposableCleaner)
        {
            disposableCleaner.Dispose();
        }

        if (TextInserter is IDisposable disposableInserter)
        {
            disposableInserter.Dispose();
        }
    }
}

public sealed class WhisperCppTranscriber :
    ITranscriber,
    IProviderComponent,
    IInferenceWarmup
{
    private readonly string _modelPath;
    private readonly string _language;
    private readonly int _threads;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _disposed;

    public WhisperCppTranscriber(
        string modelPath,
        string language,
        int configuredThreads)
    {
        _modelPath = Path.GetFullPath(modelPath);
        _language = language;
        _threads = configuredThreads == 0
            ? Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
            : configuredThreads;
    }

    public string ProviderName => "local-cpu";

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        _ = await GetProcessorAsync(linkedCancellation.Token);
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var token = linkedCancellation.Token;

        await _processingGate.WaitAsync(token);
        try
        {
            var processor = await GetProcessorAsync(token);
            if (wavAudio.CanSeek)
            {
                wavAudio.Position = 0;
            }

            var transcript = new StringBuilder();
            await foreach (var segment in processor
                .ProcessAsync(wavAudio, token)
                .ConfigureAwait(false))
            {
                transcript.Append(segment.Text);
            }

            return transcript.ToString().Trim();
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(_modelPath))
        {
            throw new LocalModelNotFoundException(_modelPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        if (!_processingGate.Wait(TimeSpan.FromSeconds(5)))
        {
            return;
        }

        try
        {
            _processor?.Dispose();
            _factory?.Dispose();
            _processor = null;
            _factory = null;
        }
        finally
        {
            _processingGate.Release();
        }

        _lifetime.Dispose();
        _initializationGate.Dispose();
        _processingGate.Dispose();
    }

    private async Task<WhisperProcessor> GetProcessorAsync(
        CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            return _processor;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_processor is not null)
            {
                return _processor;
            }

            ValidateConfiguration();

            return await Task.Run(
                () =>
                {
                    var factory = WhisperFactory.FromPath(_modelPath);
                    try
                    {
                        var processor = factory.CreateBuilder()
                            .WithLanguage(_language)
                            .WithThreads(_threads)
                            .Build();
                        _factory = factory;
                        _processor = processor;
                        return processor;
                    }
                    catch
                    {
                        factory.Dispose();
                        throw;
                    }
                },
                cancellationToken);
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}

public sealed class LocalModelNotFoundException : FileNotFoundException
{
    public LocalModelNotFoundException(string modelPath)
        : base($"The local Whisper model was not found at '{modelPath}'.", modelPath)
    {
        ModelPath = modelPath;
    }

    public string ModelPath { get; }
}
