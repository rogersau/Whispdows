using System.IO;
using Microsoft.Win32;

namespace Whispdows;

public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly string _executablePath;

    public StartupRegistration(string valueName, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(valueName))
        {
            throw new ArgumentException("A startup value name is required.", nameof(valueName));
        }

        _valueName = valueName;
        _executablePath = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unavailable.");
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) is string value
                && string.Equals(value, ExpectedValue, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            RemoveCurrentRegistration();
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Windows did not allow access to the per-user startup key.");
        }

        key.SetValue(_valueName, ExpectedValue, RegistryValueKind.String);
    }

    private string ExpectedValue => $"\"{Path.GetFullPath(_executablePath)}\"";

    private void RemoveCurrentRegistration()
    {
        using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (existingKey?.GetValue(_valueName) is string value
            && string.Equals(value, ExpectedValue, StringComparison.OrdinalIgnoreCase))
        {
            existingKey.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }

}

public static class StartupConfiguration
{
    public const string EnableCommand = "--enable-startup";

    public static bool IsEnableCommand(IReadOnlyList<string> arguments)
    {
        return arguments.Count == 1
            && string.Equals(
                arguments[0],
                EnableCommand,
                StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable(
        SettingsLoader settingsLoader,
        IStartupRegistration startupRegistration)
    {
        ArgumentNullException.ThrowIfNull(settingsLoader);
        ArgumentNullException.ThrowIfNull(startupRegistration);

        var settings = settingsLoader.LoadOrCreate();
        settings.LaunchAtLogin = true;
        settingsLoader.Save(settings);
        startupRegistration.SetEnabled(true);
    }
}
