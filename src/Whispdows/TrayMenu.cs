using System.Drawing;
using Forms = System.Windows.Forms;

namespace Whispdows;

public sealed class TrayMenu : IDisposable
{
    private readonly Icon _enabledIcon;
    private readonly Icon _disabledIcon;
    private readonly Icon _listeningIcon;
    private readonly Icon _processingIcon;
    private readonly Icon _errorIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Forms.ToolStripMenuItem _launchAtLoginItem;
    private readonly Action<bool> _onEnabledChanged;
    private readonly Action<bool> _onLaunchAtLoginChanged;
    private bool _updatingChecks;
    private bool _disposed;
    private DictationState _dictationState;

    public TrayMenu(
        bool enabled,
        bool launchAtLogin,
        Action<bool> onEnabledChanged,
        Action<bool> onLaunchAtLoginChanged,
        Action onReloadRequested,
        Action onOpenSettingsEditorRequested,
        Action onOpenSettingsRequested,
        Action onOpenReadmeRequested,
        Action onExitRequested)
    {
        _onEnabledChanged = onEnabledChanged;
        _onLaunchAtLoginChanged = onLaunchAtLoginChanged;
        _dictationState = enabled
            ? DictationState.Idle
            : DictationState.Disabled;
        _enabledIcon = LoadIcon("whispdows-tray-enabled.ico");
        _disabledIcon = LoadIcon("whispdows-tray-disabled.ico");
        _listeningIcon = LoadIcon("whispdows-tray-listening.ico");
        _processingIcon = LoadIcon("whispdows-tray-processing.ico");
        _errorIcon = LoadIcon("whispdows-tray-error.ico");

        _statusItem = new Forms.ToolStripMenuItem("Disabled")
        {
            Enabled = false
        };

        _enabledItem = new Forms.ToolStripMenuItem("&Enabled")
        {
            CheckOnClick = true,
            Checked = enabled
        };
        _enabledItem.CheckedChanged += EnabledItemOnCheckedChanged;

        _launchAtLoginItem = new Forms.ToolStripMenuItem("&Launch at login")
        {
            CheckOnClick = true,
            Checked = launchAtLogin
        };
        _launchAtLoginItem.CheckedChanged += LaunchAtLoginItemOnCheckedChanged;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_launchAtLoginItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var settingsItem = CreateActionItem("&Settings…", onOpenSettingsEditorRequested);
        settingsItem.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(settingsItem);
        menu.Items.Add(CreateActionItem("&Reload settings", onReloadRequested));
        menu.Items.Add(CreateActionItem("Open settings &folder", onOpenSettingsRequested));
        menu.Items.Add(CreateActionItem("&Help / Documentation", onOpenReadmeRequested));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateActionItem("E&xit Whispdows", onExitRequested));

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = enabled ? _enabledIcon : _disabledIcon,
            Text = "Whispdows",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => onOpenSettingsEditorRequested();
        UpdateIcon();
    }

    public void ApplySettings(bool enabled, bool launchAtLogin)
    {
        SetChecks(enabled, launchAtLogin);
    }

    public void SetEnabled(bool enabled)
    {
        SetChecks(enabled, _launchAtLoginItem.Checked);
    }

    public void SetLaunchAtLogin(bool launchAtLogin)
    {
        SetChecks(_enabledItem.Checked, launchAtLogin);
    }

    public void SetState(DictationState state)
    {
        _dictationState = state;
        UpdateIcon();
    }

    public void ShowInfo(string message)
    {
        ShowBalloon(message, Forms.ToolTipIcon.Info);
    }

    public void ShowError(string message)
    {
        ShowBalloon(message, Forms.ToolTipIcon.Error);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _enabledIcon.Dispose();
        _disabledIcon.Dispose();
        _listeningIcon.Dispose();
        _processingIcon.Dispose();
        _errorIcon.Dispose();
    }

    private static Forms.ToolStripMenuItem CreateActionItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private void EnabledItemOnCheckedChanged(object? sender, EventArgs e)
    {
        UpdateIcon();
        if (!_updatingChecks)
        {
            _onEnabledChanged(_enabledItem.Checked);
        }
    }

    private void LaunchAtLoginItemOnCheckedChanged(object? sender, EventArgs e)
    {
        if (!_updatingChecks)
        {
            _onLaunchAtLoginChanged(_launchAtLoginItem.Checked);
        }
    }

    private void SetChecks(bool enabled, bool launchAtLogin)
    {
        _updatingChecks = true;
        try
        {
            _enabledItem.Checked = enabled;
            _launchAtLoginItem.Checked = launchAtLogin;
            UpdateIcon();
        }
        finally
        {
            _updatingChecks = false;
        }
    }

    private void UpdateIcon()
    {
        if (_disposed)
        {
            return;
        }

        var effectiveState = !_enabledItem.Checked
            ? DictationState.Disabled
            : _dictationState;
        var stateLabel = effectiveState switch
        {
            DictationState.Disabled => "Disabled",
            DictationState.Recording => "Listening",
            DictationState.Transcribing => "Transcribing",
            DictationState.Cleaning => "Cleaning transcript",
            DictationState.Pasting => "Pasting",
            DictationState.Error => "Needs attention",
            _ => "Ready"
        };

        _notifyIcon.Icon = effectiveState switch
        {
            DictationState.Disabled => _disabledIcon,
            DictationState.Recording => _listeningIcon,
            DictationState.Transcribing
                or DictationState.Cleaning
                or DictationState.Pasting => _processingIcon,
            DictationState.Error => _errorIcon,
            _ => _enabledIcon
        };
        _statusItem.Text = stateLabel;
        _notifyIcon.Text = $"Whispdows — {stateLabel}";
    }

    private static Icon LoadIcon(string fileName)
    {
        var resourceName = $"Whispdows.Assets.{fileName}";
        using var stream = typeof(TrayMenu).Assembly.GetManifestResourceStream(
            resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded tray icon '{resourceName}' is missing.");
        using var loaded = new Icon(stream);
        return (Icon)loaded.Clone();
    }

    private void ShowBalloon(string message, Forms.ToolTipIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "Whispdows";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }
}
