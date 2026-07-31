using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class RecordedAudioTests
{
    [Fact]
    public void Dispose_clears_and_releases_the_audio_buffer()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var recording = new RecordedAudio(bytes, TimeSpan.FromSeconds(1));

        recording.Dispose();

        Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => recording.WavBytes);
    }
}
