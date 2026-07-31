using System.IO;
using System.Text;
using NAudio.Wave;

namespace Whispdows;

public sealed class ChunkingTranscriber : ITranscriber, IProviderComponent
{
    private readonly ITranscriber _inner;
    private readonly TimeSpan _chunkDuration;
    private bool _disposed;

    public ChunkingTranscriber(
        ITranscriber inner,
        TimeSpan chunkDuration)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (chunkDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkDuration));
        }

        _chunkDuration = chunkDuration;
    }

    public string ProviderName =>
        (_inner as IProviderComponent)?.ProviderName ?? "cloud";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.ValidateConfiguration();
    }

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);

        await using var seekable = await EnsureSeekableAsync(
            wavAudio,
            cancellationToken);
        using var reader = new WaveFileReader(
            new NonDisposingStream(seekable.Stream));
        var blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
        var requestedBytes = checked((long)Math.Ceiling(
            reader.WaveFormat.AverageBytesPerSecond
            * _chunkDuration.TotalSeconds));
        var chunkBytes = Math.Max(
            blockAlign,
            requestedBytes - (requestedBytes % blockAlign));

        var transcript = new StringBuilder();
        while (reader.Position < reader.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var chunk = ReadChunk(
                reader,
                chunkBytes,
                cancellationToken);
            if (chunk.Length <= 44)
            {
                break;
            }

            var text = await _inner.TranscribeAsync(chunk, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (transcript.Length > 0)
            {
                transcript.AppendLine();
            }

            transcript.Append(text.Trim());
        }

        return transcript.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
    }

    private static MemoryStream ReadChunk(
        WaveFileReader reader,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        using (var writer = new WaveFileWriter(
            new NonDisposingStream(output),
            reader.WaveFormat))
        {
            var buffer = new byte[81920];
            long remaining = maximumBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = reader.Read(buffer, 0, requested);
                if (read == 0)
                {
                    break;
                }

                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        output.Position = 0;
        return output;
    }

    private static async Task<TemporarySeekableStream> EnsureSeekableAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            source.Position = 0;
            return TemporarySeekableStream.Borrow(source);
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            $"whispdows-transcription-{Guid.NewGuid():N}.wav");
        var file = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous
                | FileOptions.SequentialScan
                | FileOptions.DeleteOnClose);
        try
        {
            await source.CopyToAsync(file, cancellationToken);
            file.Position = 0;
            return TemporarySeekableStream.Own(file);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private sealed class TemporarySeekableStream : IAsyncDisposable
    {
        private readonly bool _ownsStream;

        private TemporarySeekableStream(Stream stream, bool ownsStream)
        {
            Stream = stream;
            _ownsStream = ownsStream;
        }

        public Stream Stream { get; }

        public static TemporarySeekableStream Borrow(Stream stream) =>
            new(stream, ownsStream: false);

        public static TemporarySeekableStream Own(Stream stream) =>
            new(stream, ownsStream: true);

        public async ValueTask DisposeAsync()
        {
            if (_ownsStream)
            {
                await Stream.DisposeAsync();
            }
        }
    }

    private sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;

        public NonDisposingStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            // The caller owns the wrapped stream.
        }
    }
}
