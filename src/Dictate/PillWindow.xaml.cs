using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Dictate;

public enum PillState
{
    Listening,
    Transcribing,
    Cleaning,
    Pasted,
    Copied,
    CopiedTargetChanged,
    NoSpeechDetected,
    Error
}

public partial class PillWindow : Window, IRecordingPill
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly record struct PillVisual(string Indicator, string Message, string Color);

    public PillWindow()
    {
        InitializeComponent();
        SetState(PillState.Listening);
    }

    public void SetState(PillState state, string? errorMessage = null)
    {
        var visual = state switch
        {
            PillState.Listening => new PillVisual("●", "Listening…", "#FF71D7A5"),
            PillState.Transcribing => new PillVisual("◌", "Transcribing…", "#FFF2C14E"),
            PillState.Cleaning => new PillVisual("◌", "Cleaning…", "#FFF2C14E"),
            PillState.Pasted => new PillVisual("✓", "Pasted", "#FF71D7A5"),
            PillState.Copied => new PillVisual("✓", "Copied", "#FFF2C14E"),
            PillState.CopiedTargetChanged => new PillVisual("✓", "Copied — target changed", "#FFF2C14E"),
            PillState.NoSpeechDetected => new PillVisual("–", "No speech detected", "#FFF2C14E"),
            PillState.Error => new PillVisual("!", string.IsNullOrWhiteSpace(errorMessage) ? "Error" : errorMessage, "#FFFF8A80"),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown pill state.")
        };

        Indicator.Text = visual.Indicator;
        Indicator.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(visual.Color));
        Message.Text = visual.Message;
    }

    public void ShowForTargetWindow(nint targetWindow)
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionOnTargetMonitor(targetWindow);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            SetWindowPos(
                source.Handle,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
    }

    public void HidePill()
    {
        Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(source.Handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            source.Handle,
            GwlExStyle,
            new IntPtr(currentStyle | WsExNoActivate | WsExToolWindow | WsExTransparent));
        source.AddHook(WindowProcedure);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.RemoveHook(WindowProcedure);
        }

        base.OnClosed(e);
    }

    private void PositionOnTargetMonitor(nint targetWindow)
    {
        var screen = targetWindow == nint.Zero
            ? Forms.Screen.PrimaryScreen
            : Forms.Screen.FromHandle((IntPtr)targetWindow);

        screen ??= Forms.Screen.AllScreens.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice
            ?? new System.Windows.Media.Matrix(1, 0, 0, 1, 0, 0);
        var topLeft = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var workWidth = bottomRight.X - topLeft.X;
        var bottomMargin = 16 * fromDevice.M11;

        Left = topLeft.X + ((workWidth - ActualWidth) / 2);
        Top = bottomRight.Y - ActualHeight - bottomMargin;
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
