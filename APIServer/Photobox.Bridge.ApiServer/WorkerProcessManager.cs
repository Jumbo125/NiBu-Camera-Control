// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: WorkerProcessManager.cs
// Zweck: Startet den Worker-Prozess kontrolliert und mit Cooldown-Schutz.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - Worker-Exe validieren
// - Startversuche serialisieren
// - Cooldown zwischen Startversuchen einhalten
// - manuelle und automatische Starts unterstützen
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Serilog;

namespace Photobox.Bridge.ApiServer;


public sealed class WorkerProcessManager
{
    private readonly WorkerSettings _w;
    private readonly Serilog.ILogger _log = Log.ForContext<WorkerProcessManager>();

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private DateTime _lastStartAttemptUtc = DateTime.MinValue;

    public WorkerProcessManager(IOptions<WorkerSettings> w) => _w = w.Value;

    public async Task<(bool ok, string? error)> EnsureStartedAsync(string reason, CancellationToken ct)
    {
        // Cooldown (ohne Lock: “best effort”)
        var now = DateTime.UtcNow;
        if ((now - _lastStartAttemptUtc).TotalMilliseconds < _w.StartCooldownMs)
            return (false, "cooldown");

        await _startGate.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if ((now - _lastStartAttemptUtc).TotalMilliseconds < _w.StartCooldownMs)
                return (false, "cooldown");

            _lastStartAttemptUtc = now;

            if (string.IsNullOrWhiteSpace(_w.ExePath))
            {
                _log.Error("Worker start skipped: ExePath missing. Reason={Reason}", reason);
                try { Console.WriteLine($"[Worker] start skipped: ExePath missing. Reason={reason}"); } catch { }
                return (false, "worker_exe_path_missing");
            }

            var exe = _w.ExePath!;
            if (!File.Exists(exe))
            {
                _log.Error("Worker start skipped: ExePath not found: {Exe}. Reason={Reason}", exe, reason);
                try { Console.WriteLine($"[Worker] start skipped: ExePath not found: {exe}. Reason={reason}"); } catch { }
                return (false, "worker_exe_not_found");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = _w.Args ?? "",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
                };

                _log.Information("Starting worker (single-instance should replace old). Reason={Reason} Exe={Exe} Args={Args}",
                    reason, exe, _w.Args ?? "");

                try { Console.WriteLine($"[Worker] Starting. Reason={reason} Exe={exe} Args={_w.Args ?? ""}"); } catch { }

                Process.Start(psi);
                return (true, null);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Worker start failed. Reason={Reason}", reason);
                try { Console.WriteLine($"[Worker] start failed: {ex.Message}"); } catch { }
                return (false, ex.Message);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    // Für API/Tray bequem:
    public (bool ok, string? error) Restart()
    {
        // sync wrapper (falls du lieber async willst: Endpoint async machen)
        return EnsureStartedAsync("manual_restart", CancellationToken.None).GetAwaiter().GetResult();
    }
}
