using Whispdows;
using Xunit;

namespace Whispdows.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void Installer_is_per_user_x64_and_preserves_user_configuration()
    {
        var installer = File.ReadAllText(RepositoryFile("installer", "Whispdows.iss"));

        Assert.Contains("DefaultDirName={localappdata}\\Programs\\Whispdows", installer);
        Assert.Contains("PrivilegesRequired=lowest", installer);
        Assert.Contains("ArchitecturesAllowed=x64compatible and not arm64", installer);
        Assert.Contains("MinVersion=10.0.22000", installer);
        Assert.Contains("onlyifdoesntexist uninsneveruninstall", installer);
        Assert.Contains("RemoveUserDataOnUninstall", installer);
        Assert.Contains("Parameters: \"--enable-startup\"", installer);
        Assert.Contains("RegQueryStringValue", installer);
        Assert.DoesNotContain("StringChangeEx", installer);
        Assert.DoesNotContain("PrivilegesRequired=admin", installer);
        Assert.DoesNotContain("[Services]", installer);
    }

    [Fact]
    public void Installer_offers_a_safe_opt_in_ollama_install_only_when_missing()
    {
        var installer = File.ReadAllText(RepositoryFile("installer", "Whispdows.iss"));

        Assert.Contains("Name: \"ollama\"", installer);
        Assert.Contains("Flags: unchecked; Check: ShouldOfferOllamaInstall", installer);
        Assert.Contains("{localappdata}\\Programs\\Ollama\\ollama.exe", installer);
        Assert.Contains("FileSearch('ollama.exe', GetEnv('PATH'))", installer);
        Assert.Contains("Result := not IsOllamaInstalled", installer);
        Assert.Contains("WizardIsTaskSelected('ollama')", installer);
        Assert.Contains("install --id Ollama.Ollama --exact --source winget --scope user --silent", installer);
        Assert.Contains("--accept-package-agreements --accept-source-agreements", installer);
        Assert.Contains("ResultCode <> 0", installer);
        Assert.DoesNotContain("ollama pull", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[UninstallRun]", installer);
    }

    [Fact]
    public void Release_build_is_self_contained_and_checks_the_model_and_native_runtime()
    {
        var buildScript = File.ReadAllText(
            RepositoryFile("scripts", "Build-Release.ps1"));

        Assert.Contains("--self-contained true", buildScript);
        Assert.Contains("-r $RuntimeIdentifier", buildScript);
        Assert.Contains("RuntimeIdentifier", buildScript);
        Assert.Contains("win-arm64", buildScript);
        Assert.Contains("PublishSingleFile=false", buildScript);
        Assert.Contains("PublishTrimmed=false", buildScript);
        Assert.Contains("db8a495a91d927739e50b3fc1cc4c6b8f6c2d022", buildScript);
        Assert.Contains("runtimes\\$RuntimeIdentifier", buildScript);
        Assert.Contains("'whisper.dll'", buildScript);
        Assert.Contains("'Microsoft.AI.Foundry.Local.Core.dll'", buildScript);
        Assert.Contains("'onnxruntime-genai.dll'", buildScript);
        Assert.Contains("0x8664", buildScript);
        Assert.Contains("0xAA64", buildScript);
        Assert.Contains("unexpected runtime directories", buildScript);
    }

    [Fact]
    public void Readme_documents_permissions_privacy_and_elevation_limits()
    {
        var readme = File.ReadAllText(RepositoryFile("README.md"));

        Assert.Contains("Privacy & security", readme);
        Assert.Contains("Let desktop apps access your microphone", readme);
        Assert.Contains("global low-level keyboard hook", readme);
        Assert.Contains("running as administrator", readme);
        Assert.Contains("no audio or transcript is sent over the network", readme);
        Assert.Contains("API keys", readme);
        Assert.Contains("SmartScreen", readme);
    }

    [Fact]
    public void Application_and_tray_icons_are_packaged_as_project_assets()
    {
        var project = File.ReadAllText(
            RepositoryFile("src", "Whispdows", "Whispdows.csproj"));
        var trayMenu = File.ReadAllText(
            RepositoryFile("src", "Whispdows", "TrayMenu.cs"));

        Assert.Contains(
            "<ApplicationIcon>Assets\\whispdows.ico</ApplicationIcon>",
            project);
        Assert.DoesNotContain("SystemIcons.Application", trayMenu);

        var resources = typeof(TrayMenu).Assembly.GetManifestResourceNames();
        Assert.Contains("Whispdows.Assets.whispdows-tray-enabled.ico", resources);
        Assert.Contains("Whispdows.Assets.whispdows-tray-disabled.ico", resources);
        Assert.Contains("Whispdows.Assets.whispdows-tray-listening.ico", resources);
        Assert.Contains("Whispdows.Assets.whispdows-tray-processing.ico", resources);
        Assert.Contains("Whispdows.Assets.whispdows-tray-error.ico", resources);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "windows-ai-dictation-design.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
