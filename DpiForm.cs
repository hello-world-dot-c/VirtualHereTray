using System.Runtime.InteropServices;

namespace VirtualHereTray;

/// <summary>
/// Base class for any window that has more than a handful of controls or stays open for a while (manager window,
/// search, forms). It follows the monitor DPI while it is open (docking, undocking, RDP from a device with another
/// scale factor, dragging between monitors) and it opens without the user watching it being laid out.
///
/// How it works - and why each part exists (all measured, see references/dpi-and-scaling.md):
///  1. Build the UI once in 96-dpi "design pixels" with a 12 px font and call <see cref="CaptureDesign"/> at the end
///     of the constructor, before a handle exists and before WinForms can rescale anything. Never read Font.Height or
///     SystemInformation to derive metrics at construction: they are values for the process's start-up DPI.
///  2. Whenever the window's real DPI is known (handle created) or changes (WM_DPICHANGED), every control is re-scaled
///     from the recorded design values (never incrementally: no accumulating rounding errors).
///  3. WinForms rescales a child's own font/size when that child's handle is created on a monitor whose DPI differs
///     from the process's initial DPI, and rescales fonts after OnDpiChanged returns. So the values are put back in
///     each child's HandleCreated, in Load, in Shown and once more after Shown. Children that merely inherit the form
///     font are left alone (the ambient font is already final).
///  4. Re-applying must be cheap. Assigning a size to a live control resizes its window even when the size is
///     unchanged, and a full layout pass is hundreds of ms on a 70-control window. A value is therefore only assigned
///     if it differs, sizes are only rewritten when the DPI changed, and the layout pass is skipped if nothing changed.
///     (Re-applying everything on every event made a window "settle" for ~250 ms with ~14 visible intermediate states.)
///  5. <see cref="HideUntilSettled"/> keeps the window fully transparent until it has been laid out, then repaints every
///     child synchronously and reveals it: the user sees a finished window, not controls jumping around.
///  6. The class owns the window SIZE after a DPI change (it cancels WinForms' own handling): the size the user had,
///     in 96-dpi units, scaled to the new DPI, kept inside the new monitor's working area, never smaller than the
///     content needs (height), with scrolling as the last resort. It must not trust the rectangle Windows suggests:
///     DefWindowProc applies it before OnDpiChanged runs, WinForms scales MinimumSize on its own, and the suggestion
///     was observed not to match the content (window ended up at its old pixel size with the scaled controls clipped).
/// </summary>
public abstract class DpiForm : Form
{
    public const int DesignDpi = 96;
    private const float DesignFontPx = 12f;            // 9 pt at 96 dpi

    private readonly Dictionary<Control, Design> _design = new();
    private readonly HashSet<Control> _maxSizeFree = new();
    private int _appliedDpi = DesignDpi;
    private int _sizeDpi;                              // DPI the control sizes were last written for
    private int _windowDpi;
    private bool _captured, _changed, _sizesStale, _revealPending;
    private bool _sizing;                // true while this class resizes the window itself
    private bool _inDpiMessage;          // true while WM_DPICHANGED is processed (DefWindowProc resizes the window in it)
    private SizeF _logicalSize;          // outer window size in 96-dpi units (follows the user's resizing)
    private SizeF _logicalMin;           // MinimumSize in 96-dpi units

    /// <summary>The DPI this window was last told about (handle creation or WM_DPICHANGED); 0 before the handle exists.</summary>
    protected int WindowDpi => _windowDpi;

    protected DpiForm()
    {
        AutoScaleMode = AutoScaleMode.None;            // we scale; WinForms must not do it a second time
        Font = new Font("Segoe UI", DesignFontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        Icon = AppIcon.Get(32);                        // the app logo instead of the generic WinForms icon
    }

    /// <summary>96-dpi design pixels -> pixels at <paramref name="dpi"/>.</summary>
    protected static int Px(int dpi, int designPx) => Math.Max(1, (int)Math.Round(designPx * dpi / 96.0));

    /// <summary>DPI to lay out for: the window's own once it exists, otherwise the monitor it is about to open on.</summary>
    protected int DpiFor(Point near) => _windowDpi > 0 ? _windowDpi : Icons.DpiAt(near);

    /// <summary>Called after the controls were re-scaled and something changed (refit list columns, wrap widths, ...).</summary>
    protected virtual void OnDpiApplied() { }

    /// <summary>The form never writes this control's MaximumSize (it follows the window, e.g. a wrapping label).</summary>
    protected void LeaveMaxSizeAlone(Control c) => _maxSizeFree.Add(c);

    // ---- lifecycle ---------------------------------------------------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != 0x02E0 /*WM_DPICHANGED*/) { base.WndProc(ref m); return; }
        // DefWindowProc applies Windows' suggested rectangle before OnDpiChanged runs, while the old DPI is still
        // recorded: that resize must not be taken for the user's own resizing.
        _inDpiMessage = true;
        try { base.WndProc(ref m); }
        finally { _inDpiMessage = false; }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecordLogicalSize();
    }

    /// <summary>
    /// Remembers size and minimum size in dpi-independent units. Also called from Load/Shown: a derived class may set
    /// the size before the handle (and so the DPI) exists and later set the same size again, which raises no Resize.
    /// </summary>
    private void RecordLogicalSize()
    {
        if (_sizing || _inDpiMessage || _windowDpi <= 0 || WindowState != FormWindowState.Normal || Width <= 0 || Height <= 0) return;
        _logicalSize = new SizeF(Width * 96f / _windowDpi, Height * 96f / _windowDpi);
        _logicalMin = new SizeF(MinimumSize.Width * 96f / _windowDpi, MinimumSize.Height * 96f / _windowDpi);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_windowDpi <= 0) _windowDpi = (int)GetDpiForWindow(Handle);
        ApplyDpi(_windowDpi, force: false);
        AppIcon.ApplyTo(this, _windowDpi);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpi(_windowDpi, force: true);
        RecordLogicalSize();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyDpi(_windowDpi, force: true);             // child handles exist now
        BeginInvoke(() =>                              // posted: runs after derived OnShown work (placement, focus)
        {
            if (IsDisposed) return;
            ApplyDpi(_windowDpi, force: true);
            RecordLogicalSize();
            EnsureContentFits();
            Reveal();
        });
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);                  // raises the DpiChanged event for subscribers
        int oldDpi = _windowDpi > 0 ? _windowDpi : (e.DeviceDpiOld > 0 ? e.DeviceDpiOld : DesignDpi);
        _windowDpi = e.DeviceDpiNew > 0 ? e.DeviceDpiNew : (int)GetDpiForWindow(Handle);
        var before = Bounds;
        var logicalBefore = _logicalSize;

        e.Cancel = true;                       // we scale the controls and size the window ourselves (see class comment, 6)
        _sizing = true;
        try
        {
            ApplyDpi(_windowDpi, force: true);
            AppIcon.ApplyTo(this, _windowDpi);
            ResizeForDpi(oldDpi, _windowDpi, e.SuggestedRectangle);
            EnsureContentFits();
        }
        finally { _sizing = false; }
        // Evidence for the next "the window is the wrong size" report: what Windows suggested vs what was done.
        Controller.LogInfo($"{GetType().Name} DPI {oldDpi}->{_windowDpi}: was {before.Width}x{before.Height}; Windows suggested " +
            $"{e.SuggestedRectangle.Width}x{e.SuggestedRectangle.Height} at {e.SuggestedRectangle.Left},{e.SuggestedRectangle.Top}; " +
            $"now {Width}x{Height} at {Left},{Top}; work area {Screen.FromControl(this).WorkingArea}; logical {logicalBefore.Width:0}x{logicalBefore.Height:0}");

        BeginInvoke(() =>                      // WinForms may still rescale child fonts after this handler returns
        {
            if (IsDisposed) return;
            _sizing = true;
            try { ApplyDpi(_windowDpi, force: true); EnsureContentFits(); }
            finally { _sizing = false; }
        });
    }

    /// <summary>
    /// New outer size = the size the user had (in 96-dpi units) scaled to the new DPI; the position comes from Windows'
    /// suggestion. Both stay inside the working area of the monitor the window is moving to, and so does MinimumSize
    /// (a minimum larger than the monitor would push the window off the screen).
    /// </summary>
    private void ResizeForDpi(int oldDpi, int newDpi, Rectangle suggested)
    {
        if (WindowState != FormWindowState.Normal) return;
        var logical = _logicalSize.IsEmpty ? new SizeF(Width * 96f / oldDpi, Height * 96f / oldDpi) : _logicalSize;
        int w = (int)Math.Round(logical.Width * newDpi / 96f), h = (int)Math.Round(logical.Height * newDpi / 96f);
        // WinForms scales MinimumSize by itself, so scaling the current value again would double it: use the logical one.
        MinimumSize = _logicalMin.IsEmpty ? Size.Empty
            : new Size((int)Math.Round(_logicalMin.Width * newDpi / 96f), (int)Math.Round(_logicalMin.Height * newDpi / 96f));

        var wa = Screen.FromRectangle(suggested).WorkingArea;
        w = Math.Min(w, wa.Width);
        h = Math.Min(h, wa.Height);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
        SetBounds(Math.Max(wa.Left, Math.Min(suggested.Left, wa.Right - w)), Math.Max(wa.Top, Math.Min(suggested.Top, wa.Bottom - h)), w, h);
    }

    /// <summary>The control whose rows say how much room the content needs (default: the only child).</summary>
    protected virtual Control? LayoutRoot => Controls.Count == 1 ? Controls[0] : null;

    /// <summary>
    /// Makes sure everything is visible: grows the window to the height the content needs (up to the working area) and,
    /// if even that is not enough, lets it scroll vertically instead of clipping. Only the HEIGHT is enforced: a
    /// control's preferred width is not a requirement (wrapping text adapts, and using it made windows screen-wide).
    /// </summary>
    private void EnsureContentFits()
    {
        var root = LayoutRoot;
        if (root is null || !IsHandleCreated || WindowState != FormWindowState.Normal) return;

        int needClientH = RequiredClientHeight(root);
        var wa = Screen.FromControl(this).WorkingArea;
        int h = Math.Max(Height, Math.Min(needClientH + (Height - ClientSize.Height), wa.Height));
        if (h != Height)
        {
            bool was = _sizing;
            _sizing = true;                    // growing for the content is not the user's chosen size
            try { SetBounds(Left, Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h)), Width, h); }
            finally { _sizing = was; }
        }
        bool tooSmall = needClientH > ClientSize.Height;
        if (AutoScroll != tooSmall) AutoScroll = tooSmall;
        AutoScrollMinSize = tooSmall ? new Size(0, needClientH) : Size.Empty;
    }

    /// <summary>
    /// Client height the layout needs: rows that size to their content or have a fixed height count in full; rows that
    /// share the leftover space (percent rows, e.g. lists) only need a few lines. (TableLayoutPanel.GetPreferredSize
    /// would count every list at its default height and ask for far too much.)
    /// </summary>
    private int RequiredClientHeight(Control root)
    {
        if (root is TableLayoutPanel table)
        {
            table.PerformLayout();
            var rows = table.GetRowHeights();
            int sum = table.Padding.Vertical;
            for (int i = 0; i < rows.Length; i++)
                sum += i < table.RowStyles.Count && table.RowStyles[i].SizeType == SizeType.Percent ? Font.Height * 3 : rows[i];
            return sum;
        }
        return root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
    }

    // ---- opening without visible re-layout -----------------------------------------------------------------------

    /// <summary>
    /// Call right before <c>Show()</c> (when the window is not visible yet). The window stays transparent while it is
    /// created and laid out; after a synchronous repaint of all children it is revealed. Costs ~60-70 ms of latency,
    /// removes the white first frame and every intermediate layout. A timer reveals it anyway if Shown never arrives.
    /// </summary>
    protected void HideUntilSettled()
    {
        if (Visible || _revealPending) return;
        _revealPending = true;
        Opacity = 0;
        var guard = new System.Windows.Forms.Timer { Interval = 1500 };
        guard.Tick += (_, _) => { guard.Stop(); guard.Dispose(); Reveal(); };
        guard.Start();
    }

    private void Reveal()
    {
        if (!_revealPending || IsDisposed) return;
        _revealPending = false;
        if (IsHandleCreated) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 /*INVALIDATE*/ | 0x0080 /*ALLCHILDREN*/ | 0x0100 /*UPDATENOW*/);
        Opacity = 1;
    }

    // ---- scaling -------------------------------------------------------------------------------------------------

    private void ApplyDpi(int dpi, bool force)
    {
        if (dpi <= 0) dpi = DesignDpi;
        CaptureDesign();
        if (!force && dpi == _appliedDpi) return;
        _appliedDpi = dpi;
        _changed = false;
        _sizesStale = dpi != _sizeDpi;
        _sizeDpi = dpi;

        SuspendLayout();
        try { Apply(this, dpi); }
        finally { ResumeLayout(_changed); }            // a full layout pass only if something was assigned
        if (_changed) OnDpiApplied();
    }

    /// <summary>Records the 96-dpi metrics of every control. Call at the end of the derived constructor.</summary>
    protected void CaptureDesign()
    {
        if (_captured) return;
        _captured = true;
        var formFont = Font;
        Walk(this, c =>
        {
            var d = new Design
            {
                Font = c.Font,
                ExplicitFont = !ReferenceEquals(c, this) && !SameFont(c.Font, formFont),
                Margin = c.Margin, Padding = c.Padding, Min = c.MinimumSize, Max = c.MaximumSize, Size = c.Size, AutoSize = c.AutoSize,
            };
            if (c is TableLayoutPanel t)
            {
                d.Columns = t.ColumnStyles.Cast<ColumnStyle>().Select(s => (s.SizeType, s.Width)).ToArray();
                d.Rows = t.RowStyles.Cast<RowStyle>().Select(s => (s.SizeType, s.Height)).ToArray();
            }
            if (c is ListView lv) d.ListColumns = lv.Columns.Cast<ColumnHeader>().Select(h => h.Width).ToArray();
            _design[c] = d;
            if (!ReferenceEquals(c, this)) c.HandleCreated += OnChildHandleCreated;
        });
    }

    private void OnChildHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Control c && _design.TryGetValue(c, out var d) && d.ExplicitFont)
            c.Font = ScaleFont(d.Font, _appliedDpi / (float)DesignDpi);
    }

    private void Apply(Control root, int dpi)
    {
        float k = dpi / (float)DesignDpi;
        Walk(root, c =>
        {
            if (!_design.TryGetValue(c, out var d)) return;
            bool isForm = ReferenceEquals(c, this);

            if (isForm || d.ExplicitFont)
            {
                var font = ScaleFont(d.Font, k);
                if (SameFont(font, c.Font)) font.Dispose();
                else { c.Font = font; _changed = true; }
            }
            if (!isForm)
            {
                var margin = ScalePadding(d.Margin, k);
                if (c.Margin != margin) { c.Margin = margin; _changed = true; }

                // Auto-sized labels/buttons/panels measure themselves. Everything else takes its size from the design
                // value, otherwise a size grown at a higher DPI sticks as the new preferred size. Docked controls get
                // their size from the layout, so they are rewritten only when the DPI changed; free-standing ones are
                // also checked every time (WinForms may have rescaled them when their handle was created).
                bool selfSizing = d.AutoSize && c is Label or ButtonBase or Panel;
                if (!selfSizing)
                {
                    var size = ScaleSize(d.Size, dpi);
                    if (_sizesStale || (c.Dock == DockStyle.None && c.Size != size)) { c.Size = size; _changed = true; }
                }
                var min = ScaleSize(d.Min, dpi);
                if (c.MinimumSize != min) { c.MinimumSize = min; _changed = true; }
                var max = ScaleSize(d.Max, dpi);
                if (c.MaximumSize != max && !_maxSizeFree.Contains(c)) { c.MaximumSize = max; _changed = true; }
            }
            var padding = ScalePadding(d.Padding, k);
            if (c.Padding != padding) { c.Padding = padding; _changed = true; }

            if (c is TableLayoutPanel t)
            {
                for (int i = 0; i < d.Columns!.Length && i < t.ColumnStyles.Count; i++)
                    if (d.Columns[i].Type == SizeType.Absolute && t.ColumnStyles[i].Width != d.Columns[i].Value * k)
                    { t.ColumnStyles[i].Width = d.Columns[i].Value * k; _changed = true; }
                for (int i = 0; i < d.Rows!.Length && i < t.RowStyles.Count; i++)
                    if (d.Rows[i].Type == SizeType.Absolute && t.RowStyles[i].Height != d.Rows[i].Value * k)
                    { t.RowStyles[i].Height = d.Rows[i].Value * k; _changed = true; }
            }
            if (c is ListView lv && d.ListColumns is { } cols)
                for (int i = 0; i < cols.Length && i < lv.Columns.Count; i++)
                {
                    int w = (int)Math.Round(cols[i] * k);
                    if (lv.Columns[i].Width != w) { lv.Columns[i].Width = w; _changed = true; }
                }
        });
    }

    private static void Walk(Control c, Action<Control> action)
    {
        action(c);
        foreach (Control child in c.Controls) Walk(child, action);
    }

    private static bool SameFont(Font a, Font b) =>
        a.Name == b.Name && a.Style == b.Style && a.Unit == b.Unit && Math.Abs(a.Size - b.Size) < 0.01f;

    private static Font ScaleFont(Font design, float k)
    {
        float designPx = design.Unit == GraphicsUnit.Pixel ? design.Size : design.SizeInPoints * DesignDpi / 72f;
        return new Font(design.FontFamily, (float)Math.Round(designPx * k), design.Style, GraphicsUnit.Pixel);
    }

    private static Padding ScalePadding(Padding p, float k) => new(
        (int)Math.Round(p.Left * k), (int)Math.Round(p.Top * k), (int)Math.Round(p.Right * k), (int)Math.Round(p.Bottom * k));

    private static Size ScaleSize(Size s, int dpi) =>
        new(s.Width == 0 ? 0 : Px(dpi, s.Width), s.Height == 0 ? 0 : Px(dpi, s.Height));

    private sealed class Design
    {
        public Font Font = null!;
        public bool ExplicitFont, AutoSize;
        public Padding Margin, Padding;
        public Size Min, Max, Size;
        public (SizeType Type, float Value)[]? Columns, Rows;
        public int[]? ListColumns;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
}
