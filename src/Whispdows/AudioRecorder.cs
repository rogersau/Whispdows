using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Whispdows;

public interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    void Start(AudioSettings settings);

    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public sealed class RecordedAudio : IDisposable
{
    private byte[]? _wavBytes;

    public RecordedAudio(byte[] wavBytes, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        _wavBytes = wavBytes;
        Duration = duration;
    }

    public byte[] WavBytes => _wavBytes
        ?? throw new ObjectDisposedException(nameof(RecordedAudio));

    public TimeSpan Duration { get; }

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _wavBytes, null);
        if (bytes is not null)
        {
            Array.Clear(bytes);
        }
    }
}

public sealed class AudioRecorder : IAudioRecorder
{
    private static readonly WaveFormat TranscriptionFormat = new(16000, 16, 1);

    private readonly object _sync = new();
    private CaptureSession? _session;
    private bool _disposed;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _session is not null;
            }
        }
    }

    public void Start(AudioSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("Audio capture is already active.");
            }
        }

        CaptureSession? session = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            try
            {
                device = string.Equals(settings.DeviceId, "default", StringComparison.OrdinalIgnoreCase)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                    : enumerator.GetDevice(settings.DeviceId);
            }
            catch
            {
                enumerator.Dispose();
                throw;
            }

            WasapiCapture capture;
            try
            {
                capture = new WasapiCapture(device);
            }
            catch
            {
                device.Dispose();
                enumerator.Dispose();
                throw;
            }

            session = new CaptureSession(enumerator, device, capture);
            lock (_sync)
            {
                if (_session is not null)
                {
                    throw new InvalidOperationException("Audio capture is already active.");
                }

                _session = session;
            }

            session.Start();
        }
        catch (Exception exception)
        {
            if (session is not null)
            {
                CompleteSession(session);
            }

            throw new AudioRecorderException("The microphone is unavailable.", exception);
        }
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        var session = GetSession();
        try
        {
            session.RequestStop();
            var stopException = await session.WaitForStopAsync(cancellationToken);
            if (stopException is not null)
            {
                throw new AudioRecorderException("Microphone capture stopped unexpectedly.", stopException);
            }

            var rawAudio = session.TakeRawAudio();
            var sourceFormat = session.CaptureFormat;
            var duration = session.Duration;
            try
            {
                return await Task.Run(
                    () => ConvertToTranscriptionWave(rawAudio, sourceFormat, duration),
                    cancellationToken);
            }
            finally
            {
                Array.Clear(rawAudio);
            }
        }
        finally
        {
            CompleteSession(session);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        CaptureSession? session;
        lock (_sync)
        {
            session = _session;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            session.RequestStop();
            await session.WaitForStopAsync(cancellationToken);
        }
        finally
        {
            CompleteSession(session);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CaptureSession? session;
        lock (_sync)
        {
            session = _session;
            _session = null;
        }

        session?.Dispose();
    }

    internal static RecordedAudio ConvertToTranscriptionWave(
        byte[] rawAudio,
        WaveFormat sourceFormat,
        TimeSpan duration)
    {
        using var rawStream = new MemoryStream(rawAudio, writable: false);
        using var source = new RawSourceWaveStream(rawStream, sourceFormat);
        using var resampler = new MediaFoundationResampler(source, TranscriptionFormat)
        {
            ResamplerQuality = 60
        };
        using var output = new MemoryStream();

        using (var writer = new WaveFileWriter(output, TranscriptionFormat))
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.Write(buffer, 0, bytesRead);
            }
        }

        return new RecordedAudio(output.ToArray(), duration);
    }

    private CaptureSession GetSession()
    {
        lock (_sync)
        {
            return _session
                ?? throw new InvalidOperationException("Audio capture is not active.");
        }
    }

    private void CompleteSession(CaptureSession session)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        session.Dispose();
    }

    private sealed class CaptureSession : IDisposable
    {
        private readonly object _bufferSync = new();
        private readonly MMDeviceEnumerator _enumerator;
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly MemoryStream _rawAudio = new();
        private readonly TaskCompletionSource<Exception?> _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopRequested;
        private bool _disposed;
        private long _capturedBytes;

        public CaptureSession(
            MMDeviceEnumerator enumerator,
            MMDevice device,
            WasapiCapture capture)
        {
            _enumerator = enumerator;
            _device = device;
            _capture = capture;
            CaptureFormat = capture.WaveFormat;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
        }

        public WaveFormat CaptureFormat { get; }

        public TimeSpan Duration =>
            CaptureFormat.AverageBytesPerSecond <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)_capturedBytes / CaptureFormat.AverageBytesPerSecond);

        public void Start()
        {
            _capture.StartRecording();
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                _capture.StopRecording();
            }
        }

        public Task<Exception?> WaitForStopAsync(CancellationToken cancellationToken)
        {
            return _stopped.Task.WaitAsync(cancellationToken);
        }

        public byte[] TakeRawAudio()
        {
            lock (_bufferSync)
            {
                var bytes = _rawAudio.ToArray();
                ClearRawAudio();
                return bytes;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _device.Dispose();
            _enumerator.Dispose();

            lock (_bufferSync)
            {
                ClearRawAudio();
                _rawAudio.Dispose();
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            lock (_bufferSync)
            {
                if (_disposed)
                {
                    return;
                }

                _rawAudio.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                _capturedBytes += eventArgs.BytesRecorded;
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            _stopped.TrySetResult(eventArgs.Exception);
        }

        private void ClearRawAudio()
        {
            if (_rawAudio.TryGetBuffer(out var buffer))
            {
                Array.Clear(buffer.Array!, buffer.Offset, checked((int)_rawAudio.Length));
            }

            _rawAudio.SetLength(0);
        }
    }
}

public sealed class AudioRecorderException : Exception
{
    public AudioRecorderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
