using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var options = WorkerOptions.Parse(args);
using var pipe = new NamedPipeClientStream(
    ".",
    options.PipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous);
await pipe.ConnectAsync(30_000);

using var reader = new StreamReader(
    pipe,
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    detectEncodingFromByteOrderMarks: false,
    leaveOpen: true);
using var writer = new StreamWriter(
    pipe,
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    leaveOpen: true)
{
    AutoFlush = true
};
using var transcriber = new WorkerTranscriber(options);

while (await reader.ReadLineAsync() is { } requestJson)
{
    WorkerRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<WorkerRequest>(requestJson);
    }
    catch (JsonException)
    {
        await WriteResponseAsync(new WorkerResponse(false, null, "InvalidRequest"));
        continue;
    }

    if (request?.Operation == "shutdown")
    {
        await WriteResponseAsync(new WorkerResponse(true, null, null));
        break;
    }

    try
    {
        if (request?.Operation == "warmup")
        {
            transcriber.WarmUp();
            await WriteResponseAsync(new WorkerResponse(true, null, null));
            continue;
        }

        if (request?.Operation == "transcribe"
            && !string.IsNullOrWhiteSpace(request.AudioPath))
        {
            var transcript = transcriber.Transcribe(request.AudioPath);
            await WriteResponseAsync(new WorkerResponse(true, transcript, null));
            continue;
        }

        await WriteResponseAsync(new WorkerResponse(false, null, "InvalidRequest"));
    }
    catch (Exception exception)
    {
        await WriteResponseAsync(new WorkerResponse(
            false,
            null,
            $"{exception.GetType().Name}: {exception.Message}"));
    }
}

async Task WriteResponseAsync(WorkerResponse response)
{
    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
}

internal sealed class WorkerTranscriber : IDisposable
{
    private readonly WorkerOptions _options;
    private OpenVinoWhisperPipeline? _pipeline;

    public WorkerTranscriber(WorkerOptions options)
    {
        _options = options;
    }

    public void WarmUp()
    {
        _ = GetPipeline();
    }

    public string Transcribe(string audioPath)
    {
        var samples = WaveReader.ReadPcm16Mono16Khz(audioPath);
        try
        {
            return GetPipeline().Generate(samples);
        }
        finally
        {
            Array.Clear(samples);
        }
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
    }

    private OpenVinoWhisperPipeline GetPipeline()
    {
        if (_pipeline is not null)
        {
            return _pipeline;
        }

        if (!Directory.Exists(_options.ModelPath))
        {
            throw new DirectoryNotFoundException(
                $"The OpenVINO model directory does not exist: {_options.ModelPath}");
        }

        Directory.CreateDirectory(_options.CachePath);
        _pipeline = new OpenVinoWhisperPipeline(
            _options.ModelPath,
            _options.Device,
            _options.CachePath);
        return _pipeline;
    }
}

internal sealed class OpenVinoWhisperPipeline : IDisposable
{
    private IntPtr _pipeline;

    public OpenVinoWhisperPipeline(
        string modelPath,
        string device,
        string cachePath)
    {
        var status = NativeMethods.WhisperPipelineCreateWithCache(
            modelPath,
            device,
            2,
            out _pipeline,
            "CACHE_DIR",
            cachePath);
        CheckStatus(status, $"create the Whisper pipeline on {device}");
    }

    public unsafe string Generate(float[] samples)
    {
        ObjectDisposedException.ThrowIf(_pipeline == IntPtr.Zero, this);
        IntPtr results = IntPtr.Zero;
        fixed (float* samplePointer = samples)
        {
            CheckStatus(
                NativeMethods.WhisperPipelineGenerate(
                    _pipeline,
                    samplePointer,
                    (nuint)samples.Length,
                    IntPtr.Zero,
                    out results),
                "transcribe audio");
        }

        try
        {
            CheckStatus(
                NativeMethods.WhisperDecodedResultsGetTextsCount(
                    results,
                    out var count),
                "read the transcription result count");

            var transcript = new StringBuilder();
            for (nuint index = 0; index < count; index++)
            {
                CheckStatus(
                    NativeMethods.WhisperDecodedResultsMeasureTextAt(
                        results,
                        index,
                        null,
                        out var requiredSize),
                    "measure the transcription result");
                if (requiredSize == 0 || requiredSize > int.MaxValue)
                {
                    continue;
                }

                var textBytes = new byte[(int)requiredSize];
                CheckStatus(
                    NativeMethods.WhisperDecodedResultsReadTextAt(
                        results,
                        index,
                        textBytes,
                        ref requiredSize),
                    "read the transcription result");
                var textLength = Array.IndexOf(textBytes, (byte)0);
                if (textLength < 0)
                {
                    textLength = textBytes.Length;
                }

                transcript.Append(Encoding.UTF8.GetString(textBytes, 0, textLength));
                Array.Clear(textBytes);
            }

            return transcript.ToString().Trim();
        }
        finally
        {
            if (results != IntPtr.Zero)
            {
                NativeMethods.WhisperDecodedResultsFree(results);
            }
        }
    }

    public void Dispose()
    {
        var pipeline = Interlocked.Exchange(ref _pipeline, IntPtr.Zero);
        if (pipeline != IntPtr.Zero)
        {
            NativeMethods.WhisperPipelineFree(pipeline);
        }
    }

    private static void CheckStatus(int status, string operation)
    {
        if (status != 0)
        {
            var statusName = Marshal.PtrToStringUTF8(
                NativeMethods.GetErrorInfo(status));
            var detail = Marshal.PtrToStringUTF8(
                NativeMethods.GetLastErrorMessage());
            throw new OpenVinoException(
                $"OpenVINO GenAI could not {operation} " +
                $"({statusName ?? "status"} {status})" +
                (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"));
        }
    }
}

internal static class NativeMethods
{
    private const string LibraryName = "openvino_genai_c.dll";
    private const string RuntimeLibraryName = "openvino_c.dll";

    [DllImport(
        RuntimeLibraryName,
        EntryPoint = "ov_get_error_info",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr GetErrorInfo(int status);

    [DllImport(
        RuntimeLibraryName,
        EntryPoint = "ov_get_last_err_msg",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr GetLastErrorMessage();

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_pipeline_create",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WhisperPipelineCreateWithCache(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelsPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string device,
        nuint propertyArgumentsSize,
        out IntPtr pipeline,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string propertyName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string propertyValue);

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_pipeline_free",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void WhisperPipelineFree(IntPtr pipeline);

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_pipeline_generate",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int WhisperPipelineGenerate(
        IntPtr pipeline,
        float* rawSpeech,
        nuint rawSpeechSize,
        IntPtr config,
        out IntPtr results);

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_decoded_results_get_texts_count",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WhisperDecodedResultsGetTextsCount(
        IntPtr results,
        out nuint count);

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_decoded_results_get_text_at",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WhisperDecodedResultsMeasureTextAt(
        IntPtr results,
        nuint index,
        byte[]? text,
        out nuint textSize);

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_decoded_results_get_text_at",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WhisperDecodedResultsReadTextAt(
        IntPtr results,
        nuint index,
        byte[] text,
        ref nuint textSize);

    [DllImport(
        LibraryName,
        EntryPoint = "ov_genai_whisper_decoded_results_free",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void WhisperDecodedResultsFree(IntPtr results);
}

internal static class WaveReader
{
    public static float[] ReadPcm16Mono16Khz(string audioPath)
    {
        using var stream = File.OpenRead(Path.GetFullPath(audioPath));
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46464952
            || reader.ReadUInt32() < 4
            || reader.ReadUInt32() != 0x45564157)
        {
            throw new InvalidDataException("The audio file is not a RIFF WAVE file.");
        }

        ushort format = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? pcmBytes = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = reader.ReadUInt32();
            var chunkSize = reader.ReadUInt32();
            if (chunkSize > int.MaxValue || stream.Position + chunkSize > stream.Length)
            {
                throw new InvalidDataException("The WAVE file contains an invalid chunk.");
            }

            if (chunkId == 0x20746D66)
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("The WAVE format chunk is incomplete.");
                }

                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                stream.Position += chunkSize - 16;
            }
            else if (chunkId == 0x61746164)
            {
                pcmBytes = reader.ReadBytes((int)chunkSize);
            }
            else
            {
                stream.Position += chunkSize;
            }

            if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
            {
                stream.Position++;
            }
        }

        if (format != 1 || channels != 1 || sampleRate != 16_000 || bitsPerSample != 16)
        {
            throw new InvalidDataException(
                "OpenVINO Whisper requires 16 kHz mono 16-bit PCM audio.");
        }

        if (pcmBytes is null || pcmBytes.Length % 2 != 0)
        {
            throw new InvalidDataException("The WAVE audio data is missing or incomplete.");
        }

        var samples = new float[pcmBytes.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BitConverter.ToInt16(pcmBytes, index * 2) / 32768f;
        }

        Array.Clear(pcmBytes);
        return samples;
    }
}

internal sealed record WorkerRequest(string Operation, string? AudioPath);

internal sealed record WorkerResponse(bool Ok, string? Text, string? ErrorType);

internal sealed record WorkerOptions(
    string PipeName,
    string ModelPath,
    string CachePath,
    string Device,
    string Language)
{
    public static WorkerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length
                || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Worker arguments must be --name value pairs.");
            }

            values[args[index][2..]] = args[index + 1];
        }

        var device = Required("device").ToUpperInvariant();
        if (device is not "NPU" and not "GPU" and not "CPU")
        {
            throw new ArgumentException(
                "The worker device must be NPU, GPU, or CPU.");
        }

        return new WorkerOptions(
            Required("pipe"),
            Path.GetFullPath(Required("model")),
            Path.GetFullPath(Required("cache")),
            device,
            Required("language"));

        string Required(string name)
        {
            return values.TryGetValue(name, out var value)
                && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : throw new ArgumentException(
                        $"The --{name} argument is required.");
        }
    }
}

internal sealed class OpenVinoException : Exception
{
    public OpenVinoException(string message)
        : base(message)
    {
    }
}
