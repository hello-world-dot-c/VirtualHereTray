namespace VirtualHereTray;

/// <summary>
/// Tree view of servers and devices, mirroring the original VirtualHere client window. Built on <see cref="DpiForm"/>:
/// everything is designed in 96-dpi pixels and the base class re-scales the controls and sizes the window when it
/// opens and whenever it moves to a monitor with another DPI. Only what WinForms cannot scale itself (the tree's
/// image list and row height) is handled here.
/// </summary>
public sealed class ManagerForm : DpiForm
{
    private readonly Controller _c;
    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill, ShowLines = true, ShowRootLines = true, HideSelection = false, FullRowSelect = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly ImageList _images = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly ContextMenuStrip _ctx = new();
    private readonly StatusStrip _status = new() { SizingGrip = true };
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private bool _forceClose;
    private int _treeDpi;
    private bool _rebuilding, _rebuildAgain;

    public ManagerForm(Controller c)
    {
        _c = c;
        Text = "Device Manager";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;

        _tree.ImageList = _images;
        // WinForms may rescale ItemHeight when the handle is created. Only the row height is re-set there: rebuilding
        // nodes or swapping the image list inside HandleCreated made the tree show its root twice.
        _tree.HandleCreated += (_, _) => SetRowHeight();
        _tree.NodeMouseDoubleClick += async (_, e) =>
        {
            if (e.Node?.Tag is VhDevice d) { e.Node.Expand(); await _c.ToggleDeviceAsync(d); }
        };
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _tree.SelectedNode = e.Node;
            _ctx.Items.Clear();
            switch (e.Node?.Tag)
            {
                case VhDevice d: _ctx.Items.AddRange(Menus.Device(_c, d)); break;
                case VhServer s: _ctx.Items.AddRange(Menus.Server(_c, s)); break;
                default: _ctx.Items.AddRange(Menus.Tools(_c)); break;
            }
            _ctx.Show(Cursor.Position);
        };
        _tree.KeyDown += async (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space && _tree.SelectedNode?.Tag is VhDevice d)
            { await _c.ToggleDeviceAsync(d); e.Handled = true; e.SuppressKeyPress = true; }
        };

        var menu = new MenuStrip();
        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownOpening += (_, _) => { tools.DropDownItems.Clear(); tools.DropDownItems.AddRange(Menus.Tools(_c)); };
        tools.DropDownItems.Add(new ToolStripMenuItem());   // so the dropdown arrow exists before first open
        menu.Items.Add(tools);

        _status.Items.Add(_statusLabel);
        Controls.Add(_tree);
        Controls.Add(_status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _c.Changed += Rebuild;
        Rebuild();
        CaptureDesign();            // last statement: records the 96-dpi design values
    }

    // ---- placement ---------------------------------------------------------------------------------------------

    /// <summary>Size for the monitor the window opens on, bottom-right corner just past the click, fully on screen.</summary>
    private void Place()
    {
        var p = Dialogs.Anchor;
        var wa = Screen.FromPoint(p).WorkingArea;
        int dpi = DpiFor(p);                                  // the target monitor's DPI before the window exists
        var size = new Size(Math.Min(Px(dpi, 476), wa.Width), Math.Min(Px(dpi, 459), wa.Height));
        MinimumSize = new Size(Math.Min(Px(dpi, 336), size.Width), Math.Min(Px(dpi, 279), size.Height));
        Size = size;
        int margin = Px(dpi, 24);
        Location = new Point(Math.Max(wa.Left, Math.Min(p.X - size.Width + margin, wa.Right - size.Width)),
                             Math.Max(wa.Top, Math.Min(p.Y - size.Height + margin, wa.Bottom - size.Height)));
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); Place(); }

    public void ShowAndActivate()
    {
        Rebuild();
        if (!Visible)
        {
            Place();
            HideUntilSettled();
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _ = _c.RefreshAsync();
    }

    public void ForceClose() { _forceClose = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        _c.Changed -= Rebuild;
        base.OnFormClosing(e);
    }

    // ---- DPI-dependent tree metrics ----------------------------------------------------------------------------

    protected override void OnDpiApplied() => ApplyTreeMetrics();

    protected override void OnShown(EventArgs e) { base.OnShown(e); ApplyTreeMetrics(); }

    /// <summary>Image list size and row height follow the window's DPI; the images are redrawn only when it changed.</summary>
    /// <summary>
    /// Row height and the indent between tree levels, in 96-dpi design pixels scaled to the window's DPI. The indent
    /// decides how long the dotted connector lines are; left alone it kept a fixed pixel value, so the lines looked long on
    /// a 100% monitor and short on a high-DPI one.
    /// </summary>
    private void SetRowHeight()
    {
        int dpi = WindowDpi > 0 ? WindowDpi : Icons.DpiAt(Dialogs.Anchor);
        int row = Px(dpi, 20), indent = Px(dpi, 18);
        if (_tree.ItemHeight != row) _tree.ItemHeight = row;
        if (_tree.Indent != indent) _tree.Indent = indent;
    }

    private void ApplyTreeMetrics()
    {
        int dpi = WindowDpi > 0 ? WindowDpi : Icons.DpiAt(Dialogs.Anchor);
        SetRowHeight();
        if (dpi == _treeDpi) return;
        _treeDpi = dpi;
        Icons.RefreshDpi(dpi);                                // bitmaps are drawn for this DPI
        _images.Images.Clear();
        _images.ImageSize = new Size(Px(dpi, 16), Px(dpi, 16));
        Rebuild();
    }

    // ---- content -----------------------------------------------------------------------------------------------

    private int ImageIndex(string key, Func<Bitmap> make)
    {
        if (!_images.Images.ContainsKey(key)) _images.Images.Add(key, make());
        return _images.Images.IndexOfKey(key);
    }

    private void Rebuild()
    {
        // Adding the first node can create the tree's handle, which re-applies the tree metrics and so asks for another
        // rebuild while this one is still running: do it afterwards instead of nesting (that produced duplicate roots).
        if (_rebuilding) { _rebuildAgain = true; return; }
        _rebuilding = true;
        try { RebuildCore(); }
        finally { _rebuilding = false; }
        if (_rebuildAgain) { _rebuildAgain = false; Rebuild(); }
    }

    private void RebuildCore()
    {
        var expanded = new HashSet<string>(_tree.Nodes.Cast<TreeNode>().SelectMany(Flatten).Where(n => n.IsExpanded).Select(n => n.Name));
        var selected = _tree.SelectedNode?.Name;
        bool firstBuild = _tree.Nodes.Count == 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var usbIdx = ImageIndex("usb", () => Icons.Glyph("", Color.FromArgb(0, 102, 153)));
        var root = new TreeNode("USB Servers", usbIdx, usbIdx) { Name = "root" };
        var st = _c.State;
        if (st != null)
        {
            foreach (var s in st.Servers)
            {
                int si = ImageIndex("srv" + s.Connected, () => Icons.Server(s.Connected));
                var sn = new TreeNode(s.Name + (s.Connected ? "" : " (not connected)"), si, si) { Name = "s:" + s.Serial, Tag = s };
                foreach (var d in s.Devices)
                {
                    var bmp = Icons.Device(d);
                    int di = ImageIndex($"d{Icons.GlyphFor(d)}{(int)d.State}", () => bmp);
                    var text = d.DisplayName;
                    if (!string.IsNullOrEmpty(d.ComPort)) text += $" ({d.ComPort})";
                    if (d.State == DeviceState.InUseByOther) text += $" ({d.InUseByText})";
                    if (d.AutoUseEnabled) text += " ★";
                    var dn = new TreeNode(text, di, di) { Name = "d:" + d.Address, Tag = d };
                    if (d.InUseByMe) dn.NodeFont = new Font(Font, FontStyle.Bold);
                    sn.Nodes.Add(dn);
                }
                root.Nodes.Add(sn);
            }
        }
        _tree.Nodes.Add(root);

        foreach (var n in Flatten(root))
            if (firstBuild || expanded.Contains(n.Name)) n.Expand();
        root.Expand();
        if (selected != null && _tree.Nodes.Find(selected, true).FirstOrDefault() is { } sel) _tree.SelectedNode = sel;
        else _tree.SelectedNode = root;
        _tree.EndUpdate();

        _statusLabel.Text = st == null ? (_c.Error ?? "Not connected")
            : $"{st.Servers.Count(s => s.Connected)} server(s) connected, {st.InUseCount} device(s) in use";
    }

    private static IEnumerable<TreeNode> Flatten(TreeNode n)
    {
        yield return n;
        foreach (TreeNode c in n.Nodes) foreach (var x in Flatten(c)) yield return x;
    }
}
