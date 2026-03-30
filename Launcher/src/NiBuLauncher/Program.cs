using NiBuLauncher.Services;

namespace NiBuLauncher;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            name: "Global\\NiBuPhotoboothLauncher",
            createdNew: out var createdNew);

        if (!createdNew)
            return;

        // Headless watchdog mode (Task Scheduler)
        if (args.Any(a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase)))
        {
            await Watchdog.RunAsync(appBaseDir: AppContext.BaseDirectory, CancellationToken.None);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}