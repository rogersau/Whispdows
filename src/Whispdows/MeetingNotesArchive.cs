using System.IO;
using System.Text;

namespace Whispdows;

public sealed record MeetingNotesArchiveResult(
    string MarkdownPath,
    string AudioPath);

public interface IMeetingNotesArchive
{
    Task<MeetingNotesArchiveResult> SaveAsync(
        DateTimeOffset startedAt,
        MeetingRecording recording,
        string? transcript,
        MeetingNotesContent? notes,
        CancellationToken cancellationToken);
}

public sealed class MeetingNotesArchive : IMeetingNotesArchive
{
    private readonly string _outputDirectory;

    public MeetingNotesArchive(string configuredDirectory)
    {
        _outputDirectory = ResolveOutputDirectory(configuredDirectory);
    }

    public async Task<MeetingNotesArchiveResult> SaveAsync(
        DateTimeOffset startedAt,
        MeetingRecording recording,
        string? transcript,
        MeetingNotesContent? notes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        Directory.CreateDirectory(_outputDirectory);

        var reservation = ReservePaths(startedAt);
        var paths = reservation.Paths;
        var audioTemporary = paths.AudioPath + ".tmp";
        var markdownTemporary = paths.MarkdownPath + ".tmp";

        try
        {
            await using (var source = recording.OpenRead())
            await using (var destination = new FileStream(
                audioTemporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(audioTemporary, paths.AudioPath);

            var markdown = RenderMarkdown(startedAt, transcript, notes);
            await File.WriteAllTextAsync(
                markdownTemporary,
                markdown,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(markdownTemporary, paths.MarkdownPath);
            return paths;
        }
        finally
        {
            TryDelete(audioTemporary);
            TryDelete(markdownTemporary);
            TryDelete(reservation.MarkerPath);
        }
    }

    public static string ResolveOutputDirectory(string configuredDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredDirectory);
        var trimmed = configuredDirectory.Trim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmed == "~")
        {
            return Path.GetFullPath(home);
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Combine(
                home,
                trimmed[2..].Replace('/', Path.DirectorySeparatorChar)));
        }

        return Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(trimmed));
    }

    internal static string RenderMarkdown(
        DateTimeOffset startedAt,
        string? transcript,
        MeetingNotesContent? notes)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine($"# Meeting Notes — {startedAt:yyyy-MM-dd HH:mm}");
        markdown.AppendLine();

        if (notes is null)
        {
            markdown.AppendLine("## Summary");
            markdown.AppendLine();
            markdown.AppendLine(
                string.IsNullOrWhiteSpace(transcript)
                    ? "_Transcription did not complete. The audio recording was preserved._"
                    : "_Automatic notes could not be generated. The transcript and audio were preserved._");
        }
        else
        {
            markdown.AppendLine("## Summary");
            markdown.AppendLine();
            foreach (var bullet in notes.Summary)
            {
                markdown.AppendLine($"- {OneLine(bullet)}");
            }

            markdown.AppendLine();
            markdown.AppendLine("## Decisions made");
            markdown.AppendLine();
            if (notes.Decisions.Count == 0)
            {
                markdown.AppendLine("- None recorded.");
            }
            else
            {
                foreach (var decision in notes.Decisions)
                {
                    markdown.AppendLine($"- {OneLine(decision)}");
                }
            }

            markdown.AppendLine();
            markdown.AppendLine("## Action items");
            markdown.AppendLine();
            if (notes.ActionItems.Count == 0)
            {
                markdown.AppendLine("- None recorded.");
            }
            else
            {
                foreach (var action in notes.ActionItems)
                {
                    markdown.AppendLine(
                        $"- [ ] **{OneLine(action.Owner)}** — {OneLine(action.Task)}");
                }
            }
        }

        markdown.AppendLine();
        markdown.AppendLine("---");
        markdown.AppendLine();
        markdown.AppendLine("## Full transcript");
        markdown.AppendLine();
        markdown.AppendLine(
            string.IsNullOrWhiteSpace(transcript)
                ? "_Transcript unavailable._"
                : transcript.Trim());
        return markdown.ToString();
    }

    private PathReservation ReservePaths(DateTimeOffset startedAt)
    {
        var baseName = startedAt.ToString("yyyy-MM-dd-HHmm");
        var suffix = 1;
        while (true)
        {
            var name = suffix == 1 ? baseName : $"{baseName}-{suffix:D2}";
            var markdownPath = Path.Combine(_outputDirectory, name + ".md");
            var audioPath = Path.Combine(_outputDirectory, name + ".wav");
            var markerPath = Path.Combine(
                _outputDirectory,
                "." + name + ".reserve");
            if (File.Exists(markdownPath)
                || File.Exists(audioPath)
                || File.Exists(markdownPath + ".tmp")
                || File.Exists(audioPath + ".tmp"))
            {
                suffix++;
                continue;
            }

            try
            {
                using (new FileStream(
                    markerPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                }

                if (!File.Exists(markdownPath) && !File.Exists(audioPath))
                {
                    return new PathReservation(
                        new MeetingNotesArchiveResult(
                            markdownPath,
                            audioPath),
                        markerPath);
                }

                TryDelete(markerPath);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
            }

            suffix++;
        }
    }

    private static string OneLine(string value)
    {
        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PathReservation(
        MeetingNotesArchiveResult Paths,
        string MarkerPath);
}
