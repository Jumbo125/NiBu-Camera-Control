using System.Text.Json;

namespace NiBuLauncher.Services;

public sealed class UiStrings
{
    private readonly Dictionary<string, string> _map;
    private UiStrings(Dictionary<string, string> map) => _map = map;

    public static string UiDir(string baseDir) => Path.Combine(baseDir, "launcher", "ui");

    public static UiStrings Load(string baseDir, string lang)
    {
        var path = Path.Combine(UiDir(baseDir), $"strings.{lang}.json");
        if (!File.Exists(path))
            return new UiStrings(new Dictionary<string, string>());

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("meta")) continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
            }
            return new UiStrings(map);
        }
        catch
        {
            return new UiStrings(new Dictionary<string, string>());
        }
    }

    public string T(string key, IDictionary<string, string>? vars = null)
    {
        if (!_map.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            s = (vars != null && vars.TryGetValue("fallback", out var fb) && !string.IsNullOrWhiteSpace(fb)) ? fb : key;

        if (vars != null)
        {
            foreach (var kv in vars)
            {
                if (kv.Key.Equals("fallback", StringComparison.OrdinalIgnoreCase)) continue;
                s = s.Replace("{" + kv.Key + "}", kv.Value);
            }
        }
        return s;
    }

    public static Dictionary<string, string> Vars(params (string k, string v)[] items)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in items) d[k] = v;
        return d;
    }

    public sealed record LangInfo(string Code, string Name, string Path)
    {
        public override string ToString() => Name;
    }

    public static List<LangInfo> DiscoverLanguages(string baseDir)
    {
        var dir = UiDir(baseDir);
        Directory.CreateDirectory(dir);

        var files = Directory.GetFiles(dir, "strings.*.json", SearchOption.TopDirectoryOnly);
        var list = new List<LangInfo>();

        foreach (var f in files)
        {
            var file = Path.GetFileName(f);
            var parts = file.Split('.');
            if (parts.Length < 3) continue;

            var code = parts[1].Trim().ToLowerInvariant();
            var name = code.ToUpperInvariant();

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("meta", out var meta))
                {
                    if (meta.TryGetProperty("code", out var mc) && mc.ValueKind == JsonValueKind.String)
                        code = (mc.GetString() ?? code).Trim().ToLowerInvariant();
                    if (meta.TryGetProperty("name", out var mn) && mn.ValueKind == JsonValueKind.String)
                        name = (mn.GetString() ?? name).Trim();
                }
            }
            catch { }

            list.Add(new LangInfo(code, name, f));
        }

        return list.OrderBy(x => x.Name).ThenBy(x => x.Code).ToList();
    }

    public static string LoadLanguage(string baseDir, IEnumerable<LangInfo>? available = null)
    {
        var path = Path.Combine(UiDir(baseDir), "lang.txt");
        try
        {
            if (File.Exists(path))
            {
                var s = File.ReadAllText(path).Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        catch { }

        var langs = available?.ToList() ?? DiscoverLanguages(baseDir);
        if (langs.Any(l => l.Code == "de")) return "de";
        if (langs.Count > 0) return langs[0].Code;
        return "de";
    }

    public static void SaveLanguage(string baseDir, string lang)
    {
        var dir = UiDir(baseDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "lang.txt"), lang.Trim().ToLowerInvariant());
    }
}
