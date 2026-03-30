using System.Diagnostics;
using System.Text;

namespace NiBuLauncher.Services;

public sealed class ScriptRunner
{
    private readonly string _baseDir;
    private readonly Action<string> _log;

    public ScriptRunner(string baseDir, Action<string> log)
    {
        _baseDir = baseDir;
        _log = log;
    }

    public sealed record BatchResult(int ExitCode, string Stdout, string Stderr, string Summary);

    /// <summary>
    /// Runs a .bat (non-admin) and streams stdout/stderr to UI log.
    /// Optional timeout: null or <=0 means no timeout.
    /// </summary>
    public async Task RunBatchAsync(
        string fullPath,
        string args = "",
        int? timeoutSeconds = 15,
        CancellationToken ct = default)
    {
        _ = await RunBatchCaptureAsync(fullPath, args, timeoutSeconds, ct);
    }

    /// <summary>
    /// Runs a .bat and returns captured stdout/stderr while streaming everything into the UI log.
    /// Optional timeout: null or <=0 means no timeout.
    /// </summary>
    public async Task<BatchResult> RunBatchCaptureAsync(
        string fullPath,
        string args = "",
        int? timeoutSeconds = 15,
        CancellationToken ct = default)
    {
        if (!File.Exists(fullPath))
            return new BatchResult(2, "", $"File not found: {fullPath}", $"Script fehlt: {fullPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{fullPath}\" {args}\"",
            // stabiler: im Script-Ordner starten (wenn möglich)
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? _baseDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            _log(e.Data);
        };

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            _log("[ERR] " + e.Data);
        };

        try
        {
            _log($"[RUN] {Path.GetFileName(fullPath)} {args}".Trim());
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var exitTask = p.WaitForExitAsync(ct);

            // --- Timeout optional ---
            if (timeoutSeconds is int t && t > 0)
            {
                var finished = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(t), ct));
                if (finished != exitTask)
                {
                    _log($"[TIMEOUT] {Path.GetFileName(fullPath)} läuft länger als {t}s -> Kill");
                    try { p.Kill(entireProcessTree: true); }
                    catch (Exception kex) { _log("[EX] Kill failed: " + kex.Message); }

                    try { await exitTask; } catch { /* ignore */ }

                    var outTimeout = stdout.ToString();
                    var errTimeout = stderr.ToString();
                    _log($"[DONE] {Path.GetFileName(fullPath)} -> Timeout");
                    return new BatchResult(124, outTimeout, errTimeout, $"Timeout (>{t}s)");
                }
            }

            // normal beendet (oder ohne Timeout)
            await exitTask;

            // manchmal hilfreich, um letzte Output-Events zu flushen
            try { p.WaitForExit(); } catch { }

            var exit = p.ExitCode;
            var outText = stdout.ToString();
            var errText = stderr.ToString();
            var summary = exit == 0 ? "OK" : $"ExitCode={exit}";

            _log($"[DONE] {Path.GetFileName(fullPath)} -> {summary}");
            return new BatchResult(exit, outText, errText, summary);
        }
        catch (OperationCanceledException)
        {
            _log($"[CANCEL] {Path.GetFileName(fullPath)}");
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return new BatchResult(125, stdout.ToString(), stderr.ToString(), "Cancelled");
        }
        catch (Exception ex)
        {
            _log("[EX] " + ex);
            return new BatchResult(2, stdout.ToString(), stderr.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Starts a .bat detached (does not wait, no stdout/stderr capture).
    /// Useful for long-running start scripts/services.
    /// </summary>
    public Task RunBatchDetachedAsync(string fullPath, string args = "")
    {
        if (!File.Exists(fullPath))
        {
            _log($"[ERR] File not found: {fullPath}");
            return Task.CompletedTask;
        }

        var wd = Path.GetDirectoryName(fullPath) ?? _baseDir;

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // start "" /b -> startet abgekoppelt und kehrt sofort zurück
            Arguments = $"/c start \"\" /b \"{fullPath}\" {args}",
            WorkingDirectory = wd,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _log($"[RUN][DETACH] {Path.GetFileName(fullPath)} {args}".Trim());
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log("[EX] " + ex);
        }

        return Task.CompletedTask;
    }

    // Deine Admin-Methode bleibt wie gehabt (nicht angefasst)
    public async Task<BatchResult> RunBatchElevatedCaptureAsync(string fullPath, string args = "", int timeoutSeconds = 180)
    {
        if (!File.Exists(fullPath))
            return new BatchResult(2, "", $"File not found: {fullPath}", $"Script fehlt: {fullPath}");

        var logsDir = Path.Combine(_baseDir, "logs");
        Directory.CreateDirectory(logsDir);

        var outFile = Path.Combine(
            logsDir,
            $"elevated_{Path.GetFileNameWithoutExtension(fullPath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        );

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{fullPath}\" {args} > \"{outFile}\" 2>&1\"",
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
        };

        try
        {
            _log($"[RUN][ADMIN] {Path.GetFileName(fullPath)} {args}".Trim());
            using var p = Process.Start(psi);
            if (p == null)
                return new BatchResult(2, "", "", "Start failed");

            var exitTask = p.WaitForExitAsync();
            var finished = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

            if (finished != exitTask)
            {
                _log($"[TIMEOUT] {Path.GetFileName(fullPath)} läuft länger als {timeoutSeconds}s -> Kill");
                try { p.Kill(entireProcessTree: true); } catch { }
                return new BatchResult(124, "", "", $"Timeout (>{timeoutSeconds}s)");
            }

            var text = File.Exists(outFile) ? File.ReadAllText(outFile) : "";
            if (!string.IsNullOrWhiteSpace(text))
                _log(text.TrimEnd());

            var exit = p.ExitCode;
            var summary = exit == 0 ? "OK" : $"ExitCode={exit}";
            _log($"[DONE][ADMIN] {Path.GetFileName(fullPath)} -> {summary}");

            return new BatchResult(exit, text, "", summary);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log("[INFO] Admin-Ausführung abgebrochen (UAC).");
            return new BatchResult(1223, "", "", "UAC cancelled");
        }
    }
}
