using System.Runtime.InteropServices;

namespace Dictate;

internal static class NativeWindow
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
}
