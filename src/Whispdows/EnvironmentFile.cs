using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace Whispdows;

public sealed class ProviderSecrets
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public ProviderSecrets(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, StringComparer.Ordinal));
    }

    public static ProviderSecrets Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    public string Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _values.TryGetValue(name, out var value) ? value : string.Empty;
    }

    public bool Has(string name)
    {
        return !string.IsNullOrWhiteSpace(Get(name));
    }

    public ProviderSecrets WithUpdates(IReadOnlyDictionary<string, string?> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var values = new Dictionary<string, string>(_values, StringComparer.Ordinal);
        foreach (var update in updates)
        {
            if (string.IsNullOrWhiteSpace(update.Key))
            {
                throw new ArgumentException("A secret name must not be empty.", nameof(updates));
            }

            values[update.Key] = update.Value ?? string.Empty;
        }

        return new ProviderSecrets(values);
    }

    internal IReadOnlyDictionary<string, string> CopyValues()
    {
        return new Dictionary<string, string>(_values, StringComparer.Ordinal);
    }
}

public sealed class EnvironmentFileLoader
{
    private const string DefaultContents =
        "OPENAI_API_KEY=" + "\r\n" +
        "GROQ_API_KEY=" + "\r\n" +
        "AZURE_SPEECH_KEY=" + "\r\n";

    private readonly string _path;

    public EnvironmentFileLoader(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public ProviderSecrets LoadOrCreate()
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new EnvironmentFileException("The .env path has no parent directory.");
        Directory.CreateDirectory(directory);

        if (!File.Exists(_path))
        {
            try
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(DefaultContents);
            }
            catch (IOException) when (File.Exists(_path))
            {
                // Another process created it between the existence check and CreateNew.
            }
        }

        return Parse(File.ReadAllText(_path));
    }

    public static ProviderSecrets Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        using var reader = new StringReader(contents);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                throw new EnvironmentFileException(
                    $".env line {lineNumber} must use NAME=value syntax.");
            }

            var name = trimmed[..separator].Trim();
            if (!IsValidName(name))
            {
                throw new EnvironmentFileException(
                    $".env line {lineNumber} contains an invalid variable name.");
            }

            if (!values.TryAdd(name, Unquote(trimmed[(separator + 1)..].Trim())))
            {
                throw new EnvironmentFileException(
                    $".env contains duplicate variable '{name}'.");
            }
        }

        return new ProviderSecrets(values);
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        return name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}

public sealed class EnvironmentFileException : Exception
{
    public EnvironmentFileException(string message)
        : base(message)
    {
    }
}
