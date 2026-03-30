// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: MainForm.cs
// Zweck: Stellt die grafische Bedienoberfläche des Workers bereit.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Kameraauswahl und Refresh ermöglichen
// - LiveView, FPS, Settings und Capture über UI bedienen
// - Logs und Statusinformationen anzeigen
// - Vorschau und Interaktion für den lokalen Betrieb bereitstellen

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Photobox.CameraBridge.Core;
using System.Collections.Generic;
using CameraInfo = Photobox.CameraBridge.Core.CameraInfoDto;
using System.Globalization;

namespace Photobox.CameraBridge.UI
{
    public sealed class MainForm : Form
    {
        private readonly AppSettings _settings;
        private readonly CameraHost _host;
        private readonly RingLogger _log;
        private readonly StartupOptions _startup;

        // optional: Watchdog beim Refresh kurz pausieren
        private readonly Action<bool> _setWatchdogEnabled;

        private readonly ListBox _lstCameras = new ListBox();
        private readonly Button _btnRefresh = new Button();
        private readonly Button _btnSelect = new Button();

        private readonly Button _btnLiveStart = new Button();
        private readonly Button _btnLiveStop = new Button();

        // FPS controls
        private readonly NumericUpDown _nudFps = new NumericUpDown();
        private readonly Button _btnFpsApply = new Button();
        private readonly Button _btnFpsGet = new Button();

        private readonly Button _btnGetSettings = new Button();
        private readonly Button _btnSetSettings = new Button();
        private readonly ComboBox _cbIso = new ComboBox();
        private readonly ComboBox _cbShutter = new ComboBox();
        private readonly ComboBox _cbWb = new ComboBox();

        private readonly ComboBox _cbAperture = new ComboBox();
        private readonly ComboBox _cbExposure = new ComboBox();

        private readonly Button _btnCapture = new Button();

        private readonly TextBox _txtLog = new TextBox();
        private readonly Label _lblStatus = new Label();

        private PictureBox _preview;

        private Icon _appIcon;

        /// <summary>
        /// When true, clicking the window close button (X) will hide the window
        /// instead of terminating the process. The tray menu controls actual exit.
        /// </summary>
        public bool HideOnClose { get; set; } = true;

        private bool _forceClose;

        private bool _closing;
        private Action<string> _logHandler;

        // Refresh-Serialisierung (kein paralleler Refresh, kein "busy für immer")
        private readonly object _refreshLock = new object();
        private Task _refreshTask;

        public MainForm(
            AppSettings settings,
            CameraHost host,
            RingLogger log,
            StartupOptions startup = null,
            Action<bool> setWatchdogEnabled = null)
        {
            _settings = settings;
            _host = host;
            _log = log;
            _startup = startup ?? new StartupOptions();
            _setWatchdogEnabled = setWatchdogEnabled;

            Text = "Photobox CameraBridge (Worker)";
            Width = 1100;
            Height = 700;

            try
            {
                _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (_appIcon != null)
                    Icon = _appIcon;
            }
            catch { }

            BuildLayout();

            Shown += (_, __) =>
            {
                AppendLog("UI opened.");
                try { SyncFromWorkerState(); } catch { }
            };

            FormClosing += (_, e) =>
            {
                // "X" hides the UI (default), app keeps running (tray)
                if (HideOnClose && !_forceClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    try { Hide(); } catch { }
                    return;
                }

                // real close: detach handlers & dispose UI resources
                _closing = true;

                try
                {
                    if (_logHandler != null)
                        _log.LineAppended -= _logHandler;
                }
                catch { }

                try { _appIcon?.Dispose(); _appIcon = null; } catch { }

                try
                {
                    var img = _preview?.Image;
                    if (_preview != null) _preview.Image = null;
                    img?.Dispose();
                }
                catch { }

                // IMPORTANT:
                // No _host.StopLiveView() / _host.Dispose() here.
                // Cleanup happens in Program.cs (ShutdownOnce).
            };


            _logHandler = line =>
            {
                if (_closing) return;
                if (!IsHandleCreated) return;

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (_closing) return;
                        AppendLog(line);
                    }));
                }
                catch { }
            };
            _log.LineAppended += _logHandler;
        }

        /// <summary>
        /// Called by the tray context to exit the process (bypasses HideOnClose).
        /// </summary>
        public void RequestCloseForExit()
        {
            _forceClose = true;
            try
            {
                if (InvokeRequired) BeginInvoke((Action)(() => Close()));
                else Close();
            }
            catch { }
        }

        /// <summary>
        /// Sync UI controls with current worker state.
        /// This does NOT force a refresh; it just reads the current snapshot.
        /// </summary>
        public void SyncFromWorkerState()
        {
            if (_closing) return;

            EnsureFolders();

            try
            {
                var fps = _host.LiveView.TargetFps;
                if (fps < (int)_nudFps.Minimum) fps = (int)_nudFps.Minimum;
                if (fps > (int)_nudFps.Maximum) fps = (int)_nudFps.Maximum;
                _nudFps.Value = fps;
            }
            catch { }

            PopulateCameraListFromHost();

            try
            {
                var sel = _host.GetSelectedCameraId();
                if (sel.HasValue && sel.Value >= 0 && sel.Value < _lstCameras.Items.Count)
                    _lstCameras.SelectedIndex = sel.Value;

                LoadSettingsToUi();
            }
            catch { }
        }

        private void BuildLayout()
        {
            var left = new Panel { Dock = DockStyle.Left, Width = 420, Padding = new Padding(10) };
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            Controls.Add(right);
            Controls.Add(left);

            _lstCameras.Dock = DockStyle.Top;
            _lstCameras.Height = 220;

            _btnRefresh.Text = "Refresh Cameras";
            _btnRefresh.Dock = DockStyle.Top;
            _btnRefresh.Height = 36;
            _btnRefresh.Click += async (_, __) => await RefreshCameras(autoSelectMaybe: false);

            _btnSelect.Text = "Select Camera";
            _btnSelect.Dock = DockStyle.Top;
            _btnSelect.Height = 36;
            _btnSelect.Click += (_, __) => SelectCameraFromList();

            _btnLiveStart.Text = "Start LiveView Pump";
            _btnLiveStart.Dock = DockStyle.Top;
            _btnLiveStart.Height = 36;
            _btnLiveStart.Click += (_, __) =>
            {
                try { _host.StartLiveView(); SetStatus("LiveView started"); }
                catch (Exception ex) { SetStatus("LiveView start failed: " + ex.Message); }
            };

            _btnLiveStop.Text = "Stop LiveView Pump";
            _btnLiveStop.Dock = DockStyle.Top;
            _btnLiveStop.Height = 36;
            _btnLiveStop.Click += (_, __) =>
            {
                try { _host.StopLiveView(); SetStatus("LiveView stopped"); }
                catch (Exception ex) { SetStatus("LiveView stop failed: " + ex.Message); }
            };

            var groupFps = new GroupBox { Text = "LiveView FPS", Dock = DockStyle.Top, Height = 95, Padding = new Padding(10) };
            var fpsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

            _nudFps.Minimum = 1;
            _nudFps.Maximum = 60;
            _nudFps.Width = 80;

            _btnFpsGet.Text = "Get";
            _btnFpsGet.Width = 70;
            _btnFpsGet.Click += (_, __) =>
            {
                try
                {
                    var fps = _host.LiveView.TargetFps;
                    if (fps < (int)_nudFps.Minimum) fps = (int)_nudFps.Minimum;
                    if (fps > (int)_nudFps.Maximum) fps = (int)_nudFps.Maximum;
                    _nudFps.Value = fps;
                    SetStatus("Current LiveView FPS: " + fps);
                }
                catch (Exception ex) { SetStatus("Get FPS failed: " + ex.Message); }
            };

            _btnFpsApply.Text = "Apply";
            _btnFpsApply.Width = 70;
            _btnFpsApply.Click += (_, __) =>
            {
                try
                {
                    var fps = (int)_nudFps.Value;
                    _host.LiveView.SetTargetFps(fps);
                    SetStatus("LiveView FPS set to: " + fps);
                }
                catch (Exception ex) { SetStatus("Set FPS failed: " + ex.Message); }
            };

            fpsRow.Controls.Add(new Label { Text = "FPS:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            fpsRow.Controls.Add(_nudFps);
            fpsRow.Controls.Add(_btnFpsApply);
            fpsRow.Controls.Add(_btnFpsGet);
            groupFps.Controls.Add(fpsRow);

            // --- Settings Group (FIXED Layout) ---
            var groupSettings = new GroupBox
            {
                Text = "Settings (ISO / Shutter / WB / Aperture / Exposure)",
                Dock = DockStyle.Top,
                Height = 260,
                Padding = new Padding(10)
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6, // ISO, Shutter, WB, Aperture, Exposure, Buttons
                AutoSize = false
            };

            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            for (int r = 0; r < grid.RowCount; r++)
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Make combos fill their cells (prevents "invisible" tiny controls)
            _cbIso.Dock = DockStyle.Fill;
            _cbShutter.Dock = DockStyle.Fill;
            _cbWb.Dock = DockStyle.Fill;
            _cbAperture.Dock = DockStyle.Fill;
            _cbExposure.Dock = DockStyle.Fill;

            grid.Controls.Add(new Label { Text = "ISO", AutoSize = true }, 0, 0);
            grid.Controls.Add(_cbIso, 1, 0);

            grid.Controls.Add(new Label { Text = "Shutter", AutoSize = true }, 0, 1);
            grid.Controls.Add(_cbShutter, 1, 1);

            grid.Controls.Add(new Label { Text = "WhiteBal", AutoSize = true }, 0, 2);
            grid.Controls.Add(_cbWb, 1, 2);

            grid.Controls.Add(new Label { Text = "Aperture", AutoSize = true }, 0, 3);
            grid.Controls.Add(_cbAperture, 1, 3);

            grid.Controls.Add(new Label { Text = "Exposure", AutoSize = true }, 0, 4);
            grid.Controls.Add(_cbExposure, 1, 4);

            var rowButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _btnGetSettings.Text = "Get";
            _btnSetSettings.Text = "Set";
            _btnGetSettings.Width = 80;
            _btnSetSettings.Width = 80;
            _btnGetSettings.Click += (_, __) => LoadSettingsToUi();
            _btnSetSettings.Click += async (_, __) => await ApplySettingsFromUi();
            rowButtons.Controls.Add(_btnGetSettings);
            rowButtons.Controls.Add(_btnSetSettings);

            // Buttons in their own row (no overlap with Aperture!)
            grid.Controls.Add(rowButtons, 1, 5);

            groupSettings.Controls.Add(grid);

            _btnCapture.Text = "Capture Photo (auto)";
            _btnCapture.Dock = DockStyle.Top;
            _btnCapture.Height = 36;
            _btnCapture.Click += async (_, __) => await CaptureToDefaultFolder();

            _lblStatus.Dock = DockStyle.Bottom;
            _lblStatus.Height = 30;
            _lblStatus.Text = "Ready.";

            left.Controls.Add(_lblStatus);
            left.Controls.Add(_btnCapture);
            left.Controls.Add(groupSettings);
            left.Controls.Add(groupFps);
            left.Controls.Add(_btnLiveStop);
            left.Controls.Add(_btnLiveStart);
            left.Controls.Add(_btnSelect);
            left.Controls.Add(_btnRefresh);
            left.Controls.Add(_lstCameras);

            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ScrollBars = ScrollBars.Both;
            _txtLog.ReadOnly = true;
            _txtLog.Font = new Font("Consolas", 9);
            right.Controls.Add(_txtLog);

            InitializePreview(right);

            var info = new Label
            {
                Dock = DockStyle.Top,
                Height = 70,
                Text =
                    "HTTP/MJPEG/API läuft jetzt im separaten net8 ApiServer (Kestrel).\r\n" +
                    "Dieses Programm ist der Worker (Kamera-SDK + NamedPipe IPC).\r\n" +
                    "Starte ApiServer.exe zusätzlich, dann Tablet/Web -> ApiServer.",
                AutoSize = false
            };
            right.Controls.Add(info);
        }

        private void InitializePreview(Control container)
        {
            _preview = new PictureBox
            {
                Dock = DockStyle.Right,
                Width = 300,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };
            container.Controls.Add(_preview);
        }

        private void EnsureFolders()
        {
            try
            {
                var folder = _settings.DefaultCaptureFolder;
                if (string.IsNullOrWhiteSpace(folder))
                    folder = @"C:\Photobox\captures";
                Directory.CreateDirectory(folder);
            }
            catch { }
        }

        private void PopulateCameraListFromHost()
        {
            List<CameraInfo> list;
            try { list = _host.GetCameraList() ?? new List<CameraInfo>(); }
            catch { list = new List<CameraInfo>(); }

            UiSafe(() =>
            {
                _lstCameras.BeginUpdate();
                try
                {
                    _lstCameras.Items.Clear();
                    foreach (var c in list)
                        _lstCameras.Items.Add($"[{c.Id}] {c.DisplayName} | {c.Manufacturer} | {c.Model}");
                }
                finally { _lstCameras.EndUpdate(); }

                SetStatus($"Loaded {list.Count} camera(s) (snapshot).");
            });
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => AppendLog(line)));
                return;
            }

            _txtLog.AppendText(line + Environment.NewLine);
        }

        private void SetStatus(string status)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetStatus(status)));
                return;
            }

            _lblStatus.Text = status;
            AppendLog(status);
        }

        // PUBLIC Refresh Entry: wartet auf laufenden Refresh statt neuen zu starten
        private async Task RefreshCameras(bool autoSelectMaybe)
        {
            Task taskToAwait;
            lock (_refreshLock)
            {
                if (_refreshTask != null && !_refreshTask.IsCompleted)
                {
                    taskToAwait = _refreshTask;
                }
                else
                {
                    _refreshTask = RefreshCamerasInternal(autoSelectMaybe);
                    taskToAwait = _refreshTask;
                }
            }
            await taskToAwait;
        }

        private async Task RefreshCamerasInternal(bool autoSelectMaybe)
        {
            UiSafe(() =>
            {
                _btnRefresh.Enabled = false;
                _btnSelect.Enabled = false;
                SetStatus("Refreshing cameras...");
            });

            bool watchdogPaused = false;

            try
            {
                if (_setWatchdogEnabled != null)
                {
                    try { _setWatchdogEnabled(false); watchdogPaused = true; } catch { }
                }

                // ✅ genau EINMAL refreshen, ohne zurück in den UI-Context zu springen
                await _host.RefreshAsync().ConfigureAwait(false);

                // Daten holen (nicht UI)
                var list = _host.GetCameraList() ?? new System.Collections.Generic.List<CameraInfo>();

                // ✅ ALLES was UI anfasst in UiSafe
                UiSafe(() =>
                {
                    _lstCameras.BeginUpdate();
                    try
                    {
                        _lstCameras.Items.Clear();
                        foreach (var c in list)
                            _lstCameras.Items.Add($"[{c.Id}] {c.DisplayName} | {c.Manufacturer} | {c.Model}");
                    }
                    finally { _lstCameras.EndUpdate(); }

                    if (autoSelectMaybe && list.Count > 0)
                    {
                        var sel = _startup.AutoSelectCameraId ?? 0;
                        if (sel < 0 || sel >= list.Count) sel = 0;

                        if (_lstCameras.Items.Count > sel)
                        {
                            _lstCameras.SelectedIndex = sel;
                            _host.SelectCamera(sel);
                            LoadSettingsToUi();
                            SetStatus($"Auto-selected camera #{sel}.");
                        }
                        else
                        {
                            SetStatus($"Found {list.Count} camera(s). (auto-select skipped)");
                        }
                    }
                    else
                    {
                        SetStatus($"Found {list.Count} camera(s).");
                    }

                    _btnRefresh.Enabled = true;
                    _btnSelect.Enabled = true;
                });
            }
            catch (Exception ex)
            {
                UiSafe(() =>
                {
                    SetStatus("Refresh failed: " + ex.Message);
                    _btnRefresh.Enabled = true;
                    _btnSelect.Enabled = true;
                });
            }
            finally
            {
                if (_setWatchdogEnabled != null && watchdogPaused)
                {
                    try { _setWatchdogEnabled(true); } catch { }
                }
            }
        }

        private void SelectCameraFromList()
        {
            var idx = _lstCameras.SelectedIndex;
            if (idx < 0) { SetStatus("Select a camera first."); return; }

            var ok = _host.SelectCamera(idx);
            SetStatus(ok ? $"Selected camera #{idx}" : "Select failed");
            LoadSettingsToUi();
        }

        private void LoadSettingsToUi()
        {
            // ✅ ensure UI-thread, because this can be called from various contexts
            if (InvokeRequired) { BeginInvoke((Action)LoadSettingsToUi); return; }

            var s = _host.GetSettings();
            if (s == null) { SetStatus("No selected camera."); return; }

            _cbIso.Items.Clear();
            _cbIso.Items.AddRange((s.IsoOptions ?? new List<string>()).Cast<object>().ToArray());

            _cbShutter.Items.Clear();
            _cbShutter.Items.AddRange((s.ShutterOptions ?? new List<string>()).Cast<object>().ToArray());

            _cbWb.Items.Clear();
            _cbWb.Items.AddRange((s.WhiteBalanceOptions ?? new List<string>()).Cast<object>().ToArray());

            _cbAperture.Items.Clear();
            _cbAperture.Items.AddRange((s.ApertureOptions ?? new List<string>()).Cast<object>().ToArray());

            _cbExposure.Items.Clear();
            _cbExposure.Items.AddRange((s.ExposureOptions ?? new List<string>()).Cast<object>().ToArray());

            // aktuelle Werte anzeigen (erhalten)
            _cbIso.Text = s.Iso ?? "";
            _cbShutter.Text = s.Shutter ?? "";
            _cbWb.Text = s.WhiteBalance ?? "";
            _cbAperture.Text = s.Aperture ?? "";
            _cbExposure.Text = s.Exposure ?? "";

            var wb = s.WhiteBalance ?? "";
if (!string.IsNullOrWhiteSpace(wb) && !_cbWb.Items.Contains(wb))
    _cbWb.Items.Insert(0, wb);

_cbWb.SelectedItem = wb;   // falls vorhanden
_cbWb.Text = wb;           // fallback

            // (Optional) helps debug "empty UI"
            AppendLog($"Settings loaded. ISOopts={_cbIso.Items.Count}, Shutteropts={_cbShutter.Items.Count}, WBopts={_cbWb.Items.Count}, Aopts={_cbAperture.Items.Count}, Eopts={_cbExposure.Items.Count}");
            SetStatus("Settings loaded.");
        }

        private async Task ApplySettingsFromUi()
        {
            try
            {
                // Exposure: leer => null, sonst float parse (auch "0" muss gelten)
                double? exposure = null;
                var expText = (_cbExposure.Text ?? "").Trim();
                if (expText.Length > 0)
                {
                    var normalized = expText.Replace(',', '.');
                    if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        exposure = n;
                }

                await _host.SetSettingsAsync(
                    _cbIso.Text,
                    _cbShutter.Text,
                    _cbWb.Text,
                    _cbAperture.Text,
                    exposure
                ).ConfigureAwait(false);

                SetStatus("Settings applied.");
            }
            catch (Exception ex)
            {
                SetStatus("Set settings failed: " + ex.Message);
            }
        }

        private async Task CaptureToDefaultFolder()
        {
            try
            {
                var folder = _settings.DefaultCaptureFolder;
                if (string.IsNullOrWhiteSpace(folder))
                    folder = @"C:\Photobox\captures";

                folder = Path.GetFullPath(folder);
                Directory.CreateDirectory(folder);

                var file = Path.Combine(folder, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jpg");
                int i = 1;
                while (File.Exists(file))
                {
                    file = Path.Combine(folder, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + i + ".jpg");
                    i++;
                }

                SetStatus("Capturing to: " + file);

                var result = await _host.CaptureAsync(file).ConfigureAwait(false);
                SetStatus("Capture OK: " + result);

                if (File.Exists(result))
                    SetPreviewImage(result);
            }
            catch (Exception ex)
            {
                SetStatus("Capture failed: " + ex.Message);
            }
        }

        private void SetPreviewImage(string jpgPath)
        {
            if (_preview == null) return;
            if (string.IsNullOrWhiteSpace(jpgPath) || !File.Exists(jpgPath)) return;

            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetPreviewImage(jpgPath)));
                return;
            }

            try
            {
                using (var fs = new FileStream(jpgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var img = Image.FromStream(fs))
                {
                    var bmp = new Bitmap(img);
                    var old = _preview.Image;
                    _preview.Image = bmp;
                    old?.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppendLog("Preview load failed: " + ex.Message);
            }
        }

        private void UiSafe(Action action)
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
    }
}
