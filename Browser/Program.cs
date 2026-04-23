using System;
using System.IO;
using System.Windows.Forms;

namespace Fotobox.WebView2Host;

internal static class Program
{
    public const string HardcodedTitle = "NiBu-Photobox-Browser";
    public const string HardcodedIconRelativePath = "Assets\\app.ico";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var configPath = Path.Combine(AppContext.BaseDirectory, "init.json");
        var config = AppConfig.Load(configPath);
        var baseDirectory = Path.GetDirectoryName(configPath)!;

        Application.Run(new MainForm(config, baseDirectory));
    }
}