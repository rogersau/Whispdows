using Microsoft.Win32;

namespace Dictate;

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
            return key?.GetValue(_valueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existingKey?.DeleteValue(_valueName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Windows did not allow access to the per-user startup key.");
        }

        key.SetValue(_valueName, $"\"{_executablePath}\"", RegistryValueKind.String);
    }
}
