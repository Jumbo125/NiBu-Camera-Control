using System.Diagnostics;
using System.Text.Json;

namespace NiBuLauncher.Services;

public static class Watchdog
{
    public static async Task RunAsync(string appBaseDir, CancellationToken token)
    {
        var logDir = Path.Combine(appBaseDir, "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, "launcher_watchdog.log");
        var lastRestartFile = Path.Combine(logDir, ".last_restart");
        var pauseFile = Path.Combine(appBaseDir, "launcher", "watchdog_pause.json");


        var intervalSec = 5;
        var cooldownSec = 20;

        void Log(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            File.AppendAllText(logFile, line + Environment.NewLine);
        }

        bool CooldownActive()
        {
            try
            {
                if (!File.Exists(lastRestartFile)) return false;
                var s = File.ReadAllText(lastRestartFile).Trim();
                if (!DateTime.TryParse(s, out var last)) return false;
                return (DateTime.Now - last).TotalSeconds < cooldownSec;
            }
            catch { return false; }
        }

        void MarkRestart() => File.WriteAllText(lastRestartFile, DateTime.Now.ToString("o"));

        DateTime _lastPauseLog = DateTime.MinValue;

        bool IsPaused(out TimeSpan remaining, out string reason)
        {
            remaining = TimeSpan.Zero;
            reason = "";
            try
            {
                if (!File.Exists(pauseFile)) return false;
                var json = File.ReadAllText(pauseFile);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("untilUtc", out var u))
                {
                    // invalid file -> remove
                    File.Delete(pauseFile);
                    return false;
                }
                var untilStr = u.GetString();
                if (!DateTimeOffset.TryParse(untilStr, out var until))
                {
                    File.Delete(pauseFile);
                    return false;
                }
                if (doc.RootElement.TryGetProperty("reason", out var r))
                    reason = r.GetString() ?? "";

                var now = DateTimeOffset.UtcNow;
                if (now < until)
                {
                    remaining = until - now;
                    return true;
                }

                // expired
                File.Delete(pauseFile);
                Log("WATCHDOG PAUSE expired");
                return false;
            }
            catch
            {
                return false;
            }
        }

        async Task<bool> HttpOk(string url, string? expect = null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = await http.GetAsync(url, token);
                if (!resp.IsSuccessStatusCode) return false;
                if (expect != null)
                {
                    var body = (await resp.Content.ReadAsStringAsync(token)).Trim();
                    return body.Equals(expect, StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }
            catch { return false; }
        }

        async Task Restart(string reason)
        {
            if (CooldownActive())
            {
                Log($"RESTART SKIPPED (cooldown): {reason}");
                return;
            }

            Log($"RESTART BACKEND: {reason}");
            MarkRestart();

            var startBat = Path.Combine(appBaseDir, "launcher", "start.bat");
            if (!File.Exists(startBat))
            {
                Log($"START-SCRIPT FEHLT: {startBat}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{startBat}\" /clean /nopause\"",
                    WorkingDirectory = Path.GetDirectoryName(startBat) ?? appBaseDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex)
            {
                Log("RESTART ERROR: " + ex.Message);
            }
        }

        Log("WATCHDOG STARTED");

        while (!token.IsCancellationRequested)
        {
            var (caddyPort, _) = ConfigReader.ReadCaddyPhpPorts(appBaseDir);
            var bridgePort = ConfigReader.ReadBridgePort(appBaseDir);
            var pythonPort = ConfigReader.ReadPythonPort(appBaseDir);

            if (IsPaused(out var remaining, out var why))
            {
                // keep task running, but temporarily do not restart while paused
                if ((DateTime.Now - _lastPauseLog).TotalSeconds >= 15)
                {
                    _lastPauseLog = DateTime.Now;
                    Log($"WATCHDOG PAUSED ({remaining.TotalSeconds:0}s left) Reason: {why}");
                }
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), token);
                continue;
            }

            var caddyOk = await HttpOk($"http://127.0.0.1:{caddyPort}/watchdog/_health.txt", "OK");
            var phpOk = await HttpOk($"http://127.0.0.1:{caddyPort}/watchdog/_php_ping.php", "OK");
            var bridgeOk = await HttpOk($"http://127.0.0.1:{bridgePort}/api/status");
            var pythonOk = await HttpOk($"http://127.0.0.1:{pythonPort}/ping");

            if (!caddyOk)
                await Restart("Caddy health failed");
            else if (!phpOk)
                await Restart("PHP FastCGI not responding");
            else if (!bridgeOk)
                await Restart("CameraBridge API down");
            else if (!pythonOk)
                await Restart("Python server down");

            await Task.Delay(TimeSpan.FromSeconds(intervalSec), token);
        }
    }
}
