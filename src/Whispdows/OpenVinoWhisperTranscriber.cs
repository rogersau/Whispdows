using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Whispdows;

/// <summary>
/// Runs the current OpenVINO GenAI Whisper pipeline in a dedicated process.
/// Native runtime or driver failures therefore become normal provider fallback
/// instead of terminating the tray application.
/// </summary>
public sealed class OpenVinoWhisperTranscriber :
    ITranscriber,
    IProviderComponent,
    IInferenceWarmup
{
    private static readonly string[] RequiredModelFiles =
    [
        "config.json",
        "generation_config.json",
        "openvino_decoder_model.bin",
        "openvino_decoder_model.xml",
        "openvino_encoder_model.bin",
        "openvino_encoder_model.xml",
        "openvino_tokenizer.bin",
        "openvino_tokenizer.xml"
    ];

    private readonly string _modelDirectory;
    private readonly string _language;
    private readonly string _openVinoDevice;
    private readonly string _cacheDirectory;
    private readonly string _workerPath;
    private readonly object _warmupSync = new();
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _warmupTask;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _worker;
    private bool _disposed;

    public OpenVinoWhisperTranscriber(
        string modelDirectory,
        string language,
        InferenceDevice device,
        string cacheRoot)
    {
        if (device is not InferenceDevice.Npu and not InferenceDevice.Gpu)
        {
            throw new ArgumentOutOfRangeException(
                nameof(device),
                device,
                "The OpenVINO GenAI Whisper tier supports NPU or GPU devices.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _modelDirectory = Path.GetFullPath(modelDirectory);
        _language = language;
        _openVinoDevice = device.ToProviderSuffix().ToUpperInvariant();
        _cacheDirectory = Path.Combine(
            Path.GetFullPath(cacheRoot),
            Path.GetFileName(_modelDirectory),
            device.ToProviderSuffix());
        _workerPath = Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            "openvino-genai",
            "Whispdows.InferenceWorker.exe");
    }

    public string ProviderName =>
        $"openvino-genai-{_openVinoDevice.ToLowerInvariant()}";

    public void ValidateConfiguration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            throw new OnDeviceInferenceUnavailableException(
                "The bundled OpenVINO GenAI Whisper model is English-only; " +
                "using the next inference tier for this language.");
        }

        if (!Directory.Exists(_modelDirectory))
        {
            throw new OnDeviceInferenceUnavailableException(
                $"The OpenVINO GenAI Whisper model is missing at '{_modelDirectory}'. " +
                "Run scripts\\Get-WhisperModel.ps1 to install the pinned NPU model.");
        }

        var missingModelFile = RequiredModelFiles.FirstOrDefault(
            file => !File.Exists(Path.Combine(_modelDirectory, file)));
        if (missingModelFile is not null)
        {
            throw new OnDeviceInferenceUnavailableException(
                $"The OpenVINO GenAI Whisper model is incomplete; '{missingModelFile}' is missing.");
        }

        if (!File.Exists(_workerPath))
        {
            throw new OnDeviceInferenceUnavailableException(
                $"The OpenVINO GenAI inference worker is missing at '{_workerPath}'.");
        }

        var runtimeDirectory = Path.GetDirectoryName(_workerPath)!;
        foreach (var runtimeFile in new[]
        {
            "openvino_genai_c.dll",
            "openvino_genai.dll",
            "openvino.dll",
            _openVinoDevice == "NPU"
                ? "openvino_intel_npu_plugin.dll"
                : "openvino_intel_gpu_plugin.dll"
        })
        {
            if (!File.Exists(Path.Combine(runtimeDirectory, runtimeFile)))
            {
                throw new OnDeviceInferenceUnavailableException(
                    $"The OpenVINO GenAI runtime is incomplete; '{runtimeFile}' is missing.");
            }
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken) =>
        EnsureWarmupStarted().WaitAsync(cancellationToken);

    public async Task<string> TranscribeAsync(
        Stream wavAudio,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavAudio);

        var warmup = EnsureWarmupStarted();
        if (!warmup.IsCompleted)
        {
            throw new OnDeviceInferenceUnavailableException(
                $"{ProviderName} is still warming up; using the next inference tier.");
        }

        try
        {
            await warmup.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnDeviceInferenceUnavailableException(
                $"{ProviderName} initialization was canceled.");
        }

        var wavPath = Path.Combine(
            Path.GetTempPath(),
            $"whispdows-openvino-{Guid.NewGuid():N}.wav");
        byte[]? wavBytes = null;
        try
        {
            using var buffer = new MemoryStream();
            if (wavAudio.CanSeek)
            {
                wavAudio.Position = 0;
            }

            await wavAudio.CopyToAsync(buffer, cancellationToken);
            wavBytes = buffer.ToArray();
            await File.WriteAllBytesAsync(wavPath, wavBytes, cancellationToken);
            var response = await ExecuteRequestAsync(
                new WorkerRequest("transcribe", wavPath),
                cancellationToken);
            return response.Text?.Trim() ?? string.Empty;
        }
        finally
        {
            if (wavBytes is not null)
            {
                Array.Clear(wavBytes);
            }

            try
            {
                File.Delete(wavPath);
            }
            catch
            {
                // Best-effort cleanup of temporary microphone audio.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        DisposeWorker();
    }

    private Task EnsureWarmupStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_warmupSync)
        {
            return _warmupTask ??= Task.Run(async () =>
            {
                await ExecuteRequestAsync(
                    new WorkerRequest("warmup", null),
                    _lifetime.Token);
            });
        }
    }

    private async Task<WorkerResponse> ExecuteRequestAsync(
        WorkerRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var token = linkedCancellation.Token;

        await _processingGate.WaitAsync(token);
        try
        {
            ValidateConfiguration();
            await EnsureWorkerAsync(token);
            try
            {
                await _writer!.WriteLineAsync(
                    JsonSerializer.Serialize(request).AsMemory(),
                    token);
                var responseJson = await _reader!.ReadLineAsync(token);
                if (responseJson is null)
                {
                    throw WorkerExitedException();
                }

                var response = JsonSerializer.Deserialize<WorkerResponse>(responseJson)
                    ?? throw new InvalidDataException(
                        "The OpenVINO GenAI worker returned an empty response.");
                if (!response.Ok)
                {
                    throw new OnDeviceInferenceUnavailableException(
                        $"OpenVINO GenAI Whisper failed on {_openVinoDevice} " +
                        $"({response.ErrorType ?? "unknown error"}).");
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OnDeviceInferenceUnavailableException)
            {
                DisposeWorker();
                throw;
            }
            catch (Exception exception)
            {
                DisposeWorker();
                throw new OnDeviceInferenceUnavailableException(
                    $"The OpenVINO GenAI worker failed on {_openVinoDevice}.",
                    exception);
            }
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false } && _pipe?.IsConnected == true)
        {
            return;
        }

        DisposeWorker();
        Directory.CreateDirectory(_cacheDirectory);
        var pipeName = $"whispdows-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var startInfo = new ProcessStartInfo
        {
            FileName = _workerPath,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        AddWorkerArgument(startInfo, "--pipe", pipeName);
        AddWorkerArgument(startInfo, "--model", _modelDirectory);
        AddWorkerArgument(startInfo, "--cache", _cacheDirectory);
        AddWorkerArgument(startInfo, "--device", _openVinoDevice);
        AddWorkerArgument(startInfo, "--language", _language);

        var worker = Process.Start(startInfo)
            ?? throw new OnDeviceInferenceUnavailableException(
                "Windows could not start the OpenVINO GenAI inference worker.");
        _pipe = pipe;
        _worker = worker;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var connectTask = pipe.WaitForConnectionAsync(timeout.Token);
            var exitTask = worker.WaitForExitAsync(timeout.Token);
            var completed = await Task.WhenAny(connectTask, exitTask);
            if (completed == exitTask)
            {
                throw WorkerExitedException();
            }

            await connectTask;
            _reader = new StreamReader(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            _writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DisposeWorker();
            throw new OnDeviceInferenceUnavailableException(
                "The OpenVINO GenAI inference worker did not start within 30 seconds.");
        }
        catch
        {
            DisposeWorker();
            throw;
        }
    }

    private OnDeviceInferenceUnavailableException WorkerExitedException()
    {
        var exitCode = _worker is { HasExited: true }
            ? _worker.ExitCode.ToString()
            : "unknown";
        return new OnDeviceInferenceUnavailableException(
            $"The OpenVINO GenAI worker exited unexpectedly on {_openVinoDevice} " +
            $"(exit code {exitCode}).");
    }

    private void DisposeWorker()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;

        if (_worker is not null)
        {
            try
            {
                if (!_worker.HasExited)
                {
                    _worker.Kill(entireProcessTree: true);
                    _worker.WaitForExit(2000);
                }
            }
            catch
            {
                // Best-effort cleanup during fallback or shutdown.
            }

            _worker.Dispose();
            _worker = null;
        }
    }

    private static void AddWorkerArgument(
        ProcessStartInfo startInfo,
        string name,
        string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private sealed record WorkerRequest(string Operation, string? AudioPath);

    private sealed record WorkerResponse(bool Ok, string? Text, string? ErrorType);
}
