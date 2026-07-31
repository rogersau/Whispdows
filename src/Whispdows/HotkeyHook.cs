using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Whispdows;

public sealed class HotkeyHook : IDisposable
{
    private const int WhKeyboardLowLevel = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmSystemKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x00000001;
    private const uint LlkhfInjected = 0x00000010;

    private readonly Dispatcher _dispatcher;
    private readonly Action<HotkeyEvent> _eventHandler;
    private readonly LowLevelKeyboardProcedure _callback;
    private readonly HashSet<uint> _pressedModifierKeys = [];
    private IntPtr _hookHandle;
    private HotkeyBinding? _binding;
    private bool _triggerKeyDown;
    private bool _triggerActive;
    private bool _escapeKeyDown;
    private volatile bool _recordingActive;
    private bool _disposed;

    public HotkeyHook(Dispatcher dispatcher, Action<HotkeyEvent> eventHandler)
    {
        _dispatcher = dispatcher;
        _eventHandler = eventHandler;
        _callback = HookCallback;
    }

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    public void Install(HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsInstalled)
        {
            Remove();
        }

        _binding = binding;
        ResetKeyState();
        _hookHandle = SetWindowsHookEx(WhKeyboardLowLevel, _callback, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            _binding = null;
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not install the global hold-to-talk keyboard hook.");
        }
    }

    public void Remove()
    {
        var handle = _hookHandle;
        _hookHandle = IntPtr.Zero;
        _binding = null;
        _recordingActive = false;
        ResetKeyState();

        if (handle != IntPtr.Zero && !UnhookWindowsHookEx(handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not remove the global hold-to-talk keyboard hook.");
        }
    }

    public void SetRecordingActive(bool recordingActive)
    {
        _recordingActive = recordingActive;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Remove();
        }
        catch (Win32Exception)
        {
            // The process is exiting; there is no useful recovery path.
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _binding is null)
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        var message = unchecked((int)wParam.ToInt64());
        var isKeyDown = message is WmKeyDown or WmSystemKeyDown;
        var isKeyUp = message is WmKeyUp or WmSystemKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        var keyboardEvent = Marshal.PtrToStructure<LowLevelKeyboardEvent>(lParam);
        if ((keyboardEvent.Flags & LlkhfInjected) != 0)
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        var virtualKey = NormalizeVirtualKey(
            keyboardEvent.VirtualKey,
            keyboardEvent.ScanCode,
            keyboardEvent.Flags);
        if (virtualKey == (uint)VirtualKey.Escape)
        {
            return HandleEscape(code, wParam, lParam, isKeyDown, isKeyUp);
        }

        if (_binding.Shortcut.IsTrackedModifierKey(virtualKey))
        {
            if (isKeyDown)
            {
                _pressedModifierKeys.Add(virtualKey);
            }
            else
            {
                _pressedModifierKeys.Remove(virtualKey);
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (virtualKey != (uint)_binding.Shortcut.TriggerKey)
        {
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (isKeyDown)
        {
            if (!_triggerKeyDown)
            {
                _triggerKeyDown = true;
                if (_binding.Shortcut.AreModifiersPressed(_pressedModifierKeys))
                {
                    _triggerActive = true;
                    Dispatch(HotkeyEvent.TriggerPressed);
                }
            }

            return _triggerActive && _binding.Suppress
                ? new IntPtr(1)
                : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        var wasActive = _triggerActive;
        _triggerKeyDown = false;
        _triggerActive = false;
        if (wasActive)
        {
            Dispatch(HotkeyEvent.TriggerReleased);
        }

        return wasActive && _binding.Suppress
            ? new IntPtr(1)
            : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr HandleEscape(
        int code,
        IntPtr wParam,
        IntPtr lParam,
        bool isKeyDown,
        bool isKeyUp)
    {
        if (isKeyDown && _recordingActive)
        {
            if (!_escapeKeyDown)
            {
                _escapeKeyDown = true;
                Dispatch(HotkeyEvent.Cancelled);
            }

            return new IntPtr(1);
        }

        if (isKeyUp && _escapeKeyDown)
        {
            _escapeKeyDown = false;
            return new IntPtr(1);
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Dispatch(HotkeyEvent hotkeyEvent)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => _eventHandler(hotkeyEvent)));
    }

    private void ResetKeyState()
    {
        _pressedModifierKeys.Clear();
        _triggerKeyDown = false;
        _triggerActive = false;
        _escapeKeyDown = false;
    }

    internal static uint NormalizeVirtualKey(uint virtualKey, uint scanCode, uint flags)
    {
        return virtualKey switch
        {
            0x10 => scanCode == 0x36
                ? (uint)VirtualKey.RightShift
                : (uint)VirtualKey.LeftShift,
            0x11 => (flags & LlkhfExtended) != 0
                ? (uint)VirtualKey.RightControl
                : (uint)VirtualKey.LeftControl,
            0x12 => (flags & LlkhfExtended) != 0
                ? (uint)VirtualKey.RightAlt
                : (uint)VirtualKey.LeftAlt,
            _ => virtualKey
        };
    }

    private delegate IntPtr LowLevelKeyboardProcedure(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardEvent
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookIdentifier,
        LowLevelKeyboardProcedure callback,
        IntPtr module,
        uint threadIdentifier);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);
}
