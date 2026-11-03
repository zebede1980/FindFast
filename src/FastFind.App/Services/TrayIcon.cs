using System.Drawing;
using System.Windows.Forms;

namespace FastFind.App.Services;

/// <summary>Notification-area icon with a small menu. Keeps the app (and its in-memory index) alive while the window is hidden.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _tipShown;

    public event Action? ShowRequested;
    public event Action? NewSearchRequested;
    public event Action? RebuildRequested;
    public event Action? ExitRequested;

    public TrayIcon(string hotkeyText)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show FastFind", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add("New search", null, (_, _) => NewSearchRequested?.Invoke());
        menu.Items.Add("Rebuild index", null, (_, _) => RebuildRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        Icon? icon = null;
        try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception) { }

        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = string.IsNullOrEmpty(hotkeyText) ? "FastFind" : $"FastFind ({hotkeyText})",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowRequested?.Invoke(); };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void UpdateHotkeyText(string hotkeyText)
        => _icon.Text = string.IsNullOrEmpty(hotkeyText) ? "FastFind" : $"FastFind ({hotkeyText})";

    /// <summary>Shown once per session when the window is first hidden, so the user knows where the app went.</summary>
    public void ShowHiddenTip(string hotkeyText)
    {
        if (_tipShown) return;
        _tipShown = true;
        string hint = string.IsNullOrEmpty(hotkeyText) ? "Click the tray icon to bring it back." : $"Press {hotkeyText} or click the tray icon to bring it back.";
        _icon.ShowBalloonTip(4000, "FastFind is still running", $"The index stays in memory so searches remain instant. {hint}", ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
