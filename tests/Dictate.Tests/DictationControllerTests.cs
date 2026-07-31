using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class DictationControllerTests
{
    [Fact]
    public async Task Trigger_press_starts_one_recording_and_shows_the_pill()
    {
        using var fixture = new ControllerFixture();
        fixture.Controller.Enable();

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);
        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        Assert.Equal(DictationState.Recording, fixture.Controller.State);
        Assert.Equal(1, fixture.Recorder.StartCount);
        Assert.Equal(1, fixture.Pill.ShowCount);
        Assert.Equal(new IntPtr(1234), fixture.Pill.TargetWindow);
        Assert.Contains(PillState.Listening, fixture.Pill.States);
    }

    [Fact]
    public async Task Trigger_release_stops_and_discards_the_captured_audio()
    {
        using var fixture = new ControllerFixture();
        fixture.Controller.Enable();
        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerReleased);

        Assert.Equal(DictationState.Idle, fixture.Controller.State);
        Assert.Equal(1, fixture.Recorder.StopCount);
        Assert.Contains(PillState.Transcribing, fixture.Pill.States);
        Assert.Contains(PillState.Cleaning, fixture.Pill.States);
        Assert.Contains(PillState.Pasted, fixture.Pill.States);
        Assert.Equal(1, fixture.Transcriber.CallCount);
        Assert.Equal(1, fixture.Cleaner.CallCount);
        Assert.Equal(1, fixture.Inserter.CallCount);
        Assert.NotNull(fixture.Recorder.LastRecording);
        Assert.Throws<ObjectDisposedException>(() => fixture.Recorder.LastRecording!.WavBytes);
    }

    [Fact]
    public async Task Escape_cancels_without_creating_a_recording_result()
    {
        using var fixture = new ControllerFixture();
        fixture.Controller.Enable();
        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.Cancelled);

        Assert.Equal(DictationState.Idle, fixture.Controller.State);
        Assert.Equal(1, fixture.Recorder.CancelCount);
        Assert.Equal(0, fixture.Recorder.StopCount);
        Assert.True(fixture.Pill.Hidden);
    }

    [Fact]
    public async Task Disabling_while_recording_cancels_and_enters_disabled_state()
    {
        using var fixture = new ControllerFixture();
        fixture.Controller.Enable();
        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        await fixture.Controller.DisableAsync();

        Assert.Equal(DictationState.Disabled, fixture.Controller.State);
        Assert.Equal(1, fixture.Recorder.CancelCount);
        Assert.True(fixture.Pill.Hidden);
    }

    [Fact]
    public async Task Disabled_controller_ignores_the_hotkey()
    {
        using var fixture = new ControllerFixture();

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        Assert.Equal(DictationState.Disabled, fixture.Controller.State);
        Assert.Equal(0, fixture.Recorder.StartCount);
    }

    [Fact]
    public async Task Microphone_start_failure_enters_error_and_shows_a_concise_message()
    {
        using var fixture = new ControllerFixture();
        fixture.Recorder.ThrowOnStart = true;
        fixture.Controller.Enable();

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        Assert.Equal(DictationState.Error, fixture.Controller.State);
        Assert.Contains(PillState.Error, fixture.Pill.States);
        Assert.Equal("Microphone unavailable", fixture.Pill.LastErrorMessage);
    }

    [Fact]
    public async Task Hotkey_press_is_ignored_while_audio_is_being_processed()
    {
        using var fixture = new ControllerFixture();
        fixture.Recorder.DelayStop = true;
        fixture.Controller.Enable();
        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        var releaseTask = fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerReleased);
        Assert.Equal(1, fixture.Recorder.StopCount);

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);
        fixture.Recorder.CompleteStop();
        await releaseTask;

        Assert.Equal(1, fixture.Recorder.StartCount);
        Assert.Equal(DictationState.Idle, fixture.Controller.State);
    }

    [Fact]
    public async Task Short_recording_is_discarded_before_transcription()
    {
        using var fixture = new ControllerFixture();
        fixture.Recorder.NextDuration = TimeSpan.FromMilliseconds(100);
        fixture.Controller.Enable();
        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerPressed);

        await fixture.Controller.HandleHotkeyEventAsync(HotkeyEvent.TriggerReleased);

        Assert.Equal(0, fixture.Transcriber.CallCount);
        Assert.Equal(0, fixture.Inserter.CallCount);
        Assert.Contains(PillState.NoSpeechDetected, fixture.Pill.States);
    }

    private sealed class ControllerFixture : IDisposable
    {
        public ControllerFixture()
        {
            Recorder = new FakeAudioRecorder();
            Pill = new FakeRecordingPill();
            Transcriber = new FakeTranscriber();
            Cleaner = new FakeTextCleaner();
            Inserter = new FakeTextInserter();
            Controller = new DictationController(
                Recorder,
                Pill,
                () => new IntPtr(1234),
                new AudioSettings
                {
                    DeviceId = "default",
                    MaxSeconds = 3600
                },
                new DictationPipeline(Transcriber, Cleaner, Inserter));
        }

        public FakeAudioRecorder Recorder { get; }

        public FakeRecordingPill Pill { get; }

        public FakeTranscriber Transcriber { get; }

        public FakeTextCleaner Cleaner { get; }

        public FakeTextInserter Inserter { get; }

        public DictationController Controller { get; }

        public void Dispose()
        {
            Controller.Dispose();
        }
    }

    private sealed class FakeAudioRecorder : IAudioRecorder
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int CancelCount { get; private set; }

        public bool IsRecording { get; private set; }

        public RecordedAudio? LastRecording { get; private set; }

        public bool ThrowOnStart { get; set; }

        public bool DelayStop { get; set; }

        public TimeSpan NextDuration { get; set; } = TimeSpan.FromSeconds(1);

        private TaskCompletionSource<bool> StopCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start(AudioSettings settings)
        {
            if (ThrowOnStart)
            {
                throw new AudioRecorderException(
                    "The microphone is unavailable.",
                    new InvalidOperationException());
            }

            StartCount++;
            IsRecording = true;
        }

        public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            IsRecording = false;
            if (DelayStop)
            {
                await StopCompletion.Task.WaitAsync(cancellationToken);
            }

            LastRecording = new RecordedAudio([1, 2, 3, 4], NextDuration);
            return LastRecording;
        }

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            CancelCount++;
            IsRecording = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsRecording = false;
        }

        public void CompleteStop()
        {
            StopCompletion.TrySetResult(true);
        }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public int CallCount { get; private set; }

        public void ValidateConfiguration()
        {
        }

        public Task<string> TranscribeAsync(
            Stream wavAudio,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult("raw transcript");
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeTextCleaner : ITextCleaner
    {
        public int CallCount { get; private set; }

        public Task<string> CleanAsync(
            string transcript,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult("Clean transcript.");
        }
    }

    private sealed class FakeTextInserter : ITextInserter
    {
        public int CallCount { get; private set; }

        public Task<TextInsertionResult> InsertAsync(
            string text,
            nint targetWindow,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.Equal("Clean transcript.", text);
            Assert.Equal(new IntPtr(1234), targetWindow);
            return Task.FromResult(TextInsertionResult.Pasted);
        }
    }

    private sealed class FakeRecordingPill : IRecordingPill
    {
        public List<PillState> States { get; } = [];

        public int ShowCount { get; private set; }

        public nint TargetWindow { get; private set; }

        public bool Hidden { get; private set; }

        public string? LastErrorMessage { get; private set; }

        public void SetState(PillState state, string? errorMessage = null)
        {
            States.Add(state);
            LastErrorMessage = errorMessage;
        }

        public void ShowForTargetWindow(nint targetWindow)
        {
            ShowCount++;
            TargetWindow = targetWindow;
            Hidden = false;
        }

        public void HidePill()
        {
            Hidden = true;
        }
    }
}
