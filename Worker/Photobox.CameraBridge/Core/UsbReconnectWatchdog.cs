// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: UsbReconnectWatchdog.cs
// Zweck: Überwacht Kameraabbrüche und stößt bei Bedarf einen Reconnect an.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Verbindungsverlust zur Kamera erkennen
// - Reconnect-Versuche steuern
// - gewünschten LiveView-Zustand nach Wiederverbindung wiederherstellen
// - automatisches Recovery im Dauerbetrieb ermöglichen

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Photobox.CameraBridge.Core
{
    /// <summary>
    /// Reconnect watchdog: detects camera disappearance and retries reconnect.
    /// If LiveView was desired/active, it will be restored after reconnect.
    /// Default Enabled=true.
    /// </summary>
    public sealed class UsbReconnectWatchdog : IDisposable
    {
        private readonly CameraHost _host;
        private readonly RingLogger _log;

        private CancellationTokenSource _cts;
        private Task _loopTask;

        private volatile bool _enabled = true;
        private volatile bool _liveViewDesired;
        private volatile bool _hadCamera;

        private DateTime _lastAttemptUtc = DateTime.MinValue;
        private int _reconnectInProgress = 0;

        // Wird von LiveViewPump.Stuck gesetzt: Kamera ist laut CameraDeviceManager weiterhin
        // "verbunden", reagiert aber wiederholt nicht mehr auf StartLiveView (z.B. dauerhaft
        // "MTP device busy"). SafeHasCamera() allein erkennt das nicht, da das Gerät ja noch
        // in ConnectedDevices auftaucht - deshalb dieser separate Trigger für einen harten Reconnect.
        private volatile bool _forceReconnectRequested;

        // Gegen reine Wiederholungsmeldungen: nach X erfolglosen Versuchen einmal einen Hinweis loggen.
        private const int HintAfterAttempts = 5; // ~5 * 2.5s = ~12.5s
        private int _consecutiveFailedAttempts = 0;

        // Backoff für erzwungene Reconnects (LiveView.Stuck): ohne das hier hämmerte der
        // Watchdog eine wirklich wedged Nikon-MTP-Verbindung alle ~800ms (jeder Loop-Tick)
        // mit einem vollen harten Reconnect, ohne jede Pause - der 2.5s-Cooldown unten galt
        // nur für den "Kamera fehlt"-Fall, nicht für forceReconnect. Beobachtung im Log: die
        // Kamera erholte sich teils erst nach mehreren Minuten Dauerbeschuss von selbst.
        // Verdacht: das Dauerfeuer verhindert/verzögert genau die Erholung, die es erzwingen soll.
        private static readonly TimeSpan ForcedBaseDelay = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan ForcedMaxDelay = TimeSpan.FromSeconds(20);
        private int _forcedAttemptStreak = 0;

        public UsbReconnectWatchdog(CameraHost host, RingLogger log)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _log = log;

            if (_host.LiveView != null)
                _host.LiveView.Stuck += OnLiveViewStuck;
        }

        private void OnLiveViewStuck()
        {
            _log?.Warn("Watchdog: LiveView meldet sich als blockiert (wiederholtes StartLiveView-Versagen) -> harter Reconnect wird angefordert.");
            _forceReconnectRequested = true;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                _log?.Info("Watchdog: " + (value ? "ENABLED" : "DISABLED"));
            }
        }

        public bool LiveViewDesired => _liveViewDesired;

        public void SetLiveViewDesired(bool desired) => _liveViewDesired = desired;

        public void Start()
        {
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            _hadCamera = SafeHasCamera();

            _loopTask = Task.Run(() => Loop(_cts.Token));
            _log?.Info("Watchdog: started. baseline camera=" + _hadCamera);
        }

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(800, ct).ConfigureAwait(false); } catch { }
                if (ct.IsCancellationRequested) break;

                if (!Enabled)
                {
                    continue;
                }

                bool hasCamera = SafeHasCamera();
                bool forceReconnect = _forceReconnectRequested;

                if (hasCamera && !forceReconnect)
                {
                    _hadCamera = true;
                    _forcedAttemptStreak = 0;
                    continue;
                }

                // camera missing OR forced (Kamera "da", aber laut LiveViewPump blockiert/nicht ansprechbar)
                if (_hadCamera)
                {
                    _hadCamera = false;

                    // snapshot: if LV was active at the time of loss, remember intent
                    if (SafeIsLiveViewRunning() || forceReconnect)
                        _liveViewDesired = true;

                    _log?.Warn("Watchdog: camera LOST" + (forceReconnect ? " (forced, Kamera meldet busy)" : "") +
                        ". liveViewDesired=" + _liveViewDesired + " -> reconnect loop begins.");
                }
                var now = DateTime.UtcNow;

                // Forced reconnects (echtes "MTP busy") bekommen wachsenden Backoff statt des
                // festen 2.5s-Takts, damit wir eine wedged Kamera nicht im Sekundentakt weiter
                // beschießen. _forceReconnectRequested bleibt bewusst gesetzt, solange wir noch
                // in der Cooldown-Phase sind - erst der tatsächliche Versuch verbraucht ihn.
                var requiredDelay = forceReconnect
                    ? TimeSpan.FromSeconds(Math.Min(
                        ForcedBaseDelay.TotalSeconds * Math.Pow(2, _forcedAttemptStreak),
                        ForcedMaxDelay.TotalSeconds))
                    : TimeSpan.FromSeconds(2.5);

                if ((now - _lastAttemptUtc) < requiredDelay)
                    continue;

                _lastAttemptUtc = now;
                if (forceReconnect)
                    _forceReconnectRequested = false;

                await AttemptReconnect(ct, hard: forceReconnect);
            }
        }

        private bool SafeHasCamera()
        {
            try
            {
                var list = _host.GetCameraList();
                return list != null && list.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool SafeIsLiveViewRunning()
        {
            try
            {
                // Avoid hard dependency on a specific property name.
                var lv = _host.LiveView;
                if (lv == null) return false;

                var t = lv.GetType();
                var p = t.GetProperty("IsRunning", BindingFlags.Public | BindingFlags.Instance)
                     ?? t.GetProperty("Running", BindingFlags.Public | BindingFlags.Instance)
                     ?? t.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Instance);

                if (p != null && p.PropertyType == typeof(bool))
                    return (bool)p.GetValue(lv);

                // fallback: if there is a method like GetState() returning bool
                var m = t.GetMethod("IsRunning", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                     ?? t.GetMethod("Running", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (m != null && m.ReturnType == typeof(bool))
                    return (bool)m.Invoke(lv, null);
            }
            catch { }

            return false;
        }

        private async Task AttemptReconnect(CancellationToken ct, bool hard = false)
        {
            if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 1)
                return;

            try
            {
                _log?.Info("Watchdog: attempting reconnect" + (hard ? " (hard, closing SDK connection first)" : "") + "...");

                if (hard)
                {
                    // Ein reines Stop/Start auf demselben SDK-Handle bringt eine wirklich blockierte
                    // (z.B. dauerhaft "MTP device busy") Kamera nicht zurück. Verbindung explizit
                    // schließen, damit RefreshAsync() unten das Gerät sauber neu öffnet.
                    var serialBeforeClose = _host.GetStatusSnapshot()?.Serial;

                    try { _host.TryCloseSelectedCameraConnection(); }
                    catch (Exception ex) { _log?.Warn("Watchdog: TryCloseSelectedCameraConnection failed: " + ex.Message); }

                    // Beobachtung (manuell im Geräte-Manager reproduziert): ein reines Schließen des
                    // SDK-Handles reicht bei einer wirklich wedged Kamera nicht - erst ein Deaktivieren/
                    // Aktivieren des Devnodes bringt sie zuverlässig zurück. Das ging bisher nur, wenn
                    // der Worker-Prozess komplett beendet wurde (SDK-Handle war sonst noch offen und
                    // hat CM_Disable_DevNode per PnP-Veto blockiert). Da der Handle jetzt gerade
                    // geschlossen wurde, gezielt nur diesen einen Devnode per PnP neu enumerieren -
                    // kein Prozess-Neustart nötig.
                    try
                    {
                        var reset = TargetedUsbReset.TryResetBySerial(serialBeforeClose, _log);
                        _log?.Info("Watchdog: targeted PnP reset " + (reset ? "succeeded" : "did not run/succeed") + ".");
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn("Watchdog: TargetedUsbReset.TryResetBySerial failed: " + ex.Message);
                    }
                }

                // best-effort: stop LV to release SDK resources
                try { _host.StopLiveView(); } catch { }

                try
                {
                    var refreshTask = _host.RefreshAsync();
                    var winner = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
                    if (winner != refreshTask)
                        _log?.Warn("Watchdog: RefreshAsync timed out after 8s — USB device may be unresponsive.");
                }
                catch (Exception ex)
                {
                    _log?.Warn("Watchdog: RefreshAsync failed: " + ex.Message);
                }

                if (!SafeHasCamera())
                {
                    _consecutiveFailedAttempts++;
                    if (hard) _forcedAttemptStreak++;
                    _log?.Warn("Watchdog: still no camera.");

                    if (_consecutiveFailedAttempts == HintAfterAttempts)
                    {
                        _log?.Warn("Watchdog: keine Kamera gefunden nach " + _consecutiveFailedAttempts +
                            " Versuchen (~" + (int)(_consecutiveFailedAttempts * 2.5) + "s). " +
                            "Falls die Kamera eingesteckt ist, aber trotzdem nicht gefunden wird: " +
                            "sie ist evtl. ausgeschaltet, in einem schlechten USB-Zustand, oder der " +
                            "USB-Port/Hub antwortet nicht mehr -> Stromversorgung/Kabel prüfen.");
                    }

                    return;
                }

                try
                {
                    _host.SelectCamera(0);
                }
                catch (Exception ex)
                {
                    _log?.Warn("Watchdog: SelectCamera(0) failed: " + ex.Message);
                }

                _hadCamera = true;
                _consecutiveFailedAttempts = 0;
                _log?.Info("Watchdog: camera reconnected.");

                if (_liveViewDesired)
                {
                    try
                    {
                        _host.StartLiveView();
                        _log?.Info("Watchdog: LiveView restored.");

                        // Ob der Restore wirklich gehalten hat, zeigt sich erst asynchron im
                        // LiveViewPump-RunLoop (Frames kommen an oder auch nicht) - hier kein
                        // throw bedeutet nur "SDK-Aufruf ist durchgelaufen", nicht "Kamera liefert
                        // wieder Bilder". Der Streak wird deshalb NICHT hier zurückgesetzt, sondern
                        // oben im Loop, sobald hasCamera true UND kein neuer forceReconnect mehr
                        // ansteht (siehe "_forcedAttemptStreak = 0" beim ruhigen Zustand).
                    }
                    catch (Exception ex)
                    {
                        if (hard) _forcedAttemptStreak++;
                        _log?.Warn("Watchdog: LiveView restore failed: " + ex.Message);
                    }
                }
                else if (hard)
                {
                    // Reconnect selbst hat geklappt (Kamera wieder da), auch ohne LiveView-Restore
                    // ist das ein echter Fortschritt.
                    _forcedAttemptStreak = 0;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectInProgress, 0);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_host.LiveView != null)
                    _host.LiveView.Stuck -= OnLiveViewStuck;
            }
            catch { }

            try { _cts?.Cancel(); } catch { }

            try { _loopTask?.Wait(1500); } catch { }
            try { _cts?.Dispose(); } catch { }

            _cts = null;
            _loopTask = null;
        }
    }
}
