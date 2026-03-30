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

        // Dynamisch verstellbare FPS (thread-safe)
        private int _targetFps;

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

        public void Start(ICameraDevice cam, FrameHub hub)
        {
            if (cam == null) throw new ArgumentNullException(nameof(cam));
            if (hub == null) throw new ArgumentNullException(nameof(hub));
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunLoop(cam, hub, _cts.Token));
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

            _log.Info($"LiveViewPump starting (fps={TargetFps})");

            await _mta.InvokeAsync(() =>
            {
                cam.PreventShutDown = true;
                cam.StartLiveView();
            }).ConfigureAwait(false);

            var lastFrameAt = sw.Elapsed;
            var lastKeepAliveAt = sw.Elapsed;

            while (!ct.IsCancellationRequested)
            {
                var loopStart = sw.Elapsed;

                try
                {
                    LiveViewData lv = await _mta.InvokeAsync(() => cam.GetLiveViewImage()).ConfigureAwait(false);

                    if (TryExtractJpeg(lv, out var jpeg))
                    {
                        hub.Update(jpeg);
                        lastFrameAt = sw.Elapsed;
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
                        await _mta.InvokeAsync(() =>
                        {
                            try { cam.StopLiveView(); } catch { }
                            try { cam.StartLiveView(); } catch { }
                        }).ConfigureAwait(false);
                        lastFrameAt = sw.Elapsed;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("LiveViewPump loop error", ex);
                    await Task.Delay(200, ct).ConfigureAwait(false);
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
