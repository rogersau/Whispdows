using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class MeetingNotesArchiveTests
{
    [Fact]
    public async Task Save_writes_summary_transcript_and_audio_side_by_side()
    {
        using var sandbox = new ArchiveSandbox();
        using var recording = sandbox.CreateRecording([1, 2, 3, 4]);
        var archive = new MeetingNotesArchive(sandbox.OutputDirectory);
        var notes = new MeetingNotesContent(
            ["One", "Two", "Three", "Four", "Five"],
            ["Use the local-first design"],
            [new MeetingActionItem("Sam", "Write the README")]);

        var result = await archive.SaveAsync(
            new DateTimeOffset(2026, 7, 31, 9, 5, 0, TimeSpan.FromHours(10)),
            recording,
            "Full spoken transcript.",
            notes,
            CancellationToken.None);

        Assert.Equal("2026-07-31-0905.md", Path.GetFileName(result.MarkdownPath));
        Assert.Equal("2026-07-31-0905.wav", Path.GetFileName(result.AudioPath));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(result.AudioPath));
        var markdown = File.ReadAllText(result.MarkdownPath);
        Assert.Contains("## Summary", markdown);
        var summarySection = markdown[
            markdown.IndexOf("## Summary", StringComparison.Ordinal)..markdown.IndexOf("## Decisions made", StringComparison.Ordinal)];
        Assert.Equal(
            5,
            summarySection.Split('\n').Count(line => line.StartsWith("- ")));
        Assert.Contains("## Decisions made", markdown);
        Assert.Contains("**Sam** — Write the README", markdown);
        Assert.Contains("---", markdown);
        Assert.Contains("## Full transcript", markdown);
        Assert.EndsWith("Full spoken transcript." + Environment.NewLine, markdown);
    }

    [Fact]
    public async Task Save_uses_a_suffix_instead_of_overwriting_a_meeting()
    {
        using var sandbox = new ArchiveSandbox();
        var archive = new MeetingNotesArchive(sandbox.OutputDirectory);
        var started = new DateTimeOffset(2026, 7, 31, 9, 5, 0, TimeSpan.Zero);
        File.WriteAllText(
            Path.Combine(sandbox.OutputDirectory, "2026-07-31-0905.md"),
            "existing");

        using var recording = sandbox.CreateRecording([8, 9]);
        var result = await archive.SaveAsync(
            started,
            recording,
            transcript: null,
            notes: null,
            CancellationToken.None);

        Assert.Equal("2026-07-31-0905-02.md", Path.GetFileName(result.MarkdownPath));
        Assert.Equal("existing", File.ReadAllText(
            Path.Combine(sandbox.OutputDirectory, "2026-07-31-0905.md")));
        Assert.Contains("audio recording was preserved", File.ReadAllText(result.MarkdownPath));
    }

    [Fact]
    public async Task Concurrent_archives_reserve_distinct_file_names()
    {
        using var sandbox = new ArchiveSandbox();
        var started = new DateTimeOffset(2026, 7, 31, 9, 5, 0, TimeSpan.Zero);
        using var firstRecording = sandbox.CreateRecording([1]);
        using var secondRecording = sandbox.CreateRecording([2]);
        var firstArchive = new MeetingNotesArchive(sandbox.OutputDirectory);
        var secondArchive = new MeetingNotesArchive(sandbox.OutputDirectory);

        var results = await Task.WhenAll(
            firstArchive.SaveAsync(
                started,
                firstRecording,
                null,
                null,
                CancellationToken.None),
            secondArchive.SaveAsync(
                started,
                secondRecording,
                null,
                null,
                CancellationToken.None));

        Assert.Equal(
            2,
            results.Select(result => result.MarkdownPath).Distinct().Count());
        Assert.All(results, result => Assert.True(File.Exists(result.AudioPath)));
    }

    private sealed class ArchiveSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WhispdowsMeetingArchiveTests",
            Guid.NewGuid().ToString("N"));

        public ArchiveSandbox()
        {
            OutputDirectory = Path.Combine(_root, "MeetingNotes");
            Directory.CreateDirectory(OutputDirectory);
        }

        public string OutputDirectory { get; }

        public MeetingRecording CreateRecording(byte[] bytes)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(path, bytes);
            return new MeetingRecording(path, TimeSpan.FromMinutes(1));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
