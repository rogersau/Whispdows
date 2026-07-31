using Dictate;
using NAudio.Wave;
using Xunit;

namespace Dictate.Tests;

public sealed class AudioRecorderTests
{
    [Fact]
    public void Conversion_produces_mono_16khz_16bit_pcm_wave()
    {
        var sourceFormat = new WaveFormat(48000, 16, 2);
        var duration = TimeSpan.FromMilliseconds(100);
        var rawAudio = new byte[sourceFormat.AverageBytesPerSecond / 10];

        using var recording = AudioRecorder.ConvertToTranscriptionWave(
            rawAudio,
            sourceFormat,
            duration);
        using var waveStream = new MemoryStream(recording.WavBytes, writable: false);
        using var reader = new WaveFileReader(waveStream);

        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(duration, recording.Duration);
        Assert.InRange(reader.TotalTime.TotalMilliseconds, 90, 110);
    }
}
