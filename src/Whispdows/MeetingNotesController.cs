using System.Diagnostics;

namespace Whispdows;

public enum MeetingNotesState
{
    Idle,
    Recording,
    Transcribing,
    GeneratingNotes,
    Saving,
    Error
}

public sealed class MeetingNotesController : IDisposable
{
    private static readonly TimeSpan MinimumRecordingDuration =
        TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly IMeetingAudioRecorder _recorder;
    private readonly AudioSettings _audioSettings;
    private readonly ITranscriber _transcriber;
    private readonly IMeetingNotesGenerator _notesGenerator;
    private readonly IMeetingNotesArchive _archive;
    private readonly IAppLogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _lifetime = new();

    private DateTimeOffset _startedAt;
    private MeetingNotesState _state = MeetingNotesState.Idle;
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource<bool>? _operationCompletion;
    private int _resourcesDisposed;
    private bool _disposed;

    public MeetingNotesController(
        IMeetingAudioRecorder recorder,
        AudioSettings audioSettings,
        ITranscriber transcriber,
        IMeetingNotesGenerator notesGenerator,
        IMeetingNotesArchive archive,
        IAppLogger? logger = null,
        Func<DateTimeOffset>? clock = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _audioSettings = audioSettings ?? throw new ArgumentNullException(nameof(audioSettings));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _notesGenerator = notesGenerator
            ?? throw new ArgumentNullException(nameof(notesGenerator));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _logger = logger ?? NullAppLogger.Instance;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public event Action<MeetingNotesState>? StateChanged;

    public event Action<MeetingNotesArchiveResult>? Completed;

    public event Action<string>? ErrorOccurred;

    public MeetingNotesState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public void ValidateConfiguration()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        _transcriber.ValidateConfiguration();
        _notesGenerator.ValidateConfiguration();
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not (MeetingNotesState.Idle or MeetingNotesState.Error))
            {
                throw new InvalidOperationException(
                    "A meeting recording or notes operation is already active.");
            }

            ValidateConfiguration();
            try
            {
                var startedAt = _clock();
                _recorder.Start(_audioSettings);
                _operationCancellation = new CancellationTokenSource();
                _operationCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _startedAt = startedAt;
                SetStateLocked(MeetingNotesState.Recording);
            }
            catch (Exception exception)
            {
                _logger.LogException("meeting-recording-start", exception);
                SetStateLocked(MeetingNotesState.Error);
                throw;
            }
        }
    }

    public async Task<MeetingNotesArchiveResult?> StopAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset startedAt;
        CancellationToken operationToken;
        CancellationTokenSource operationCancellation;
        TaskCompletionSource<bool> operationCompletion;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != MeetingNotesState.Recording)
            {
                throw new InvalidOperationException(
                    "Meeting audio capture is not active.");
            }

            operationCancellation = _operationCancellation
                ?? throw new InvalidOperationException(
                    "The meeting operation is not initialized.");
            operationCompletion = _operationCompletion
                ?? throw new InvalidOperationException(
                    "The meeting operation is not initialized.");
            operationToken = operationCancellation.Token;
            startedAt = _startedAt;
            SetStateLocked(MeetingNotesState.Transcribing);
        }

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token,
                operationToken);
        var token = linkedCancellation.Token;

        MeetingRecording? recording = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            recording = await _recorder.StopAsync(token).ConfigureAwait(false);
            _logger.LogDuration("meeting-recording", "wasapi-mixed", stopwatch.Elapsed);

            if (recording.Duration < MinimumRecordingDuration)
            {
                SetState(MeetingNotesState.Idle);
                return null;
            }

            string transcript;
            stopwatch.Restart();
            try
            {
                await using var audio = recording.OpenRead();
                transcript = await _transcriber
                    .TranscribeAsync(audio, token)
                    .ConfigureAwait(false);
                _logger.LogDuration(
                    "meeting-transcription",
                    ProviderName(_transcriber),
                    stopwatch.Elapsed);
                if (string.IsNullOrWhiteSpace(transcript)
                    || string.Equals(
                        transcript.Trim(),
                        "[BLANK_AUDIO]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "No speech was detected in the meeting recording.");
                }
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                _logger.LogException("meeting-transcription", exception);
                var recovery = await SaveRecoveryAsync(
                    startedAt,
                    recording,
                    transcript: null,
                    token).ConfigureAwait(false);
                SetError(
                    "Meeting transcription failed. The audio was preserved at " +
                    recovery.AudioPath);
                return recovery;
            }

            SetState(MeetingNotesState.GeneratingNotes);
            MeetingNotesContent notes;
            stopwatch.Restart();
            try
            {
                notes = await _notesGenerator
                    .GenerateAsync(transcript, token)
                    .ConfigureAwait(false);
                _logger.LogDuration(
                    "meeting-notes",
                    _notesGenerator.ProviderName,
                    stopwatch.Elapsed);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                _logger.LogException("meeting-notes-generation", exception);
                var recovery = await SaveRecoveryAsync(
                    startedAt,
                    recording,
                    transcript,
                    token).ConfigureAwait(false);
                SetError(
                    "Meeting note generation failed. The transcript and audio were " +
                    "preserved at " + recovery.MarkdownPath);
                return recovery;
            }

            SetState(MeetingNotesState.Saving);
            stopwatch.Restart();
            var result = await _archive.SaveAsync(
                startedAt,
                recording,
                transcript,
                notes,
                token).ConfigureAwait(false);
            _logger.LogDuration("meeting-save", "local-filesystem", stopwatch.Elapsed);
            SetState(MeetingNotesState.Idle);
            Completed?.Invoke(result);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(MeetingNotesState.Idle);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogException("meeting-stop", exception);
            SetError($"Meeting notes failed: {exception.Message}");
            return null;
        }
        finally
        {
            recording?.Dispose();
            CompleteOperation(operationCancellation, operationCompletion);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        MeetingNotesState state;
        CancellationTokenSource? operationCancellation;
        TaskCompletionSource<bool>? operationCompletion;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            state = _state;
            operationCancellation = _operationCancellation;
            operationCompletion = _operationCompletion;
            operationCancellation?.Cancel();
        }

        if (state == MeetingNotesState.Recording)
        {
            try
            {
                await _recorder
                    .CancelAsync(cancellationToken)
                    .ConfigureAwait(false);
                SetState(MeetingNotesState.Idle);
            }
            finally
            {
                if (operationCancellation is not null
                    && operationCompletion is not null)
                {
                    CompleteOperation(
                        operationCancellation,
                        operationCompletion);
                }
            }

            return;
        }

        if ((state is MeetingNotesState.Transcribing
                or MeetingNotesState.GeneratingNotes
                or MeetingNotesState.Saving)
            && operationCompletion is not null)
        {
            await operationCompletion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        MeetingNotesState state;
        CancellationTokenSource? operationCancellation;
        TaskCompletionSource<bool>? operationCompletion;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            state = _state;
            operationCancellation = _operationCancellation;
            operationCompletion = _operationCompletion;
            _lifetime.Cancel();
            operationCancellation?.Cancel();
        }

        if (state == MeetingNotesState.Recording && _recorder.IsRecording)
        {
            try
            {
                _recorder.CancelAsync(CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            if (operationCancellation is not null
                && operationCompletion is not null)
            {
                CompleteOperation(
                    operationCancellation,
                    operationCompletion);
            }
        }

        if (operationCompletion is not null
            && !operationCompletion.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            _ = operationCompletion.Task.ContinueWith(
                _ => DisposeOwnedResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        DisposeOwnedResources();
    }

    private void DisposeOwnedResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _recorder.Dispose();
        _transcriber.Dispose();
        _notesGenerator.Dispose();
        _lifetime.Dispose();
    }

    private async Task<MeetingNotesArchiveResult> SaveRecoveryAsync(
        DateTimeOffset startedAt,
        MeetingRecording recording,
        string? transcript,
        CancellationToken cancellationToken)
    {
        SetState(MeetingNotesState.Saving);
        return await _archive.SaveAsync(
            startedAt,
            recording,
            transcript,
            notes: null,
            cancellationToken).ConfigureAwait(false);
    }

    private void CompleteOperation(
        CancellationTokenSource operationCancellation,
        TaskCompletionSource<bool> operationCompletion)
    {
        lock (_sync)
        {
            if (ReferenceEquals(
                    _operationCancellation,
                    operationCancellation))
            {
                _operationCancellation = null;
            }

            if (ReferenceEquals(_operationCompletion, operationCompletion))
            {
                _operationCompletion = null;
            }
        }

        operationCompletion.TrySetResult(true);
        operationCancellation.Dispose();
    }

    private void SetError(string message)
    {
        SetState(MeetingNotesState.Error);
        ErrorOccurred?.Invoke(message);
    }

    private void SetState(MeetingNotesState state)
    {
        lock (_sync)
        {
            SetStateLocked(state);
        }
    }

    private void SetStateLocked(MeetingNotesState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }

    private static string ProviderName(ITranscriber transcriber)
    {
        return (transcriber as IProviderComponent)?.ProviderName ?? "custom";
    }
}
