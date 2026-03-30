// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: WorkerHealthMonitor.cs
// Zweck: Überwacht die Erreichbarkeit des Workers im Hintergrund und stößt bei Bedarf einen Neustart an.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - periodische Statusabfrage per IPC
// - Fehler- und Timeout-Erkennung
// - Reachability-State aktualisieren
// - automatischen Worker-Start bei längerer Nichterreichbarkeit auslösen
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Photobox.Bridge.Shared;
using Serilog;

namespace Photobox.Bridge.ApiServer;

public sealed class WorkerHealthMonitor : BackgroundService
{
    private readonly BridgePipeClient _ipc;
    private readonly WorkerHealthState _state;
    private readonly HealthSettings _health;
    private readonly WorkerSettings _worker;
    private readonly WorkerProcessManager _pm;
    private readonly Serilog.ILogger _log = Log.ForContext<WorkerHealthMonitor>();

    private int _failsInRow = 0;
    private bool? _lastReachable = null;

    // Down-Flapping verhindern: "wirklich down seit..."
    private DateTime? _downSinceUtc = null;

    // Restart-Gating gegen Thrashing
    private int _startInFlight = 0;
    private DateTime _nextStartAllowedUtc = DateTime.MinValue;
    private DateTime _startupGraceUntilUtc = DateTime.MinValue;

    // Defaults / Tunables
    private const int DefaultIntervalMs = 2000;
    private const int DefaultTimeoutMs  = 8000;   // sinnvoller Default als 800ms
    private const int RestartCooldownMs = 10000;  // min Abstand zwischen Starts
    private const int StartupGraceMs    = 15000;  // Zeit geben nach Start
    private const int MinDownMsBeforeRestart = 20000; // erst nach 20s "wirklich down" neu starten

    public WorkerHealthMonitor(
        BridgePipeClient ipc,
        WorkerHealthState state,
        IOptions<HealthSettings> health,
        IOptions<WorkerSettings> worker,
        WorkerProcessManager pm)
    {
        _ipc = ipc;
        _state = state;
        _health = health.Value;
        _worker = worker.Value;
        _pm = pm;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Heartbeat-Intervall: wenn nicht gesetzt, default 2000ms
        var intervalMs = _health.IntervalMs > 0 ? _health.IntervalMs : DefaultIntervalMs;

        // Timeout: wenn nicht gesetzt, default (deutlich höher als 800ms)
        var timeoutMs = _health.TimeoutMs > 0 ? _health.TimeoutMs : DefaultTimeoutMs;

        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = false;
            string? err = null;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(timeoutMs);

                _ = await _ipc.CallAsync<WorkerStatusDto>(Commands.StatusGet, null, cts.Token);
                ok = true;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Das ist praktisch immer CancelAfter(timeout)
                err = $"timeout({timeoutMs}ms)";
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                err = ex.Message;
            }

            if (ok)
            {
                var wasDown = (_lastReachable == false) || (_failsInRow > 0);

                _failsInRow = 0;
                _downSinceUtc = null;
                _state.SetOk();

                // Optional: nur bei Zustandswechsel ins File loggen
                if (wasDown)
                {
                    try { _log.Warning("Worker connection RESTORED."); } catch { }
                    TryWriteLine("[Health] Worker connection RESTORED");
                }

                // Heartbeat (alle IntervalMs)
                var s = _state.Snapshot();
                TryWriteLine($"[Health] Worker=OK failsInRow=0 lastOkUtc={s.LastOkUtc:O}");
                _lastReachable = true;
            }
            else
            {
                _failsInRow++;
                _state.SetFail(new Exception(err ?? "unknown_error"));

                if (_lastReachable != false)
                {
                    try { _log.Warning("Worker connection LOST."); } catch { }
                    TryWriteLine("[Health] Worker connection LOST");
                }

                var s = _state.Snapshot();
                TryWriteLine($"[Health] Worker=DOWN failsInRow={_failsInRow} lastOkUtc={(s.LastOkUtc.HasValue ? s.LastOkUtc.Value.ToString("O") : "-")} error={s.LastError}");

                // Down-Timer starten (nur einmal)
                var now = DateTime.UtcNow;
                _downSinceUtc ??= now;
                var downForMs = (now - _downSinceUtc.Value).TotalMilliseconds;

                // Autostart: nur wenn "wirklich down" UND nicht in Grace UND nicht zu oft UND nicht parallel
                var threshold = Math.Max(1, _worker.FailThreshold);
                var inGrace = now < _startupGraceUntilUtc;

                if (_worker.AutoStartWhenUnreachable &&
                    !inGrace &&
                    _failsInRow >= threshold &&
                    downForMs >= MinDownMsBeforeRestart &&
                    now >= _nextStartAllowedUtc &&
                    Interlocked.CompareExchange(ref _startInFlight, 1, 0) == 0)
                {
                    _nextStartAllowedUtc = now.AddMilliseconds(RestartCooldownMs);
                    _startupGraceUntilUtc = now.AddMilliseconds(StartupGraceMs);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _pm.EnsureStartedAsync("pipe_unreachable", CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            try { _log.Error(ex, "EnsureStartedAsync failed"); } catch { }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _startInFlight, 0);
                        }
                    });
                }

                // File-Log bei Fehler ok (aber Achtung: kann viel werden)
                try { _log.Information("Worker pipe not reachable (failsInRow={Fails}). {Msg}", _failsInRow, err); } catch { }

                _lastReachable = false;
            }

            try { await Task.Delay(intervalMs, stoppingToken); }
            catch { /* ignore */ }
        }
    }

    private static void TryWriteLine(string msg)
    {
        try { Console.WriteLine(msg); } catch { }
    }
}
