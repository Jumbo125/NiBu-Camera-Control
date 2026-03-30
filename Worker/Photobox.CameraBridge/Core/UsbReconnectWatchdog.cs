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

        public UsbReconnectWatchdog(CameraHost host, RingLogger log)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _log = log;
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

                if (hasCamera)
                {
                    _hadCamera = true;
                    continue;
                }

                // camera missing
                if (_hadCamera)
                {
                    _hadCamera = false;

                    // snapshot: if LV was active at the time of loss, remember intent
                    if (SafeIsLiveViewRunning())
                        _liveViewDesired = true;

                    _log?.Warn("Watchdog: camera LOST. liveViewDesired=" + _liveViewDesired + " -> reconnect loop begins.");
                }
                var now = DateTime.UtcNow;

                // attempt reconnect every ~2.5s while camera is missing
                if ((now - _lastAttemptUtc) > TimeSpan.FromSeconds(2.5))
                {
                    _lastAttemptUtc = now;
                    await AttemptReconnect(ct);
                }
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

        private async Task AttemptReconnect(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _reconnectInProgress, 1) == 1)
                return;

            try
            {
                _log?.Info("Watchdog: attempting reconnect...");

                // best-effort: stop LV to release SDK resources
                try { _host.StopLiveView(); } catch { }

                try
                {
                    await _host.RefreshAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Warn("Watchdog: RefreshAsync failed: " + ex.Message);
                }

                if (!SafeHasCamera())
                {
                    _log?.Warn("Watchdog: still no camera.");
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
                _log?.Info("Watchdog: camera reconnected.");

                if (_liveViewDesired)
                {
                    try
                    {
                        _host.StartLiveView();
                        _log?.Info("Watchdog: LiveView restored.");
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn("Watchdog: LiveView restore failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectInProgress, 0);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }

            try { _loopTask?.Wait(1500); } catch { }
            try { _cts?.Dispose(); } catch { }

            _cts = null;
            _loopTask = null;
        }
    }
}
