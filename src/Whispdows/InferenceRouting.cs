using System.IO;

namespace Whispdows;

public enum InferenceDevice
{
    Npu,
    Gpu,
    Cpu
}

public static class InferenceDevicePriority
{
    public static IReadOnlyList<InferenceDevice> Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(Parse).ToArray();
    }

    public static string ToProviderSuffix(this InferenceDevice device) =>
        device.ToString().ToLowerInvariant();

    private static InferenceDevice Parse(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "npu" => InferenceDevice.Npu,
            "gpu" => InferenceDevice.Gpu,
            "cpu" => InferenceDevice.Cpu,
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Inference devices must be npu, gpu, or cpu.")
        };
    }
}

public interface IInferenceWarmup
{
    Task WarmUpAsync(CancellationToken cancellationToken);
}

internal sealed class BackgroundInferenceInitialization<T> : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, Task<T>> _initialize;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<T>? _initialization;
    private bool _disposed;

    public BackgroundInferenceInitialization(
        Func<CancellationToken, Task<T>> initialize)
    {
        _initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
    }

    public Task WarmUpAsync(CancellationToken cancellationToken) =>
        EnsureStarted().WaitAsync(cancellationToken);

    public async Task<T> GetIfReadyAsync(
        string providerName,
        CancellationToken cancellationToken)
    {
        var initialization = EnsureStarted();
        if (!initialization.IsCompleted)
        {
            throw new OnDeviceInferenceUnavailableException(
                $"{providerName} is still warming up; using the next inference tier.");
        }

        return await initialization.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
    }

    private Task<T> EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            return _initialization ??= Task.Run(
                () => _initialize(_lifetime.Token),
                CancellationToken.None);
        }
    }
}

public sealed class PriorityTranscriber : ITranscriber, IProviderComponent, IInferenceWarmup
{
    private readonly IReadOnlyList<ITranscriber> _candidates;
    private string _activeProviderName;
    private bool _disposed;

    public PriorityTranscriber(IEnumerable<ITranscriber> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates.ToArray();
        if (_candidates.Count == 0)
        {
            throw new ArgumentException(
                "At least one transcription provider is required.",
                nameof(candidates));
        }

        _activeProviderName = ProviderNameOf(_candidates[0]);
    }

    public string ProviderName => Volatile.Read(ref _activeProviderName);

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? lastFailure = null;
        foreach (var candidate in _candidates)
        {
            try
            {
                candidate.ValidateConfiguration();
                return;
            }
            catch (Exception exception) when (InferenceFallback.IsEligible(exception))
            {
                lastFailure = exception;
            }
        }

        throw lastFailure
            ?? new InvalidOperationException("No transcription provider is configured.");
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? lastFailure = null;
        foreach (var candidate in _candidates)
        {
            if (candidate is not IInferenceWarmup warmup)
            {
                return;
            }

            try
            {
                await warmup.WarmUpAsync(cancellationToken);
                return;
            }
            catch (Exception exception)
                when (InferenceFallback.IsEligible(exception, cancellationToken))
            {
                lastFailure = exception;
            }
        }

        if (lastFailure is not null)
        {
            throw lastFailure;
        }
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);

        using var audioBuffer = new MemoryStream();
        if (wavAudio.CanSeek)
        {
            wavAudio.Position = 0;
        }

        await wavAudio.CopyToAsync(audioBuffer, cancellationToken);
        var audioBytes = audioBuffer.ToArray();
        try
        {
            for (var index = 0; index < _candidates.Count; index++)
            {
                var candidate = _candidates[index];
                Volatile.Write(ref _activeProviderName, ProviderNameOf(candidate));
                using var candidateAudio = new MemoryStream(audioBytes, writable: false);
                try
                {
                    return await candidate.TranscribeAsync(
                        candidateAudio,
                        cancellationToken);
                }
                catch (Exception exception)
                    when (index < _candidates.Count - 1
                        && InferenceFallback.IsEligible(exception, cancellationToken))
                {
                    // Continue down the declared device/provider priority.
                }
            }

            throw new InvalidOperationException("No transcription provider was attempted.");
        }
        finally
        {
            Array.Clear(audioBytes);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var candidate in _candidates)
        {
            candidate.Dispose();
        }
    }

    private static string ProviderNameOf(ITranscriber transcriber) =>
        (transcriber as IProviderComponent)?.ProviderName ?? "custom";
}

public sealed class PriorityTextCleaner :
    ITextCleaner,
    IConfigurationValidator,
    IProviderComponent,
    IInferenceWarmup,
    IDisposable
{
    private readonly IReadOnlyList<ITextCleaner> _candidates;
    private string _activeProviderName;
    private bool _disposed;

    public PriorityTextCleaner(IEnumerable<ITextCleaner> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates.ToArray();
        if (_candidates.Count == 0)
        {
            throw new ArgumentException(
                "At least one cleanup provider is required.",
                nameof(candidates));
        }

        _activeProviderName = ProviderNameOf(_candidates[0]);
    }

    public string ProviderName => Volatile.Read(ref _activeProviderName);

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? lastFailure = null;
        foreach (var candidate in _candidates)
        {
            if (candidate is not IConfigurationValidator validator)
            {
                return;
            }

            try
            {
                validator.ValidateConfiguration();
                return;
            }
            catch (Exception exception) when (InferenceFallback.IsEligible(exception))
            {
                lastFailure = exception;
            }
        }

        throw lastFailure
            ?? new InvalidOperationException("No cleanup provider is configured.");
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? lastFailure = null;
        foreach (var candidate in _candidates)
        {
            if (candidate is not IInferenceWarmup warmup)
            {
                return;
            }

            try
            {
                await warmup.WarmUpAsync(cancellationToken);
                return;
            }
            catch (Exception exception)
                when (InferenceFallback.IsEligible(exception, cancellationToken))
            {
                lastFailure = exception;
            }
        }

        if (lastFailure is not null)
        {
            throw lastFailure;
        }
    }

    public async Task<string> CleanAsync(
        string transcript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transcript);

        for (var index = 0; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            Volatile.Write(ref _activeProviderName, ProviderNameOf(candidate));
            try
            {
                return await candidate.CleanAsync(transcript, cancellationToken);
            }
            catch (Exception exception)
                when (index < _candidates.Count - 1
                    && InferenceFallback.IsEligible(exception, cancellationToken))
            {
                // Continue down the declared device/provider priority.
            }
        }

        throw new InvalidOperationException("No cleanup provider was attempted.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var candidate in _candidates.OfType<IDisposable>())
        {
            candidate.Dispose();
        }
    }

    private static string ProviderNameOf(ITextCleaner cleaner) =>
        (cleaner as IProviderComponent)?.ProviderName ?? "custom";
}

internal static class InferenceFallback
{
    public static bool IsEligible(
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        return !cancellationToken.IsCancellationRequested
            && exception is CloudProviderException
                or WindowsMlUnavailableException
                or OnDeviceInferenceUnavailableException
                or LocalModelNotFoundException;
    }
}

public sealed class OnDeviceInferenceUnavailableException : Exception
{
    public OnDeviceInferenceUnavailableException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
