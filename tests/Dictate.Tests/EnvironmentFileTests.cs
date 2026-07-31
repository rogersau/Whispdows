using Dictate;
using Xunit;

namespace Dictate.Tests;

public sealed class EnvironmentFileTests
{
    [Fact]
    public void LoadOrCreate_creates_an_empty_user_environment_file()
    {
        using var sandbox = new EnvironmentSandbox();

        var secrets = sandbox.Loader.LoadOrCreate();

        Assert.True(File.Exists(sandbox.Path));
        Assert.Equal(string.Empty, secrets.Get("OPENAI_API_KEY"));
        Assert.Equal(string.Empty, secrets.Get("GROQ_API_KEY"));
    }

    [Fact]
    public void LoadOrCreate_preserves_existing_keys()
    {
        using var sandbox = new EnvironmentSandbox();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sandbox.Path)!);
        File.WriteAllText(
            sandbox.Path,
            "OPENAI_API_KEY='openai-key'\nGROQ_API_KEY=groq=key\n");

        var secrets = sandbox.Loader.LoadOrCreate();

        Assert.Equal("openai-key", secrets.Get("OPENAI_API_KEY"));
        Assert.Equal("groq=key", secrets.Get("GROQ_API_KEY"));
        Assert.Contains("groq=key", File.ReadAllText(sandbox.Path));
    }

    [Fact]
    public void Parse_ignores_blank_lines_and_comments()
    {
        var secrets = EnvironmentFileLoader.Parse(
            "\n# API keys\nOPENAI_API_KEY=\"secret\"\n");

        Assert.Equal("secret", secrets.Get("OPENAI_API_KEY"));
        Assert.Equal(string.Empty, secrets.Get("MISSING"));
    }

    [Fact]
    public void Parse_rejects_duplicate_names_without_exposing_values()
    {
        var exception = Assert.Throws<EnvironmentFileException>(
            () => EnvironmentFileLoader.Parse(
                "OPENAI_API_KEY=first-secret\nOPENAI_API_KEY=second-secret\n"));

        Assert.Contains("duplicate", exception.Message);
        Assert.DoesNotContain("first-secret", exception.Message);
        Assert.DoesNotContain("second-secret", exception.Message);
    }

    private sealed class EnvironmentSandbox : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DictateEnvironmentTests",
            Guid.NewGuid().ToString("N"));

        public EnvironmentSandbox()
        {
            Path = System.IO.Path.Combine(_root, "Dictate", ".env");
            Loader = new EnvironmentFileLoader(Path);
        }

        public string Path { get; }

        public EnvironmentFileLoader Loader { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
