using System.Text.RegularExpressions;

namespace Whispdows;

public interface ITextCleaner
{
    Task<string> CleanAsync(string transcript, CancellationToken cancellationToken);
}

public sealed class BasicTextCleaner : ITextCleaner, IProviderComponent
{
    private static readonly Regex RepeatedWhitespace =
        new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedFiller =
        new(@"\b(um|uh|erm)(?:[\s,]+\1\b)+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LeadingFiller =
        new(@"^(?:um|uh|erm)\b(?:[\s,.:;–—-]+|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _style;

    public BasicTextCleaner(string style)
    {
        if (style is not ("auto" or "sentence" or "fragment"))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown cleanup style.");
        }

        _style = style;
    }

    public string ProviderName => "basic";

    public Task<string> CleanAsync(string transcript, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transcript);

        var cleaned = RepeatedWhitespace.Replace(transcript.Trim(), " ");
        cleaned = RepeatedFiller.Replace(cleaned, "$1");
        cleaned = LeadingFiller.Replace(cleaned, string.Empty).Trim();
        cleaned = RepeatedWhitespace.Replace(cleaned, " ");

        if (_style == "sentence" && cleaned.Length > 0)
        {
            cleaned = CapitalizeFirstLetter(cleaned);
            if (!EndsWithSentencePunctuation(cleaned))
            {
                cleaned = AddFinalPunctuation(cleaned);
            }
        }

        return Task.FromResult(cleaned);
    }

    private static string CapitalizeFirstLetter(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsLetter(value[index]))
            {
                continue;
            }

            var uppercase = char.ToUpperInvariant(value[index]);
            return uppercase == value[index]
                ? value
                : value[..index] + uppercase + value[(index + 1)..];
        }

        return value;
    }

    private static bool EndsWithSentencePunctuation(string value)
    {
        for (var index = value.Length - 1; index >= 0; index--)
        {
            var character = value[index];
            if (character is '"' or '\'' or '’' or '”' or ')' or ']' or '}')
            {
                continue;
            }

            return character is '.' or '!' or '?' or '…';
        }

        return false;
    }

    private static string AddFinalPunctuation(string value)
    {
        var insertionIndex = value.Length;
        while (insertionIndex > 0
            && value[insertionIndex - 1] is '"' or '\'' or '’' or '”' or ')' or ']' or '}')
        {
            insertionIndex--;
        }

        return value.Insert(insertionIndex, ".");
    }
}

public sealed class NoOpTextCleaner : ITextCleaner, IProviderComponent
{
    public string ProviderName => "none";

    public Task<string> CleanAsync(string transcript, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(transcript);
    }
}
