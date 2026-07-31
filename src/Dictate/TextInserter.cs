using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace Dictate;

public enum TextInsertionResult
{
    Pasted,
    Copied,
    TargetChanged
}

public interface ITextInserter
{
    Task<TextInsertionResult> InsertAsync(
        string text,
        nint targetWindow,
        CancellationToken cancellationToken);
}

public sealed record ClipboardSnapshot(object Data);

public interface IClipboardService
{
    Task<ClipboardSnapshot?> CaptureAsync(CancellationToken cancellationToken);

    Task SetTextAsync(string text, CancellationToken cancellationToken);

    uint GetSequenceNumber();

    Task<bool> ContainsTextAsync(string text, CancellationToken cancellationToken);

    Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IInputSender
{
    bool SendPasteShortcut();
}

public sealed class TextInserter : ITextInserter
{
    private readonly PasteSettings _settings;
    private readonly IClipboardService _clipboard;
    private readonly IInputSender _inputSender;
    private readonly Func<nint> _getForegroundWindow;

    public TextInserter(PasteSettings settings)
        : this(
            settings,
            new WindowsClipboardService(),
            new WindowsInputSender(),
            NativeWindow.GetForegroundWindow)
    {
    }

    internal TextInserter(
        PasteSettings settings,
        IClipboardService clipboard,
        IInputSender inputSender,
        Func<nint> getForegroundWindow)
    {
        _settings = new PasteSettings
        {
            RestoreClipboard = settings.RestoreClipboard,
            RestoreDelayMs = settings.RestoreDelayMs
        };
        _clipboard = clipboard;
        _inputSender = inputSender;
        _getForegroundWindow = getForegroundWindow;
    }

    public async Task<TextInsertionResult> InsertAsync(
        string text,
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (targetWindow == nint.Zero || _getForegroundWindow() != targetWindow)
        {
            await _clipboard.SetTextAsync(text, cancellationToken);
            return TextInsertionResult.TargetChanged;
        }

        var snapshot = await _clipboard.CaptureAsync(cancellationToken);
        await _clipboard.SetTextAsync(text, cancellationToken);
        var ownedSequence = _clipboard.GetSequenceNumber();

        if (!_inputSender.SendPasteShortcut())
        {
            return TextInsertionResult.Copied;
        }

        await Task.Delay(_settings.RestoreDelayMs, cancellationToken);
        if (!_settings.RestoreClipboard || snapshot is null)
        {
            return TextInsertionResult.Pasted;
        }

        if (_clipboard.GetSequenceNumber() != ownedSequence)
        {
            return TextInsertionResult.Pasted;
        }

        try
        {
            if (!await _clipboard.ContainsTextAsync(text, cancellationToken))
            {
                return TextInsertionResult.Pasted;
            }

            await _clipboard.RestoreAsync(snapshot, cancellationToken);
            return TextInsertionResult.Pasted;
        }
        catch (Exception exception) when (exception is COMException or ExternalException)
        {
            return TextInsertionResult.Copied;
        }
    }
}

public sealed class WindowsClipboardService : IClipboardService
{
    private const int RetryCount = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public async Task<ClipboardSnapshot?> CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dataObject = await RetryAsync(
                CaptureDataObject,
                cancellationToken);
            return dataObject is null ? null : new ClipboardSnapshot(dataObject);
        }
        catch (Exception exception) when (exception is COMException or ExternalException)
        {
            return null;
        }
    }

    private static System.Windows.IDataObject? CaptureDataObject()
    {
        var source = System.Windows.Clipboard.GetDataObject();
        if (source is null)
        {
            return null;
        }

        var snapshot = new System.Windows.DataObject();
        foreach (var format in source.GetFormats(autoConvert: false))
        {
            try
            {
                var data = source.GetData(format, autoConvert: false);
                if (data is not null)
                {
                    snapshot.SetData(format, data);
                }
            }
            catch (Exception exception) when (exception is COMException or ExternalException)
            {
                // A single unavailable format should not prevent restoring the rest.
            }
        }

        return snapshot;
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        return RetryAsync(
            () => System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText),
            cancellationToken);
    }

    public uint GetSequenceNumber()
    {
        return NativeWindow.GetClipboardSequenceNumber();
    }

    public Task<bool> ContainsTextAsync(string text, CancellationToken cancellationToken)
    {
        return RetryAsync(
            () => System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText)
                && string.Equals(
                    System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText),
                    text,
                    StringComparison.Ordinal),
            cancellationToken);
    }

    public Task RestoreAsync(
        ClipboardSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Data is not System.Windows.IDataObject dataObject)
        {
            throw new ArgumentException("The clipboard snapshot is invalid.", nameof(snapshot));
        }

        return RetryAsync(
            () => System.Windows.Clipboard.SetDataObject(dataObject, copy: true),
            cancellationToken);
    }

    private static async Task RetryAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        await RetryAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken);
    }

    private static async Task<T> RetryAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (Exception exception) when (
                exception is COMException or ExternalException
                && attempt < RetryCount)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }
}

public sealed class WindowsInputSender : IInputSender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;

    public bool SendPasteShortcut()
    {
        var inputs = new[]
        {
            CreateKeyboardInput(VirtualKeyControl, keyUp: false),
            CreateKeyboardInput(VirtualKeyV, keyUp: false),
            CreateKeyboardInput(VirtualKeyV, keyUp: true),
            CreateKeyboardInput(VirtualKeyControl, keyUp: true)
        };

        var sent = SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeInput>());
        return sent == inputs.Length;
    }

    private static NativeInput CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new NativeInput
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int inputSize);
}
