using System.Drawing;
using Forms = System.Windows.Forms;

namespace Dictate;

public sealed class TrayMenu : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Forms.ToolStripMenuItem _launchAtLoginItem;
    private readonly Action<bool> _onEnabledChanged;
    private readonly Action<bool> _onLaunchAtLoginChanged;
    private bool _updatingChecks;
    private bool _disposed;

    public TrayMenu(
        bool enabled,
        bool launchAtLogin,
        Action<bool> onEnabledChanged,
        Action<bool> onLaunchAtLoginChanged,
        Action onReloadRequested,
        Action onOpenSettingsRequested,
        Action onOpenReadmeRequested,
        Action onExitRequested)
    {
        _onEnabledChanged = onEnabledChanged;
        _onLaunchAtLoginChanged = onLaunchAtLoginChanged;

        _enabledItem = new Forms.ToolStripMenuItem("Enabled")
        {
            CheckOnClick = true,
            Checked = enabled
        };
        _enabledItem.CheckedChanged += EnabledItemOnCheckedChanged;

        _launchAtLoginItem = new Forms.ToolStripMenuItem("Launch at login")
        {
            CheckOnClick = true,
            Checked = launchAtLogin
        };
        _launchAtLoginItem.CheckedChanged += LaunchAtLoginItemOnCheckedChanged;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_launchAtLoginItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateActionItem("Reload settings", onReloadRequested));
        menu.Items.Add(CreateActionItem("Open settings folder", onOpenSettingsRequested));
        menu.Items.Add(CreateActionItem("Open README", onOpenReadmeRequested));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateActionItem("Exit", onExitRequested));

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "Dictate",
            Visible = true
        };
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
    }

    private static Forms.ToolStripMenuItem CreateActionItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private void EnabledItemOnCheckedChanged(object? sender, EventArgs e)
    {
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
        }
        finally
        {
            _updatingChecks = false;
        }
    }

    private void ShowBalloon(string message, Forms.ToolTipIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "Dictate";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }
}
