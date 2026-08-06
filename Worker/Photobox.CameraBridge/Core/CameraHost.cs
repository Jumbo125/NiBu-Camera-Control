// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: CameraHost.cs
// Zweck: Kapselt die zentrale Kamerasteuerung des Workers.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Kameras suchen, auswählen und verwalten
// - LiveView und FrameHub koordinieren
// - Capture-Abläufe ausführen
// - Kameraeigenschaften und Verbindungslogik bündeln

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CameraControl.Devices;
using CameraControl.Devices.Classes;
using System.IO;
using System.Threading;

namespace Photobox.CameraBridge.Core
{
    public sealed class CameraHost : IDisposable
    {
        private readonly MtaWorker _mta;
        private readonly RingLogger _log;
        private readonly CameraMap _map;
        private readonly AppSettings _settings;

        // blockt Capture gegen andere Operationen
        private readonly SemaphoreSlim _captureGate = new SemaphoreSlim(1, 1);

        // serialisiert Select/Start/Stop, damit nix parallel gegeneinander läuft
        private readonly SemaphoreSlim _switchGate = new SemaphoreSlim(1, 1);

        private static readonly TimeSpan SwitchWaitCaptureTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan LiveViewStopTimeout = TimeSpan.FromSeconds(5);

        public CameraDeviceManager Manager { get; }
        public FrameHub FrameHub { get; } = new FrameHub();
        public LiveViewPump LiveView { get; }
        public bool HttpStreamingEnabled { get; set; }

        // Thread-sicherer, gecachter Snapshot der zuletzt bekannten Kamera-Infos. Wird am Ende
        // jedes erfolgreichen RefreshAsync()/SelectCamera()/SelectCameraBySerial() auf dem
        // MTA-Thread aktualisiert. GetStatusSnapshot() liest NUR diesen Cache, damit ein
        // Health-Ping (GetStatusAsync) niemals auf dem MTA-Thread blockieren kann, selbst wenn
        // der gerade in Manager.ConnectToCamera() gegen eine wedged Kamera hängt.
        private readonly object _snapshotLock = new object();
        private CameraSnapshot _snapshot = CameraSnapshot.Empty;

        public CameraSnapshot GetStatusSnapshot()
        {
            lock (_snapshotLock)
                return _snapshot;
        }

        private void UpdateSnapshotUnsafe()
        {
            var cam = Manager.SelectedCameraDevice;
            var snap = cam == null
                ? CameraSnapshot.Empty
                : new CameraSnapshot
                {
                    SelectedId = GetSelectedCameraIdUnsafe(),
                    DisplayName = cam.DisplayName,
                    Manufacturer = cam.Manufacturer,
                    Model = cam.DeviceName,
                    Serial = cam.SerialNumber
                };

            lock (_snapshotLock)
                _snapshot = snap;

            if (cam != null)
                _cameraResponsive = true;
        }

        // Kamera-Gesundheit getrennt von Prozess-/Snapshot-Liveness: wird auf false gesetzt,
        // sobald LiveViewPump meldet, dass die Kamera wiederholt nicht mehr reagiert (Stuck),
        // und wieder auf true, sobald ein Refresh/Select erneut erfolgreich eine Kamera liefert.
        // GetStatusSnapshot()-Konsumenten (Health-Ping) lesen dies lock-frei mit.
        private volatile bool _cameraResponsive = true;
        public bool CameraResponsive => _cameraResponsive;

        public CameraHost(MtaWorker mta, RingLogger log, CameraMap map, AppSettings settings)
        {
            _mta = mta;
            _log = log;
            _map = map;
            _settings = settings;

            Manager = new CameraDeviceManager
            {
                LoadWiaDevices = settings.LoadWiaDevices,
                DisableNativeDrivers = false
            };

            LiveView = new LiveViewPump(_mta, _log, _settings);
            LiveView.Stuck += () => _cameraResponsive = false;
        }

        public async Task RefreshAsync()
        {
            await _mta.InvokeAsync(() =>
            {
                EnsureDefaultDeviceClassLoaded();
                ApplyOverrides();
                ApplyNikonFallbackForConnectedModels();
                Manager.ConnectToCamera();
                UpdateSnapshotUnsafe();
            }).ConfigureAwait(false);

            _log.Info($"Refresh done. Found: {Manager.ConnectedDevices.Count} camera(s).");
        }

        private void EnsureDefaultDeviceClassLoaded()
        {
            if (CameraDeviceManager.DeviceClass != null && CameraDeviceManager.DeviceClass.Count > 0)
                return;

            var mi = typeof(CameraDeviceManager).GetMethod("PopulateDeviceClass", BindingFlags.Instance | BindingFlags.NonPublic);
            mi?.Invoke(Manager, null);
        }

        private void ApplyOverrides()
        {
            if (_map?.Overrides == null || _map.Overrides.Count == 0)
                return;

            foreach (var kv in _map.Overrides)
            {
                var model = kv.Key?.Trim();
                var typeName = kv.Value?.Trim();

                if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(typeName))
                    continue;

                var t = ResolveType(typeName);
                if (t == null)
                {
                    _log.Warn($"camera-map override ignored: type not found: {typeName}");
                    continue;
                }

                CameraDeviceManager.DeviceClass[model] = t;
                _log.Info($"camera-map override: '{model}' -> {t.FullName}");
            }
        }

        private void ApplyNikonFallbackForConnectedModels()
        {
            var defaultType = ResolveType(_map?.NikonDefaultDriver);
            if (defaultType == null)
            {
                _log.Warn($"nikon fallback ignored: type not found: {_map?.NikonDefaultDriver}");
                return;
            }

            if (CameraDeviceManager.DeviceClass == null || CameraDeviceManager.DeviceClass.Count == 0)
                return;

            var explicitOverrides = new HashSet<string>(
                (_map?.Overrides ?? new Dictionary<string, string>()).Keys,
                StringComparer.OrdinalIgnoreCase);

            var keys = CameraDeviceManager.DeviceClass.Keys.ToList();
            var applied = 0;

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (key.IndexOf("nikon", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // explizite Overrides immer respektieren
                if (explicitOverrides.Contains(key))
                    continue;

                CameraDeviceManager.DeviceClass[key] = defaultType;
                applied++;
                _log.Info($"nikon fallback applied: '{key}' -> {defaultType.FullName}");
            }

            if (applied == 0)
                _log.Info("nikon fallback: no Nikon keys found to override.");
        }

        private Type ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var t = Type.GetType(typeName, throwOnError: false);
            if (t != null)
                return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(typeName, throwOnError: false);
                if (t != null)
                    return t;
            }

            return null;
        }

        public int? GetSelectedCameraId()
        {
            return (_mta.IsOnWorkerThread
                ? GetSelectedCameraIdUnsafe()
                : _mta.InvokeAsync(GetSelectedCameraIdUnsafe).GetAwaiter().GetResult());
        }

        private int? GetSelectedCameraIdUnsafe()
        {
            var cam = Manager.SelectedCameraDevice;
            if (cam == null)
                return null;

            var list = Manager.ConnectedDevices;
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], cam))
                    return i;
            }

            var serial = cam.SerialNumber;
            if (!string.IsNullOrEmpty(serial))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i]?.SerialNumber, serial, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return null;
        }

        public List<CameraInfoDto> GetCameraList()
        {
            return (_mta.IsOnWorkerThread
                ? BuildCameraListUnsafe()
                : _mta.InvokeAsync(BuildCameraListUnsafe).GetAwaiter().GetResult());
        }

        private List<CameraInfoDto> BuildCameraListUnsafe()
        {
            return Manager.ConnectedDevices
                .Select((c, idx) => new CameraInfoDto
                {
                    Id = idx,
                    DisplayName = c.DisplayName,
                    Manufacturer = c.Manufacturer,
                    Model = c.DeviceName,
                    Serial = c.SerialNumber,
                    Port = c.PortName,
                    IsConnected = c.IsConnected
                })
                .ToList();
        }

        public bool SelectCamera(int id)
        {
            _switchGate.Wait();
            try
            {
                var idOk = _mta.InvokeAsync(() => id >= 0 && id < Manager.ConnectedDevices.Count)
                    .GetAwaiter().GetResult();
                if (!idOk)
                    return false;

                var okStop = StopAllBeforeSwitchAsync().GetAwaiter().GetResult();
                if (!okStop)
                    return false;

                return _mta.InvokeAsync(() =>
                {
                    if (id < 0 || id >= Manager.ConnectedDevices.Count)
                        return false;

                    Manager.SelectedCameraDevice = Manager.ConnectedDevices[id];

                    var cam = Manager.SelectedCameraDevice;
                    _log.Info($"Selected camera: #{id} -> {cam?.DisplayName}");
                    _log.Info($"Selected SDK type: {cam?.GetType().FullName}");
                    _log.Info($"Selected maker/model: {cam?.Manufacturer} / {cam?.DeviceName}");

                    UpdateSnapshotUnsafe();
                    return true;
                }).GetAwaiter().GetResult();
            }
            finally
            {
                _switchGate.Release();
            }
        }

        public bool SelectCameraBySerial(string serial)
        {
            _switchGate.Wait();
            try
            {
                if (string.IsNullOrWhiteSpace(serial))
                    return false;

                serial = serial.Trim();

                var idx = _mta.InvokeAsync(() =>
                {
                    for (int i = 0; i < Manager.ConnectedDevices.Count; i++)
                    {
                        var c = Manager.ConnectedDevices[i];
                        var sn = c?.SerialNumber;
                        if (!string.IsNullOrWhiteSpace(sn) &&
                            string.Equals(sn.Trim(), serial, StringComparison.OrdinalIgnoreCase))
                            return i;
                    }
                    return -1;
                }).GetAwaiter().GetResult();

                if (idx < 0)
                    return false;

                var okStop = StopAllBeforeSwitchAsync().GetAwaiter().GetResult();
                if (!okStop)
                    return false;

                return _mta.InvokeAsync(() =>
                {
                    var idx2 = -1;
                    for (int i = 0; i < Manager.ConnectedDevices.Count; i++)
                    {
                        var c = Manager.ConnectedDevices[i];
                        var sn = c?.SerialNumber;
                        if (!string.IsNullOrWhiteSpace(sn) &&
                            string.Equals(sn.Trim(), serial, StringComparison.OrdinalIgnoreCase))
                        {
                            idx2 = i;
                            break;
                        }
                    }

                    if (idx2 < 0)
                        return false;

                    Manager.SelectedCameraDevice = Manager.ConnectedDevices[idx2];

                    var cam = Manager.SelectedCameraDevice;
                    _log.Info($"Selected camera by serial: '{serial}' -> {cam?.DisplayName}");
                    _log.Info($"Selected SDK type: {cam?.GetType().FullName}");
                    _log.Info($"Selected maker/model: {cam?.Manufacturer} / {cam?.DeviceName}");

                    UpdateSnapshotUnsafe();
                    return true;
                }).GetAwaiter().GetResult();
            }
            finally
            {
                _switchGate.Release();
            }
        }

        private async Task<bool> StopAllBeforeSwitchAsync()
        {
            if (!await _captureGate.WaitAsync(SwitchWaitCaptureTimeout).ConfigureAwait(false))
            {
                _log.Warn("Camera switch rejected: capture still running (timeout).");
                return false;
            }

            try
            {
                await StopLiveViewHardAsync().ConfigureAwait(false);
                return true;
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private async Task StopLiveViewHardAsync()
        {
            if (LiveView.IsRunning)
            {
                try
                {
                    await LiveView.StopAsync(LiveViewStopTimeout).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            try
            {
                await _mta.InvokeAsync(() =>
                {
                    var cam = Manager.SelectedCameraDevice;
                    if (cam == null)
                        return;

                    try { cam.StopLiveView(); } catch { }
                    try { cam.PreventShutDown = false; } catch { }
                }).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await Task.Delay(150).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        public CameraSettingsDto GetSettings()
        {
            return (_mta.IsOnWorkerThread
                ? GetSettingsUnsafe()
                : _mta.InvokeAsync(GetSettingsUnsafe).GetAwaiter().GetResult());
        }

        private CameraSettingsDto GetSettingsUnsafe()
        {
            var cam = Manager.SelectedCameraDevice;
            if (cam == null)
                return null;

            return new CameraSettingsDto
            {
                Iso = cam.IsoNumber?.Value,
                Shutter = cam.ShutterSpeed?.Value,
                WhiteBalance = cam.WhiteBalance?.Value,

                Aperture = cam.FNumber?.Value,
                Exposure = cam.ExposureCompensation?.Value,

                IsoOptions = cam.IsoNumber?.Values?.ToList() ?? new List<string>(),
                ShutterOptions = cam.ShutterSpeed?.Values?.ToList() ?? new List<string>(),
                WhiteBalanceOptions = cam.WhiteBalance?.Values?.ToList() ?? new List<string>(),

                ApertureOptions = cam.FNumber?.Values?.ToList() ?? new List<string>(),
                ExposureOptions = cam.ExposureCompensation?.Values?.ToList() ?? new List<string>()
            };
        }

        private enum CameraProfile
        {
            NikonDslr,
            WebcamLike,
            GenericDslr
        }

        private static CameraProfile GetCameraProfile(object camObj, string manufacturer, string model)
        {
            var maker = manufacturer ?? "";
            var mdl = model ?? "";
            var type = camObj?.GetType().FullName ?? "";

            if (maker.IndexOf("nikon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mdl.IndexOf("nikon", StringComparison.OrdinalIgnoreCase) >= 0)
                return CameraProfile.NikonDslr;

            if (type.IndexOf("wia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                type.IndexOf("webcam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                type.IndexOf("uvc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                maker.IndexOf("uvc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                maker.IndexOf("integrated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mdl.IndexOf("webcam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mdl.IndexOf("uvc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mdl.IndexOf("integrated camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mdl.IndexOf("integrated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mdl.IndexOf("virtual camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                maker.IndexOf("obs", StringComparison.OrdinalIgnoreCase) >= 0)
                return CameraProfile.WebcamLike;

            return CameraProfile.GenericDslr;
        }

        private static bool IsNikonCamera(string manufacturer, string model)
        {
            return GetCameraProfile(null, manufacturer, model) == CameraProfile.NikonDslr;
        }

        private static bool IsDeviceBusy(Exception ex)
        {
            if (ex == null)
                return false;

            var msg = ex.Message ?? "";
            return msg.IndexOf("device busy", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task RetryBusyAsync(Func<Task> action, string op, int maxAttempts = 8, int firstDelayMs = 120)
        {
            var delay = firstDelayMs;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await action().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (IsDeviceBusy(ex) && attempt < maxAttempts)
                {
                    _log.Warn($"{op}: Device Busy (attempt {attempt}/{maxAttempts}) -> retry in {delay}ms");
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 1000);
                }
            }
        }

        public async Task<bool> SetSettingsAsync(string iso, string shutter, string wb, string aperture, double? exposure)
        {
            if (string.IsNullOrWhiteSpace(iso) &&
                string.IsNullOrWhiteSpace(shutter) &&
                string.IsNullOrWhiteSpace(wb) &&
                string.IsNullOrWhiteSpace(aperture) &&
                exposure is null)
                return true;

            await _captureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cam = await _mta.InvokeAsync(() => Manager.SelectedCameraDevice).ConfigureAwait(false);
                if (cam == null)
                    return false;

                var wasLiveViewRunning = LiveView.IsRunning;
                if (wasLiveViewRunning)
                {
                    _log.Info("SetSettings while LiveView running -> stopping LiveView temporarily.");
                    try { await LiveView.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                    try { await _mta.InvokeAsync(() => { try { cam.StopLiveView(); } catch { } }).ConfigureAwait(false); } catch { }
                    await Task.Delay(150).ConfigureAwait(false);
                }

                try
                {
                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(iso) && cam.IsoNumber != null) cam.IsoNumber.Value = iso;
                        if (!string.IsNullOrWhiteSpace(shutter) && cam.ShutterSpeed != null) cam.ShutterSpeed.Value = shutter;
                        if (!string.IsNullOrWhiteSpace(wb) && cam.WhiteBalance != null) cam.WhiteBalance.Value = wb;
                        if (!string.IsNullOrWhiteSpace(aperture) && cam.FNumber != null) cam.FNumber.Value = aperture;

                        if (exposure.HasValue && cam.ExposureCompensation != null)
                            cam.ExposureCompensation.Value = exposure.Value.ToString(CultureInfo.InvariantCulture);
                    }), "ApplySettings").ConfigureAwait(false);

                    await Task.Delay(120).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    if (wasLiveViewRunning)
                    {
                        try
                        {
                            try { await _mta.InvokeAsync(() => { try { cam.PreventShutDown = true; } catch { } }).ConfigureAwait(false); } catch { }
                            LiveView.Start(cam, FrameHub);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Restarting LiveView after SetSettings failed.", ex);
                        }
                    }
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }

        public async Task<string> CaptureAsync(string targetFile, bool startLiveViewAfterCapture = false)
        {
            if (string.IsNullOrWhiteSpace(targetFile))
                throw new ArgumentException("targetFile is empty", nameof(targetFile));

            var dir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await _captureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cam = await _mta.InvokeAsync(() => Manager.SelectedCameraDevice).ConfigureAwait(false);
                if (cam == null)
                    throw new InvalidOperationException("No camera selected.");

                _log.Info($"CaptureAsync SDK type: {cam.GetType().FullName}");
                _log.Info($"CaptureAsync maker/model: {cam.Manufacturer} / {cam.DeviceName}");

                bool wasLiveViewRunning = LiveView.IsRunning;

                if (wasLiveViewRunning)
                {
                    _log.Info("Capture while LiveView running -> stopping LiveView temporarily.");
                    try { await LiveView.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                    try
                    {
                        await _mta.InvokeAsync(() =>
                        {
                            try { cam.StopLiveView(); } catch { }
                            try { cam.PreventShutDown = false; } catch { }
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }

                object capturedHandle = null;
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

                PhotoCapturedEventHandler handler = (sender, e) =>
                {
                    try
                    {
                        var t = e.GetType();

                        object handle =
                              t.GetProperty("Handle")?.GetValue(e)
                           ?? t.GetProperty("ObjectHandle")?.GetValue(e)
                           ?? t.GetProperty("Item")?.GetValue(e)
                           ?? t.GetProperty("PhotoHandle")?.GetValue(e);

                        if (handle == null)
                        {
                            _log.Warn("PhotoCaptured received but no handle property found. ArgsType=" +
                                      t.FullName + " Props=" +
                                      string.Join(",", t.GetProperties().Select(p => p.Name)));
                            return;
                        }

                        _log.Info("PhotoCaptured event received. handle=" + handle);
                        tcs.TrySetResult(handle);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("PhotoCaptured handler failed", ex);
                    }
                };

                await _mta.InvokeAsync(() => cam.PhotoCaptured += handler).ConfigureAwait(false);

                try
                {
                    var isNikon = IsNikonCamera(cam?.Manufacturer, cam?.DeviceName);
                    var saveToChanged = false;

                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        if (!cam.CaptureInSdRam)
                        {
                            cam.CaptureInSdRam = true;
                            saveToChanged = true;
                        }
                    }), "Set CaptureInSdRam/SaveTo", maxAttempts: isNikon ? 6 : 8, firstDelayMs: isNikon ? 300 : 120)
                    .ConfigureAwait(false);

                    if (saveToChanged)
                    {
                        var settleMs = isNikon ? 1500 : 150;
                        _log.Info($"Pre-capture settle after SaveTo change: {settleMs}ms");
                        await Task.Delay(settleMs).ConfigureAwait(false);
                    }
                    else
                    {
                        var settleMs = isNikon ? 500 : 120;
                        _log.Info($"Pre-capture settle before CapturePhoto: {settleMs}ms");
                        await Task.Delay(settleMs).ConfigureAwait(false);
                    }

                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        _log.Info("CapturePhoto()");
                        cam.CapturePhoto();
                    }), "CapturePhoto", maxAttempts: isNikon ? 6 : 8, firstDelayMs: isNikon ? 1200 : 120)
                    .ConfigureAwait(false);

                    capturedHandle = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        _log.Info("TransferFile() -> " + targetFile);
                        cam.TransferFile(capturedHandle, targetFile);
                        cam.ReleaseResurce(capturedHandle);
                    }), "TransferFile").ConfigureAwait(false);

                    try
                    {
                        if (!File.Exists(targetFile) || new FileInfo(targetFile).Length == 0)
                            _log.Warn("TransferFile finished but file missing/empty: " + targetFile);
                    }
                    catch
                    {
                    }

                    _log.Info("Capture complete: " + targetFile);
                    return targetFile;
                }
                finally
                {
                    await _mta.InvokeAsync(() => cam.PhotoCaptured -= handler).ConfigureAwait(false);

                    if (wasLiveViewRunning || startLiveViewAfterCapture)
                    {
                        try
                        {
                            try { await _mta.InvokeAsync(() => { try { cam.PreventShutDown = true; } catch { } }).ConfigureAwait(false); } catch { }
                            LiveView.Start(cam, FrameHub);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Restarting LiveView after capture failed.", ex);
                        }
                    }
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }


        public async Task<string> CaptureWithTemporarySettingsAsync(
            string targetFile,
            string iso,
            string shutter,
            string wb,
            string aperture,
            double? exposure,
            bool applySettings,
            bool restoreAfterShoot,
            bool startLiveViewAfterCapture = false)
        {
            if (string.IsNullOrWhiteSpace(targetFile))
                throw new ArgumentException("targetFile is empty", nameof(targetFile));

            var dir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await _captureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cam = await _mta.InvokeAsync(() => Manager.SelectedCameraDevice).ConfigureAwait(false);
                if (cam == null)
                    throw new InvalidOperationException("No camera selected.");

                _log.Info($"CaptureWithTemporarySettingsAsync SDK type: {cam.GetType().FullName}");
                _log.Info($"CaptureWithTemporarySettingsAsync maker/model: {cam.Manufacturer} / {cam.DeviceName}");
                _log.Info($"CaptureWithTemporarySettingsAsync apply={applySettings}, restoreAfter={restoreAfterShoot}, startLiveViewAfterCapture={startLiveViewAfterCapture}");

                var wasLiveViewRunning = LiveView.IsRunning;
                var profile = GetCameraProfile(cam, cam.Manufacturer, cam.DeviceName);
                var isNikon = profile == CameraProfile.NikonDslr;
                var stopLiveViewBeforeCapture =
                    profile == CameraProfile.NikonDslr ||
                    profile == CameraProfile.GenericDslr;
                var restartLiveViewAfterCapture =
                    profile == CameraProfile.NikonDslr ||
                    profile == CameraProfile.GenericDslr;

                _log.Info($"CaptureWithTemporarySettingsAsync profile={profile}, wasLiveViewRunning={wasLiveViewRunning}, stopBefore={stopLiveViewBeforeCapture}, restartAfter={restartLiveViewAfterCapture}");

                CameraSettingsDto old = null;
                double? oldExposure = null;

                if (applySettings)
                {
                    old = new CameraSettingsDto
                    {
                        Iso = cam.IsoNumber?.Value,
                        Shutter = cam.ShutterSpeed?.Value,
                        WhiteBalance = cam.WhiteBalance?.Value,
                        Aperture = cam.FNumber?.Value,
                        Exposure = cam.ExposureCompensation?.Value
                    };
                    oldExposure = TryParseExposureValue(old.Exposure);
                }

                if (wasLiveViewRunning && stopLiveViewBeforeCapture)
                {
                    _log.Info("CaptureWithTemporarySettingsAsync: stopping LiveView before capture.");
                    try { await LiveView.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                    try
                    {
                        await _mta.InvokeAsync(() =>
                        {
                            try { cam.PreventShutDown = false; } catch { }
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    var postStopMs = isNikon ? 700 : 250;
                    _log.Info($"CaptureWithTemporarySettingsAsync: Post-LiveView-stop settle: {postStopMs}ms");
                    await Task.Delay(postStopMs).ConfigureAwait(false);
                }

                object capturedHandle = null;
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

                PhotoCapturedEventHandler handler = (sender, e) =>
                {
                    try
                    {
                        var t = e.GetType();

                        object handle =
                              t.GetProperty("Handle")?.GetValue(e)
                           ?? t.GetProperty("ObjectHandle")?.GetValue(e)
                           ?? t.GetProperty("Item")?.GetValue(e)
                           ?? t.GetProperty("PhotoHandle")?.GetValue(e);

                        if (handle == null)
                        {
                            _log.Warn("PhotoCaptured received but no handle property found. ArgsType=" +
                                      t.FullName + " Props=" +
                                      string.Join(",", t.GetProperties().Select(p => p.Name)));
                            return;
                        }

                        _log.Info("PhotoCaptured event received. handle=" + handle);
                        tcs.TrySetResult(handle);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("PhotoCaptured handler failed", ex);
                    }
                };

                await _mta.InvokeAsync(() => cam.PhotoCaptured += handler).ConfigureAwait(false);

                try
                {
                    if (applySettings)
                    {
                        await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                        {
                            if (!string.IsNullOrWhiteSpace(iso) && cam.IsoNumber != null) cam.IsoNumber.Value = iso;
                            if (!string.IsNullOrWhiteSpace(shutter) && cam.ShutterSpeed != null) cam.ShutterSpeed.Value = shutter;
                            if (!string.IsNullOrWhiteSpace(wb) && cam.WhiteBalance != null) cam.WhiteBalance.Value = wb;
                            if (!string.IsNullOrWhiteSpace(aperture) && cam.FNumber != null) cam.FNumber.Value = aperture;

                            if (exposure.HasValue && cam.ExposureCompensation != null)
                                cam.ExposureCompensation.Value = exposure.Value.ToString(CultureInfo.InvariantCulture);
                        }), "ApplySettings").ConfigureAwait(false);

                        await Task.Delay(isNikon ? 200 : 120).ConfigureAwait(false);
                    }

                    var saveToChanged = false;

                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        if (!cam.CaptureInSdRam)
                        {
                            cam.CaptureInSdRam = true;
                            saveToChanged = true;
                        }
                    }), "Set CaptureInSdRam/SaveTo", maxAttempts: isNikon ? 6 : 8, firstDelayMs: isNikon ? 300 : 120)
                    .ConfigureAwait(false);

                    var settleMs =
                        saveToChanged
                            ? (isNikon ? 500 : 150)
                            : (isNikon ? 250 : 120);

                    _log.Info($"Pre-capture settle before CapturePhoto: {settleMs}ms (saveToChanged={saveToChanged})");
                    await Task.Delay(settleMs).ConfigureAwait(false);

                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        _log.Info("CapturePhoto()");
                        cam.CapturePhoto();
                    }), "CapturePhoto", maxAttempts: isNikon ? 6 : 8, firstDelayMs: isNikon ? 1200 : 120)
                    .ConfigureAwait(false);

                    capturedHandle = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                    await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                    {
                        _log.Info("TransferFile() -> " + targetFile);
                        cam.TransferFile(capturedHandle, targetFile);
                        cam.ReleaseResurce(capturedHandle);
                    }), "TransferFile").ConfigureAwait(false);

                    try
                    {
                        if (!File.Exists(targetFile) || new FileInfo(targetFile).Length == 0)
                            _log.Warn("TransferFile finished but file missing/empty: " + targetFile);
                    }
                    catch
                    {
                    }

                    _log.Info("Capture complete: " + targetFile);
                    return targetFile;
                }
                finally
                {
                    try
                    {
                        if (applySettings && restoreAfterShoot && old != null)
                        {
                            await RetryBusyAsync(() => _mta.InvokeAsync(() =>
                            {
                                if (cam.IsoNumber != null) cam.IsoNumber.Value = old.Iso;
                                if (cam.ShutterSpeed != null) cam.ShutterSpeed.Value = old.Shutter;
                                if (cam.WhiteBalance != null) cam.WhiteBalance.Value = old.WhiteBalance;
                                if (cam.FNumber != null) cam.FNumber.Value = old.Aperture;
                                if (oldExposure.HasValue && cam.ExposureCompensation != null)
                                    cam.ExposureCompensation.Value = oldExposure.Value.ToString(CultureInfo.InvariantCulture);
                            }), "RestoreSettings").ConfigureAwait(false);

                            await Task.Delay(isNikon ? 150 : 100).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("RestoreSettings failed after capture.", ex);
                    }

                    await _mta.InvokeAsync(() => cam.PhotoCaptured -= handler).ConfigureAwait(false);

                    var shouldRestartLiveView =
                        restartLiveViewAfterCapture &&
                        (wasLiveViewRunning || startLiveViewAfterCapture);

                    if (shouldRestartLiveView)
                    {
                        try
                        {
                            try { await _mta.InvokeAsync(() => { try { cam.PreventShutDown = true; } catch { } }).ConfigureAwait(false); } catch { }
                            LiveView.Start(cam, FrameHub);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Restarting LiveView after capture failed.", ex);
                        }
                    }
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private static double? TryParseExposureValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var t = value.Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : (double?)null;
        }

        public void StartLiveView()
        {
            _switchGate.Wait();
            try
            {
                var cam = _mta.InvokeAsync(() =>
                {
                    var c = Manager.SelectedCameraDevice;
                    if (c == null)
                        throw new InvalidOperationException("No camera selected.");
                    try { c.PreventShutDown = true; } catch { }
                    return c;
                }).GetAwaiter().GetResult();

                var profile = GetCameraProfile(cam, cam.Manufacturer, cam.DeviceName);
                _log.Info($"StartLiveView profile={profile}, alreadyRunning={LiveView.IsRunning}, maker={cam.Manufacturer}, model={cam.DeviceName}, sdkType={cam.GetType().FullName}");

                if (profile == CameraProfile.NikonDslr)
                {
                    // Redundante externe Aufrufe (LiveView läuft schon) NICHT mehr hart
                    // durchziehen: ein bereits laufender Nikon-LiveView wurde hier bisher bei
                    // jedem erneuten Aufruf (z.B. periodischer Keepalive-Ping) komplett gestoppt
                    // und neu gestartet. Nach mehreren solchen Zyklen ohne jeden Auslöser durch
                    // den Nutzer konnte das zuverlässig in "MTP device busy" enden. Der
                    // absichtliche Stop/Restart rund um einen Capture (CaptureWithTemporarySettingsAsync)
                    // läuft über einen anderen Pfad und ist davon nicht betroffen.
                    if (LiveView.IsRunning)
                    {
                        _log.Info("StartLiveView skipped (Nikon, already running) - avoiding redundant hard restart.");
                        return;
                    }

                    StopLiveViewHardAsync().GetAwaiter().GetResult();
                    LiveView.Start(cam, FrameHub);
                    return;
                }

                if (LiveView.IsRunning)
                {
                    _log.Info("StartLiveView skipped because already running.");
                    return;
                }

                LiveView.Start(cam, FrameHub);
            }
            finally
            {
                _switchGate.Release();
            }
        }

        public void StopLiveView()
        {
            _switchGate.Wait();
            try
            {
                StopLiveViewHardAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _switchGate.Release();
            }
        }

        /// <summary>
        /// Schließt die SDK-Verbindung zur aktuell ausgewählten Kamera hart (cam.Close()), statt nur
        /// LiveView zu stoppen. Wird für einen "harten" Reconnect benötigt: Wenn die Kamera in einen
        /// dauerhaften "MTP device busy"-Zustand gerät, hilft ein simples Stop/Start auf demselben
        /// SDK-Handle nicht mehr - der Transport muss neu aufgebaut werden. Danach sollte RefreshAsync()
        /// aufgerufen werden, damit CameraDeviceManager das Gerät neu enumeriert.
        /// Best-effort: Fehler werden geloggt, aber nicht weitergeworfen.
        /// </summary>
        public void TryCloseSelectedCameraConnection()
        {
            try
            {
                _mta.InvokeAsync(() =>
                {
                    var cam = Manager.SelectedCameraDevice;
                    if (cam == null)
                        return;

                    try { cam.StopLiveView(); } catch { }
                    try { cam.PreventShutDown = false; } catch { }
                    try { cam.Close(); }
                    catch (Exception ex) { _log.Warn("TryCloseSelectedCameraConnection: cam.Close() failed: " + ex.Message); }
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.Warn("TryCloseSelectedCameraConnection failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Harte Kamera-Recovery für einen dauerhaften "MTP device busy"-Zustand, aus dem
        /// Stop/Start auf demselben SDK-Handle nicht mehr herausführt: LiveView stoppen,
        /// SDK-Verbindung schließen, Geräte neu enumerieren und - falls vorhanden - wieder
        /// Kamera 0 auswählen (analog zum UsbReconnectWatchdog bei LiveView.Stuck).
        /// Der RefreshAsync ist per Timeout abgesichert, damit der Aufrufer bei weiterhin
        /// wedged Kamera nicht unbegrenzt wartet. Best-effort; true, wenn danach wieder eine
        /// Kamera ausgewählt ist. Achtung: NICHT aufrufen, während _captureGate gehalten wird
        /// (SelectCamera nimmt es selbst) - der Capture-Pfad ruft dies zwischen zwei Versuchen
        /// auf, wenn das Gate wieder frei ist.
        /// </summary>
        public async Task<bool> TryHardRecoverAsync()
        {
            try { LiveView.Stop(); } catch { }
            try { TryCloseSelectedCameraConnection(); } catch { }

            try
            {
                var refreshTask = RefreshAsync();
                var winner = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
                if (winner != refreshTask)
                {
                    _log.Warn("TryHardRecoverAsync: RefreshAsync timed out after 8s - camera still unresponsive.");
                    return false;
                }
                await refreshTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("TryHardRecoverAsync: RefreshAsync failed: " + ex.Message);
                return false;
            }

            try
            {
                var list = GetCameraList();
                if (list != null && list.Count > 0)
                {
                    SelectCamera(0);
                    _log.Info("TryHardRecoverAsync: camera reselected after hard reconnect.");
                    return true;
                }

                _log.Warn("TryHardRecoverAsync: no camera found after refresh.");
            }
            catch (Exception ex)
            {
                _log.Warn("TryHardRecoverAsync: SelectCamera(0) failed: " + ex.Message);
            }

            return false;
        }

        public void Dispose()
        {
            try { StopLiveView(); } catch { }

            try
            {
                _mta.InvokeAsync(() =>
                {
                    foreach (var cam in Manager.ConnectedDevices.ToList())
                    {
                        try { cam.Close(); } catch { }
                    }
                }).GetAwaiter().GetResult();
            }
            catch
            {
            }

            try { _captureGate.Dispose(); } catch { }
            try { _switchGate.Dispose(); } catch { }
        }
    }

    public sealed class CameraInfoDto
    {
        public int Id { get; set; }
        public string DisplayName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string Serial { get; set; }
        public string Port { get; set; }
        public bool IsConnected { get; set; }
    }

    /// <summary>
    /// Gecachter, thread-sicher lesbarer Snapshot der zuletzt bekannten Kamera-Auswahl.
    /// Wird ausschließlich auf dem MTA-Thread geschrieben (siehe CameraHost.UpdateSnapshotUnsafe)
    /// und lock-frei bzw. per einfachem lock von jedem Thread gelesen - insbesondere vom
    /// Health-Ping, der niemals auf den MTA-Thread blockieren darf.
    /// </summary>
    public sealed class CameraSnapshot
    {
        public static readonly CameraSnapshot Empty = new CameraSnapshot();

        public int? SelectedId { get; set; }
        public string DisplayName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string Serial { get; set; }
    }

    public sealed class CameraSettingsDto
    {
        public string Iso { get; set; }
        public string Shutter { get; set; }
        public string WhiteBalance { get; set; }

        public string Aperture { get; set; }
        public string Exposure { get; set; } // raw string aus SDK (z.B. "0", "-0.3", "1.0")

        public List<string> IsoOptions { get; set; }
        public List<string> ShutterOptions { get; set; }
        public List<string> WhiteBalanceOptions { get; set; }

        public List<string> ApertureOptions { get; set; }
        public List<string> ExposureOptions { get; set; }
    }
}
