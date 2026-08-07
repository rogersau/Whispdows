using NAudio.Wave;
using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class ChunkingTranscriberTests
{
    [Fact]
    public async Task Meeting_audio_is_sent_to_cloud_in_bounded_chunks()
    {
        var inner = new RecordingTranscriber();
        using var chunking = new ChunkingTranscriber(
            inner,
            TimeSpan.FromSeconds(1));
        using var audio = CreateWave(TimeSpan.FromMilliseconds(2500));

        var transcript = await chunking.TranscribeAsync(
            audio,
            CancellationToken.None);

        Assert.Equal(3, inner.Calls);
        Assert.All(inner.AudioLengths, length => Assert.InRange(length, 44, 33000));
        Assert.Equal("part 1" + Environment.NewLine + "part 2" + Environment.NewLine + "part 3", transcript);
    }

    private static MemoryStream CreateWave(TimeSpan duration)
    {
        var output = new MemoryStream();
        using (var writer = new WaveFileWriter(
            new NonClosingStream(output),
            new WaveFormat(16000, 16, 1)))
        {
            writer.Write(new byte[(int)(32000 * duration.TotalSeconds)]);
        }

        output.Position = 0;
        return output;
    }

    private sealed class RecordingTranscriber : ITranscriber
    {
        public int Calls { get; private set; }

        public List<long> AudioLengths { get; } = [];

        public void ValidateConfiguration()
        {
        }

        public Task<string> TranscribeAsync(
            Stream wavAudio,
            CancellationToken cancellationToken)
        {
            Calls++;
            AudioLengths.Add(wavAudio.Length);
            return Task.FromResult($"part {Calls}");
        }

        public void Dispose()
        {
        }
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
        }
    }
}
