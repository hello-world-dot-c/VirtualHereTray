namespace VirtualHereTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A restarted instance (see TrayApp.RestartSelf) waits for its predecessor to release the mutex instead of
        // handing over to it.
        bool restarted = args.Contains("--restarted");
        bool demo = args.Contains("--demo");           // made-up data, no real client involved
        if (demo) { Controller.Demo = true; VhClient.Demo = true; }
        string suffix = demo ? ".Demo" : "";
        var deadline = DateTime.UtcNow.AddSeconds(restarted ? 10 : 0);
        Mutex mutex; bool first;
        while (true)
        {
            mutex = new Mutex(true, @"Local\VirtualHereTray.SingleInstance" + suffix, out first);
            if (first || DateTime.UtcNow >= deadline) break;
            mutex.Dispose();
            Thread.Sleep(200);
        }
        using var _m = mutex;
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VirtualHereTray.Show" + suffix);
        if (!first) { show.Set(); return; }       // ask the running instance to open its window

        ApplicationConfiguration.Initialize();
        ToolStripManager.Renderer = new DpiMenuRenderer();
        // Install the WinForms context up front so async continuations in the controller resume on the UI thread.
        var sync = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sync);
        var app = new TrayApp();
        var waiter = new Thread(() =>
        {
            while (show.WaitOne()) sync.Post(_ => app.ShowManager(), null);
        }) { IsBackground = true };
        waiter.Start();
        Application.Run(app);
    }
}
