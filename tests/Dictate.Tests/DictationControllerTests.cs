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

    private sealed class ControllerFixture : IDisposable
    {
        public ControllerFixture()
        {
            Recorder = new FakeAudioRecorder();
            Pill = new FakeRecordingPill();
            Controller = new DictationController(
                Recorder,
                Pill,
                () => new IntPtr(1234),
                new AudioSettings
                {
                    DeviceId = "default",
                    MaxSeconds = 3600
                });
        }

        public FakeAudioRecorder Recorder { get; }

        public FakeRecordingPill Pill { get; }

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

            LastRecording = new RecordedAudio([1, 2, 3, 4], TimeSpan.FromSeconds(1));
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
