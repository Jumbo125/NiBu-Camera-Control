using System.Text.Json;

namespace NiBuLauncher.Services;

public static class ConfigReader
{
    public static (int caddyPort, int phpPort) ReadCaddyPhpPorts(string appBaseDir)
    {
        var path = Path.Combine(appBaseDir, "launcher", "caddy_php_port.json");
        if (!File.Exists(path))
            return (8050, 8051);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        int caddy = TryGetInt(root, "caddy_port") ?? 8050;
        int php = TryGetInt(root, "php_port") ?? 8051;
        return (caddy, php);
    }

    public static int ReadBridgePort(string appBaseDir)
    {
        var path = Path.Combine(appBaseDir, "booth", "tools", "camerabridge", "APIServer", "ApiServer_settings.json");
        if (!File.Exists(path))
            return 8052;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        // Most likely: { "Bridge": { "Port": 8052, ... } } or { "Port": 8052 }
        var bridge = TryGetObject(root, "Bridge") ?? root;
        return TryGetInt(bridge, "Port") ?? TryGetInt(root, "Bridge.Port") ?? 8052;
    }

    public static int ReadPythonPort(string appBaseDir)
    {
        var path = Path.Combine(appBaseDir, "booth", "tools", "python_portable", "server_config.json");
        if (!File.Exists(path))
            return 8053;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        // Unknown key name across installs -> try a few common ones.
        return TryGetInt(root, "Python_ServerPort")
               ?? TryGetInt(root, "ServerPort")
               ?? TryGetInt(root, "port")
               ?? 8053;
    }

    private static JsonElement? TryGetObject(JsonElement root, string key)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals(key) && prop.Value.ValueKind == JsonValueKind.Object)
                return prop.Value;
        }
        return null;
    }

    private static int? TryGetInt(JsonElement root, string key)
    {
        // Direct property
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals(key))
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var n))
                    return n;
                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var s))
                    return s;
            }
        }

        // Optional dotted-path lookup like "Bridge.Port"
        if (key.Contains('.'))
        {
            var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
            JsonElement cur = root;
            foreach (var part in parts)
            {
                if (cur.ValueKind != JsonValueKind.Object) return null;
                if (!cur.TryGetProperty(part, out var next))
                    return null;
                cur = next;
            }
            if (cur.ValueKind == JsonValueKind.Number && cur.TryGetInt32(out var n))
                return n;
            if (cur.ValueKind == JsonValueKind.String && int.TryParse(cur.GetString(), out var s))
                return s;
        }

        return null;
    }
}
