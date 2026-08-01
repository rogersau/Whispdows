using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace Whispdows;

/// <summary>
/// Owns the process-wide Foundry Local/Windows ML runtime and its model cache.
/// The runtime is initialized lazily so a configured online fallback can still
/// start when Windows ML, a model catalog, or an execution provider is unavailable.
/// </summary>
public sealed class WindowsMlRuntime : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<ModelCacheKey, Task<IModel>> _models = [];
    private readonly string _appDataDirectory;
    private Task<FoundryLocalManager>? _managerTask;
    private bool _disposed;

    public WindowsMlRuntime(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _appDataDirectory = Path.GetFullPath(appDataDirectory);
    }

    public async Task<IModel> GetModelAsync(
        string alias,
        InferenceDevice device,
        CancellationToken cancellationToken)
    {
        ValidateAlias(alias);
        var cacheKey = new ModelCacheKey(alias.ToLowerInvariant(), device);

        Task<IModel> modelTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_models.TryGetValue(cacheKey, out modelTask!))
            {
                modelTask = LoadModelAsync(alias, device);
                _models[cacheKey] = modelTask;
            }
        }

        try
        {
            return await modelTask.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_sync)
            {
                if (_models.TryGetValue(cacheKey, out var current)
                    && ReferenceEquals(current, modelTask))
                {
                    _models.Remove(cacheKey);
                }
            }

            throw;
        }
    }

    public void Dispose()
    {
        Task<FoundryLocalManager>? managerTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            managerTask = _managerTask;
            _models.Clear();
        }

        if (managerTask is null || !managerTask.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            FoundryLocalManager.Instance.Dispose();
        }
        catch
        {
            // Shutdown must not prevent the tray app from exiting.
        }
    }

    private async Task<IModel> LoadModelAsync(
        string alias,
        InferenceDevice device)
    {
        var manager = await GetManagerAsync(CancellationToken.None);
        try
        {
            var catalog = await manager.GetCatalogAsync();
            var model = await catalog.GetModelAsync(alias);
            if (model is null)
            {
                throw new WindowsMlUnavailableException(
                    $"The Windows ML model '{alias}' is not available in the Foundry Local catalog.");
            }

            var foundryDevice = device switch
            {
                InferenceDevice.Npu => DeviceType.NPU,
                InferenceDevice.Gpu => DeviceType.GPU,
                InferenceDevice.Cpu => DeviceType.CPU,
                _ => DeviceType.Invalid
            };
            var variant = model.Variants.FirstOrDefault(candidate =>
                candidate.Info.Runtime?.DeviceType == foundryDevice);
            if (variant is null)
            {
                var availableDevices = model.Variants
                    .Select(candidate =>
                        candidate.Info.Runtime?.DeviceType.ToString() ?? "Unknown")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase);
                throw new WindowsMlUnavailableException(
                    $"The Windows ML model '{alias}' has no {device.ToProviderSuffix().ToUpperInvariant()} variant. " +
                    $"Available devices: {string.Join(", ", availableDevices)}.");
            }

            await variant.DownloadAsync();
            await variant.LoadAsync();
            return variant;
        }
        catch (WindowsMlUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new WindowsMlUnavailableException(
                $"Windows ML could not load the local model '{alias}'.",
                exception);
        }
    }

    private Task<FoundryLocalManager> GetManagerAsync(CancellationToken cancellationToken)
    {
        Task<FoundryLocalManager> managerTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _managerTask ??= InitializeManagerAsync();
            managerTask = _managerTask;
        }

        return managerTask.WaitAsync(cancellationToken);
    }

    private async Task<FoundryLocalManager> InitializeManagerAsync()
    {
        try
        {
            Directory.CreateDirectory(_appDataDirectory);
            await FoundryLocalManager.CreateAsync(
                new Configuration
                {
                    AppName = "Whispdows",
                    AppDataDir = _appDataDirectory,
                    LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Warning
                },
                NullLogger.Instance);

            var manager = FoundryLocalManager.Instance;
            // This makes hardware-accelerated variants visible to the catalog.
            // Foundry Local returns a result for individual EP failures; the model
            // can still use a registered CPU provider when one is available.
            await manager.DownloadAndRegisterEpsAsync();
            return manager;
        }
        catch (Exception exception)
        {
            throw new WindowsMlUnavailableException(
                "Windows ML could not initialize the local inference runtime.",
                exception);
        }
    }

    private static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException(
                "A Windows ML model alias is required.",
                nameof(alias));
        }

        if (alias.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                "A Windows ML model alias cannot contain control characters.",
                nameof(alias));
        }
    }

    private readonly record struct ModelCacheKey(
        string Alias,
        InferenceDevice Device);
}

public sealed class WindowsMlUnavailableException : Exception
{
    public WindowsMlUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
