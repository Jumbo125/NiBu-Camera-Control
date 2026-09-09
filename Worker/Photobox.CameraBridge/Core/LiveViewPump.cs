// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: LiveViewPump.cs
// Zweck: Holt fortlaufend LiveView-Bilder von der Kamera und speist sie in den FrameHub ein.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - LiveView an der Kamera starten und stoppen
// - Frames zyklisch abrufen
// - Ziel-FPS steuern
// - KeepAlive und Laufzeitverhalten überwachen

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CameraControl.Devices;
using CameraControl.Devices.Classes;

namespace Photobox.CameraBridge.Core
{
    public sealed class LiveViewPump
    {
        private readonly MtaWorker _mta;
        private readonly RingLogger _log;
        private readonly AppSettings _settings;

        private CancellationTokenSource _cts;
        private Task _task;

        // Reines Diagnose-Timing (siehe README_LiveView_Startverzoegerung.md). _startTimingSw wird
        // in Start() gesetzt (von außen übergeben oder hier neu erzeugt) und misst bis zum ersten
        // nach diesem Start empfangenen Frame. _firstFrameTimed sorgt dafür, dass danach nicht bei
        // jedem weiteren Frame im Dauerbetrieb geloggt wird. Kein Einfluss auf das Verhalten.
        private Stopwatch _startTimingSw;
        private volatile bool _firstFrameTimed;

        // Dynamisch verstellbare FPS (thread-safe)
        private int _targetFps;

        // Zählt aufeinanderfolgende gescheiterte "no frames -> restart"-Versuche.
        // Wird bei jedem erfolgreichen Frame-Empfang bzw. Neustart zurückgesetzt.
        private int _consecutiveRestartFailures;

        private const int StuckAfterFailures = 5;

        // Beobachtung im Log: direkt nach einem frischen Reconnect (mit oder ohne PnP-Reset)
        // schlägt der ALLERERSTE StartLiveView-Aufruf öfter einmalig transient fehl ("Invalid
        // status" 0xA004, oder ein Shutdown-COMException-Muster) - offenbar eine kurze
        // Race zwischen SDK-Session-Aufbau und dem ersten Kommando, die sich fast immer nach
        // einem kurzen Moment von selbst löst (siehe z.B. 09:45:23 im Log: attempt 1 schlägt
        // fehl, unmittelbar danach läuft alles normal weiter - kein Reconnect nötig). Weil der
        // Fehlerzähler oben bewusst NICHT pro Start() zurückgesetzt wird (siehe Start()), reißt
        // genau dieser eine transiente Fehler nach einem Reconnect sofort wieder die
        // StuckAfterFailures-Schwelle und löst einen weiteren vollen harten Reconnect aus -
        // beobachtet als 3-4 PnP-Resets hintereinander für ein einziges Vorfall. Ein kurzer,
        // lokaler Retry NUR für den initialen Start (unten) fängt das ab, bevor es überhaupt
        // als Fehlversuch gezählt wird.
        private const int InitialStartRetryAttempts = 3;
        private static readonly TimeSpan InitialStartRetryDelay = TimeSpan.FromMilliseconds(600);

        /// <summary>
        /// Wird ausgelöst, wenn LiveView wiederholt (siehe StuckAfterFailures) nicht neu gestartet
        /// werden konnte (z.B. dauerhaftes "MTP device busy"). Konsumenten (z.B. UsbReconnectWatchdog)
        /// können darauf mit einem harten Reconnect reagieren, da ein simples Stop/Start auf demselben
        /// SDK-Handle in diesem Zustand nichts mehr bewirkt.
        /// </summary>
        public event Action Stuck;

        public bool IsRunning => _task != null && !_task.IsCompleted;

        public int TargetFps
        {
            get
            {
                var v = Volatile.Read(ref _targetFps);
                return ClampFps(v);
            }
        }

        public LiveViewPump(MtaWorker mta, RingLogger log, AppSettings settings)
        {
            _mta = mta;
            _log = log;
            _settings = settings;

            // Initialwert aus Config
            Volatile.Write(ref _targetFps, ClampFps(_settings?.LiveViewFps ?? 20));
        }

        public void SetTargetFps(int fps)
        {
            fps = ClampFps(fps);
            Volatile.Write(ref _targetFps, fps);
            _log.Info("LiveView target fps set to " + fps);
        }

        public void Start(ICameraDevice cam, FrameHub hub) => Start(cam, hub, null);

        /// <summary>
        /// timingSw: optionale, von CameraHost.StartLiveView() durchgereichte Stopwatch für reines
        /// [LV-TIMING]-Diagnoselogging (siehe README_LiveView_Startverzoegerung.md). Wird hier nur
        /// gespeichert, um bis zum ersten Frame nach diesem Start damit weiterzumessen - kein
        /// Einfluss auf das eigentliche Start-/Poll-Verhalten.
        /// </summary>
        public void Start(ICameraDevice cam, FrameHub hub, Stopwatch timingSw)
        {
            if (cam == null) throw new ArgumentNullException(nameof(cam));
            if (hub == null) throw new ArgumentNullException(nameof(hub));
            if (IsRunning) return;

            _startTimingSw = timingSw ?? Stopwatch.StartNew();
            _firstFrameTimed = false;

            // WICHTIG: den Fehlerzähler hier NICHT zurücksetzen. Wenn die Kamera in einen
            // dauerhaften "MTP device busy"-Zustand kippt, stößt der Aufrufer (ApiServer/Frontend)
            // bei jedem Fehlversuch einen frischen Start an. Würde jeder Start den Zähler auf 0
            // setzen, erreichten die aufeinanderfolgenden Fehlschläge nie StuckAfterFailures und
            // der harte Reconnect (LiveView.Stuck -> UsbReconnectWatchdog) würde NIE ausgelöst -
            // genau der beobachtete Endlos-"MTP device busy". Der Zähler wird stattdessen beim
            // ersten erfolgreich empfangenen Frame bzw. nach erfolgreichem Recovery-Neustart
            // im RunLoop auf 0 gesetzt - also nur bei echtem Fortschritt.

            // Token in eine lokale Variable ziehen statt das Feld _cts erst innerhalb der Lambda
            // zu lesen: ruft ein anderer Thread währenddessen StopAsync() auf (setzt _cts = null),
            // kann die Lambda sonst mit einem bereits null gewordenen Feld starten -> NRE
            // (beobachtet im Log: "RunLoop task faulted / NullReferenceException" mitten in einer
            // Busy-Storm-Situation mit vielen gleichzeitigen Start/Stop-Aufrufen).
            var cts = new CancellationTokenSource();
            _cts = cts;
            _task = Task.Run(() => RunLoop(cam, hub, cts.Token));

            // Beobachtet die Task-Exception sofort und loggt sie, statt sie unbeobachtet zu lassen
            // (sonst taucht sie u.U. erst Stunden später über den Finalizer-Thread als
            // "Unobserved task exception" auf, siehe TaskScheduler.UnobservedTaskException in Program.cs).
            _task.ContinueWith(t =>
            {
                var ex = t.Exception?.Flatten().InnerException ?? t.Exception;
                _log.Error("LiveViewPump: RunLoop task faulted", ex);
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        public async Task StopAsync(TimeSpan? timeout = null)
        {
            try { _cts?.Cancel(); } catch { }

            var t = _task;
            _task = null;          // <— wichtig: Neustart erlauben
            _cts = null;

            if (t == null) return;

            var to = timeout ?? TimeSpan.FromSeconds(5);
            try
            {
                var completed = await Task.WhenAny(t, Task.Delay(to)).ConfigureAwait(false);
                if (completed != t)
                    _log.Warn("LiveViewPump stop timed out; continuing anyway.");
            }
            catch { }
        }


        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private async Task RunLoop(ICameraDevice cam, FrameHub hub, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var timingSw = _startTimingSw;

            _log.Info($"LiveViewPump starting (fps={TargetFps})");
            if (timingSw != null)
                _log.Info($"[LV-TIMING] +{timingSw.ElapsedMilliseconds}ms T2 RunLoop entered (Task.Run angelaufen)");

            try
            {
                await StartLiveViewWithShortRetryAsync(cam).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Auch der Erst-Start zählt zu den aufeinanderfolgenden Fehlversuchen und muss ab
                // dem Schwellwert einen harten Reconnect anfordern. Sonst bleibt die Kamera bei
                // dauerhaftem "MTP device busy" hängen, weil jeder Aufrufer nur einen frischen,
                // sofort abbrechenden Pump erzeugt (der Recovery-Zweig weiter unten wird bei einem
                // fehlgeschlagenen Erst-Start gar nicht erst erreicht).
                var failures = NoteRestartFailure();
                _log.Error($"LiveViewPump: initial StartLiveView failed (attempt {failures}), aborting start.{CodeSuffix(ex)}", ex);

                if (failures >= StuckAfterFailures)
                {
                    _log.Error($"LiveViewPump: StartLiveView {failures}x in Folge nicht möglich - Kamera scheint dauerhaft blockiert (z.B. MTP device busy).{CodeSuffix(ex)} Fordere harten Reconnect an.", ex);
                    RaiseStuck();
                }

                return;
            }

            var lastFrameAt = sw.Elapsed;
            var lastKeepAliveAt = sw.Elapsed;

            while (!ct.IsCancellationRequested)
            {
                var loopStart = sw.Elapsed;

                try
                {
                    var timingSwLoop = _firstFrameTimed ? null : _startTimingSw;
                    if (timingSwLoop != null)
                        _log.Info($"[LV-TIMING] +{timingSwLoop.ElapsedMilliseconds}ms T5 cam.GetLiveViewImage() poll attempt begin");

                    LiveViewData lv = await _mta.InvokeAsync(() => cam.GetLiveViewImage()).ConfigureAwait(false);

                    if (timingSwLoop != null)
                        _log.Info($"[LV-TIMING] +{timingSwLoop.ElapsedMilliseconds}ms T6 cam.GetLiveViewImage() returned, extracting JPEG");

                    if (TryExtractJpeg(lv, out var jpeg))
                    {
                        hub.Update(jpeg);
                        lastFrameAt = sw.Elapsed;
                        if (_consecutiveRestartFailures != 0)
                            Volatile.Write(ref _consecutiveRestartFailures, 0);

                        if (timingSwLoop != null)
                        {
                            _log.Info($"[LV-TIMING] +{timingSwLoop.ElapsedMilliseconds}ms T7 erstes gültiges LiveView-Frame in FrameHub geschrieben (hub.Update) - Ende der Start-Zeitmessung");
                            _firstFrameTimed = true;
                        }
                    }
                    else if (timingSwLoop != null)
                    {
                        _log.Info($"[LV-TIMING] +{timingSwLoop.ElapsedMilliseconds}ms T6x kein gültiges JPEG in dieser Poll-Antwort, nächster Versuch folgt");
                    }

                    // KeepAlive (NotImplementedException wird abgefangen)
                    if (sw.Elapsed - lastKeepAliveAt > TimeSpan.FromSeconds(Math.Max(15, _settings.KeepAliveSeconds)))
                    {
                        try
                        {
                            await _mta.InvokeAsync(() =>
                            {
                                try { cam.GetStatus(OperationEnum.LiveView); }
                                catch (NotImplementedException) { /* ignore */ }
                            }).ConfigureAwait(false);
                        }
                        catch
                        {
                            /* ignore: KeepAlive darf den Stream nicht killen */
                        }

                        lastKeepAliveAt = sw.Elapsed;
                    }

                    // Watchdog: wenn länger keine Frames -> LiveView neu starten
                    if (sw.Elapsed - lastFrameAt > TimeSpan.FromSeconds(2))
                    {
                        _log.Warn("No LiveView frames for >2s, restarting LiveView...");

                        Exception restartError = null;
                        await _mta.InvokeAsync(() =>
                        {
                            try { cam.StopLiveView(); }
                            catch (Exception ex) { _log.Warn("LiveViewPump: StopLiveView during recovery failed: " + ex.Message); }

                            try { cam.StartLiveView(); }
                            catch (Exception ex) { restartError = ex; }
                        }).ConfigureAwait(false);

                        lastFrameAt = sw.Elapsed;

                        if (restartError == null)
                        {
                            if (_consecutiveRestartFailures != 0)
                                Volatile.Write(ref _consecutiveRestartFailures, 0);
                        }
                        else
                        {
                            var failures = NoteRestartFailure();
                            _log.Warn($"LiveViewPump: StartLiveView during recovery failed (attempt {failures}): {restartError.Message}{CodeSuffix(restartError)}");

                            if (failures >= StuckAfterFailures)
                            {
                                // Ein simples Stop/Start auf demselben SDK-Handle hilft hier erwiesenermaßen
                                // nicht mehr weiter (z.B. dauerhaftes "MTP device busy") -> Konsumenten
                                // benachrichtigen, damit ein harter Reconnect ausgelöst werden kann, und
                                // per Backoff nicht weiter unnötig gegen die blockierte Kamera anrennen.
                                _log.Error($"LiveViewPump: LiveView {failures}x in Folge nicht neu startbar - Kamera scheint blockiert (z.B. dauerhaft MTP busy).{CodeSuffix(restartError)} Fordere harten Reconnect an.", restartError);
                                RaiseStuck();

                                var backoff = TimeSpan.FromSeconds(Math.Min(30, 2 * failures));
                                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }

                                lastFrameAt = sw.Elapsed;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Ohne Zähler/Backoff hämmerte dieser Zweig eine SDK-Verbindung, die z.B. durch
                    // einen parallelen harten Reconnect gerade geschlossen wurde (COMException
                    // 0x802A0002 "Shutdown bereits aufgerufen"), unbegrenzt im ~200ms-Takt weiter -
                    // beobachtet als minutenlange Fehlerflut ohne jede Eskalation. Jetzt wie die
                    // anderen Fehlerpfade oben behandeln: mitzählen und ab StuckAfterFailures Stuck
                    // auslösen + Backoff, statt endlos gegen ein totes Handle zu retryen.
                    var failures = NoteRestartFailure();
                    _log.Error($"LiveViewPump loop error (attempt {failures}){CodeSuffix(ex)}", ex);

                    if (failures >= StuckAfterFailures)
                    {
                        _log.Error($"LiveViewPump: {failures}x in Folge Loop-Fehler - Kamera/SDK-Verbindung scheint ungültig.{CodeSuffix(ex)} Fordere harten Reconnect an.", ex);
                        RaiseStuck();

                        var backoff = TimeSpan.FromSeconds(Math.Min(30, 2 * failures));
                        try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    }
                    else
                    {
                        try { await Task.Delay(200, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    }
                }

                // Dynamischer FPS-Delay (mit work-time Kompensation)
                var fpsNow = TargetFps;
                var framePeriod = TimeSpan.FromMilliseconds(1000.0 / fpsNow);
                var workTime = sw.Elapsed - loopStart;
                var remaining = framePeriod - workTime;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

                try
                {
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            _log.Info("LiveViewPump stopping...");

            await _mta.InvokeAsync(() =>
            {
                try { cam.StopLiveView(); } catch { }
                cam.PreventShutDown = false;
            }).ConfigureAwait(false);

            _log.Info("LiveViewPump stopped.");
        }

        // Fängt eine kurzlebige Race direkt nach einem frischen Reconnect ab (siehe Kommentar bei
        // InitialStartRetryAttempts oben), bevor sie den kumulativen Fehlerzähler erreicht.
        private async Task StartLiveViewWithShortRetryAsync(ICameraDevice cam)
        {
            var timingSw = _startTimingSw;

            for (int attempt = 1; ; attempt++)
            {
                if (timingSw != null)
                    _log.Info($"[LV-TIMING] +{timingSw.ElapsedMilliseconds}ms T3 SDK cam.StartLiveView() call begin (attempt {attempt})");

                try
                {
                    await _mta.InvokeAsync(() =>
                    {
                        cam.PreventShutDown = true;
                        cam.StartLiveView();
                    }).ConfigureAwait(false);

                    if (timingSw != null)
                        _log.Info($"[LV-TIMING] +{timingSw.ElapsedMilliseconds}ms T4 SDK cam.StartLiveView() call returned OK (attempt {attempt})");
                    return;
                }
                catch when (attempt < InitialStartRetryAttempts)
                {
                    if (timingSw != null)
                        _log.Info($"[LV-TIMING] +{timingSw.ElapsedMilliseconds}ms T3x SDK cam.StartLiveView() attempt {attempt} failed transiently -> {InitialStartRetryDelay.TotalMilliseconds}ms Retry-Delay");
                    _log.Info($"LiveViewPump: initial StartLiveView attempt {attempt} failed transiently, retrying in {InitialStartRetryDelay.TotalMilliseconds}ms...");
                    await Task.Delay(InitialStartRetryDelay).ConfigureAwait(false);
                }
            }
        }

        private int NoteRestartFailure()
        {
            return Interlocked.Increment(ref _consecutiveRestartFailures);
        }

        // Der rohe MTP-Statuscode (z.B. 0xA004 = MTP_Invalid_Status vs. 0x2019 = MTP_Device_Busy,
        // siehe ErrorCodes.cs) wurde bisher nirgends geloggt - im File-Log stand nur der
        // Anzeigetext ("Invalid status."), der zwei unterschiedliche Fehlerursachen gleich
        // aussehen lässt. Für künftige Diagnose an den relevanten Stellen mit ausgeben.
        private static string CodeSuffix(Exception ex) =>
            ex is DeviceException de ? $" [MtpCode=0x{de.ErrorCode:X4}]" : "";

        private void RaiseStuck()
        {
            try { Stuck?.Invoke(); }
            catch (Exception ex) { _log.Warn("LiveViewPump: Stuck-Handler warf Exception: " + ex.Message); }
        }

        private static int ClampFps(int fps)
        {
            // Praktisch sinnvoll: 1..60 (du kannst min auch 5 setzen, wenn du willst)
            if (fps < 1) fps = 1;
            if (fps > 60) fps = 60;
            return fps;
        }

        private static bool TryExtractJpeg(LiveViewData lv, out byte[] jpeg)
        {
            jpeg = null;
            if (lv?.ImageData == null || lv.ImageData.Length < 4) return false;

            var buf = lv.ImageData;
            int off = lv.ImageDataPosition;
            if (off < 0 || off > buf.Length - 2) off = 0;

            // Fallback: JPEG SOI suchen
            if (!(buf[off] == 0xFF && buf[off + 1] == 0xD8))
            {
                for (int i = 0; i < buf.Length - 1; i++)
                {
                    if (buf[i] == 0xFF && buf[i + 1] == 0xD8) { off = i; break; }
                }
                if (!(buf[off] == 0xFF && buf[off + 1] == 0xD8)) return false;
            }

            int len = buf.Length - off;
            if (len <= 0) return false;

            jpeg = new byte[len];
            Buffer.BlockCopy(buf, off, jpeg, 0, len);
            return true;
        }
    }
}
