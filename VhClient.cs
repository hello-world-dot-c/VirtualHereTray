using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;

namespace VirtualHereTray;

public enum DeviceState { Unknown = 0, Available = 1, Connecting = 2, InUseByMe = 3, InUseByOther = 4, Unavailable = 5 }

public sealed class VhDevice
{
    public string Address = "";          // e.g. "WORKBENCH.104"
    public string Vendor = "";
    public string Product = "";
    public string Nickname = "";
    public string Serial = "";
    public string BoundClient = "";
    public string ClientId = "";         // "(user)" of whoever holds the device
    public string BoundIp = "";
    public string AutoUse = "not-set";
    public string ComPort = "";
    public int VendorId, ProductId;
    public int InterfaceClass, InterfaceSubClass, InterfaceProtocol;
    public int NumPorts;
    public DeviceState State;
    public string DevId = "";            // the id part of the address, same on every entry of a duplicated server
    public string BoundUuid = "";
    public string ReleaseAddress = "";   // address to use for "stop using" (differs only when held via a hidden duplicate entry)

    public string DisplayName => !string.IsNullOrWhiteSpace(Nickname) ? Nickname.Trim() : Product.Trim();
    public bool AutoUseEnabled => !string.IsNullOrEmpty(AutoUse) && AutoUse != "not-set";
    public bool InUseByMe => State == DeviceState.InUseByMe;

    /// <summary>Who holds the device, e.g. "Alex at LAB-PC" (empty if unknown or hidden by the server).</summary>
    public string HolderText
    {
        get
        {
            var user = ClientId.Trim().Trim('(', ')').Trim();
            var host = !string.IsNullOrWhiteSpace(BoundClient) ? BoundClient.Trim() : BoundIp;
            if (user.Length > 0 && host.Length > 0) return $"{user} at {host}";
            return user.Length > 0 ? user : host;
        }
    }

    /// <summary>The original client's wording: "In use by Alex at LAB-PC".</summary>
    public string InUseByText => HolderText.Length > 0 ? "In use by " + HolderText : "In use by another client";
}

public sealed class VhServer
{
    public string Name = "";
    public string Host = "";
    public string Hostname = "";
    public int Port;
    public int ConnectionState;          // 3 = logged in / connected
    public bool Error;
    public int MaxDevices;               // license limit (0 = unlimited)
    public string Serial = "";
    public string ConnUuid = "";
    public int ConnectionId;
    public List<VhDevice> Devices = new();

    public bool Connected => ConnectionState == 3 && !Error;
    // The address prefix VirtualHere uses in "<hostname>.<id>" device addresses.
    public string AddressPrefix => Hostname;
}

public sealed class VhState
{
    public List<VhServer> Servers = new();
    public bool AutoFind, AutoUseAll, ReverseLookup, ReverseSslLookup;
    public bool RunningAsService;
    public IEnumerable<VhDevice> AllDevices => Servers.SelectMany(s => s.Devices);
    public int InUseCount => AllDevices.Count(d => d.InUseByMe);
}

/// <summary>
/// Talks to a running VirtualHere client through its documented IPC named pipe (\\.\pipe\vhclient),
/// the same channel "vhui64.exe -t &lt;command&gt;" uses.
/// </summary>
public sealed class VhClient
{
    private const string PipeName = "vhclient";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static bool IsBackendRunning()
    {
        try { return File.Exists(@"\\.\pipe\" + PipeName); }
        catch { return false; }
    }

    /// <summary>Demo mode (--demo): nothing is ever sent to a real client.</summary>
    public static bool Demo;

    private static string DemoReply(string command) =>
        command.StartsWith("DEVICE INFO,") || command.StartsWith("SERVER INFO,")
            ? string.Join("\n", "NAME: Demo", "STATE: Logged in", "ADDRESS: " + command[(command.IndexOf(',') + 1)..], "IN USE BY: NO ONE", "(demo data)")
            : "OK";

    public async Task<string> SendAsync(string command, int timeoutMs = 8000)
    {
        if (Demo) return DemoReply(command);
        await _gate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);
            try { pipe.ReadMode = PipeTransmissionMode.Message; } catch { /* byte mode server */ }

            var data = Encoding.UTF8.GetBytes(command + "\n");
            await pipe.WriteAsync(data, cts.Token);
            await pipe.FlushAsync(cts.Token);

            // The server answers on a byte-mode pipe and does not close it, so there is no framing:
            // stop at a known terminator, otherwise once the stream has gone quiet.
            var sb = new StringBuilder();
            var buf = new byte[65536];
            var dec = Encoding.UTF8.GetDecoder();
            var chars = new char[65536];
            while (true)
            {
                int n;
                if (sb.Length == 0) n = await pipe.ReadAsync(buf, cts.Token);
                else
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    idle.CancelAfter(150);
                    try { n = await pipe.ReadAsync(buf, idle.Token); }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested) { break; }
                }
                if (n <= 0) break;
                int c = dec.GetChars(buf, 0, n, chars, 0);
                sb.Append(chars, 0, c);
                if (IsComplete(command, sb)) break;
            }
            return sb.ToString();
        }
        finally { _gate.Release(); }
    }

    private static bool IsComplete(string command, StringBuilder sb)
    {
        if (command == "GET CLIENT STATE") return sb.Length >= 8 && sb.ToString(sb.Length - Math.Min(sb.Length, 16), Math.Min(sb.Length, 16)).TrimEnd().EndsWith("</state>");
        if (command == "LIST") return sb.ToString().Contains("as a service");
        return false;
    }

    public async Task<VhState> GetStateAsync()
    {
        var xml = await SendAsync("GET CLIENT STATE");
        var state = ParseState(xml);

        // The flag toggles (auto-find, auto-use all, ...) are only reported by LIST.
        var list = await SendAsync("LIST");
        state.AutoFind = list.Contains("Auto-Find currently on", StringComparison.OrdinalIgnoreCase);
        state.AutoUseAll = list.Contains("Auto-Use All currently on", StringComparison.OrdinalIgnoreCase);
        state.ReverseLookup = list.Contains("Reverse Lookup currently on", StringComparison.OrdinalIgnoreCase);
        state.ReverseSslLookup = list.Contains("Reverse SSL Lookup currently on", StringComparison.OrdinalIgnoreCase);
        state.RunningAsService = list.Contains("running as a service", StringComparison.OrdinalIgnoreCase)
                                 && !list.Contains("not running as a service", StringComparison.OrdinalIgnoreCase);
        return state;
    }

    private static int I(XElement e, string a) => int.TryParse((string?)e.Attribute(a), out var v) ? v : 0;
    private static string S(XElement e, string a) => (string?)e.Attribute(a) ?? "";

    private const string NoUuid = "AAAAAAAAAAAAAAAAAAAAAA==";

    /// <summary>
    /// The client reports state 3 for a device held by *any* client. Whose it is follows from the connection the
    /// device is bound to: one of ours or someone else's.
    /// </summary>
    private static DeviceState DeviceStateOf(int raw, string boundUuid, HashSet<string> mine)
    {
        if (raw != 3) return (DeviceState)raw;
        if (string.IsNullOrEmpty(boundUuid) || boundUuid == NoUuid) return DeviceState.InUseByMe;   // no owner info: assume ours
        return mine.Contains(boundUuid) ? DeviceState.InUseByMe : DeviceState.InUseByOther;
    }

    private static string Ip(string b64)
    {
        try
        {
            var b = Convert.FromBase64String(b64);
            return b.Length == 4 && b.Any(x => x != 0) ? new System.Net.IPAddress(b).ToString() : "";
        }
        catch { return ""; }
    }

    public static VhState ParseState(string xml)
    {
        var state = new VhState();
        var start = xml.IndexOf("<state", StringComparison.Ordinal);
        if (start < 0) return state;
        var root = XDocument.Parse(xml[start..]).Root!;
        foreach (var se in root.Elements("server"))
        {
            var ce = se.Element("connection");
            if (ce == null) continue;
            var server = new VhServer
            {
                Name = S(ce, "serverName"),
                Host = S(ce, "host"),
                Hostname = S(ce, "hostname"),
                Port = I(ce, "port"),
                ConnectionState = I(ce, "state"),
                Error = S(ce, "error") == "true",
                MaxDevices = I(ce, "license_max_devices"),
                Serial = S(ce, "serverSerial"),
                ConnUuid = S(ce, "uuid"),
                ConnectionId = I(ce, "connectionId"),
            };
            if (string.IsNullOrWhiteSpace(server.Name)) server.Name = server.Hostname;
            foreach (var de in se.Descendants("device"))
            {
                var d = new VhDevice
                {
                    Address = server.Hostname + "." + S(de, "address"),
                    Vendor = S(de, "vendor").Trim(),
                    Product = S(de, "product"),
                    Nickname = S(de, "nickname"),
                    Serial = S(de, "deviceSerial"),
                    BoundClient = S(de, "boundClientHostname"),
                    BoundIp = Ip(S(de, "boundConnectionIp")),
                    ClientId = S(de, "clientId").Trim(),
                    AutoUse = S(de, "autoUse"),
                    ComPort = S(de, "comPortName"),
                    VendorId = I(de, "idVendor"),
                    ProductId = I(de, "idProduct"),
                    InterfaceClass = I(de, "firstInterfaceClass"),
                    InterfaceSubClass = I(de, "firstInterfaceSubClass"),
                    InterfaceProtocol = I(de, "firstInterfaceProtocol"),
                    NumPorts = I(de, "numPorts"),
                    DevId = S(de, "address"),
                    BoundUuid = S(de, "boundConnectionUUID"),
                    State = (DeviceState)I(de, "state"),     // refined below once all connections are known
                };
                d.ReleaseAddress = d.Address;
                server.Devices.Add(d);
            }
            state.Servers.Add(server);
        }

        // Every connection of this client counts as "me".
        var mine = state.Servers.Select(sv => sv.ConnUuid).Where(u => !string.IsNullOrEmpty(u)).ToHashSet();
        foreach (var d in state.AllDevices.ToList()) d.State = DeviceStateOf((int)d.State, d.BoundUuid, mine);

        state.Servers = Consolidate(state.Servers);
        return state;
    }

    /// <summary>
    /// The same server can be configured twice (e.g. found via EasyFind and also added by address). Show it once,
    /// always through the same connection: the connected entry with the lowest connection id. A device that is
    /// held through the other entry is released through that entry.
    /// </summary>
    private static List<VhServer> Consolidate(List<VhServer> all)
    {
        var result = new List<VhServer>();
        foreach (var group in all.GroupBy(sv => string.IsNullOrEmpty(sv.Serial) ? Guid.NewGuid().ToString() : sv.Serial))
        {
            var members = group.ToList();
            var primary = members.Where(m => m.Connected).OrderBy(m => m.ConnectionId).FirstOrDefault() ?? members[0];
            foreach (var d in primary.Devices.Where(d => d.InUseByMe && !string.IsNullOrEmpty(d.BoundUuid) && d.BoundUuid != primary.ConnUuid))
            {
                var holder = members.FirstOrDefault(m => m.ConnUuid == d.BoundUuid);
                var twin = holder?.Devices.FirstOrDefault(x => x.DevId == d.DevId);
                if (twin != null) d.ReleaseAddress = twin.Address;
            }
            result.Add(primary);
        }
        // keep the order the client reported the servers in
        return result.OrderBy(r => all.IndexOf(r)).ToList();
    }

    // ---- Commands -------------------------------------------------------------------------

    public Task<string> UseAsync(string address) => SendAsync("USE," + address, 30000);
    /// <summary>"Disconnect from user": asks the server to release a device held by another client.</summary>
    public Task<string> DisconnectUserAsync(string address) => SendAsync("STOP USING," + address, 30000);
    public Task<string> StopUsingAsync(string address) => SendAsync("STOP USING," + address, 30000);
    public Task<string> StopUsingAllLocalAsync() => SendAsync("STOP USING ALL LOCAL", 30000);
    public Task<string> StopUsingAllAsync() => SendAsync("STOP USING ALL", 30000);
    public Task<string> DeviceInfoAsync(string address) => SendAsync("DEVICE INFO," + address);
    public Task<string> ServerInfoAsync(string serverName) => SendAsync("SERVER INFO," + serverName);
    public Task<string> RenameDeviceAsync(string address, string nickname) => SendAsync($"DEVICE RENAME,{address},{nickname}");
    public Task<string> RenameServerAsync(string hostPort, string name) => SendAsync($"SERVER RENAME,{hostPort},{name}");
    public Task<string> ToggleAutoFindAsync() => SendAsync("AUTOFIND");
    public Task<string> ToggleAutoUseAllAsync() => SendAsync("AUTO USE ALL");
    public Task<string> ToggleReverseAsync() => SendAsync("REVERSE");
    public Task<string> ToggleSslReverseAsync() => SendAsync("SSLREVERSE");
    public Task<string> AutoUseHubAsync(string serverName) => SendAsync("AUTO USE HUB," + serverName);
    public Task<string> AutoUsePortAsync(string address) => SendAsync("AUTO USE PORT," + address);
    public Task<string> AutoUseDeviceAsync(string address) => SendAsync("AUTO USE DEVICE," + address);
    public Task<string> AutoUseDevicePortAsync(string address) => SendAsync("AUTO USE DEVICE PORT," + address);
    public Task<string> AutoUseClearAllAsync() => SendAsync("AUTO USE CLEAR ALL");
    public Task<string> ManualHubListAsync() => SendAsync("MANUAL HUB LIST");
    public Task<string> ManualHubAddAsync(string addr) => SendAsync("MANUAL HUB ADD," + addr);
    public Task<string> ManualHubRemoveAsync(string addr) => SendAsync("MANUAL HUB REMOVE," + addr);
    public Task<string> ManualHubRemoveAllAsync() => SendAsync("MANUAL HUB REMOVE ALL");
    public Task<string> ListLicensesAsync() => SendAsync("LIST LICENSES");
    public Task<string> LicenseServerAsync(string key) => SendAsync("LICENSE SERVER," + key);
    public Task<string> ExitAsync() => SendAsync("EXIT");

    // ---- Backend process management ------------------------------------------------------

    public static string? FindBackendPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var name in new[] { "vhui64", "vhui32", "vhuit64", "vhuit32" })
        {
            try
            {
                var p = Process.GetProcessesByName(name).FirstOrDefault();
                var path = p?.MainModule?.FileName;
                if (path != null) return path;
            }
            catch { /* access denied etc. */ }
        }
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Programs", "VirtualHere", "vhui64.exe");
        return File.Exists(def) ? def : null;
    }
}
