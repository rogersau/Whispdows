using System.Text;
using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class SecureSecretsStoreTests
{
    [Fact]
    public void Save_and_load_round_trip_without_writing_plaintext_keys()
    {
        using var sandbox = new SecretsSandbox();
        var original = new ProviderSecrets(new Dictionary<string, string>
        {
            ["OPENAI_API_KEY"] = "openai-secret",
            ["GROQ_API_KEY"] = "groq-secret"
        });
        var store = new SecureSecretsStore(sandbox.SecurePath, sandbox.LegacyPath);

        store.Save(original);
        var loaded = store.LoadOrCreate();

        Assert.Equal("openai-secret", loaded.Get("OPENAI_API_KEY"));
        Assert.Equal("groq-secret", loaded.Get("GROQ_API_KEY"));
        Assert.DoesNotContain(
            "openai-secret",
            Encoding.UTF8.GetString(File.ReadAllBytes(sandbox.SecurePath)));
        Assert.DoesNotContain(
            "groq-secret",
            Encoding.UTF8.GetString(File.ReadAllBytes(sandbox.SecurePath)));
    }

    [Fact]
    public void Load_or_create_migrates_legacy_env_and_clears_plaintext_values()
    {
        using var sandbox = new SecretsSandbox();
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.LegacyPath)!);
        File.WriteAllText(
            sandbox.LegacyPath,
            "OPENAI_API_KEY=openai-secret\nGROQ_API_KEY=groq-secret\n");
        var store = new SecureSecretsStore(sandbox.SecurePath, sandbox.LegacyPath);

        var loaded = store.LoadOrCreate();

        Assert.Equal("openai-secret", loaded.Get("OPENAI_API_KEY"));
        Assert.Equal("groq-secret", loaded.Get("GROQ_API_KEY"));
        var legacyContents = File.ReadAllText(sandbox.LegacyPath);
        Assert.DoesNotContain("openai-secret", legacyContents);
        Assert.DoesNotContain("groq-secret", legacyContents);
        Assert.Contains("migrated", legacyContents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void New_non_empty_env_values_are_imported_into_an_existing_store()
    {
        using var sandbox = new SecretsSandbox();
        var store = new SecureSecretsStore(
            sandbox.SecurePath,
            sandbox.LegacyPath);
        store.Save(new ProviderSecrets(new Dictionary<string, string>
        {
            ["GROQ_API_KEY"] = "existing-groq"
        }));
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.LegacyPath)!);
        File.WriteAllText(
            sandbox.LegacyPath,
            "OPENAI_API_KEY=new-openai\nGROQ_API_KEY=\n");

        var loaded = store.LoadOrCreate();

        Assert.Equal("new-openai", loaded.Get("OPENAI_API_KEY"));
        Assert.Equal("existing-groq", loaded.Get("GROQ_API_KEY"));
        Assert.DoesNotContain(
            "new-openai",
            File.ReadAllText(sandbox.LegacyPath));
    }

    private sealed class SecretsSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WhispdowsSecureSecretsTests",
            Guid.NewGuid().ToString("N"));

        public string SecurePath => Path.Combine(_root, "Whispdows", "secrets.dat");

        public string LegacyPath => Path.Combine(_root, "Whispdows", ".env");

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
