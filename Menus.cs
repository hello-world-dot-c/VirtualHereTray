namespace VirtualHereTray;

/// <summary>Builds the menu items shared by the tray menu and the manager window's context menus.</summary>
public static class Menus
{
    private static ToolStripMenuItem Item(string text, Func<Task> action, bool check = false, bool enabled = true, Image? image = null)
    {
        var mi = new ToolStripMenuItem(text) { Checked = check, Enabled = enabled, Image = image };
        var button = MouseButtons.None;
        mi.MouseDown += (_, e) => button = e.Button;    // Click is raised for right-clicks too
        mi.Click += async (_, _) =>
        {
            if (button == MouseButtons.Right) return;
            Dialogs.SetAnchor();
            try { await action(); }
            catch (Exception ex) { Controller.Log(ex); Dialogs.Message(ex.Message, "VirtualHere", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        return mi;
    }

    public static ToolStripItem[] Device(Controller c, VhDevice d)
    {
        var server = c.ServerOf(d);
        var items = new List<ToolStripItem>();
        if (d.State == DeviceState.InUseByOther)
        {
            items.Add(Item(d.InUseByText, () => Task.CompletedTask, enabled: false));
            items.Add(Item("Disconnect from user…", () => c.DisconnectUserAsync(d)));
        }
        else
        {
            var inUse = d.State is DeviceState.InUseByMe or DeviceState.Connecting;
            items.Add(Item(inUse ? "Stop using this device" : "Use this device", () => c.ToggleDeviceAsync(d)));
        }
        items.Add(new ToolStripSeparator());
        items.Add(Item("Auto-use this device (any port)", () => c.RunAsync(x => x.AutoUseDeviceAsync(d.Address), "Auto-use"), d.AutoUse == "device"));
        items.Add(Item("Auto-use this device on this port", () => c.RunAsync(x => x.AutoUseDevicePortAsync(d.Address), "Auto-use"), d.AutoUse == "devicePort"));
        items.Add(Item("Auto-use any device on this port", () => c.RunAsync(x => x.AutoUsePortAsync(d.Address), "Auto-use"), d.AutoUse == "port"));
        items.Add(new ToolStripSeparator());
        items.Add(Item("Device info…", async () =>
        {
            var info = await c.Client.DeviceInfoAsync(d.Address);
            Dialogs.Text(d.DisplayName, info);
        }));
        items.Add(Item("Rename device…", async () =>
        {
            var n = Dialogs.Input("Rename device", "Nickname for " + d.Product.Trim() + " (empty to reset):", d.Nickname, allowEmpty: true);
            if (n != null) await c.RunAsync(x => x.RenameDeviceAsync(d.Address, n), "Rename failed");
        }));
        return items.ToArray();
    }

    public static ToolStripItem[] Server(Controller c, VhServer s)
    {
        var hostPort = s.Hostname + ":" + s.Port;
        var anyInUse = s.Devices.Any(d => d.InUseByMe);
        var allAuto = s.Devices.Count > 0 && s.Devices.All(d => d.AutoUse == "server");
        return new ToolStripItem[]
        {
            Item("Use all devices on this server", () => c.UseAllAsync(s),
                enabled: s.Connected && s.Devices.Any(d => d.State == DeviceState.Available)),
            Item("Stop using all devices on this server", async () =>
            {
                foreach (var d in s.Devices.Where(d => d.InUseByMe).ToList())
                    await c.Client.StopUsingAsync(d.ReleaseAddress);
                await c.RefreshAsync();
            }, enabled: anyInUse),
            Item("Auto-use all devices on this server", () => c.RunAsync(x => x.AutoUseHubAsync(s.Name), "Auto-use"), allAuto),
            new ToolStripSeparator(),
            Item("All device info (table)…", () => { Dialogs.DeviceTable(s); return Task.CompletedTask; }, enabled: s.Devices.Count > 0),
            Item("Server info…", async () => Dialogs.Text(s.Name, await c.Client.ServerInfoAsync(s.Name))),
            Item("Rename server…", async () =>
            {
                var n = Dialogs.Input("Rename server", "New name for " + s.Name + ":", s.Name);
                if (n != null) await c.RunAsync(x => x.RenameServerAsync(hostPort, n), "Rename failed");
            }),
        };
    }

    /// <summary>The global options (Tools menu equivalent).</summary>
    public static ToolStripItem[] Tools(Controller c)
    {
        var st = c.State;
        bool online = st != null;
        return new ToolStripItem[]
        {
            Item("Auto-find servers", () => c.RunAsync(x => x.ToggleAutoFindAsync(), "Auto-find"), st?.AutoFind == true, online),
            Item("Auto-use all devices", () => c.RunAsync(x => x.ToggleAutoUseAllAsync(), "Auto-use all"), st?.AutoUseAll == true, online),
            Item("Reverse lookup", () => c.RunAsync(x => x.ToggleReverseAsync(), "Reverse lookup"), st?.ReverseLookup == true, online),
            Item("Reverse SSL lookup", () => c.RunAsync(x => x.ToggleSslReverseAsync(), "Reverse SSL lookup"), st?.ReverseSslLookup == true, online),
            new ToolStripSeparator(),
            Item("Specify servers…", () => Dialogs.HubsAsync(c), enabled: online),
            Item("Licenses…", () => Dialogs.LicensesAsync(c), enabled: online),
            Item("Clear all auto-use settings", () => c.RunAsync(x => x.AutoUseClearAllAsync(), "Auto-use"), enabled: online),
            new ToolStripSeparator(),
            Item("Stop using all devices", () => c.RunAsync(x => x.StopUsingAllLocalAsync(), "Stop using all"), enabled: st?.InUseCount > 0),
            Item("Show original VirtualHere window", () => c.ShowOriginalClientAsync()),
            Item("Shut down VirtualHere client", async () =>
            {
                if (Dialogs.Message("Disconnect from all servers and shut down the VirtualHere client?", "VirtualHere",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await c.ShutdownClientAsync();
            }, enabled: online),
        };
    }
}
