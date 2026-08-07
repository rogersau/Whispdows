using System.Buffers.Binary;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Whispdows;

/// <summary>Captures a microphone and the Windows default render (loopback) device.</summary>
public interface IMeetingAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    void Start(AudioSettings settings);

    Task<MeetingRecording> StopAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>A normalized meeting recording backed by a local temporary WAV file.</summary>
public sealed class MeetingRecording : IDisposable
{
    private string? _filePath;

    internal MeetingRecording(string filePath, TimeSpan duration)
    {
        FilePath = Path.GetFullPath(filePath);
        Duration = duration;
        _filePath = FilePath;
    }

    public string FilePath { get; }

    public TimeSpan Duration { get; }

    public Stream OpenRead()
    {
        var path = _filePath ?? throw new ObjectDisposedException(nameof(MeetingRecording));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
    }

    public void Dispose()
    {
        var path = Interlocked.Exchange(ref _filePath, null);
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class MeetingAudioRecorder : IMeetingAudioRecorder
{
    private readonly object _sync = new();
    private readonly Func<AudioSettings, CapturePair> _captureFactory;
    private CaptureSession? _session;
    private bool _disposed;

    public MeetingAudioRecorder()
        : this(CreateCapturePair)
    {
    }

    internal MeetingAudioRecorder(Func<AudioSettings, CapturePair> captureFactory)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
    }

    public bool IsRecording
    {
        get
        {
            lock (_sync) return _session is not null;
        }
    }

    public void Start(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                throw new InvalidOperationException(
                    "Meeting audio capture is already active.");
            }

            CapturePair pair;
            try
            {
                pair = _captureFactory(settings);
            }
            catch (Exception exception)
            {
                throw new AudioRecorderException(
                    "The meeting audio devices are unavailable.",
                    exception);
            }

            var session = new CaptureSession(pair);
            _session = session;

            try
            {
                session.Start();
            }
            catch (Exception exception)
            {
                _session = null;
                session.Dispose();
                throw new AudioRecorderException(
                    "The meeting audio devices could not be started.",
                    exception);
            }
        }
    }

    public async Task<MeetingRecording> StopAsync(CancellationToken cancellationToken = default)
    {
        var session = GetSession();
        try
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            using var mic = session.OpenMicrophoneAudio();
            using var system = session.OpenSystemAudio();
            return await Task.Run(
                () => NormalizeAndMix(
                    mic,
                    session.MicrophoneFormat,
                    system,
                    session.SystemFormat),
                cancellationToken).ConfigureAwait(false);
        }
        finally { CompleteSession(session); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        CaptureSession? session;
        lock (_sync) session = _session;
        if (session is null) return;
        try { await session.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { CompleteSession(session); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CaptureSession? session;
        lock (_sync) { session = _session; _session = null; }
        session?.Dispose();
    }

    internal static MeetingRecording NormalizeAndMix(
        byte[] microphone, WaveFormat microphoneFormat,
        byte[] systemAudio, WaveFormat systemFormat)
    {
        ArgumentNullException.ThrowIfNull(microphone);
        ArgumentNullException.ThrowIfNull(systemAudio);
        using var microphoneStream = new MemoryStream(microphone, writable: false);
        using var systemStream = new MemoryStream(systemAudio, writable: false);
        return NormalizeAndMix(
            microphoneStream,
            microphoneFormat,
            systemStream,
            systemFormat);
    }

    internal static MeetingRecording NormalizeAndMix(
        Stream microphone,
        WaveFormat microphoneFormat,
        Stream systemAudio,
        WaveFormat systemFormat)
    {
        ArgumentNullException.ThrowIfNull(microphone);
        ArgumentNullException.ThrowIfNull(systemAudio);
        var micPath = Path.Combine(
            Path.GetTempPath(),
            $"whispdows-mic-normalized-{Guid.NewGuid():N}.wav");
        var systemPath = Path.Combine(
            Path.GetTempPath(),
            $"whispdows-system-normalized-{Guid.NewGuid():N}.wav");
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"whispdows-meeting-{Guid.NewGuid():N}.wav");
        try
        {
            NormalizeToWave(microphone, microphoneFormat, micPath);
            NormalizeToWave(systemAudio, systemFormat, systemPath);

            using var micReader = new WaveFileReader(micPath);
            using var systemReader = new WaveFileReader(systemPath);
            var micProvider = micReader.ToSampleProvider();
            var systemProvider = systemReader.ToSampleProvider();
            var micBuffer = new float[81920];
            var systemBuffer = new float[81920];
            var mixedBuffer = new float[81920];
            long sampleCount = 0;
            using (var writer = new WaveFileWriter(
                outputPath,
                new WaveFormat(16000, 16, 1)))
            {
                while (true)
                {
                    var micRead = micProvider.Read(
                        micBuffer,
                        0,
                        micBuffer.Length);
                    var systemRead = systemProvider.Read(
                        systemBuffer,
                        0,
                        systemBuffer.Length);
                    var count = Math.Max(micRead, systemRead);
                    if (count == 0)
                    {
                        break;
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var hasMic = index < micRead;
                        var hasSystem = index < systemRead;
                        var value = hasMic && hasSystem
                            ? (micBuffer[index] + systemBuffer[index]) / 2f
                            : hasMic
                                ? micBuffer[index]
                                : systemBuffer[index];
                        mixedBuffer[index] = Math.Clamp(value, -1f, 1f);
                    }

                    writer.WriteSamples(mixedBuffer, 0, count);
                    sampleCount += count;
                }
            }

            return new MeetingRecording(
                outputPath,
                TimeSpan.FromSeconds(sampleCount / 16000d));
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            TryDelete(micPath);
            TryDelete(systemPath);
        }
    }

    private static void NormalizeToWave(
        Stream source,
        WaveFormat sourceFormat,
        string destination)
    {
        if (source.CanSeek)
        {
            source.Position = 0;
        }

        if (source.CanSeek && source.Length == 0)
        {
            using var empty = new WaveFileWriter(
                destination,
                new WaveFormat(16000, 16, 1));
            return;
        }

        using var raw = new RawSourceWaveStream(source, sourceFormat);
        using var resampler = new MediaFoundationResampler(
            raw,
            new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        WaveFileWriter.CreateWaveFile(destination, resampler);
    }

    internal static float[] NormalizeTo16kMono(byte[] bytes, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(format);
        var channels = Math.Max(1, format.Channels);
        var bits = format.BitsPerSample;
        var bytesPerSample = Math.Max(1, bits / 8);
        var frameSize = Math.Max(1, channels * bytesPerSample);
        var frames = bytes.Length / frameSize;
        if (frames == 0) return Array.Empty<float>();
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * frameSize;
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var sampleOffset = offset + channel * bytesPerSample;
                sum += ReadSample(bytes, sampleOffset, bits, format.Encoding);
            }
            mono[frame] = sum / channels;
        }

        if (format.SampleRate == 16000) return mono;
        var outputLength = Math.Max(1, (int)Math.Round(mono.Length * 16000d / format.SampleRate));
        var result = new float[outputLength];
        var scale = (double)(mono.Length - 1) / Math.Max(1, outputLength - 1);
        for (var i = 0; i < outputLength; i++)
        {
            var position = i * scale;
            var lower = (int)Math.Floor(position);
            var upper = Math.Min(lower + 1, mono.Length - 1);
            var fraction = (float)(position - lower);
            result[i] = mono[lower] + (mono[upper] - mono[lower]) * fraction;
        }
        return result;
    }

    private static float ReadSample(byte[] bytes, int offset, int bits, WaveFormatEncoding encoding)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
            return Math.Clamp(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4))), -1f, 1f);
        if (bits == 16) return BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2)) / 32768f;
        if (bits == 32) return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) / 2147483648f;
        return 0f;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private CaptureSession GetSession()
    {
        lock (_sync) return _session ?? throw new InvalidOperationException("Meeting audio capture is not active.");
    }

    private void CompleteSession(CaptureSession session)
    {
        lock (_sync) if (ReferenceEquals(_session, session)) _session = null;
        session.Dispose();
    }

    private static CapturePair CreateCapturePair(AudioSettings settings)
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? microphone = null;
        MMDevice? render = null;
        try
        {
            microphone = string.Equals(settings.DeviceId, "default", StringComparison.OrdinalIgnoreCase)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(settings.DeviceId);
            render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new CapturePair(new WasapiCapture(microphone), new WasapiLoopbackCapture(render), enumerator, microphone, render);
        }
        catch { microphone?.Dispose(); render?.Dispose(); enumerator.Dispose(); throw; }
    }

    internal sealed class CapturePair : IDisposable
    {
        public CapturePair(IWaveIn microphone, IWaveIn systemAudio, IDisposable? owner = null, IDisposable? microphoneDevice = null, IDisposable? systemDevice = null)
        { Microphone = microphone; SystemAudio = systemAudio; Owner = owner; MicrophoneDevice = microphoneDevice; SystemDevice = systemDevice; }
        public IWaveIn Microphone { get; }
        public IWaveIn SystemAudio { get; }
        private IDisposable? Owner { get; }
        private IDisposable? MicrophoneDevice { get; }
        private IDisposable? SystemDevice { get; }
        public void Dispose() { Microphone.Dispose(); SystemAudio.Dispose(); SystemDevice?.Dispose(); MicrophoneDevice?.Dispose(); Owner?.Dispose(); }
    }

    private sealed class CaptureSession : IDisposable
    {
        private readonly CapturePair _pair;
        private readonly string _microphonePath = Path.Combine(Path.GetTempPath(), $"whispdows-mic-{Guid.NewGuid():N}.raw");
        private readonly string _systemAudioPath = Path.Combine(Path.GetTempPath(), $"whispdows-system-{Guid.NewGuid():N}.raw");
        private readonly FileStream _microphone;
        private readonly FileStream _systemAudio;
        private readonly TaskCompletionSource<Exception?> _micStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> _systemStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopRequested;
        private bool _disposed;
        public CaptureSession(CapturePair pair)
        {
            _pair = pair;
            _microphone = new FileStream(_microphonePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            _systemAudio = new FileStream(_systemAudioPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            MicrophoneFormat = pair.Microphone.WaveFormat;
            SystemFormat = pair.SystemAudio.WaveFormat;
            pair.Microphone.DataAvailable += OnMicrophoneData;
            pair.SystemAudio.DataAvailable += OnSystemData;
            pair.Microphone.RecordingStopped += OnMicrophoneStopped;
            pair.SystemAudio.RecordingStopped += OnSystemStopped;
        }
        public WaveFormat MicrophoneFormat { get; }
        public WaveFormat SystemFormat { get; }
        public void Start() { _pair.Microphone.StartRecording(); _pair.SystemAudio.StartRecording(); }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                try { _pair.Microphone.StopRecording(); } catch (Exception ex) { _micStopped.TrySetResult(ex); }
                try { _pair.SystemAudio.StopRecording(); } catch (Exception ex) { _systemStopped.TrySetResult(ex); }
            }
            var results = await Task.WhenAll(_micStopped.Task, _systemStopped.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            var error = results.FirstOrDefault(exception => exception is not null);
            if (error is not null) throw new AudioRecorderException("Meeting audio capture stopped unexpectedly.", error);
        }
        public Stream OpenMicrophoneAudio() => Open(
            _microphone,
            _microphonePath);
        public Stream OpenSystemAudio() => Open(
            _systemAudio,
            _systemAudioPath);
        private static Stream Open(FileStream stream, string path)
        {
            lock (stream)
            {
                stream.Flush(true);
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    81920,
                    FileOptions.SequentialScan);
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pair.Microphone.DataAvailable -= OnMicrophoneData;
            _pair.SystemAudio.DataAvailable -= OnSystemData;
            _pair.Microphone.RecordingStopped -= OnMicrophoneStopped;
            _pair.SystemAudio.RecordingStopped -= OnSystemStopped;
            _pair.Dispose();
            lock (_microphone) _microphone.Dispose();
            lock (_systemAudio) _systemAudio.Dispose();
            TryDelete(_microphonePath); TryDelete(_systemAudioPath);
        }
        private void OnMicrophoneData(object? sender, WaveInEventArgs e)
        {
            lock (_microphone) { if (!_disposed) _microphone.Write(e.Buffer, 0, e.BytesRecorded); }
        }
        private void OnSystemData(object? sender, WaveInEventArgs e)
        {
            lock (_systemAudio) { if (!_disposed) _systemAudio.Write(e.Buffer, 0, e.BytesRecorded); }
        }
        private void OnMicrophoneStopped(object? sender, StoppedEventArgs e) => _micStopped.TrySetResult(e.Exception);
        private void OnSystemStopped(object? sender, StoppedEventArgs e) => _systemStopped.TrySetResult(e.Exception);
        private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
