namespace VirtualHereTray;

/// <summary>
/// All dialogs scale themselves explicitly. Sizes are written in 96-dpi units and run through <c>U</c>, which
/// multiplies by the form's real DPI. That is only evaluated in the form's Load event (the window exists by then,
/// so DeviceDpi is the DPI of the monitor it opened on), and WinForms auto-scaling is left off so nothing is
/// scaled twice. Never use <see cref="Icons.Px"/> here.
/// </summary>
public static class Dialogs
{
    // Where the user last clicked to open something. Dialogs that open after an await would otherwise
    // see a cursor that has moved on.
    private static Point _anchor;
    private static DateTime _anchorTime = DateTime.MinValue;

    public static void SetAnchor() { _anchor = Cursor.Position; _anchorTime = DateTime.UtcNow; }

    public static Point Anchor => DateTime.UtcNow - _anchorTime < TimeSpan.FromSeconds(15) ? _anchor : Cursor.Position;

    /// <summary>
    /// Puts the form next to where it was opened from (its bottom-right corner near the click, which is
    /// where the tray is) and keeps it fully inside the working area of that screen.
    /// </summary>
    public static void PlaceNear(Form f)
    {
        var p = Anchor;
        var wa = Screen.FromPoint(p).WorkingArea;
        // Before the window exists its size is for the DPI of the primary monitor; project it onto the target monitor.
        float ratio = f.IsHandleCreated ? 1f : Icons.DpiAt(p) / (float)Math.Max(1, f.DeviceDpi);
        int w = Math.Min((int)(f.Width * ratio), wa.Width), h = Math.Min((int)(f.Height * ratio), wa.Height);
        int margin = (int)(24 * Icons.DpiAt(p) / 96f);
        int x = p.X - w + margin, y = p.Y - h + margin;
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(x, y);
    }

    /// <summary>The scaling helper for a form: 96-dpi units to real pixels at the form's current DPI.</summary>
    public static Func<int, int> Scaler(Form f) => v => (int)Math.Round(v * f.DeviceDpi / 96.0);

    /// <summary>
    /// Creates a dialog. <paramref name="build"/> runs in the Load event, when the DPI is known, and receives the
    /// scaler; the dialog is then placed near the click and kept on screen.
    /// </summary>
    private static Form Make(string title, Action<Form, Func<int, int>> build)
    {
        var f = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.Manual,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            Icon = Icons.Tray(Icons.TrayKind.Idle),
            TopMost = true,
        };
        PlaceNear(f);       // before the window is created, so it opens on the monitor that was clicked
        f.Load += (_, _) =>
        {
            // The window exists now, so DeviceDpi is the DPI of the monitor it opened on. The font is sized from
            // that in pixels (SystemFonts can still reflect the DPI the process started with).
            f.Font = Icons.UiFont(f.DeviceDpi);
            build(f, Scaler(f));
            f.PerformLayout();
            PlaceNear(f);
        };
        f.Shown += (_, _) => PlaceNear(f);
        return f;
    }

    /// <summary>
    /// Gives a text box a monospace font. WinForms rescales a child's own font by (window DPI / the DPI the control
    /// was constructed at) when its handle is created, which would double it on a screen scaled differently from the
    /// one the process started on. Setting it after the handle exists (and again once shown) avoids that.
    /// </summary>
    private static void MonoFont(Form f, TextBox tb, int px)
    {
        void Apply() => tb.Font = new Font("Consolas", Scaler(f)(px), FontStyle.Regular, GraphicsUnit.Pixel);
        Apply();
        tb.HandleCreated += (_, _) => Apply();
        f.Shown += (_, _) => Apply();
        f.DpiChanged += (_, _) => Apply();
    }

    private static Button Btn(string text, DialogResult result, int x, int y, int w, Func<int, int> U, int h = 27) =>
        new() { Text = text, DialogResult = result, Left = U(x), Top = U(y), Width = U(w), Height = U(h) };

    public static string? Input(string title, string prompt, string initial = "", bool allowEmpty = false)
    {
        TextBox? tb = null;
        using var dlg = Make(title, (f, U) =>
        {
            // The prompt wraps, so the rest is laid out below its measured height.
            var promptH = TextRenderer.MeasureText(prompt, f.Font, new Size(U(356), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            var lbl = new Label { Text = prompt, UseMnemonic = false, Left = U(12), Top = U(12), Width = U(356), Height = promptH + U(2), AutoSize = false };
            tb = new TextBox { Text = initial, Left = U(12), Top = lbl.Bottom + U(8), Width = U(356) };
            var ok = Btn("OK", DialogResult.OK, 212, 0, 75, U);
            var cancel = Btn("Cancel", DialogResult.Cancel, 293, 0, 75, U);
            ok.Top = cancel.Top = tb.Bottom + U(14);
            f.ClientSize = new Size(U(380), ok.Bottom + U(12));
            f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };
        });
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        var v = tb!.Text.Trim();
        return v.Length == 0 && !allowEmpty ? null : v;
    }

    public static void Text(string title, string text)
    {
        using var dlg = Make(title, (f, U) =>
        {
            f.ClientSize = new Size(U(460), U(306));
            var tb = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim(),
                Left = U(10), Top = U(10), Width = U(440), Height = U(250),
            };
            MonoFont(f, tb, 13);
            var ok = Btn("Close", DialogResult.OK, 375, 269, 75, U);
            f.Controls.AddRange(new Control[] { tb, ok });
            f.AcceptButton = ok; f.CancelButton = ok;
            f.Shown += (_, _) => { tb.SelectionStart = 0; tb.SelectionLength = 0; ok.Focus(); };
        });
        dlg.ShowDialog();
    }

    /// <summary>"Specify Hubs": manage the manually specified servers (MANUAL HUB ADD/REMOVE).</summary>
    public static async Task HubsAsync(Controller c)
    {
        ListBox? list = null;
        TextBox? add = null;
        async Task Reload()
        {
            var text = await c.Client.ManualHubListAsync();
            list!.Items.Clear();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Items.Add(line);
        }
        using var dlg = Make("Specify Servers", (f, U) =>
        {
            f.ClientSize = new Size(U(400), U(290));
            var lbl = new Label { Text = "Servers added manually (host[:port] or EasyFind address):", Left = U(12), Top = U(10), Width = U(376), Height = U(20), AutoSize = false };
            list = new ListBox { Left = U(12), Top = U(34), Width = U(376), Height = U(150) };
            add = new TextBox { Left = U(12), Top = U(196), Width = U(268) };
            var addBtn = Btn("Add", DialogResult.None, 288, 194, 100, U);
            var rem = Btn("Remove selected", DialogResult.None, 12, 246, 130, U);
            var remAll = Btn("Remove all", DialogResult.None, 148, 246, 90, U);
            var close = Btn("Close", DialogResult.OK, 313, 246, 75, U);
            f.Controls.AddRange(new Control[] { lbl, list, add, addBtn, rem, remAll, close });
            f.AcceptButton = addBtn; f.CancelButton = close;

            addBtn.Click += async (_, _) =>
            {
                var a = add.Text.Trim(); if (a.Length == 0) return;
                await c.RunAsync(x => x.ManualHubAddAsync(a), "Could not add server");
                add.Clear(); await Reload();
            };
            rem.Click += async (_, _) =>
            {
                if (list.SelectedItem is string s) { await c.RunAsync(x => x.ManualHubRemoveAsync(s), "Could not remove server"); await Reload(); }
            };
            remAll.Click += async (_, _) => { await c.RunAsync(x => x.ManualHubRemoveAllAsync(), "Could not remove servers"); await Reload(); };
            f.Shown += async (_, _) =>
            {
                try { await Reload(); } catch (Exception ex) { Message(ex.Message); }
            };
        });
        dlg.ShowDialog();
    }

    public static async Task LicensesAsync(Controller c)
    {
        TextBox? tb = null;
        async Task Reload() => tb!.Text = (await c.Client.ListLicensesAsync()).Trim().Replace("\n", "\r\n");
        using var dlg = Make("Licenses", (f, U) =>
        {
            f.ClientSize = new Size(480, 290).ScaleBy(U);
            var lbl = new Label { Text = "Licensed servers:", Left = U(12), Top = U(10), Width = U(450), Height = U(20), AutoSize = false };
            tb = new TextBox
            {
                Multiline = true, ReadOnly = true, Left = U(12), Top = U(32), Width = U(456), Height = U(130),
                ScrollBars = ScrollBars.Both, WordWrap = false,
            };
            MonoFont(f, tb, 12);
            var lbl2 = new Label { Text = "Enter a license key to apply to all connected servers:", Left = U(12), Top = U(174), Width = U(450), Height = U(20), AutoSize = false };
            var key = new TextBox { Left = U(12), Top = U(198), Width = U(352) };
            var apply = Btn("Apply", DialogResult.None, 372, 196, 96, U);
            var close = Btn("Close", DialogResult.OK, 393, 248, 75, U);
            f.Controls.AddRange(new Control[] { lbl, tb, lbl2, key, apply, close });
            f.CancelButton = close;
            apply.Click += async (_, _) =>
            {
                var k = key.Text.Trim(); if (k.Length == 0) return;
                var r = await c.RunAsync(x => x.LicenseServerAsync(k));
                Message(r, "License", MessageBoxButtons.OK, MessageBoxIcon.Information);
                key.Clear(); await Reload();
            };
            f.Shown += async (_, _) =>
            {
                try { await Reload(); } catch (Exception ex) { tb.Text = ex.Message; }
            };
        });
        dlg.ShowDialog();
    }

    private static Size ScaleBy(this Size s, Func<int, int> U) => new(U(s.Width), U(s.Height));

    /// <summary>One table with the details of every device on a server (a DPI-following window, see DeviceTableWindow).</summary>
    public static void DeviceTable(VhServer server)
    {
        using var w = new DeviceTableWindow(server);
        w.ShowNearDialog();
    }

    /// <summary>
    /// Replacement for MessageBox.Show that opens next to where the user clicked (MessageBox always
    /// centres on the screen) and stays fully visible.
    /// </summary>
    public static DialogResult Message(string text, string caption = "VirtualHere", MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        Icon? img = icon switch
        {
            MessageBoxIcon.Warning => SystemIcons.Warning,
            MessageBoxIcon.Question => SystemIcons.Question,
            MessageBoxIcon.Information => SystemIcons.Information,
            MessageBoxIcon.Error => SystemIcons.Error,
            _ => null,
        };
        using var dlg = Make(caption, (f, U) =>
        {
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding(U(16));

            var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
            if (img != null)
                layout.Controls.Add(new PictureBox { Image = img.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, U(16), 0) }, 0, 0);
            else
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            layout.Controls.Add(new Label { Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(U(340), 0), Margin = new Padding(0, U(4), 0, 0) }, 1, 0);

            var bar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, U(20), 0, 0) };
            layout.Controls.Add(bar, 0, 1);
            layout.SetColumnSpan(bar, 2);

            var defs = buttons switch
            {
                MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
                MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
                _ => new[] { ("OK", DialogResult.OK) },
            };
            var made = new List<Button>();
            foreach (var (label, result) in defs)
                made.Add(new Button { Text = label, DialogResult = result, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(U(80), U(27)), Margin = new Padding(U(8), 0, 0, 0) });
            for (int i = made.Count - 1; i >= 0; i--) bar.Controls.Add(made[i]);   // right-to-left flow: last added is leftmost

            f.Controls.Add(layout);
            int def = Math.Min((int)defaultButton / 256, made.Count - 1);          // Button1/2/3 = 0, 256, 512
            f.AcceptButton = made[def];
            f.CancelButton = made.FirstOrDefault(b => b.DialogResult is DialogResult.No or DialogResult.Cancel) ?? made[0];
            f.Shown += (_, _) => made[def].Focus();
        });
        return dlg.ShowDialog();
    }
}
