using NAudio.Wave;
using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class MeetingAudioRecorderTests
{
    [Fact]
    public void Normalization_downmixes_and_resamples_to_16khz()
    {
        var source = new byte[4 * 2 * 2];
        for (var i = 0; i < 4; i++)
        {
            BitConverter.GetBytes((short)(i * 4000)).CopyTo(source, i * 4);
            BitConverter.GetBytes((short)(i * 4000)).CopyTo(source, i * 4 + 2);
        }
        var samples = MeetingAudioRecorder.NormalizeTo16kMono(source, new WaveFormat(8000, 16, 2));
        Assert.Equal(8, samples.Length);
        Assert.Equal(0, samples[0]);
        Assert.InRange(samples[^1], 0.36f, 0.38f);
    }

    [Fact]
    public void Mixing_clips_and_writes_a_mono_wave()
    {
        var loud = new byte[4];
        BitConverter.GetBytes(short.MaxValue).CopyTo(loud, 0);
        BitConverter.GetBytes(short.MaxValue).CopyTo(loud, 2);
        string path;
        using (var result = MeetingAudioRecorder.NormalizeAndMix(loud, new WaveFormat(16000, 16, 1), loud, new WaveFormat(16000, 16, 1)))
        {
            path = result.FilePath;
            using var reader = new WaveFileReader(result.OpenRead());
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.InRange(reader.ReadNextSampleFrame()[0], 0.99f, 1f);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Lifecycle_rejects_duplicate_start_and_stop_without_recording()
    {
        using var recorder = new MeetingAudioRecorder(_ =>
            new MeetingAudioRecorder.CapturePair(new FakeWaveIn(), new FakeWaveIn()));
        recorder.Start(new AudioSettings());
        Assert.True(recorder.IsRecording);
        Assert.Throws<InvalidOperationException>(() => recorder.Start(new AudioSettings()));
        await recorder.CancelAsync();
        Assert.False(recorder.IsRecording);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync());
    }

    private sealed class FakeWaveIn : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() => DataAvailable?.Invoke(this, new WaveInEventArgs(Array.Empty<byte>(), 0));
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
        public void Dispose() { }
    }
}
