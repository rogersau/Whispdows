using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whispdows;

public sealed class SecureSecretsStore
{
    private const string LegacyMigrationMarker =
        "# API keys migrated to Whispdows secure storage. Use Settings to change them.\r\n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly string _legacyPath;

    public SecureSecretsStore(string path, string legacyPath)
    {
        _path = Path.GetFullPath(path);
        _legacyPath = Path.GetFullPath(legacyPath);
    }

    public ProviderSecrets LoadOrCreate()
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new SecureSecretsException("The secure secrets path has no parent directory.");
        Directory.CreateDirectory(directory);

        ProviderSecrets secrets;
        if (File.Exists(_path))
        {
            secrets = LoadEncrypted();
        }
        else if (File.Exists(_legacyPath))
        {
            try
            {
                secrets = EnvironmentFileLoader.Parse(File.ReadAllText(_legacyPath));
                Save(secrets);
            }
            catch (EnvironmentFileException exception)
            {
                throw new SecureSecretsException(
                    "The legacy .env file could not be imported.",
                    exception);
            }
        }
        else
        {
            secrets = ProviderSecrets.Empty;
            Save(secrets);
        }

        if (File.Exists(_legacyPath))
        {
            ClearLegacyFile();
        }

        return secrets;
    }

    public void Save(ProviderSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var directory = Path.GetDirectoryName(_path)
            ?? throw new SecureSecretsException("The secure secrets path has no parent directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(secrets.CopyValues(), JsonOptions);
        var protectedBytes = WindowsDataProtection.Protect(
            Encoding.UTF8.GetBytes(json));
        var temporaryFile = _path + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryFile, protectedBytes);
            File.Move(temporaryFile, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecureSecretsException(
                "The encrypted API-key store could not be saved.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private ProviderSecrets LoadEncrypted()
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecureSecretsException(
                "The encrypted API-key store could not be read.",
                exception);
        }

        byte[] jsonBytes;
        try
        {
            jsonBytes = WindowsDataProtection.Unprotect(protectedBytes);
        }
        catch (Exception exception) when (exception is CryptographicException or Win32Exception)
        {
            throw new SecureSecretsException(
                "The encrypted API-key store could not be unlocked for this Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                jsonBytes,
                JsonOptions);
            if (values is null)
            {
                throw new SecureSecretsException("The encrypted API-key store is empty.");
            }

            return new ProviderSecrets(values);
        }
        catch (JsonException exception)
        {
            throw new SecureSecretsException(
                "The encrypted API-key store is invalid.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    private void ClearLegacyFile()
    {
        var temporaryFile = _legacyPath + ".migrating";
        try
        {
            File.WriteAllText(temporaryFile, LegacyMigrationMarker, new UTF8Encoding(false));
            File.Move(temporaryFile, _legacyPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecureSecretsException(
                "The legacy .env file still contains plaintext key material and could not be cleared.",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }
}

public sealed class SecureSecretsException : Exception
{
    public SecureSecretsException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class WindowsDataProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data)
    {
        return Transform(data, protect: true);
    }

    public static byte[] Unprotect(byte[] data)
    {
        return Transform(data, protect: false);
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            throw new CryptographicException("The data to protect must not be empty.");
        }

        var input = new DataBlob
        {
            Length = data.Length,
            Data = Marshal.AllocHGlobal(data.Length)
        };
        var output = new DataBlob();
        Marshal.Copy(data, 0, input.Data, data.Length);

        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref input,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output);

            if (!succeeded)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    protect
                        ? "Windows could not encrypt the API keys."
                        : "Windows could not decrypt the API keys.");
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
