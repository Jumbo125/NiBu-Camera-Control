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
// - Circuit Breaker für wiederholte MTP-busy/PnP-Reset-Stürme (siehe
//   CAMERA_RECOVERY_CIRCUIT_BREAKER.md): NORMAL -> COOLDOWN -> (Controlled Retry) ->
//   ONE_WORKER_RESTART -> LOCKED_FAULT, mit echter Erfolgsverifikation über FrameHub statt
//   bloßem "Kamera wieder in der Liste".

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Photobox.CameraBridge.Core
{
    public enum CameraRecoveryState
    {
        Normal,
        Cooldown,
        LockedFault
    }

    /// <summary>
    /// Reconnect watchdog: detects camera disappearance and retries reconnect.
    /// If LiveView was desired/active, it will be restored after reconnect.
    /// Default Enabled=true.
    ///
    /// Für harte, erzwungene Reconnects (LiveView.Stuck / MTP busy) gilt zusätzlich der Circuit
    /// Breaker aus CAMERA_RECOVERY_CIRCUIT_BREAKER.md: genau EIN Reconnect-Versuch pro Episode,
    /// danach COOLDOWN statt Dauerfeuer. Der einfache "Kamera physisch fehlt"-Fall (kein
    /// forceReconnect) bleibt unverändert beim alten festen 2.5s-Retry, da dort keine PnP-Resets
    /// laufen und daher kein Sturm-Risiko besteht.
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

        // -----------------------------------------------------------------------------------
        // Circuit Breaker (siehe CAMERA_RECOVERY_CIRCUIT_BREAKER.md)
        // -----------------------------------------------------------------------------------

        private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LockedFaultAutoRetryDelay = TimeSpan.FromMinutes(45);
        private static readonly TimeSpan RestartBreadcrumbValidFor = TimeSpan.FromHours(1);
        private static readonly TimeSpan RestartBreadcrumbClearAfterHealthy = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RealSuccessWaitTimeout = TimeSpan.FromSeconds(6);

        private readonly object _stateLock = new object();
        private CameraRecoveryState _state = CameraRecoveryState.Normal;
        private DateTime _cooldownUntilUtc;
        private DateTime _lockedFaultSinceUtc;
        private bool _lockedFaultAutoRetryDone;
        private string _lastFaultReason;

        private DateTime _watchdogStartedUtc;
        private RestartBreadcrumb _pendingRestartBreadcrumb;
        private bool _breadcrumbCleared;

        /// <summary>
        /// Wird für ONE_WORKER_RESTART aufgerufen: soll den Worker-Prozess geordnet beenden
        /// (analog zum "worker.shutdown"-IPC-Pfad in Program.cs). Der ApiServer erkennt danach
        /// die verschwundene Pipe und startet den Worker über die bereits vorhandene
        /// WorkerHealthMonitor/WorkerProcessManager-Logik neu - hierfür ist keine neue
        /// ApiServer-seitige Logik nötig. Wird von Program.cs gesetzt.
        /// </summary>
        public Action RequestWorkerRestart { get; set; }

        public CameraRecoveryState RecoveryState { get { lock (_stateLock) return _state; } }
        public string RecoveryReason { get { lock (_stateLock) return _lastFaultReason; } }

        public DateTime? RecoveryCooldownUntilUtc
        {
            get
            {
                lock (_stateLock)
                    return _state == CameraRecoveryState.Cooldown ? (DateTime?)_cooldownUntilUtc : null;
            }
        }

        // Backoff für erzwungene Reconnects (LiveView.Stuck): ohne das hämmerte der Watchdog eine
        // wirklich wedged Nikon-MTP-Verbindung alle ~800ms (jeder Loop-Tick) mit einem vollen
        // harten Reconnect. Bleibt als Schutz für den (seltenen) Fall, dass mehrere Ticks
        // hintereinander erneut forceReconnect anfordern, bevor der Circuit Breaker greift.
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

            _watchdogStartedUtc = DateTime.UtcNow;
            LoadRestartBreadcrumbOnStartup();

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
                    continue;

                MaybeClearBreadcrumbAfterHealthyStartup();

                var state = RecoveryState;

                if (state == CameraRecoveryState.LockedFault)
                {
                    if (IsLockedFaultAutoRetryDue())
                    {
                        _log?.Warn($"Watchdog: LOCKED_FAULT - einmaliger langer Auto-Retry nach {LockedFaultAutoRetryDelay.TotalMinutes} min.");
                        await RunControlledRetryAsync(ct, fromLockedFault: true).ConfigureAwait(false);
                    }
                    continue;
                }

                if (state == CameraRecoveryState.Cooldown)
                {
                    if (DateTime.UtcNow >= RecoveryCooldownUntilUtc)
                        await RunControlledRetryAsync(ct, fromLockedFault: false).ConfigureAwait(false);
                    continue;
                }

                // state == Normal
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

                if (forceReconnect)
                {
                    // Erzwungener Reconnect (MTP busy / LiveView.Stuck) läuft über den Circuit
                    // Breaker: genau EIN Versuch pro Episode statt Dauerfeuer. Der verbleibende
                    // Backoff-Guard schützt nur davor, dass mehrere Loop-Ticks direkt hintereinander
                    // erneut anlaufen, bevor _forceReconnectRequested unten konsumiert ist.
                    var requiredDelay = TimeSpan.FromSeconds(Math.Min(
                        ForcedBaseDelay.TotalSeconds * Math.Pow(2, _forcedAttemptStreak),
                        ForcedMaxDelay.TotalSeconds));

                    if ((now - _lastAttemptUtc) < requiredDelay)
                        continue;

                    _lastAttemptUtc = now;
                    _forceReconnectRequested = false;

                    await RunSingleRecoveryEpisodeAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // Unforced "Kamera physisch fehlt": unverändertes Verhalten, kein Circuit Breaker
                // (keine PnP-Resets hier, daher kein Sturm-Risiko).
                if ((now - _lastAttemptUtc) < TimeSpan.FromSeconds(2.5))
                    continue;

                _lastAttemptUtc = now;
                await AttemptReconnect(ct, hard: false).ConfigureAwait(false);
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

        // -----------------------------------------------------------------------------------
        // Circuit-Breaker-Episoden
        // -----------------------------------------------------------------------------------

        private async Task RunSingleRecoveryEpisodeAsync(CancellationToken ct)
        {
            if (HasValidRestartBreadcrumb())
            {
                _log?.Warn("Watchdog: erneutes MTP-busy/Stuck-Signal so kurz nach automatischem " +
                    "Worker-Neustart -> direkt LOCKED_FAULT statt einer weiteren vollen Runde " +
                    "(Breadcrumb vom " + _pendingRestartBreadcrumb.TimestampUtc + ").");
                EnterLockedFault("Wiederkehrendes MTP-busy/Stuck-Problem kurz nach automatischem Worker-Neustart.");
                return;
            }

            _log?.Warn("Watchdog: SINGLE_RECOVERY - genau ein gezielter Reconnect-Versuch.");
            var ok = await AttemptReconnect(ct, hard: true, verifyRealSuccess: true).ConfigureAwait(false);

            if (ok)
            {
                EnterNormalOnVerifiedSuccess();
                return;
            }

            EnterCooldown("SINGLE_RECOVERY ohne verifizierten Erfolg (kein Frame/Capture nach Reconnect).");
        }

        private async Task RunControlledRetryAsync(CancellationToken ct, bool fromLockedFault)
        {
            // Globale Recovery-Sperre: verhindert, dass ein paralleler LiveView-Start/Capture den
            // Retry gleichzeitig noch einmal auslöst (siehe AttemptReconnect/_reconnectInProgress).
            _log?.Warn("Watchdog: " + (fromLockedFault ? "LOCKED_FAULT-Auto-Retry" : "CONTROLLED_RETRY") +
                " - einzelner Versuch.");

            var ok = await AttemptReconnect(ct, hard: true, verifyRealSuccess: true).ConfigureAwait(false);

            if (ok)
            {
                EnterNormalOnVerifiedSuccess();
                return;
            }

            if (fromLockedFault)
            {
                lock (_stateLock)
                    _lockedFaultAutoRetryDone = true;

                _log?.Warn("Watchdog: langer Auto-Retry aus LOCKED_FAULT ohne Erfolg -> bleibt LOCKED_FAULT.");
                return;
            }

            await RunOneWorkerRestartAsync().ConfigureAwait(false);
        }

        private Task RunOneWorkerRestartAsync()
        {
            _log?.Error("Watchdog: CONTROLLED_RETRY nach COOLDOWN fehlgeschlagen -> ONE_WORKER_RESTART " +
                "(einmaliger, geordneter Worker-Neustart).", null);

            WriteRestartBreadcrumb("CONTROLLED_RETRY nach COOLDOWN fehlgeschlagen.");

            var restart = RequestWorkerRestart;
            if (restart == null)
            {
                _log?.Warn("Watchdog: kein RequestWorkerRestart-Callback registriert -> direkt LOCKED_FAULT.");
                EnterLockedFault("ONE_WORKER_RESTART nicht verfügbar (kein Callback registriert).");
                return Task.CompletedTask;
            }

            // Der eigentliche Erfolg des Neustarts (Pipe + echte Kameraaktivität) wird vom NEU
            // gestarteten Worker-Prozess bewertet - dieser Prozess beendet sich hier nur geordnet.
            // Der ApiServer (WorkerHealthMonitor, bereits vorhanden) erkennt die verschwundene
            // Pipe und startet den Worker neu (kein neuer ApiServer-Code nötig). Tritt danach im
            // neuen Prozess dasselbe Problem erneut auf, sorgt die Restart-Breadcrumb dafür, dass
            // RunSingleRecoveryEpisodeAsync direkt LOCKED_FAULT setzt, statt eine zweite volle
            // Runde (COOLDOWN, CONTROLLED_RETRY, weiterer Restart) zu fahren.
            try { restart(); }
            catch (Exception ex) { _log?.Warn("Watchdog: RequestWorkerRestart failed: " + ex.Message); }

            return Task.CompletedTask;
        }

        private void EnterCooldown(string reason)
        {
            lock (_stateLock)
            {
                _state = CameraRecoveryState.Cooldown;
                _cooldownUntilUtc = DateTime.UtcNow + CooldownDuration;
                _lastFaultReason = reason;
            }

            // COOLDOWN: LiveView aus, keine weiteren PnP-Resets, IPC/status.get bleibt ansprechbar
            // (läuft weiter über den normalen Loop-Tick, nur ohne Reconnect-Aktionen).
            try { _host.StopLiveView(); } catch { }

            _log?.Warn($"Watchdog: -> COOLDOWN bis {_cooldownUntilUtc:O}. Grund: {reason}");
        }

        private void EnterLockedFault(string reason)
        {
            lock (_stateLock)
            {
                _state = CameraRecoveryState.LockedFault;
                _lockedFaultSinceUtc = DateTime.UtcNow;
                _lockedFaultAutoRetryDone = false;
                _lastFaultReason = reason;
            }

            try { _host.StopLiveView(); } catch { }

            _log?.Error("Watchdog: -> LOCKED_FAULT. Grund: " + reason + " Betreiberhinweis: Kamera, " +
                "Netzteil, Kabel und USB-Port prüfen. Keine weiteren automatischen PnP-Resets/" +
                $"Worker-Neustarts. Manueller Reset erforderlich (oder ein einzelner Auto-Retry nach " +
                $"{LockedFaultAutoRetryDelay.TotalMinutes} min).", null);
        }

        private void EnterNormalOnVerifiedSuccess()
        {
            lock (_stateLock)
            {
                _state = CameraRecoveryState.Normal;
                _lastFaultReason = null;
            }

            _forcedAttemptStreak = 0;
            _consecutiveFailedAttempts = 0;
            TryDeleteRestartBreadcrumb();

            _log?.Info("Watchdog: echter Erfolg (Frame/Capture) nach Reconnect verifiziert -> zurück in NORMAL.");
        }

        /// <summary>
        /// Manueller Reset aus LOCKED_FAULT (oder COOLDOWN) zurück in einen neuen SINGLE_RECOVERY-
        /// Versuch, ausgelöst über das IPC-Kommando "camera.recovery.reset". Ohne Wirkung, wenn
        /// der Circuit Breaker bereits im Normalzustand ist.
        /// </summary>
        public void ManualReset()
        {
            lock (_stateLock)
            {
                _state = CameraRecoveryState.Normal;
                _lastFaultReason = null;
            }

            TryDeleteRestartBreadcrumb();
            _forcedAttemptStreak = 0;
            _forceReconnectRequested = true;

            _log?.Warn("Watchdog: manueller Reset angefordert -> zurück in NORMAL, neuer SINGLE_RECOVERY-Versuch folgt im nächsten Tick.");
        }

        private bool IsLockedFaultAutoRetryDue()
        {
            lock (_stateLock)
            {
                if (_lockedFaultAutoRetryDone) return false;
                return DateTime.UtcNow - _lockedFaultSinceUtc >= LockedFaultAutoRetryDelay;
            }
        }

        // -----------------------------------------------------------------------------------
        // Restart-Breadcrumb: überlebt einen Environment-Reset über einen Worker-Prozess-Neustart
        // hinweg (In-Memory-State geht dabei verloren) und verhindert, dass eine dauerhaft wedged
        // Kamera eine endlose Kette von NORMAL -> ... -> ONE_WORKER_RESTART -> NORMAL -> ...
        // über mehrere Prozess-Neustarts hinweg auslöst.
        // -----------------------------------------------------------------------------------

        [System.Runtime.Serialization.DataContract]
        private sealed class RestartBreadcrumb
        {
            [System.Runtime.Serialization.DataMember] public string Serial { get; set; }
            [System.Runtime.Serialization.DataMember] public string TimestampUtc { get; set; }
            [System.Runtime.Serialization.DataMember] public string Reason { get; set; }
        }

        private static string BreadcrumbPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "camera_recovery_restart.json");

        private void LoadRestartBreadcrumbOnStartup()
        {
            try
            {
                var path = BreadcrumbPath;
                if (!File.Exists(path))
                    return;

                var crumb = JsonUtil.LoadFile<RestartBreadcrumb>(path);
                if (crumb == null ||
                    !DateTime.TryParse(crumb.TimestampUtc, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                {
                    TryDeleteRestartBreadcrumb();
                    return;
                }

                if (DateTime.UtcNow - ts.ToUniversalTime() > RestartBreadcrumbValidFor)
                {
                    TryDeleteRestartBreadcrumb();
                    return;
                }

                _pendingRestartBreadcrumb = crumb;
                _log?.Warn("Watchdog: Restart-Breadcrumb gefunden (Neustart am " + crumb.TimestampUtc +
                    ", Grund: " + crumb.Reason + "). Tritt das Problem erneut auf, wird direkt LOCKED_FAULT gesetzt.");
            }
            catch (Exception ex)
            {
                _log?.Warn("Watchdog: LoadRestartBreadcrumbOnStartup failed: " + ex.Message);
            }
        }

        private bool HasValidRestartBreadcrumb()
        {
            if (_pendingRestartBreadcrumb == null)
                return false;

            if (!DateTime.TryParse(_pendingRestartBreadcrumb.TimestampUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ||
                DateTime.UtcNow - ts.ToUniversalTime() > RestartBreadcrumbValidFor)
            {
                _pendingRestartBreadcrumb = null;
                return false;
            }

            return true;
        }

        private void MaybeClearBreadcrumbAfterHealthyStartup()
        {
            if (_breadcrumbCleared || _pendingRestartBreadcrumb == null)
                return;

            // Kein erneuter forceReconnect innerhalb der Beobachtungsfrist seit Prozessstart ->
            // der Neustart hat offenbar geholfen. Breadcrumb löschen, damit ein späteres,
            // unabhängiges Wedge-Ereignis wieder den vollen Zyklus durchlaufen darf.
            if (DateTime.UtcNow - _watchdogStartedUtc < RestartBreadcrumbClearAfterHealthy)
                return;

            _breadcrumbCleared = true;
            _pendingRestartBreadcrumb = null;
            TryDeleteRestartBreadcrumb();
            _log?.Info("Watchdog: Worker läuft seit " + RestartBreadcrumbClearAfterHealthy.TotalMinutes +
                " min stabil nach automatischem Neustart -> Restart-Breadcrumb gelöscht.");
        }

        private void WriteRestartBreadcrumb(string reason)
        {
            try
            {
                var crumb = new RestartBreadcrumb
                {
                    Serial = _host.GetStatusSnapshot()?.Serial,
                    TimestampUtc = DateTime.UtcNow.ToString("O"),
                    Reason = reason
                };

                using (var fs = File.Create(BreadcrumbPath))
                    JsonUtil.WriteJson(fs, crumb);
            }
            catch (Exception ex)
            {
                _log?.Warn("Watchdog: WriteRestartBreadcrumb failed: " + ex.Message);
            }
        }

        private void TryDeleteRestartBreadcrumb()
        {
            try
            {
                var path = BreadcrumbPath;
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        // -----------------------------------------------------------------------------------
        // Reconnect-Ausführung
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Führt einen Reconnect-Versuch aus. Bei hard=true wird die SDK-Verbindung geschlossen
        /// und ein gezielter PnP-Reset (CM_Disable_DevNode/CM_Enable_DevNode) auf genau diesem
        /// Devnode ausgeführt, bevor neu enumeriert wird.
        ///
        /// verifyRealSuccess=true (Circuit-Breaker-Pfad): "Kamera wieder in der Liste" allein
        /// zählt NICHT als Erfolg. Ist LiveView gewünscht, wird nach dem Restore bis zu
        /// RealSuccessWaitTimeout auf ein tatsächlich eintreffendes Frame gewartet (FrameHub-
        /// Zähler). Ist kein LiveView gewünscht, gibt es keine SDK-Operation, an der sich ein
        /// echter Erfolg objektiv festmachen ließe - dann zählt die erfolgreiche Neuauswahl der
        /// Kamera als bestmöglicher verfügbarer Nachweis (best effort, wird geloggt).
        /// </summary>
        private async Task<bool> AttemptReconnect(CancellationToken ct, bool hard, bool verifyRealSuccess = false)
        {
            if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 1)
                return false;

            try
            {
                _log?.Info("Watchdog: attempting reconnect" + (hard ? " (hard, closing SDK connection first)" : "") + "...");

                // WICHTIG: LiveView IMMER zuerst sauber stoppen (wartet in CameraHost.StopLiveView()
                // auf das Ende der LiveViewPump-Hintergrundschleife), bevor die SDK-Verbindung
                // geschlossen wird. Vorher lief TryCloseSelectedCameraConnection() (cam.Close())
                // VOR diesem Stop - dabei konnte die noch aktive LiveViewPump-Schleife im selben
                // Moment cam.GetLiveViewImage() auf dem gerade geschlossenen SDK-Handle aufrufen
                // und lieferte dann minutenlang ungebremst "Shutdown-Funktion bereits aufgerufen"
                // (COMException 0x802A0002) im ~200ms-Takt, bevor der (zu späte) StopLiveView()
                // hier die Pump-Schleife endlich einholte. Erst stoppen, dann schließen behebt das.
                try { _host.StopLiveView(); } catch { }

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

                try
                {
                    var refreshTask = _host.RefreshAsync();
                    var winner = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(8), ct)).ConfigureAwait(false);
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

                    return false;
                }

                try
                {
                    _host.SelectCamera(0);
                }
                catch (Exception ex)
                {
                    _log?.Warn("Watchdog: SelectCamera(0) failed: " + ex.Message);
                    if (hard) _forcedAttemptStreak++;
                    return false;
                }

                _hadCamera = true;
                _consecutiveFailedAttempts = 0;
                _log?.Info("Watchdog: camera reconnected.");

                if (!_liveViewDesired)
                {
                    if (!verifyRealSuccess)
                    {
                        if (hard) _forcedAttemptStreak = 0;
                        return true;
                    }

                    // Kein LiveView gewünscht -> keine SDK-Operation verfügbar, an der sich ein
                    // echter Erfolg objektiv festmachen ließe. Neuauswahl der Kamera ist der
                    // bestmögliche verfügbare Nachweis (best effort).
                    _log?.Info("Watchdog: kein LiveView gewünscht -> Erfolgsverifikation reduziert auf 'Kamera neu ausgewählt' (best effort).");
                    if (hard) _forcedAttemptStreak = 0;
                    return true;
                }

                try
                {
                    var framesBefore = _host.FrameHub.TotalFrames;
                    _host.StartLiveView();
                    _log?.Info("Watchdog: LiveView restore requested.");

                    if (!verifyRealSuccess)
                        return true;

                    var gotFrame = await WaitForRealFrameAsync(framesBefore, RealSuccessWaitTimeout, ct).ConfigureAwait(false);
                    if (gotFrame)
                    {
                        _log?.Info("Watchdog: echtes LiveView-Frame nach Reconnect empfangen -> verifizierter Erfolg.");
                        return true;
                    }

                    _log?.Warn("Watchdog: LiveView wurde neu gestartet, aber innerhalb von " +
                        RealSuccessWaitTimeout.TotalSeconds + "s kam kein echtes Frame an -> kein verifizierter Erfolg.");
                    if (hard) _forcedAttemptStreak++;
                    return false;
                }
                catch (Exception ex)
                {
                    if (hard) _forcedAttemptStreak++;
                    _log?.Warn("Watchdog: LiveView restore failed: " + ex.Message);
                    return false;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectInProgress, 0);
            }
        }

        /// <summary>
        /// Wartet bis zu <paramref name="timeout"/> darauf, dass FrameHub.TotalFrames über
        /// <paramref name="framesBefore"/> hinaus ansteigt - d.h. dass tatsächlich ein neues Frame
        /// eintrifft, statt nur "StartLiveView() hat nicht geworfen".
        /// </summary>
        private async Task<bool> WaitForRealFrameAsync(long framesBefore, TimeSpan timeout, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (ct.IsCancellationRequested) return false;
                if (_host.FrameHub.TotalFrames > framesBefore) return true;

                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch { return false; }
            }

            return _host.FrameHub.TotalFrames > framesBefore;
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
