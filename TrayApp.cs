using System.Reflection;
using Microsoft.Win32;

namespace VirtualHereTray;

public sealed class TrayApp : ApplicationContext
{
    private readonly Controller _c = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = true };
    private const string ProjectUrl = "https://github.com/hello-world-dot-c/VirtualHereTray";
    private ManagerForm? _manager;

    public TrayApp()
    {
        _tray = new NotifyIcon { Icon = Icons.Tray(Icons.TrayKind.Offline), Text = "VirtualHere", Visible = true, ContextMenuStrip = _menu };
        // Items are built on MouseDown: a ContextMenuStrip with no items never opens, and NotifyIcon
        // pops its menu before raising MouseUp for the right button, so Opening/MouseUp are too late.
        _tray.MouseDown += (_, e) =>
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            Icons.RefreshDpi(Icons.DpiAt(Cursor.Position));     // the menu opens on the monitor that was clicked
            BuildMenu();
        };
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        _tray.DoubleClick += (_, _) => ShowManager();

        // Docking, undocking and remote sessions change the display scaling while we run.
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _restartTimer.Tick += (_, _) => OnRestartTimer();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnDisplayChanged;
        Icons.DpiChanged += OnDpiChanged;

        _c.Changed += UpdateTray;
        _c.Notify += (title, text, error) =>
            _tray.ShowBalloonTip(5000, title, text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
        _c.Start();
        UpdateTray();
        BuildMenu();
    }

    private readonly SynchronizationContext _sync;

    // The DPI of the primary monitor when this process started. WinForms keeps scaling its internals (menu spacing,
    // icon sizes, paddings) from the DPI a process started with and does not fully recover when the display changes
    // later (docking, undocking, RDP from a device with other scaling). The reliable cure is a fresh process.
    private readonly int _startDpi = Icons.PrimaryDpi();
    private readonly System.Windows.Forms.Timer _restartTimer = new() { Interval = 2500 };
    private bool _restarting;

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        _sync.Post(_ =>
        {
            Icons.RefreshDpi(Icons.PrimaryDpi());
            _restartTimer.Stop(); _restartTimer.Start();     // debounce: the display settles over a second or two
        }, null);

    private void OnRestartTimer()
    {
        _restartTimer.Stop();
        if (!_restarting && Icons.PrimaryDpi() != _startDpi) RestartSelf();
    }

    /// <summary>Starts a new instance of this app and exits; the backend client keeps running in its own process.</summary>
    private void RestartSelf()
    {
        _restarting = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
        }
        catch (Exception ex) { Controller.Log(ex); _restarting = false; return; }
        ExitApp();
    }

    private void OnDpiChanged()
    {
        UpdateTray();       // the tray icon is regenerated at the new size
        BuildMenu();        // menu images are redrawn at the new size
    }

    /// <summary>Called when a second instance is started: bring up the manager window.</summary>
    public void ShowManager()
    {
        Dialogs.SetAnchor();
        if (_manager == null || _manager.IsDisposed) _manager = new ManagerForm(_c);
        _manager.ShowAndActivate();
    }

    private void ShowMenu()
    {
        // NotifyIcon only pops its menu on right-click; invoke the same code path for the left button.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
    }

    private void UpdateTray()
    {
        var st = _c.State;
        var kind = st == null || st.Servers.All(s => !s.Connected) ? Icons.TrayKind.Offline
                 : st.InUseCount > 0 ? Icons.TrayKind.InUse : Icons.TrayKind.Idle;
        _tray.Icon = Icons.Tray(kind);

        string text;
        if (st == null) text = "VirtualHere – " + (_c.Error ?? "not running");
        else
        {
            int servers = st.Servers.Count(s => s.Connected), used = st.InUseCount;
            text = $"VirtualHere – {servers} server{(servers == 1 ? "" : "s")}, {used} device{(used == 1 ? "" : "s")} in use";
        }
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    // ---- menu ----------------------------------------------------------------------------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var st = _c.State;

        // Title: program name and version, greyed and bold. It is a link: hovering shows a tooltip, clicking opens the
        // GitHub page in the default browser (the item must stay enabled for either to work).
        var bold = new Font(_menu.Font, FontStyle.Bold);
        var version = (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion ?? "";
        version = version.Split('+')[0];
        var title = new ToolStripMenuItem($"VirtualHereTray v{version}")
        {
            Font = bold,
            ForeColor = SystemColors.GrayText,
            ToolTipText = "Click to show GitHub page",
        };
        title.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Controller.Log(ex); }
        };
        _menu.Items.Add(title);
        _menu.Items.Add(new ToolStripSeparator());
        if (st == null)
        {
            var start = new ToolStripMenuItem("Start VirtualHere client");
            start.Click += async (_, _) => await _c.StartClientAsync();
            _menu.Items.Add(start);
        }
        else if (st.Servers.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("No servers found") { Enabled = false });
        }
        else
        {
            bool first = true;
            foreach (var s in st.Servers)
            {
                if (!first) _menu.Items.Add(new ToolStripSeparator());
                first = false;
                var header = new ToolStripMenuItem(s.Name + (s.Connected ? "" : " (not connected)"))
                {
                    Image = Icons.Server(s.Connected),
                    Font = new Font(_menu.Font, FontStyle.Bold),
                };
                header.DropDownItems.AddRange(Menus.Server(_c, s));
                _menu.Items.Add(header);

                if (s.MaxDevices == 1)
                    _menu.Items.Add(new ToolStripMenuItem("   (unlicensed: one device at a time)") { Enabled = false });

                foreach (var d in s.Devices) _menu.Items.Add(DeviceItem(d));
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        var mgr = new ToolStripMenuItem("Open device manager…");
        mgr.Click += (_, _) => ShowManager();
        _menu.Items.Add(mgr);

        var tools = new ToolStripMenuItem("Tools");
        tools.DropDownItems.AddRange(Menus.Tools(_c));
        _menu.Items.Add(tools);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Controller.StartWithWindows, CheckOnClick = false };
        startup.Click += (_, _) => Controller.StartWithWindows = !Controller.StartWithWindows;
        _menu.Items.Add(startup);

        var loc = new ToolStripMenuItem("Set VirtualHere client location…");
        loc.Click += (_, _) => PickClientPath();
        _menu.Items.Add(loc);

        var exit = new ToolStripMenuItem("Exit tray utility");
        exit.Click += (_, _) => ExitApp();
        _menu.Items.Add(exit);
    }

    private ToolStripMenuItem DeviceItem(VhDevice d)
    {
        var text = d.DisplayName;
        if (!string.IsNullOrEmpty(d.ComPort)) text += $" ({d.ComPort})";
        if (d.State == DeviceState.InUseByOther)
            text += $" ({d.InUseByText})";
        if (d.AutoUseEnabled) text += " ★";

        var mi = new ToolStripMenuItem(text)
        {
            Image = Icons.Device(d),
            Font = d.InUseByMe ? new Font(_menu.Font, FontStyle.Bold) : _menu.Font,
        };
        mi.DropDownItems.AddRange(Menus.Device(_c, d));
        return mi;
    }

    // ---- misc ----------------------------------------------------------------------------

    private void PickClientPath()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Locate the VirtualHere client (vhui64.exe)",
            Filter = "VirtualHere client|vhui*.exe|All executables|*.exe",
            FileName = _c.ClientPath ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _c.Settings.ClientPath = dlg.FileName;
            _c.Settings.Save();
        }
    }

    private void ExitApp()
    {
        _c.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnDisplayChanged;
        Icons.DpiChanged -= OnDpiChanged;
        _tray.Visible = false;
        _manager?.ForceClose();
        _tray.Dispose();
        ExitThread();
    }
}
