// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: BridgeWorkerAdapter.cs
// Zweck: Adapter zwischen CameraHost/Worker-Logik und dem IPC-Interface des Workers.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - IBridgeWorker implementieren
// - Kamera-, LiveView-, Settings- und Capture-Aufrufe an den Host weiterleiten
// - Requests aus IPC-DTOs in Worker-Operationen übersetzen
// - kritische Operationen serialisieren und absichern

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Photobox.Bridge.WorkerIpc;
using Shared = Photobox.Bridge.Shared;

using Photobox.CameraBridge.Core;

namespace Photobox.CameraBridge.Ipc
{
    public sealed class BridgeWorkerAdapter : IBridgeWorker
    {
        private readonly CameraHost _host;
        private readonly UsbReconnectWatchdog _watchdog;
        private readonly AppSettings _settings;

        // Serialisiert kritische Operations
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private static bool IsAbsoluteWindowsPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            p = p.Trim();

            if (p.StartsWith(@"\\") || p.StartsWith("//")) return true;

            return p.Length >= 3
                   && char.IsLetter(p[0]) && p[1] == ':'
                   && (p[2] == '\\' || p[2] == '/');
        }

        private static double? TryParseExposure(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        private static async Task WaitForFileStableAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            const int timeoutMs = 5000;
            const int pollMs = 120;

            var sw = Stopwatch.StartNew();
            long lastLen = -1;
            int stableHits = 0;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 0)
                    {
                        var len = fi.Length;

                        try
                        {
                            using var fs = new FileStream(
                                path,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read);
                        }
                        catch
                        {
                            await Task.Delay(pollMs, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (len == lastLen)
                        {
                            stableHits++;
                            if (stableHits >= 2)
                                return;
                        }
                        else
                        {
                            lastLen = len;
                            stableHits = 0;
                        }
                    }
                }
                catch
                {
                }

                await Task.Delay(pollMs, ct).ConfigureAwait(false);
            }
        }


        private static bool GetStartLiveViewAfterCapture(object req)
        {
            if (req == null) return false;

            try
            {
                var t = req.GetType();
                var prop =
                    t.GetProperty("StartLiveViewAfterCapture")
                    ?? t.GetProperty("RestartLiveViewAfterCapture")
                    ?? t.GetProperty("CapturePlusLiveView")
                    ?? t.GetProperty("CaptureAndLiveView");

                if (prop == null || prop.PropertyType != typeof(bool))
                    return false;

                return (bool)(prop.GetValue(req) ?? false);
            }
            catch
            {
                return false;
            }
        }
        private string ComputeLastFrameUtc()
        {
            try
            {
                var lastTick = _host.FrameHub.LastFrameTick;
                if (lastTick <= 0) return DateTime.UtcNow.ToString("O");

                var now = Stopwatch.GetTimestamp();
                var ms = (long)((now - lastTick) * 1000.0 / Stopwatch.Frequency);
                if (ms < 0) return DateTime.UtcNow.ToString("O");

                return DateTime.UtcNow.AddMilliseconds(-ms).ToString("O");
            }
            catch
            {
                return DateTime.UtcNow.ToString("O");
            }
        }

        public BridgeWorkerAdapter(CameraHost host, UsbReconnectWatchdog watchdog, AppSettings settings)
        {
            _host = host;
            _watchdog = watchdog;
            _settings = settings;
        }

        public Task<Shared.WorkerStatusDto> GetStatusAsync(CancellationToken ct)
        {
            // IMPORTANT: don't read SDK/device objects from this thread.
            // Use CameraHost snapshot methods (they marshal to the MTA thread).
            var selectedId = _host.GetSelectedCameraId();
            var camInfo = (selectedId.HasValue
                ? _host.GetCameraList().FirstOrDefault(x => x.Id == selectedId.Value)
                : null);

            long total = _host.FrameHub.TotalFrames;
            long lastTick = _host.FrameHub.LastFrameTick;

            long? ageMs = null;
            string lastUtc = null;

            if (lastTick > 0)
            {
                var now = Stopwatch.GetTimestamp();
                var ms = (long)((now - lastTick) * 1000.0 / Stopwatch.Frequency);
                if (ms >= 0)
                {
                    ageMs = ms;
                    lastUtc = DateTime.UtcNow.AddMilliseconds(-ms).ToString("O");
                }
            }

            var dto = new Shared.WorkerStatusDto
            {
                LiveViewRunning = _host.LiveView.IsRunning,
                Selected = camInfo?.DisplayName,
                Manufacturer = camInfo?.Manufacturer,
                Model = camInfo?.Model,
                Serial = camInfo?.Serial,
                FramesTotal = total,
                FrameAgeMs = ageMs,
                LastFrameUtc = lastUtc,
                Source = new Shared.StreamSourceDto { Serial = camInfo?.Serial, Id = selectedId },
                WatchdogEnabled = _watchdog != null && _watchdog.Enabled
            };

            return Task.FromResult(dto);
        }

        public async Task<bool> SetSettingsAsync(CameraSettingsDtoPartial patch, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Patch hat nur ISO/Shutter/WB (siehe Interface-Datei)
                return await _host.SetSettingsAsync(
                    patch?.Iso,
                    patch?.Shutter,
                    patch?.WhiteBalance,
                    aperture: null,
                    exposure: null
                ).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<List<Shared.CameraInfoDto>> GetCamerasAsync(CancellationToken ct)
        {
            var list = _host.GetCameraList()
                .Select(c => new Shared.CameraInfoDto
                {
                    Id = c.Id,
                    DisplayName = c.DisplayName,
                    Manufacturer = c.Manufacturer,
                    Model = c.Model,
                    Serial = c.Serial,
                    Port = c.Port,
                    IsConnected = c.IsConnected
                })
                .ToList();

            return Task.FromResult(list);
        }

        public async Task<bool> SelectCameraAsync(Shared.SelectRequestDto req, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Vorgabe: LiveView vorher stoppen
                try { _host.StopLiveView(); } catch { }

                var serial = req != null ? req.Serial : null;
                var id = req != null ? req.Id : (int?)null;

                if (!string.IsNullOrWhiteSpace(serial))
                    return _host.SelectCameraBySerial(serial);

                if (id.HasValue)
                    return _host.SelectCamera(id.Value);

                return false;
            }
            finally { _gate.Release(); }
        }

        public async Task RefreshAsync(Shared.RefreshRequestDto req, CancellationToken ct)
        {
            int ms = (req != null && req.TimeoutMs > 0) ? req.TimeoutMs : 4000;

            await _gate.WaitAsync(ct).ConfigureAwait(false);

            var wdOld = _watchdog != null && _watchdog.Enabled;
            if (_watchdog != null) _watchdog.Enabled = false;

            Task refreshTask;
            try
            {
                // Refresh starten (NICHT cancelln!)
                refreshTask = _host.RefreshAsync();
            }
            catch
            {
                try { if (_watchdog != null) _watchdog.Enabled = wdOld; } catch { }
                _gate.Release();
                throw;
            }

            // Gate + Watchdog erst freigeben, wenn Refresh wirklich fertig ist
            _ = refreshTask.ContinueWith(_ =>
            {
                try { if (_watchdog != null) _watchdog.Enabled = wdOld; } catch { }
                try { _gate.Release(); } catch { }
            }, TaskScheduler.Default);

            var done = await Task.WhenAny(refreshTask, Task.Delay(ms, ct)).ConfigureAwait(false);
            if (done != refreshTask)
                throw new TimeoutException("refresh_timeout");

            await refreshTask.ConfigureAwait(false);
        }

        public async Task StartLiveViewAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { _host.StartLiveView(); }
            finally { _gate.Release(); }
        }

        public async Task StopLiveViewAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { _host.StopLiveView(); }
            finally { _gate.Release(); }
        }

        public Task<int> GetLiveViewFpsAsync(CancellationToken ct)
            => Task.FromResult(_host.LiveView.TargetFps);

        public Task SetLiveViewFpsAsync(int fps, CancellationToken ct)
        {
            _host.LiveView.SetTargetFps(fps);
            return Task.CompletedTask;
        }

        public Task<Shared.CameraSettingsDto> GetSettingsAsync(CancellationToken ct)
        {
            var s = _host.GetSettings();
            if (s == null) return Task.FromResult<Shared.CameraSettingsDto>(null);

            // Exposure kommt aus CameraHost als string -> nach double? für Shared DTO
            var exp = TryParseExposure(s.Exposure);

            var dto = new Shared.CameraSettingsDto
            {
                Iso = s.Iso,
                Shutter = s.Shutter,
                WhiteBalance = s.WhiteBalance,

                Aperture = s.Aperture,
                Exposure = exp,

                IsoOptions = s.IsoOptions ?? new List<string>(),
                ShutterOptions = s.ShutterOptions ?? new List<string>(),
                WhiteBalanceOptions = s.WhiteBalanceOptions ?? new List<string>(),
                ApertureOptions = s.ApertureOptions ?? new List<string>(),
                ExposureOptions = s.ExposureOptions ?? new List<string>(),
            };

            return Task.FromResult(dto);
        }

        public async Task<Shared.CaptureFileResultDto> CaptureToFileAsync(Shared.CaptureRequestDto req, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var apply = req != null && req.ApplySettings;
                var resetAfter = req?.ResetAfterShoot ?? true;
                var startLiveViewAfterCapture = GetStartLiveViewAfterCapture(req);

                string folder = _settings != null ? _settings.DefaultCaptureFolder : null;
                if (string.IsNullOrWhiteSpace(folder))
                    folder = @"C:\Photobox\captures";
                Directory.CreateDirectory(folder);

                string target;

                if (!string.IsNullOrWhiteSpace(req?.Path))
                    target = IsAbsoluteWindowsPath(req.Path) ? req.Path : Path.Combine(folder, req.Path.TrimStart('\\', '/'));
                else if (!string.IsNullOrWhiteSpace(req?.FileName))
                    target = Path.Combine(folder, Path.GetFileName(req.FileName));
                else
                    target = Path.Combine(folder, "pb_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg");

                if (req != null && !req.Overwrite && File.Exists(target))
                {
                    var dir = Path.GetDirectoryName(target);
                    var name = Path.GetFileNameWithoutExtension(target);
                    var ext = Path.GetExtension(target);
                    target = Path.Combine(dir ?? folder, name + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
                }

                var td = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(td))
                    Directory.CreateDirectory(td);

                var wdOld = _watchdog != null && _watchdog.Enabled;
                if (_watchdog != null) _watchdog.Enabled = false;

                string file;
                try
                {
                    file = await _host.CaptureWithTemporarySettingsAsync(
                        target,
                        req?.Iso,
                        req?.Shutter,
                        req?.WhiteBalance,
                        req?.Aperture,
                        req?.Exposure,
                        apply,
                        resetAfter,
                        startLiveViewAfterCapture
                    ).ConfigureAwait(false);

                    await WaitForFileStableAsync(file ?? target, ct).ConfigureAwait(false);
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (_watchdog != null) _watchdog.Enabled = wdOld;
                }

                return new Shared.CaptureFileResultDto { Ok = true, File = file };
            }
            finally { _gate.Release(); }
        }

        public async Task<byte[]> CaptureJpegAsync(Shared.CaptureRequestDto req, CancellationToken ct)
        {
            var tmpName = "pb_tmp_" + Guid.NewGuid().ToString("N") + ".jpg";

            var res = await CaptureToFileAsync(new Shared.CaptureRequestDto
            {
                Mode = "file",
                Overwrite = true,
                FileName = tmpName,

                ApplySettings = req != null && req.ApplySettings,
                ResetAfterShoot = req == null || req.ResetAfterShoot,

                Iso = req?.Iso,
                Shutter = req?.Shutter,
                WhiteBalance = req?.WhiteBalance,
                Aperture = req?.Aperture,
                Exposure = req?.Exposure,
            }, ct).ConfigureAwait(false);

            var bytes = File.ReadAllBytes(res.File);
            try { File.Delete(res.File); } catch { }
            return bytes;
        }

        public Task<Shared.WatchdogDto> GetWatchdogAsync(CancellationToken ct)
            => Task.FromResult(new Shared.WatchdogDto { Enabled = _watchdog != null && _watchdog.Enabled });

        public Task<Shared.WatchdogDto> SetWatchdogAsync(bool enabled, CancellationToken ct)
        {
            if (_watchdog != null) _watchdog.Enabled = enabled;
            return Task.FromResult(new Shared.WatchdogDto { Enabled = enabled });
        }

        public async Task<(long Seq, byte[]? Jpeg, string? LastFrameUtc)> WaitNextFrameAsync(long lastSeq, int timeoutMs, CancellationToken ct)
        {
            int ms = timeoutMs > 0 ? timeoutMs : 1500;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(ms);
                var (seq, jpg) = await _host.FrameHub.WaitNextAsync(lastSeq, cts.Token).ConfigureAwait(false);
                return (seq, jpg, ComputeLastFrameUtc());
            }
        }
    }
}
