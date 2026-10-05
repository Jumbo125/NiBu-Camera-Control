using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NiBuLauncher.Services;

public sealed class StatusChecker
{
    private readonly string _baseDir;
    private readonly LauncherConfig _config;

    public StatusChecker(string baseDir)
    {
        _baseDir = baseDir;
        _config = ReadLauncherConfig();
    }

    public async Task<StatusSnapshot> GetSnapshotAsync()
    {
        var (caddyPort, phpPort) = ReadCaddyPhpPorts();
        var bridgePort = ReadBridgePort();
        var pythonPort = ReadPythonPort();

        var caddy = await CheckHttpAsync(
            "caddy_windows_amd64.exe",
            $"http://127.0.0.1:{caddyPort}/watchdog/_health.txt",
            expectOkText: true,
            timeoutMs: _config.CaddyTimeoutMs);

        var php = await CheckHttpAsync(
            "php-cgi.exe",
            $"http://127.0.0.1:{caddyPort}/watchdog/_php_ping.php",
            expectOkText: true,
            timeoutMs: _config.PhpTimeoutMs);

        var bridge = await CheckHttpAsync(
            "Photobox.Bridge.ApiServer.exe",
            $"http://127.0.0.1:{bridgePort}/api/status",
            expectOkText: false,
            timeoutMs: _config.BridgeTimeoutMs);

        var py = await CheckHttpAsync(
            "python.exe",
            $"http://127.0.0.1:{pythonPort}/ping",
            expectOkText: false,
            timeoutMs: _config.PythonTimeoutMs);

        return new StatusSnapshot(caddy, php, bridge, py);
    }

    private static bool IsProcessRunning(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName).ToLowerInvariant();

        // tolerate python/pythonw mismatch
        if (name == "python")
            return Process.GetProcessesByName("python").Any() ||
                   Process.GetProcessesByName("pythonw").Any();

        if (name == "pythonw")
            return Process.GetProcessesByName("pythonw").Any() ||
                   Process.GetProcessesByName("python").Any();

        return Process.GetProcessesByName(name).Any();
    }

    private static async Task<ServiceState> CheckHttpAsync(
        string processExe,
        string url,
        bool expectOkText,
        int timeoutMs)
    {
        var proc = IsProcessRunning(processExe);

        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs)
            };

            var resp = await http.GetAsync(url);
            var body = (await resp.Content.ReadAsStringAsync()).Trim();

            bool ok;
            if (!resp.IsSuccessStatusCode)
            {
                ok = false;
            }
            else if (expectOkText)
            {
                ok = body.Contains("OK", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // for json endpoints: any 2xx is good
                ok = true;
            }

            return new ServiceState(
                proc,
                ok,
                ok ? null : $"{url} -> {(int)resp.StatusCode} {resp.ReasonPhrase} | {body}");
        }
        catch (Exception ex)
        {
            return new ServiceState(proc, false, $"{url} -> {ex.Message}");
        }
    }

    private (int caddy, int php) ReadCaddyPhpPorts()
    {
        var path = Path.Combine(_baseDir, "launcher", "caddy_php_port.json");
        if (!File.Exists(path))
            return (8050, 8051);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var caddy = root.TryGetProperty("caddy_port", out var cp) && cp.TryGetInt32(out var c)
                ? c
                : 8050;

            var php = root.TryGetProperty("php_port", out var pp) && pp.TryGetInt32(out var p)
                ? p
                : 8051;

            return (caddy, php);
        }
        catch
        {
            return (8050, 8051);
        }
    }

    private int ReadBridgePort()
    {
        var path = Path.Combine(_baseDir, "booth", "tools", "camerabridge", "APIServer", "ApiServer_settings.json");
        if (!File.Exists(path))
            return 8052;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("Port", out var p) && p.TryGetInt32(out var v))
                return v;
        }
        catch
        {
        }

        return 8052;
    }

    private int ReadPythonPort()
    {
        var path = Path.Combine(_baseDir, "booth", "tools", "python_portable", "server_config.json");
        if (!File.Exists(path))
            return 8053;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("Python_ServerPort", out var p) && p.TryGetInt32(out var v))
                return v;
        }
        catch
        {
        }

        return 8053;
    }

    private LauncherConfig ReadLauncherConfig()
    {
        var path = Path.Combine(_baseDir, "launcher", "launcher_config.ini");
        if (!File.Exists(path))
            return new LauncherConfig();

        try
        {
            var data = ParseIni(path);

            return new LauncherConfig
            {
                StatusIntervalMs = GetInt(data, "watchdog", "status_interval_ms", 5000),
                CaddyTimeoutMs = GetInt(data, "timeouts", "caddy_ms", 1200),
                PhpTimeoutMs = GetInt(data, "timeouts", "php_ms", 1200),
                BridgeTimeoutMs = GetInt(data, "timeouts", "bridge_ms", 5000),
                PythonTimeoutMs = GetInt(data, "timeouts", "python_ms", 2000)
            };
        }
        catch
        {
            return new LauncherConfig();
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = "";

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith(";") || line.StartsWith("#"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line[1..^1].Trim();

                if (!result.ContainsKey(currentSection))
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            if (!result.ContainsKey(currentSection))
                result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            result[currentSection][key] = value;
        }

        return result;
    }

    private static int GetInt(
        Dictionary<string, Dictionary<string, string>> data,
        string section,
        string key,
        int fallback)
    {
        if (data.TryGetValue(section, out var sec) &&
            sec.TryGetValue(key, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return fallback;
    }

    private sealed class LauncherConfig
    {
        public int StatusIntervalMs { get; init; } = 5000;
        public int CaddyTimeoutMs { get; init; } = 1200;
        public int PhpTimeoutMs { get; init; } = 1200;
        public int BridgeTimeoutMs { get; init; } = 5000;
        public int PythonTimeoutMs { get; init; } = 2000;
    }
}