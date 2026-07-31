using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class AppLoggerTests
{
    [Fact]
    public void Exception_logging_records_metadata_without_the_message()
    {
        using var sandbox = new LogSandbox();
        using (var logger = new RollingFileLogger(sandbox.Path))
        {
            logger.LogException(
                "transcription",
                new InvalidOperationException(
                    "raw transcript and OPENAI_API_KEY=top-secret"));
        }

        var contents = ReadAllLogs(sandbox.Path);
        Assert.Contains("event=exception", contents);
        Assert.Contains("InvalidOperationException", contents);
        Assert.DoesNotContain("raw transcript", contents);
        Assert.DoesNotContain("top-secret", contents);
    }

    [Fact]
    public void Logger_keeps_only_the_configured_number_of_small_files()
    {
        using var sandbox = new LogSandbox();
        using (var logger = new RollingFileLogger(
            sandbox.Path,
            maximumFiles: 3,
            maximumBytes: 256))
        {
            for (var index = 0; index < 30; index++)
            {
                logger.LogDuration(
                    "transcription",
                    new string('p', 128),
                    TimeSpan.FromMilliseconds(index));
            }
        }

        var files = Directory.GetFiles(sandbox.Path, "dictate-*.log");
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
    }

    private static string ReadAllLogs(string directory)
    {
        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(directory, "dictate-*.log")
                .Select(File.ReadAllText));
    }

    private sealed class LogSandbox : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DictateLogTests",
            Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(_root, "logs");

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
