namespace Admiral;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "Admiral screen next|back": one FN + arrow step from the command line (for testing), then exit.
        if (args is ["screen", var dir, ..])
        {
            AttachConsole(-1);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            return ScreenStep(dir == "back" ? -1 : 1).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, Companions.InstanceMutex, out bool first);
        if (!first)
        {
            // Already running: open its tray menu instead of starting a second copy.
            if (EventWaitHandle.TryOpenExisting(Companions.ShowMenuEvent, out var show)) { show.Set(); show.Dispose(); }
            return 0;
        }
        Application.Run(new TrayApp());
        return 0;
    }

    private static async Task<int> ScreenStep(int dir)
    {
        using var screen = new ScreenController { Log = s => Console.WriteLine("  . " + s) };
        var switcher = new ScreenSwitcher(screen) { Log = Console.WriteLine };
        switcher.Switched += id => Console.WriteLine($"now showing entry {id}");
        await switcher.StepAsync(dir);
        await screen.CloseAsync();
        Console.WriteLine(switcher.Status);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}
