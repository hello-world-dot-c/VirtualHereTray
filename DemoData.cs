namespace VirtualHereTray;

/// <summary>
/// Made-up servers and devices for <c>VirtualHereTray.exe --demo</c>: lets you look at (and click through) the menu and
/// windows without a VirtualHere setup. Nothing is sent to any client in demo mode.
/// </summary>
internal static class DemoData
{
    private const string NoOwner = "AAAAAAAAAAAAAAAAAAAAAA==";
    private static string Ip(params byte[] b) => Convert.ToBase64String(b);

    private sealed record Dev(string Id, string Vendor, string Product, int Vid, int Pid, string Serial, int Class, int Proto,
        string Holder = "", string Com = "");

    // Holder: "" = free, "me" = used by this PC, otherwise "User|Host|ip" for another client.
    public static string Xml()
    {
        var workshop = new[]
        {
            new Dev("92", "Logitech", "USB Optical Mouse", 0x046d, 0xc043, "", 3, 2),
            new Dev("87", "Microsoft", "Wired Keyboard 600", 0x045e, 0x07f8, "", 3, 1),
            new Dev("104", "Focusrite", "Scarlett Solo USB", 0x1235, 0x8211, "Y7MMWTB244AA5C", 1, 0, "me"),
            new Dev("63", "Logitech", "HD Pro Webcam C920", 0x046d, 0x082d, "A1B2C3D4", 14, 0),
            new Dev("51", "SanDisk", "Ultra USB 3.0", 0x0781, 0x5581, "4C530001230", 8, 0, "Alex|LAB-PC|192.168.0.23"),
            new Dev("35", "MSI", "LED Controller", 0x1462, 0x7d25, "A02022110205", 3, 0),
        };
        var lab = new[]
        {
            new Dev("1124", "STMicroelectronics", "STLINK-V3", 0x0483, 0x374e, "002F00343234510436303532", 255, 255, "me", "COM7"),
            new Dev("1123", "STMicroelectronics", "STLINK-V3", 0x0483, 0x374e, "0033002B3234510436303532", 255, 255, "Alex|LAB-PC|192.168.0.23"),
            new Dev("1122", "Silicon Labs", "CP2102 USB to UART Bridge Controller", 0x10c4, 0xea60, "0001", 255, 0),
            new Dev("1121", "Silicon Labs", "CP2102 USB to UART Bridge Controller", 0x10c4, 0xea60, "0002", 255, 0),
        };
        return "<state>" + Server("1", "ME1", "Workshop Server", "WORKBENCH", "S-WORKSHOP", "192.168.0.10", workshop)
                         + Server("2", "ME2", "Lab Raspberry Pi", "lab-pi", "S-LABPI", "192.168.0.20", lab) + "</state>";
    }

    private static string Server(string id, string uuid, string name, string host, string serial, string ip, Dev[] devs)
    {
        var sb = new System.Text.StringBuilder("<server>");
        sb.Append($"<connection connectionId=\"{id}\" serverName=\"{name}\" hostname=\"{host}\" serverSerial=\"{serial}\" state=\"3\" error=\"false\" host=\"{ip}\" port=\"7575\" license_max_devices=\"0\" uuid=\"{uuid}\"/>");
        foreach (var d in devs)
        {
            string bound = d.Holder == "" ? NoOwner : d.Holder == "me" ? uuid : "SOMEONE-ELSE";
            var other = d.Holder.Split('|');
            string client = d.Holder is "" or "me" ? "" : other[1];
            string user = d.Holder is "" or "me" ? "" : $" ({other[0]})";
            string boundIp = d.Holder is "" or "me" ? "AAAAAA==" : Ip(192, 168, 0, 23);
            sb.Append($"<device vendor=\"{d.Vendor}\" product=\"{d.Product}\" idVendor=\"{d.Vid}\" idProduct=\"{d.Pid}\" address=\"{d.Id}\" " +
                      $"state=\"{(d.Holder == "" ? 1 : 3)}\" connectionUUID=\"{uuid}\" boundConnectionUUID=\"{bound}\" boundConnectionIp=\"{boundIp}\" " +
                      $"boundClientHostname=\"{client}\" clientId=\"{user}\" deviceSerial=\"{d.Serial}\" nickname=\"\" firstInterfaceClass=\"{d.Class}\" " +
                      $"firstInterfaceProtocol=\"{d.Proto}\" numPorts=\"0\" comPortName=\"{d.Com}\" autoUse=\"not-set\"/>");
        }
        return sb.Append("</server>").ToString();
    }
}
