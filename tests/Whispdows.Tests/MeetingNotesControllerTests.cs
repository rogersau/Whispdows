using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class MeetingNotesControllerTests
{
    [Fact]
    public async Task Stop_transcribes_generates_and_archives_the_meeting()
    {
        using var fixture = new ControllerFixture();
        var states = new List<MeetingNotesState>();
        fixture.Controller.StateChanged += states.Add;

        fixture.Controller.Start();
        var result = await fixture.Controller.StopAsync();

        Assert.NotNull(result);
        Assert.Equal(MeetingNotesState.Idle, fixture.Controller.State);
        Assert.Equal(1, fixture.Transcriber.Calls);
        Assert.Equal(1, fixture.Generator.Calls);
        Assert.Equal("meeting transcript", fixture.Archive.Transcript);
        Assert.NotNull(fixture.Archive.Notes);
        Assert.Equal(
            [
                MeetingNotesState.Recording,
                MeetingNotesState.Transcribing,
                MeetingNotesState.GeneratingNotes,
                MeetingNotesState.Saving,
                MeetingNotesState.Idle
            ],
            states);
        Assert.False(File.Exists(fixture.Recorder.LastRecordingPath));
    }

    [Fact]
    public async Task Note_generation_failure_preserves_transcript_and_audio()
    {
        using var fixture = new ControllerFixture();
        fixture.Generator.Throw = true;
        string? error = null;
        fixture.Controller.ErrorOccurred += message => error = message;

        fixture.Controller.Start();
        var result = await fixture.Controller.StopAsync();

        Assert.NotNull(result);
        Assert.Equal(MeetingNotesState.Error, fixture.Controller.State);
        Assert.Equal("meeting transcript", fixture.Archive.Transcript);
        Assert.Null(fixture.Archive.Notes);
        Assert.Contains("preserved", error);
    }

    [Fact]
    public async Task Cancel_stops_capture_without_transcribing()
    {
        using var fixture = new ControllerFixture();
        fixture.Controller.Start();

        await fixture.Controller.CancelAsync();

        Assert.Equal(MeetingNotesState.Idle, fixture.Controller.State);
        Assert.Equal(1, fixture.Recorder.CancelCalls);
        Assert.Equal(0, fixture.Transcriber.Calls);
    }

    [Fact]
    public async Task Cancel_during_transcription_waits_for_the_active_operation()
    {
        using var fixture = new ControllerFixture();
        fixture.Transcriber.WaitForCancellation = true;
        fixture.Controller.Start();
        var stopTask = fixture.Controller.StopAsync();
        await fixture.Transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelTask = fixture.Controller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stopTask);
        await cancelTask;
        Assert.Equal(MeetingNotesState.Idle, fixture.Controller.State);
        fixture.Controller.Start();
        await fixture.Controller.CancelAsync();
    }

    private sealed class ControllerFixture : IDisposable
    {
        public ControllerFixture()
        {
            Recorder = new FakeMeetingRecorder();
            Transcriber = new FakeTranscriber();
            Generator = new FakeGenerator();
            Archive = new FakeArchive();
            Controller = new MeetingNotesController(
                Recorder,
                new AudioSettings(),
                Transcriber,
                Generator,
                Archive,
                clock: () => new DateTimeOffset(
                    2026,
                    7,
                    31,
                    9,
                    5,
                    0,
                    TimeSpan.Zero));
        }

        public FakeMeetingRecorder Recorder { get; }

        public FakeTranscriber Transcriber { get; }

        public FakeGenerator Generator { get; }

        public FakeArchive Archive { get; }

        public MeetingNotesController Controller { get; }

        public void Dispose() => Controller.Dispose();
    }

    private sealed class FakeMeetingRecorder : IMeetingAudioRecorder
    {
        public bool IsRecording { get; private set; }

        public int CancelCalls { get; private set; }

        public string LastRecordingPath { get; private set; } = string.Empty;

        public void Start(AudioSettings settings)
        {
            IsRecording = true;
        }

        public Task<MeetingRecording> StopAsync(
            CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            LastRecordingPath = Path.Combine(
                Path.GetTempPath(),
                $"whispdows-controller-test-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(LastRecordingPath, [1, 2, 3, 4]);
            return Task.FromResult(new MeetingRecording(
                LastRecordingPath,
                TimeSpan.FromMinutes(1)));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            IsRecording = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsRecording = false;
        }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public int Calls { get; private set; }

        public bool WaitForCancellation { get; set; }

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ValidateConfiguration()
        {
        }

        public async Task<string> TranscribeAsync(
            Stream wavAudio,
            CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult(true);
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return "meeting transcript";
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeGenerator : IMeetingNotesGenerator
    {
        public int Calls { get; private set; }

        public bool Throw { get; set; }

        public string ProviderName => "fake";

        public void ValidateConfiguration()
        {
        }

        public Task<MeetingNotesContent> GenerateAsync(
            string transcript,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
            {
                throw new MeetingNotesGenerationException("simulated");
            }

            return Task.FromResult(new MeetingNotesContent(
                ["One", "Two", "Three", "Four", "Five"],
                [],
                []));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeArchive : IMeetingNotesArchive
    {
        public string? Transcript { get; private set; }

        public MeetingNotesContent? Notes { get; private set; }

        public Task<MeetingNotesArchiveResult> SaveAsync(
            DateTimeOffset startedAt,
            MeetingRecording recording,
            string? transcript,
            MeetingNotesContent? notes,
            CancellationToken cancellationToken)
        {
            Transcript = transcript;
            Notes = notes;
            using var audio = recording.OpenRead();
            Assert.True(audio.Length > 0);
            return Task.FromResult(new MeetingNotesArchiveResult(
                "meeting.md",
                "meeting.wav"));
        }
    }
}
