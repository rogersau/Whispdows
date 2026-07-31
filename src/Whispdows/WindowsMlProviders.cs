using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using NAudio.Wave;
using System.IO;
using System.Text;

namespace Whispdows;

public sealed class WindowsMlTranscriber : ITranscriber, IProviderComponent
{
    private readonly WindowsMlRuntime _runtime;
    private readonly string _modelAlias;
    private readonly string _language;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private bool _disposed;

    public WindowsMlTranscriber(
        WindowsMlRuntime runtime,
        string modelAlias,
        string language)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _modelAlias = modelAlias;
        _language = language;
    }

    public string ProviderName => "windowsml";

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
        byte[]? pcmAudio = null;
        try
        {
            var model = await _runtime.GetModelAsync(_modelAlias, cancellationToken);
            var audioClient = await model.GetAudioClientAsync(cancellationToken);
            pcmAudio = await ReadPcmAudioAsync(wavAudio, cancellationToken);

            await using var session = audioClient.CreateLiveTranscriptionSession();
            session.Settings.SampleRate = 16000;
            session.Settings.Channels = 1;
            session.Settings.BitsPerSample = 16;
            session.Settings.Language =
                string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _language;

            await session.StartAsync(cancellationToken);
            var resultsTask = ReadResultsAsync(session, cancellationToken);
            try
            {
                const int chunkSize = 64 * 1024;
                for (var offset = 0; offset < pcmAudio.Length; offset += chunkSize)
                {
                    var length = Math.Min(chunkSize, pcmAudio.Length - offset);
                    await session.AppendAsync(
                        pcmAudio.AsMemory(offset, length),
                        cancellationToken);
                }

                await session.StopAsync(cancellationToken);
                return await resultsTask;
            }
            catch
            {
                try
                {
                    await session.StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve the original inference failure.
                }

                throw;
            }
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
            if (pcmAudio is not null)
            {
                Array.Clear(pcmAudio);
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
        _processingGate.Dispose();
    }

    private static async Task<byte[]> ReadPcmAudioAsync(
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

        var pcmAudio = new byte[checked((int)reader.Length)];
        var offset = 0;
        while (offset < pcmAudio.Length)
        {
            var bytesRead = reader.Read(pcmAudio, offset, pcmAudio.Length - offset);
            if (bytesRead == 0)
            {
                break;
            }

            offset += bytesRead;
        }

        if (offset != pcmAudio.Length)
        {
            Array.Resize(ref pcmAudio, offset);
        }

        return pcmAudio;
    }

    private static async Task<string> ReadResultsAsync(
        Microsoft.AI.Foundry.Local.OpenAI.LiveAudioTranscriptionSession session,
        CancellationToken cancellationToken)
    {
        var finalText = new StringBuilder();
        var lastPartial = string.Empty;
        await foreach (var response in session.GetStream(cancellationToken))
        {
            var text = response.Content?.FirstOrDefault()?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (response.IsFinal)
            {
                AppendSegment(finalText, text);
            }
            else
            {
                lastPartial = text;
            }
        }

        return finalText.Length > 0
            ? finalText.ToString().Trim()
            : lastPartial;
    }

    private static void AppendSegment(StringBuilder builder, string text)
    {
        if (builder.Length > 0
            && !char.IsWhiteSpace(builder[^1])
            && !char.IsWhiteSpace(text[0]))
        {
            builder.Append(' ');
        }

        builder.Append(text);
    }
}

public sealed class WindowsMlTextCleaner : ITextCleaner, IConfigurationValidator, IProviderComponent, IDisposable
{
    private readonly WindowsMlRuntime _runtime;
    private readonly string _modelAlias;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private bool _disposed;

    public WindowsMlTextCleaner(
        WindowsMlRuntime runtime,
        string modelAlias)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _modelAlias = modelAlias;
    }

    public string ProviderName => "windowsml";

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
            var model = await _runtime.GetModelAsync(_modelAlias, cancellationToken);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _processingGate.Dispose();
    }
}
