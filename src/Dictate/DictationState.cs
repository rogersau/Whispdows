namespace Dictate;

public enum DictationState
{
    Disabled,
    Idle,
    Recording,
    Transcribing,
    Cleaning,
    Pasting,
    Error
}

public interface IRecordingPill
{
    void SetState(PillState state, string? errorMessage = null);

    void ShowForTargetWindow(nint targetWindow);

    void HidePill();
}

public sealed class DictationController : IDisposable
{
    private static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ResultDisplayDuration = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ErrorDisplayDuration = TimeSpan.FromSeconds(2);

    private readonly IAudioRecorder _audioRecorder;
    private readonly IRecordingPill _pill;
    private readonly Func<nint> _getForegroundWindow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private AudioSettings _audioSettings;
    private CancellationTokenSource? _maximumDuration;
    private int _sessionIdentifier;
    private bool _enabled;
    private bool _disposed;

    public DictationController(
        IAudioRecorder audioRecorder,
        IRecordingPill pill,
        Func<nint> getForegroundWindow,
        AudioSettings audioSettings)
    {
        _audioRecorder = audioRecorder;
        _pill = pill;
        _getForegroundWindow = getForegroundWindow;
        _audioSettings = CopyAudioSettings(audioSettings);
        State = DictationState.Disabled;
    }

    public event Action<DictationState>? StateChanged;

    public DictationState State { get; private set; }

    public bool IsRecording => State == DictationState.Recording;

    public void Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enabled = true;
        if (State == DictationState.Disabled)
        {
            TransitionTo(DictationState.Idle);
        }
    }

    public async Task DisableAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enabled = false;

        await _gate.WaitAsync();
        try
        {
            if (State == DictationState.Recording)
            {
                await CancelRecordingLockedAsync();
            }

            if (State != DictationState.Disabled)
            {
                TransitionTo(DictationState.Disabled);
            }

            _pill.HidePill();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateAudioSettings(AudioSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audioSettings = CopyAudioSettings(settings);
    }

    public async Task HandleHotkeyEventAsync(HotkeyEvent hotkeyEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            switch (hotkeyEvent)
            {
                case HotkeyEvent.TriggerPressed:
                    await BeginRecordingLockedAsync();
                    break;
                case HotkeyEvent.TriggerReleased:
                    await CompleteRecordingLockedAsync();
                    break;
                case HotkeyEvent.Cancelled:
                    await CancelRecordingLockedAsync();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(hotkeyEvent), hotkeyEvent, null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        _shutdown.Cancel();
        CancelMaximumDuration();
        _audioRecorder.Dispose();
        _pill.HidePill();
        _shutdown.Dispose();
    }

    private async Task BeginRecordingLockedAsync()
    {
        if (!_enabled || State != DictationState.Idle)
        {
            return;
        }

        var sessionIdentifier = ++_sessionIdentifier;
        var targetWindow = _getForegroundWindow();

        try
        {
            _audioRecorder.Start(_audioSettings);
            TransitionTo(DictationState.Recording);
            _pill.SetState(PillState.Listening);
            _pill.ShowForTargetWindow(targetWindow);

            _maximumDuration = new CancellationTokenSource();
            _ = StopAtMaximumDurationAsync(
                sessionIdentifier,
                TimeSpan.FromSeconds(_audioSettings.MaxSeconds),
                _maximumDuration.Token);
        }
        catch (Exception)
        {
            if (_audioRecorder.IsRecording)
            {
                try
                {
                    await _audioRecorder.CancelAsync(_shutdown.Token);
                }
                catch
                {
                    // The original microphone-start failure is more useful.
                }
            }

            EnterErrorLocked("Microphone unavailable", sessionIdentifier);
        }
    }

    private async Task CompleteRecordingLockedAsync()
    {
        if (State != DictationState.Recording)
        {
            return;
        }

        var sessionIdentifier = _sessionIdentifier;
        CancelMaximumDuration();
        TransitionTo(DictationState.Transcribing);
        _pill.SetState(PillState.Transcribing);

        try
        {
            using var recording = await _audioRecorder.StopAsync(_shutdown.Token);
            if (recording.Duration < MinimumRecordingDuration)
            {
                _pill.SetState(PillState.NoSpeechDetected);
            }

            TransitionTo(_enabled ? DictationState.Idle : DictationState.Disabled);
            SchedulePillHide(sessionIdentifier, ResultDisplayDuration);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            TransitionTo(DictationState.Disabled);
            _pill.HidePill();
        }
        catch (Exception)
        {
            EnterErrorLocked("Microphone unavailable", sessionIdentifier);
        }
    }

    private async Task CancelRecordingLockedAsync()
    {
        if (State != DictationState.Recording)
        {
            return;
        }

        CancelMaximumDuration();
        try
        {
            await _audioRecorder.CancelAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            TransitionTo(_enabled ? DictationState.Idle : DictationState.Disabled);
            _pill.HidePill();
        }
    }

    private async Task StopAtMaximumDurationAsync(
        int sessionIdentifier,
        TimeSpan maximumDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(maximumDuration, cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (sessionIdentifier == _sessionIdentifier
                    && State == DictationState.Recording)
                {
                    await CompleteRecordingLockedAsync();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnterErrorLocked(string message, int sessionIdentifier)
    {
        CancelMaximumDuration();
        TransitionTo(DictationState.Error);
        _pill.SetState(PillState.Error, message);
        ScheduleErrorReset(sessionIdentifier);
    }

    private void SchedulePillHide(int sessionIdentifier, TimeSpan delay)
    {
        _ = HidePillAfterDelayAsync(sessionIdentifier, delay);
    }

    private async Task HidePillAfterDelayAsync(int sessionIdentifier, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _shutdown.Token);
            await _gate.WaitAsync(_shutdown.Token);
            try
            {
                if (sessionIdentifier == _sessionIdentifier
                    && State is DictationState.Idle or DictationState.Disabled)
                {
                    _pill.HidePill();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScheduleErrorReset(int sessionIdentifier)
    {
        _ = ResetErrorAfterDelayAsync(sessionIdentifier);
    }

    private async Task ResetErrorAfterDelayAsync(int sessionIdentifier)
    {
        try
        {
            await Task.Delay(ErrorDisplayDuration, _shutdown.Token);
            await _gate.WaitAsync(_shutdown.Token);
            try
            {
                if (sessionIdentifier == _sessionIdentifier
                    && State == DictationState.Error)
                {
                    TransitionTo(_enabled ? DictationState.Idle : DictationState.Disabled);
                    _pill.HidePill();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelMaximumDuration()
    {
        var cancellation = _maximumDuration;
        _maximumDuration = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void TransitionTo(DictationState next)
    {
        if (State == next)
        {
            return;
        }

        if (!IsAllowedTransition(State, next))
        {
            throw new InvalidOperationException($"Invalid dictation state transition: {State} -> {next}.");
        }

        State = next;
        StateChanged?.Invoke(State);
    }

    private static bool IsAllowedTransition(DictationState current, DictationState next)
    {
        return (current, next) switch
        {
            (DictationState.Disabled, DictationState.Idle) => true,
            (DictationState.Idle, DictationState.Disabled or DictationState.Recording or DictationState.Error) => true,
            (DictationState.Recording, DictationState.Idle or DictationState.Disabled or DictationState.Transcribing or DictationState.Error) => true,
            (DictationState.Transcribing, DictationState.Idle or DictationState.Disabled or DictationState.Cleaning or DictationState.Error) => true,
            (DictationState.Cleaning, DictationState.Idle or DictationState.Disabled or DictationState.Pasting or DictationState.Error) => true,
            (DictationState.Pasting, DictationState.Idle or DictationState.Disabled or DictationState.Error) => true,
            (DictationState.Error, DictationState.Idle or DictationState.Disabled) => true,
            _ => false
        };
    }

    private static AudioSettings CopyAudioSettings(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AudioSettings
        {
            DeviceId = settings.DeviceId,
            MaxSeconds = settings.MaxSeconds
        };
    }
}
