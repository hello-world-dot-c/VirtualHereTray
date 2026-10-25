namespace VirtualHereTray;

/// <summary>
/// One table with the details of every device on a server. A <see cref="DpiForm"/> so it keeps the right size when it is
/// dragged to a monitor with another DPI: the base class scales the window from its 96-dpi size, and the grid refits
/// its columns and rows to the new font.
/// </summary>
public sealed class DeviceTableWindow : DpiForm
{
    private readonly DataGridView _grid;
    private bool _fitted;

    public DeviceTableWindow(VhServer server)
    {
        Text = server.Name + " – devices";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,       // row height follows the font, at any DPI
        };
        string[] cols = { "Device", "Vendor", "Product", "Vendor ID", "Product ID", "Serial", "Address", "In use by", "COM port", "Auto-use" };
        foreach (var col in cols) _grid.Columns.Add(col, col);
        foreach (var d in server.Devices)
        {
            string inUse = d.State switch
            {
                DeviceState.InUseByMe => "You",
                DeviceState.InUseByOther => d.HolderText.Length > 0 ? d.HolderText + (d.BoundIp.Length > 0 ? $" ({d.BoundIp})" : "") : "Another client",
                DeviceState.Connecting => "Connecting…",
                _ => "No one",
            };
            _grid.Rows.Add(d.DisplayName, d.Vendor, d.Product.Trim(), $"0x{d.VendorId:x4}", $"0x{d.ProductId:x4}",
                d.Serial, d.Address, inUse, d.ComPort, d.AutoUseEnabled ? d.AutoUse : "");
        }
        _grid.ClearSelection();

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 27) };
        var copy = new Button { Text = "Copy table", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(100, 27) };
        copy.Click += (_, _) =>
        {
            _grid.SelectAll();
            if (_grid.GetClipboardContent() is { } data) Clipboard.SetDataObject(data, true);
            _grid.ClearSelection();
        };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = false,
        };
        bar.Controls.Add(close); bar.Controls.Add(copy);

        // grid on top (takes the rest), button bar below (its natural height)
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_grid, 0, 0);
        root.Controls.Add(bar, 0, 1);
        Controls.Add(root);
        AcceptButton = close; CancelButton = close;

        CaptureDesign();            // last statement: records the 96-dpi design values
    }

    /// <summary>Opens modally next to the click, fully on screen, without showing intermediate layouts.</summary>
    public void ShowNearDialog()
    {
        Place(initial: true);
        HideUntilSettled();
        ShowDialog();
    }

    /// <summary>Starting size for the monitor the window opens on; refined to the table's content once it is shown.</summary>
    private void Place(bool initial)
    {
        var p = Dialogs.Anchor;
        var wa = Screen.FromPoint(p).WorkingArea;
        int dpi = DpiFor(p);
        if (initial)
        {
            var size = new Size(Math.Min(Px(dpi, 776), wa.Width), Math.Min(Px(dpi, 359), wa.Height));
            MinimumSize = new Size(Math.Min(Px(dpi, 436), size.Width), Math.Min(Px(dpi, 159), size.Height));
            Size = size;
        }
        int margin = Px(dpi, 24);
        Location = new Point(Math.Max(wa.Left, Math.Min(p.X - Width + margin, wa.Right - Width)),
                             Math.Max(wa.Top, Math.Min(p.Y - Height + margin, wa.Bottom - Height)));
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); Place(initial: false); }

    protected override void OnShown(EventArgs e)
    {
        // Queued before the base class queues its own "settle and reveal" callback, so the window is fitted while it is
        // still hidden and appears at its final size.
        BeginInvoke(FitToContent);
        base.OnShown(e);
    }

    protected override void OnDpiApplied()
    {
        _grid.AutoResizeColumns();
        _grid.AutoResizeRows();
    }

    /// <summary>
    /// Opens the window as wide/high as the table needs (in real pixels at the current DPI), capped to the monitor. Only
    /// done once; after that the base class keeps the user's size in 96-dpi units across DPI changes.
    /// </summary>
    private void FitToContent()
    {
        if (_fitted) return;
        _fitted = true;
        _grid.AutoResizeColumns();
        _grid.AutoResizeRows();
        int want = _grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible) + SystemInformation.VerticalScrollBarWidth + 8;
        int rows = _grid.ColumnHeadersHeight + _grid.Rows.Cast<DataGridViewRow>().Sum(r => r.Height) + 8;
        var bar = Controls[0] is TableLayoutPanel t ? t.GetRowHeights()[1] : 0;
        var wa = Screen.FromControl(this).WorkingArea;
        int chromeW = Width - ClientSize.Width, chromeH = Height - ClientSize.Height;
        ClientSize = new Size(Math.Min(want, wa.Width - chromeW),
            Math.Min(Math.Max(rows + bar, MinimumSize.Height - chromeH), wa.Height - chromeH));
        Place(initial: false);
    }
}
