using System.Runtime.InteropServices;

namespace Whispdows;

internal static class NativeWindow
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
}
