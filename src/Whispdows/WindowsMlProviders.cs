using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using NAudio.Wave;
using System.IO;

namespace Whispdows;

public sealed class WindowsMlTranscriber :
    ITranscriber,
    IProviderComponent,
    IInferenceWarmup
{
    private readonly WindowsMlRuntime _runtime;
    private readonly string _modelAlias;
    private readonly string _language;
    private readonly InferenceDevice _device;
    private readonly BackgroundInferenceInitialization<IModel> _initialization;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private bool _disposed;

    public WindowsMlTranscriber(
        WindowsMlRuntime runtime,
        string modelAlias,
        string language,
        InferenceDevice device)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _modelAlias = modelAlias;
        _language = language;
        _device = device;
        _initialization = new BackgroundInferenceInitialization<IModel>(
            cancellationToken => _runtime.GetModelAsync(
                _modelAlias,
                _device,
                cancellationToken));
    }

    public string ProviderName => $"windowsml-{_device.ToProviderSuffix()}";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_modelAlias))
        {
            throw new InvalidOperationException(
                "A Windows ML transcription model alias must be configured.");
        }

        if (string.IsNullOrWhiteSpace(_language))
        {
            throw new InvalidOperationException(
                "A transcription language code must be configured.");
        }
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);
        ValidateConfiguration();

        await _processingGate.WaitAsync(cancellationToken);
        byte[]? wavBytes = null;
        var wavPath = Path.Combine(
            Path.GetTempPath(),
            $"whispdows-transcription-{Guid.NewGuid():N}.wav");
        try
        {
            var model = await _initialization.GetIfReadyAsync(
                ProviderName,
                cancellationToken);
            var audioClient = await model.GetAudioClientAsync(cancellationToken);
            audioClient.Settings.Language =
                string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _language;
            audioClient.Settings.Temperature = 0.0f;

            wavBytes = await ReadWavAudioAsync(wavAudio, cancellationToken);
            await File.WriteAllBytesAsync(wavPath, wavBytes, cancellationToken);
            var response = await audioClient.TranscribeAudioAsync(
                wavPath,
                cancellationToken);
            return response.Text?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsMlUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new WindowsMlUnavailableException(
                "Windows ML transcription was unavailable.",
                exception);
        }
        finally
        {
            if (wavBytes is not null)
            {
                Array.Clear(wavBytes);
            }

            try
            {
                File.Delete(wavPath);
            }
            catch
            {
                // Best-effort cleanup of the temporary audio file.
            }

            _processingGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialization.Dispose();
        _processingGate.Dispose();
    }

    private static async Task<byte[]> ReadWavAudioAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        using var wavBuffer = new MemoryStream();
        if (wavAudio.CanSeek)
        {
            wavAudio.Position = 0;
        }

        await wavAudio.CopyToAsync(wavBuffer, cancellationToken);
        wavBuffer.Position = 0;

        using var reader = new WaveFileReader(wavBuffer);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm
            || reader.WaveFormat.SampleRate != 16000
            || reader.WaveFormat.Channels != 1
            || reader.WaveFormat.BitsPerSample != 16)
        {
            throw new InvalidDataException(
                "Windows ML transcription requires mono 16 kHz 16-bit PCM WAV audio.");
        }

        return wavBuffer.ToArray();
    }

    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateConfiguration();
        return _initialization.WarmUpAsync(cancellationToken);
    }
}

public sealed class WindowsMlTextCleaner :
    ITextCleaner,
    IConfigurationValidator,
    IProviderComponent,
    IInferenceWarmup,
    IDisposable
{
    private readonly WindowsMlRuntime _runtime;
    private readonly string _modelAlias;
    private readonly InferenceDevice _device;
    private readonly BackgroundInferenceInitialization<IModel> _initialization;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private bool _disposed;

    public WindowsMlTextCleaner(
        WindowsMlRuntime runtime,
        string modelAlias,
        InferenceDevice device)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _modelAlias = modelAlias;
        _device = device;
        _initialization = new BackgroundInferenceInitialization<IModel>(
            cancellationToken => _runtime.GetModelAsync(
                _modelAlias,
                _device,
                cancellationToken));
    }

    public string ProviderName => $"windowsml-{_device.ToProviderSuffix()}";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_modelAlias))
        {
            throw new InvalidOperationException(
                "A Windows ML cleanup model alias must be configured.");
        }
    }

    public async Task<string> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transcript);
        ValidateConfiguration();

        await _processingGate.WaitAsync(cancellationToken);
        try
        {
            var model = await _initialization.GetIfReadyAsync(
                ProviderName,
                cancellationToken);
            var chatClient = await model.GetChatClientAsync(cancellationToken);
            var messages = new[]
            {
                new ChatMessage
                {
                    Role = "system",
                    Content = TextCleanupPrompt.System
                },
                new ChatMessage
                {
                    Role = "user",
                    Content = transcript
                }
            };

            var response = await chatClient.CompleteChatAsync(
                messages,
                cancellationToken);
            var cleaned = response.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                throw new InvalidOperationException(
                    "The Windows ML cleanup model returned no text.");
            }

            return cleaned;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsMlUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new WindowsMlUnavailableException(
                "Windows ML cleanup was unavailable.",
                exception);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateConfiguration();
        return _initialization.WarmUpAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialization.Dispose();
        _processingGate.Dispose();
    }
}
