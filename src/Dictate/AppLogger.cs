using System.Globalization;
using System.IO;
using System.Text;

namespace Dictate;

public interface IProviderComponent
{
    string ProviderName { get; }
}

public interface IAppLogger : IDisposable
{
    void LogState(DictationState state);

    void LogDuration(string operation, string provider, TimeSpan duration);

    void LogException(string context, Exception exception);
}

public sealed class NullAppLogger : IAppLogger
{
    public static NullAppLogger Instance { get; } = new();

    private NullAppLogger()
    {
    }

    public void LogState(DictationState state)
    {
    }

    public void LogDuration(string operation, string provider, TimeSpan duration)
    {
    }

    public void LogException(string context, Exception exception)
    {
    }

    public void Dispose()
    {
    }
}

public sealed class SafeAppLogger : IAppLogger
{
    private readonly IAppLogger _inner;
    private readonly bool _disposeInner;
    private int _disposed;

    public SafeAppLogger(IAppLogger inner, bool disposeInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _disposeInner = disposeInner;
    }

    public void LogState(DictationState state)
    {
        Try(() => _inner.LogState(state));
    }

    public void LogDuration(string operation, string provider, TimeSpan duration)
    {
        Try(() => _inner.LogDuration(operation, provider, duration));
    }

    public void LogException(string context, Exception exception)
    {
        Try(() => _inner.LogException(context, exception));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_disposeInner)
        {
            return;
        }

        try
        {
            _inner.Dispose();
        }
        catch
        {
            // Diagnostics are best effort, including during shutdown.
        }
    }

    private void Try(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            action();
        }
        catch
        {
            // Diagnostics are best effort and must never affect dictation.
        }
    }
}

public sealed class RollingFileLogger : IAppLogger
{
    private const int DefaultMaximumFiles = 5;
    private const long DefaultMaximumBytes = 256 * 1024;

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _maximumFiles;
    private readonly long _maximumBytes;
    private StreamWriter? _writer;
    private int _fileSequence;
    private bool _disposed;

    public RollingFileLogger(
        string directory,
        int maximumFiles = DefaultMaximumFiles,
        long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        }

        if (maximumBytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _directory = Path.GetFullPath(directory);
        _maximumFiles = maximumFiles;
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_directory);
        OpenNextFile();
    }

    public void LogState(DictationState state)
    {
        Write($"event=state state={state}");
    }

    public void LogDuration(
        string operation,
        string provider,
        TimeSpan duration)
    {
        Write(
            $"event=duration operation={Sanitize(operation)} " +
            $"provider={Sanitize(provider)} milliseconds={Math.Max(0, duration.TotalMilliseconds).ToString("F0", CultureInfo.InvariantCulture)}");
    }

    public void LogException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(
            $"event=exception context={Sanitize(context)} " +
            $"type={Sanitize(exception.GetType().FullName ?? exception.GetType().Name)} " +
            $"hresult={exception.HResult.ToString(CultureInfo.InvariantCulture)}");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string fields)
    {
        lock (_sync)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                var line = $"{DateTimeOffset.UtcNow:O} {fields}";
                var byteCount = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                if (_writer.BaseStream.Length + byteCount > _maximumBytes)
                {
                    OpenNextFile();
                }

                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch
            {
                DisableLocked();
            }
        }
    }

    private void OpenNextFile()
    {
        _writer?.Dispose();
        _writer = null;

        Directory.CreateDirectory(_directory);
        string path;
        do
        {
            path = Path.Combine(
                _directory,
                $"dictate-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{_fileSequence++:D3}.log");
        }
        while (File.Exists(path));

        _writer = new StreamWriter(
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        var files = Directory
            .EnumerateFiles(_directory, "dictate-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_maximumFiles)
            .ToArray();

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // A prior process may still have its newest log open.
            }
            catch (UnauthorizedAccessException)
            {
                // Logging must never prevent dictation from running.
            }
        }
    }

    private static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sanitized = value
            .Replace('\r', '_')
            .Replace('\n', '_')
            .Replace(' ', '_');
        return sanitized.Length <= 128 ? sanitized : sanitized[..128];
    }

    private void DisableLocked()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // There is no recovery path if the log destination is unusable.
        }

        _writer = null;
    }
}
