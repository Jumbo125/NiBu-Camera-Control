using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fotobox.WebView2Host;

public sealed class AppConfig
{
    public string? Url { get; set; }
    public string DefaultUrl { get; set; } = "http://127.0.0.1";
    public int DefaultPort { get; set; } = 8080;
    public string? LocalIndexPath { get; set; } = "wwwroot/index.html";
    public bool Kiosk { get; set; } = false;
    public string? Title { get; set; }
    public string? Icon { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AllowDevTools { get; set; } = false;

    [JsonIgnore]
    public string EffectiveTitle => string.IsNullOrWhiteSpace(Title) ? Program.HardcodedTitle : Title!.Trim();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static AppConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            var fallback = new AppConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, JsonSerializer.Serialize(fallback, JsonOptions));
            return fallback;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public string ResolveStartupTarget(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;

        if (TryResolveConfigTarget(Url, configDirectory, out var configuredTarget))
        {
            return configuredTarget;
        }

        if (TryResolveConfigTarget(LocalIndexPath, configDirectory, out var localIndexTarget, requireExistingFile: true))
        {
            return localIndexTarget;
        }

        return BuildDefaultUrl();
    }

    public string? ResolveCustomIconPath(string configPath)
    {
        if (string.IsNullOrWhiteSpace(Icon))
        {
            return null;
        }

        var configDirectory = Path.GetDirectoryName(configPath)!;
        var iconPath = Path.Combine(configDirectory, Icon.Trim());
        return File.Exists(iconPath) ? iconPath : null;
    }

    private static bool TryResolveConfigTarget(string? rawValue, string configDirectory, out string target, bool requireExistingFile = false)
    {
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var trimmed = rawValue.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile && requireExistingFile && !File.Exists(absoluteUri.LocalPath))
            {
                return false;
            }

            target = absoluteUri.AbsoluteUri;
            return true;
        }

        var candidatePath = Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.GetFullPath(Path.Combine(configDirectory, trimmed));

        if (File.Exists(candidatePath))
        {
            target = new Uri(candidatePath).AbsoluteUri;
            return true;
        }

        if (requireExistingFile)
        {
            return false;
        }

        if (TryNormalizeWebUrl(trimmed, out var normalizedWebUrl))
        {
            target = normalizedWebUrl;
            return true;
        }

        return false;
    }

    private string BuildDefaultUrl()
    {
        var baseUrl = string.IsNullOrWhiteSpace(DefaultUrl) ? "http://127.0.0.1" : DefaultUrl.Trim();

        if (!TryNormalizeWebUrl(baseUrl, out var normalizedBaseUrl))
        {
            normalizedBaseUrl = "http://127.0.0.1";
        }

        var builder = new UriBuilder(normalizedBaseUrl);
        if (DefaultPort > 0)
        {
            builder.Port = DefaultPort;
        }

        return builder.Uri.ToString();
    }

    private static bool TryNormalizeWebUrl(string rawValue, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var value = rawValue.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            normalizedUrl = absoluteUri.AbsoluteUri;
            return true;
        }

        if (!LooksLikeHostOrIp(value))
        {
            return false;
        }

        var withScheme = "http://" + value;
        if (Uri.TryCreate(withScheme, UriKind.Absolute, out var httpUri) &&
            (httpUri.Scheme == Uri.UriSchemeHttp || httpUri.Scheme == Uri.UriSchemeHttps))
        {
            normalizedUrl = httpUri.AbsoluteUri;
            return true;
        }

        return false;
    }

    private static bool LooksLikeHostOrIp(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Contains('\\'))
        {
            return false;
        }

        if (!value.Contains('/') && HasLikelyLocalFileExtension(value))
        {
            return false;
        }

        if (value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hostPortPath = value.Split('/', 2)[0];
        var hostOnly = hostPortPath;

        if (hostPortPath.StartsWith('[') && hostPortPath.Contains("]"))
        {
            var endIndex = hostPortPath.IndexOf(']');
            hostOnly = hostPortPath[..(endIndex + 1)];
        }
        else if (hostPortPath.Count(c => c == ':') == 1)
        {
            var colonIndex = hostPortPath.LastIndexOf(':');
            var possiblePort = hostPortPath[(colonIndex + 1)..];
            if (int.TryParse(possiblePort, out _))
            {
                hostOnly = hostPortPath[..colonIndex];
            }
        }

        var trimmedHost = hostOnly.Trim('[', ']');
        if (IPAddress.TryParse(trimmedHost, out _))
        {
            return true;
        }

        return trimmedHost.Contains('.') &&
               trimmedHost.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.');
    }

    private static bool HasLikelyLocalFileExtension(string value)
    {
        var fileName = Path.GetFileName(value);
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrWhiteSpace(extension);
    }
}
