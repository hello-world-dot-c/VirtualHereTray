using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace VirtualHereTray;

public sealed class Settings
{
    public string? ClientPath { get; set; }
    public bool AutoStartClient { get; set; } = true;
    public bool HideClientWindow { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirtualHereTray", "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Owns the VirtualHere connection, the polled state, and every action the UI can trigger.</summary>
public sealed class Controller
{
    public readonly VhClient Client = new();
    public readonly Settings Settings = Settings.Load();
    public VhState? State { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;
    public event Action<string, string, bool>? Notify;      // title, text, isError

    private readonly Timer _timer = new() { Interval = 2000 };
    private string _signature = "";
    private bool _refreshing;
    private bool _launching;

    /// <summary>Set by --demo: show made-up data and never touch a real VirtualHere client.</summary>
    public static bool Demo;

    public void Start()
    {
        if (Demo)
        {
            State = VhClient.ParseState(DemoData.Xml());
            State.AutoFind = true;
            Changed?.Invoke();
            return;
        }
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = StartupAsync();
    }

    private async Task StartupAsync()
    {
        if (!VhClient.IsBackendRunning() && Settings.AutoStartClient) await LaunchBackendAsync();
        await RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    // ---- state polling -------------------------------------------------------------------

    public async Task RefreshAsync()
    {
        if (Demo) return;
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            VhState? st = null; string? err = null;
            try { st = await Client.GetStateAsync(); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.Xml.XmlException)
            {
                Log(ex);
                err = _launching ? "Starting VirtualHere client…" : "VirtualHere client is not running";
            }
            State = st; Error = err;
            Watchdog(st != null);
            var sig = Signature(st, err);
            if (sig != _signature) { _signature = sig; Changed?.Invoke(); }
        }
        finally { _refreshing = false; }
    }

    // ---- keeping the client alive --------------------------------------------------------

    private const int MaxRestartAttempts = 5;
    private bool _suppressRestart;      // the user shut the client down on purpose
    private bool _wasUp;
    private int _restartAttempts;
    private DateTime _nextRestart = DateTime.MinValue;

    /// <summary>Restarts a client that disappeared (crash, killed), backing off 5 s, 10 s, 20 s… up to 5 tries.</summary>
    private void Watchdog(bool up)
    {
        if (up)
        {
            if (!_wasUp && _restartAttempts > 0) Notify?.Invoke("VirtualHere", "The VirtualHere client had stopped and was restarted.", false);
            if (!_wasUp) _suppressRestart = false;     // only a fresh start clears it, not the poll that precedes the exit
            _wasUp = true; _restartAttempts = 0;
            return;
        }
        _wasUp = false;
        if (_launching || _suppressRestart || !Settings.AutoStartClient) return;
        if (_restartAttempts >= MaxRestartAttempts || DateTime.UtcNow < _nextRestart) return;
        _restartAttempts++;
        _nextRestart = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, _restartAttempts - 1)));
        _ = RestartAsync();
    }

    private async Task RestartAsync()
    {
        await LaunchBackendAsync();
        if (!VhClient.IsBackendRunning() && _restartAttempts >= MaxRestartAttempts)
            Notify?.Invoke("VirtualHere", "The VirtualHere client stopped and could not be restarted. Use \"Start VirtualHere client\" in the tray menu.", true);
    }

    /// <summary>User-initiated start: clears any "shut down on purpose" state and the retry budget.</summary>
    public async Task StartClientAsync()
    {
        _suppressRestart = false; _restartAttempts = 0; _nextRestart = DateTime.MinValue;
        await LaunchBackendAsync();
        await RefreshAsync();
    }

    /// <summary>User-initiated shut down: the watchdog must not bring the client back.</summary>
    public async Task ShutdownClientAsync()
    {
        _suppressRestart = true;
        await RunAsync(x => x.ExitAsync());
    }

    /// <summary>Diagnostics for rare events (DPI changes). Appends to dpi.log, trimmed past 200 KB.</summary>
    internal static void LogInfo(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirtualHereTray");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "dpi.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    internal static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirtualHereTray");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last-error.txt"), DateTime.Now + "\n" + ex);
        }
        catch { }
    }

    private static string Signature(VhState? s, string? err)
    {
        if (s == null) return "E:" + err;
        var sb = new System.Text.StringBuilder();
        sb.Append(s.AutoFind).Append(s.AutoUseAll).Append(s.ReverseLookup).Append(s.ReverseSslLookup);
        foreach (var srv in s.Servers)
        {
            sb.Append('|').Append(srv.Name).Append(srv.ConnectionState).Append(srv.Error);
            foreach (var d in srv.Devices)
                sb.Append(';').Append(d.Address).Append(d.DisplayName).Append((int)d.State).Append(d.HolderText).Append(d.AutoUse).Append(d.ComPort);
        }
        return sb.ToString();
    }

    // ---- actions -------------------------------------------------------------------------

    /// <summary>Runs a command, reports anything other than "OK" to the user, then refreshes immediately.</summary>
    public async Task<string> RunAsync(Func<VhClient, Task<string>> action, string? failTitle = null)
    {
        string result;
        try { result = (await action(Client)).Trim(); }
        catch (Exception ex) { result = "ERROR: " + ex.Message; }
        if (failTitle != null && !result.Equals("OK", StringComparison.OrdinalIgnoreCase) && result.Length > 0)
            Notify?.Invoke(failTitle, result, true);
        await RefreshAsync();
        return result;
    }

    public Task ToggleDeviceAsync(VhDevice d)
    {
        if (Demo)       // in demo mode a click just flips the device between free and used
        {
            if (d.State == DeviceState.Available) d.State = DeviceState.InUseByMe;
            else if (d.State == DeviceState.InUseByMe) d.State = DeviceState.Available;
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        switch (d.State)
        {
            case DeviceState.InUseByMe:
            case DeviceState.Connecting:
                return RunAsync(c => c.StopUsingAsync(d.ReleaseAddress), "Could not stop using " + d.DisplayName);
            case DeviceState.InUseByOther:
                Notify?.Invoke(d.DisplayName, d.InUseByText + ". Use \"Disconnect from user…\" in the device menu to take it over.", true);
                return Task.CompletedTask;
            default:
                return RunAsync(c => c.UseAsync(d.Address), "Could not use " + d.DisplayName);
        }
    }

    /// <summary>Forces a device held by another client to be released, then re-reads the state to see whether it worked.</summary>
    public async Task DisconnectUserAsync(VhDevice d)
    {
        var holder = d.HolderText.Length > 0 ? d.HolderText : "another client";
        if (Dialogs.Message($"\"{d.DisplayName}\" is in use by {holder}.\n\nDisconnect it from that user? Whatever they are doing with the device is interrupted.",
                "VirtualHere", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var r = await RunAsync(c => c.DisconnectUserAsync(d.Address));
        var now = State?.AllDevices.FirstOrDefault(x => x.Address == d.Address);
        if (now is { State: DeviceState.InUseByOther })
            Notify?.Invoke("Could not disconnect " + d.DisplayName, string.IsNullOrWhiteSpace(r) || r == "OK" ? "The server kept the device with " + holder + "." : r, true);
    }

    /// <summary>Uses every free device on a server, one after the other, and reports the ones that failed.</summary>
    public async Task UseAllAsync(VhServer s)
    {
        var failed = new List<string>();
        foreach (var d in s.Devices.Where(d => d.State == DeviceState.Available).ToList())
        {
            string r;
            try { r = (await Client.UseAsync(d.Address)).Trim(); } catch (Exception ex) { r = "ERROR: " + ex.Message; }
            if (!r.Equals("OK", StringComparison.OrdinalIgnoreCase)) failed.Add($"{d.DisplayName}: {r}");
        }
        if (failed.Count > 0) Notify?.Invoke("Could not use all devices on " + s.Name, string.Join("\n", failed), true);
        await RefreshAsync();
    }

    public VhServer? ServerOf(VhDevice d) => State?.Servers.FirstOrDefault(s => s.Devices.Contains(d));

    // ---- the real VirtualHere client process ---------------------------------------------

    public string? ClientPath => VhClient.FindBackendPath(Settings.ClientPath);

    /// <summary>
    /// Starts the original client on a private, never-displayed desktop. Its main window, tray icon and
    /// any dialogs are created there, so none of them ever reach the user's screen, while the IPC pipe
    /// (which isn't tied to a desktop) keeps working. The client keeps running if this app exits.
    /// </summary>
    public async Task LaunchBackendAsync(bool visible = false)
    {
        var path = ClientPath;
        if (path == null) { Error = "VirtualHere client not found"; Notify?.Invoke("VirtualHere", "Could not find vhui64.exe. Set its location from the tray menu.", true); return; }
        _launching = true;
        try
        {
            if (visible || !Settings.HideClientWindow)
                Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true });
            else
                StartOnHiddenDesktop(path);
            for (int i = 0; i < 50 && !VhClient.IsBackendRunning(); i++) await Task.Delay(200);
        }
        catch (Exception ex) { Notify?.Invoke("VirtualHere", "Failed to start client: " + ex.Message, true); }
        finally { _launching = false; }
    }

    private static void StartOnHiddenDesktop(string exe)
    {
        var desk = CreateDesktopW("VirtualHereTrayHidden", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, IntPtr.Zero);
        if (desk == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // The desktop handle is deliberately left open: it must outlive the client process.
        var si = new STARTUPINFO { lpDesktop = @"WinSta0\VirtualHereTrayHidden" };
        si.cb = Marshal.SizeOf(si);
        if (!CreateProcessW(null, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Path.GetDirectoryName(exe), ref si, out var pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    }

    /// <summary>Shows the original window; if the client runs on the hidden desktop, offers to restart it visibly.</summary>
    public async Task ShowOriginalClientAsync()
    {
        var path = ClientPath;
        var procs = path == null ? Array.Empty<Process>() : Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        int shown = 0;
        foreach (var p in procs) shown += ShowWindowsOf(p.Id);
        if (shown > 0) return;
        if (procs.Length > 0 && Dialogs.Message(
                "The VirtualHere client is running on a hidden desktop, so its window can't be shown.\n\n" +
                "Restart it with a visible window? This disconnects all devices you are using.",
                "VirtualHere", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (procs.Length > 0)
        {
            _suppressRestart = true;      // we relaunch it ourselves, visibly
            try { await Client.ExitAsync(); } catch { }
            for (int i = 0; i < 50 && VhClient.IsBackendRunning(); i++) await Task.Delay(100);
        }
        await LaunchBackendAsync(visible: true);
    }

    // ---- Win32 helpers -------------------------------------------------------------------

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    private static int ShowWindowsOf(int pid)
    {
        int n = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && GetWindowTextLength(h) > 0) { ShowWindow(h, 9); SetForegroundWindow(h); n++; }
            return true;
        }, IntPtr.Zero);
        return n;
    }

    // ---- start with Windows --------------------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("VirtualHereTray") != null; }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (value) k.SetValue("VirtualHereTray", "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue("VirtualHereTray", false);
        }
    }
}
