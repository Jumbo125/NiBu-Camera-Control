using System;
using System.IO;
using System.Windows.Forms;

namespace Fotobox.WebView2Host;

internal static class Program
{
    public const string HardcodedTitle = "NiBu-Photobox-Browser";
    public const string HardcodedIconRelativePath = "Assets\\app.ico";

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var configPath = Path.Combine(AppContext.BaseDirectory, "init.json");
        var config = AppConfig.Load(configPath);
        var baseDirectory = Path.GetDirectoryName(configPath)!;

        ApplyCommandLineOverrides(config, args);

        Application.Run(new MainForm(config, baseDirectory));
    }

    private static void ApplyCommandLineOverrides(AppConfig config, string[] args)
    {
        foreach (var rawArg in args)
        {
            if (string.IsNullOrWhiteSpace(rawArg))
                continue;

            var arg = rawArg.Trim();

            if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                config.Url = arg.Substring("--url=".Length).Trim().Trim('"');
                continue;
            }

            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("--port=".Length).Trim().Trim('"');
                if (int.TryParse(value, out var port) && port > 0 && port < 65536)
                {
                    config.DefaultPort = port;
                }
                continue;
            }

            if (arg.StartsWith("--kiosk=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("--kiosk=".Length).Trim().Trim('"');

                if (bool.TryParse(value, out var kiosk))
                {
                    config.Kiosk = kiosk;
                    continue;
                }

                if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    config.Kiosk = true;
                    continue;
                }

                if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    config.Kiosk = false;
                    continue;
                }
            }
        }
    }
}