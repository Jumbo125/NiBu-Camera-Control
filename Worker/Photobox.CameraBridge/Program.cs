// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: Program.cs
// Zweck: Einstiegspunkt des Workers sowie Initialisierung von Logging, Host, IPC und UI-/Headless-Modus.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - Startargumente auswerten
// - Logging und Konfiguration initialisieren
// - CameraHost, Watchdog und IPC-Server starten
// - UI-, Tray- oder Headless-Betrieb auswählen

using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows.Forms;
using Photobox.CameraBridge.Core;
using Photobox.CameraBridge.UI;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Drawing;
using System.Globalization;

// IPC
using Photobox.Bridge.WorkerIpc;
using Shared = Photobox.Bridge.Shared;

//JSon
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;



namespace Photobox.CameraBridge
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Restart-Support (waitforpid/delayms)
            args = ApplyRestartArgs(args);

            // --log support
            string logPath = null;
            bool logEnabled = false;

            try
            {
                var logEq = args?.FirstOrDefault(a => a != null && a.StartsWith("--log=", StringComparison.OrdinalIgnoreCase));
                if (logEq != null)
                {
                    logEnabled = true;
                    logPath = logEq.Substring("--log=".Length).Trim().Trim('"');
                }

                if (args?.Any(a => string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase)) == true)
                    logEnabled = true;

                if (logEnabled && string.IsNullOrWhiteSpace(logPath))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var name = "Photobox.CameraBridge.log";
                    try
                    {
                        var exe = Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(exe))
                            name = Path.GetFileNameWithoutExtension(exe) + ".log";
                    }
                    catch { }

                    logPath = Path.Combine(baseDir, name);
                }
                else if (logEnabled && !string.IsNullOrWhiteSpace(logPath) && !Path.IsPathRooted(logPath))
                {
                    logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logPath);
                }

                // remove --log args so StartupOptions.Parse doesn't warn about unknown args
                args = (args ?? Array.Empty<string>())
                    .Where(a => !string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase))
                    .Where(a => !(a != null && a.StartsWith("--log=", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
            catch { /* best effort */ }

            var logger = new RingLogger(capacity: 5000);
            FileLogSink fileSink = null;

            if (logEnabled)
            {
                try
                {
                    fileSink = new FileLogSink(logger, logPath);
                    logger.Info("File logging enabled: " + logPath);
                }
                catch (Exception ex)
                {
                    logger.Warn("Failed to enable file logging: " + ex.Message);
                }
            }

            // Global exception logging
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => logger.Error("UI thread exception", e.Exception);
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
                    logger.Error("Unhandled exception (AppDomain)", ex);
                }
                catch { }
            };

            try
            {
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    try { logger.Error("Unobserved task exception", e.Exception); } catch { }
                    try { e.SetObserved(); } catch { }
                };
            }
            catch { }

            if (TryParseDebugCli(args, out var debugCli))
            {
                var exitCode = RunDebugCliAsync(debugCli, logger).GetAwaiter().GetResult();
                Environment.ExitCode = exitCode;
                return;
            }

            var opts = StartupOptions.Parse(args, logger);

            Mutex oneInstanceMutex = null;
            bool oneInstanceMutexOwned = false;

            BridgeIpcServer ipc = null;
            UsbReconnectWatchdog watchdog = null;

            try
            {
                if (opts.OneInstance)
                    oneInstanceMutex = EnsureSingleInstance(logger, out oneInstanceMutexOwned);

                var settings = AppSettingsLoader.LoadOrDefault(
                    baseDir: AppDomain.CurrentDomain.BaseDirectory,
                    logger: logger);

                var cameraMap = CameraMapLoader.LoadOrDefault(
                    baseDir: AppDomain.CurrentDomain.BaseDirectory,
                    logger: logger);

                if (opts.LiveViewFps.HasValue)
                {
                    try
                    {
                        settings.LiveViewFps = opts.LiveViewFps.Value;
                        logger.Info("Startup FPS override: " + opts.LiveViewFps.Value);
                    }
                    catch { }
                }

                var mta = new MtaWorker(logger);
                var cameraHost = new CameraHost(mta, logger, cameraMap, settings);

                // USB Watchdog
                watchdog = new UsbReconnectWatchdog(cameraHost, logger) { Enabled = true };
                watchdog.SetLiveViewDesired(opts.AutoStartLiveView);
                watchdog.Start();

                // IPC starten
                var ipcWorker = new Photobox.CameraBridge.Ipc.BridgeWorkerAdapter(cameraHost, watchdog, settings);

                var pipeName = ReadPipeNameFromAppSettingsJson(AppDomain.CurrentDomain.BaseDirectory)
                ?? Shared.PipeNames.CommandPipe;

                logger.Info("IPC: using pipe name: " + pipeName);

                ipc = new BridgeIpcServer(ipcWorker, pipeName: pipeName, log: s => logger.Info(s));
                ipc.Start();

                int shutdownDone = 0;
                void ShutdownOnce()
                {
                    if (Interlocked.Exchange(ref shutdownDone, 1) == 1) return;

                    try { ipc?.Dispose(); } catch { }                  // 0) IPC stoppen
                    try { watchdog?.Dispose(); } catch { }             // 1) Watchdog stoppen
                    try { cameraHost.StopLiveView(); } catch { }       // 2) LiveView stoppen
                    try { cameraHost.Dispose(); } catch { }            // 3) Kamera freigeben
                    try { mta.Dispose(); } catch { }                   // 4) Worker/Thread weg
                }

                AppDomain.CurrentDomain.ProcessExit += (_, __) => ShutdownOnce();

                
                // Tray braucht Message-Loop => WinForms immer initialisieren
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                logger.Info(opts.Headless
                    ? "Starting in HEADLESS mode (Worker IPC enabled). UI is available via Tray -> Open."
                    : "Starting in UI mode (Worker IPC enabled). Tray is ALWAYS enabled.");

                Application.Run(new WorkerAppContext(
                    trayText: "cameraBridge Worker",
                    originalArgs: args,
                    shutdown: ShutdownOnce,
                    logger: logger,
                    settings: settings,
                    cameraHost: cameraHost,
                    watchdog: watchdog,
                    opts: opts,
                    pipeName: pipeName
                ));

                ShutdownOnce();
            }
            finally
            {
                try { fileSink?.Dispose(); } catch { }

                try
                {
                    if (oneInstanceMutexOwned)
                        oneInstanceMutex?.ReleaseMutex();
                }
                catch { }

                try { oneInstanceMutex?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Always-on tray ApplicationContext:
        /// - Tray is always present (both modes)
        /// - UI is created lazily (first time "Open" is clicked)
        /// - In --headless mode, UI is NOT shown automatically
        /// - Closing the window (X) hides the UI instead of exiting
        /// - Restart re-launches same exe with same args + waitforpid/delayms.
        /// </summary>
        private sealed class WorkerAppContext : ApplicationContext
        {
            private readonly NotifyIcon _icon;
            private readonly Icon _trayIcon;
            private readonly string[] _args;
            private readonly Action _shutdown;
            private readonly RingLogger _log;
            private readonly AppSettings _settings;
            private readonly CameraHost _cameraHost;
            private readonly UsbReconnectWatchdog _watchdog;
            private readonly StartupOptions _opts;

            private MainForm _ui;
            private readonly ToolStripMenuItem _toggleUiItem;
            private int _exiting = 0;

            private int _startupStarted = 0;
            private readonly string _pipeName;

            public WorkerAppContext(
                string trayText,
                string[] originalArgs,
                Action shutdown,
                RingLogger logger,
                AppSettings settings,
                CameraHost cameraHost,
                UsbReconnectWatchdog watchdog,
                StartupOptions opts,
                string pipeName = null)
            {   
                _pipeName = pipeName ?? "";
                _args = originalArgs ?? Array.Empty<string>();
                _shutdown = shutdown ?? (() => { });
                _log = logger;
                _settings = settings;
                _cameraHost = cameraHost;
                _watchdog = watchdog;
                _opts = opts ?? new StartupOptions();

                var menu = new ContextMenuStrip();

                _toggleUiItem = new ToolStripMenuItem("Open", null, (_, __) => ToggleUi());
                menu.Items.Add(_toggleUiItem);
                menu.Items.Add(new ToolStripSeparator());
                
                menu.Items.Add($"Copy PipeName: \"{_pipeName}\"", null, (_, __) =>
                {
                    try { Clipboard.SetText(_pipeName); } catch { }
                });


                menu.Items.Add("Restart", null, (_, __) => Restart());
                menu.Items.Add("Exit", null, (_, __) => ExitApp());

                _trayIcon = TryGetExeIcon() ?? SystemIcons.Application;
                _icon = new NotifyIcon
                {
                    Text = string.IsNullOrWhiteSpace(trayText) ? "cameraBridge Worker" : TrimNotifyText(trayText),
                    ContextMenuStrip = menu,
                    Visible = true
                };

                try { _icon.Icon = _trayIcon; } catch { }

                // Doppelklick toggles UI (Open/Hide)
                _icon.DoubleClick += (_, __) => ToggleUi();

                // Start worker startup actions (same behavior for both modes)
                StartWorkerStartupOnce();

                // In non-headless mode we still auto-show the UI.
                if (!_opts.Headless)
                    ShowUi();
            }

            private void StartWorkerStartupOnce()
            {
                if (Interlocked.Exchange(ref _startupStarted, 1) == 1) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_opts.AutoStartHttp)
                            _log?.Info("AutoHTTP ignored: HTTP läuft jetzt im net8 ApiServer (Kestrel).");

                        // Pause watchdog during refresh (best effort)
                        var wdOld = _watchdog != null && _watchdog.Enabled;
                        if (_watchdog != null) _watchdog.Enabled = false;

                        try
                        {
                            if (_opts.AutoRefresh)
                            {
                                _log?.Info("AutoRefresh enabled -> Refreshing cameras...");
                                await _cameraHost.RefreshAsync().ConfigureAwait(false);
                            }

                            if (_opts.AutoSelectCamera)
                            {
                                var list = _cameraHost.GetCameraList();
                                if (list.Count > 0)
                                {
                                    var sel = _opts.AutoSelectCameraId ?? 0;
                                    if (sel < 0 || sel >= list.Count) sel = 0;
                                    _cameraHost.SelectCamera(sel);
                                    _log?.Info("AutoSelect enabled -> Selected camera #" + sel);
                                }
                                else _log?.Warn("AutoSelect enabled but no cameras found.");
                            }

                            if (_opts.AutoStartLiveView)
                            {
                                _cameraHost.StartLiveView();
                                _log?.Info("AutoLiveView enabled -> LiveView started.");
                            }
                        }
                        finally
                        {
                            if (_watchdog != null) _watchdog.Enabled = wdOld;
                        }
                    }
                    catch (Exception ex)
                    {
                        try { _log?.Error("Startup sequence failed", ex); } catch { }
                    }

                    // If UI is open, sync it after startup actions.
                    TrySyncUi();
                });
            }

            private void ToggleUi()
            {
                try
                {
                    EnsureUi();

                    if (_ui.Visible)
                        HideUi();
                    else
                        ShowUi();
                }
                catch { }
            }

            private void EnsureUi()
            {
                if (_ui != null && !_ui.IsDisposed)
                    return;

                _ui = new MainForm(
                    _settings,
                    _cameraHost,
                    _log,
                    _opts,
                    enabled => { try { if (_watchdog != null) _watchdog.Enabled = enabled; } catch { } }
                );

                _ui.HideOnClose = true;
                _ui.VisibleChanged += (_, __) => UpdateToggleText();
                _ui.FormClosed += (_, __) => UpdateToggleText();

                // Initial sync (shows current worker state without forcing a refresh)
                try { _ui.SyncFromWorkerState(); } catch { }
                UpdateToggleText();
            }

            private void ShowUi()
            {
                EnsureUi();

                try
                {
                    if (_ui.WindowState == FormWindowState.Minimized)
                        _ui.WindowState = FormWindowState.Normal;

                    if (!_ui.Visible)
                        _ui.Show();

                    _ui.Activate();
                }
                catch { }

                TrySyncUi();
                UpdateToggleText();
            }

            private void HideUi()
            {
                if (_ui == null || _ui.IsDisposed) return;
                try { _ui.Hide(); } catch { }
                UpdateToggleText();
            }

            private void TrySyncUi()
            {
                var ui = _ui;
                if (ui == null || ui.IsDisposed) return;

                try
                {
                    if (ui.IsHandleCreated)
                        ui.BeginInvoke((Action)(() => { try { ui.SyncFromWorkerState(); } catch { } }));
                }
                catch { }
            }

            private void UpdateToggleText()
            {
                try
                {
                    var ui = _ui;
                    var open = (ui == null || ui.IsDisposed || !ui.Visible);
                    _toggleUiItem.Text = open ? "Open" : "Hide";
                }
                catch { }
            }

            private static Icon TryGetExeIcon()
            {
                try
                {
                    return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                catch
                {
                    return null;
                }
            }

            private static string TrimNotifyText(string s)
            {
                // NotifyIcon.Text: max 63 chars (Win)
                if (s == null) return "cameraBridge Worker";
                s = s.Trim();
                if (s.Length <= 63) return s;
                return s.Substring(0, 63);
            }

            private void ExitApp()
            {
                if (Interlocked.Exchange(ref _exiting, 1) == 1) return;

                try { _icon.Visible = false; } catch { }
                try { _icon.Dispose(); } catch { }

                try
                {
                    if (_trayIcon != null && !ReferenceEquals(_trayIcon, SystemIcons.Application))
                        _trayIcon.Dispose();
                }
                catch { }

                // Close UI (force close) if it exists
                try
                {
                    if (_ui != null && !_ui.IsDisposed)
                        _ui.RequestCloseForExit();
                }
                catch { }

                try { _shutdown(); } catch { }

                try { ExitThread(); } catch { }
            }

            private void Restart()
            {
                try
                {
                    var exe = SafeGetExePath() ?? Application.ExecutablePath;
                    var pid = Process.GetCurrentProcess().Id;

                    // gleiche Args + Restart-Helfer (wird am Start wieder entfernt)
                    var newArgs = _args
                        .Concat(new[] { "--waitforpid=" + pid, "--delayms=400" })
                        .Select(QuoteArg);

                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = string.Join(" ", newArgs),
                        UseShellExecute = false,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                    };

                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    try { _log?.Error("Restart failed", ex); } catch { }
                }

                ExitApp();
            }

            private static string QuoteArg(string arg)
            {
                if (arg == null) return "\"\"";
                if (arg.Length == 0) return "\"\"";

                var needsQuotes = arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"');
                if (!needsQuotes) return arg;

                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            }
        }
  


        private sealed class DebugCliOptions
        {
            public string Command { get; set; } = string.Empty;
            public string? FileName { get; set; }
            public string? Path { get; set; }
            public bool Overwrite { get; set; }
            public bool ResetAfterShoot { get; set; } = true;
            public string? Iso { get; set; }
            public string? Shutter { get; set; }
            public string? WhiteBalance { get; set; }
            public string? Aperture { get; set; }
            public double? Exposure { get; set; }
            public int? CameraId { get; set; }
            public string? Serial { get; set; }
            public bool RefreshFirst { get; set; } = true;
        }

        private static bool TryParseDebugCli(string[] args, out DebugCliOptions? options)
        {
            options = null;
            args = args ?? Array.Empty<string>();

            var command = args.FirstOrDefault(a =>
                string.Equals(a, "capture-default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "capture-user", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--debug-capture-default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--debug-capture-user", StringComparison.OrdinalIgnoreCase));

            if (command == null)
                return false;

            var isDefault =
                string.Equals(command, "capture-default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "--debug-capture-default", StringComparison.OrdinalIgnoreCase);

            options = new DebugCliOptions
            {
                Command = isDefault ? "capture-default" : "capture-user",
                FileName = GetArgValue(args, "--file", "-f"),
                Path = GetArgValue(args, "--path"),
                Overwrite = HasArg(args, "--overwrite"),
                ResetAfterShoot = GetBoolArg(args, defaultValue: true, "--reset-after"),
                Iso = GetArgValue(args, "--iso"),
                Shutter = GetArgValue(args, "--shutter"),
                WhiteBalance = GetArgValue(args, "--wb", "--whitebalance", "--white-balance"),
                Aperture = GetArgValue(args, "--aperture"),
                Exposure = GetDoubleArg(args, "--exposure"),
                CameraId = GetIntArg(args, "--camera", "--select"),
                Serial = GetArgValue(args, "--serial"),
                RefreshFirst = !HasArg(args, "--skip-refresh", "--no-refresh")
            };

            return true;
        }

        private static async Task<int> RunDebugCliAsync(DebugCliOptions options, RingLogger logger)
        {
            void Say(string message)
            {
                try { Console.WriteLine(message); } catch { }
                try { logger?.Info("[debug-cli] " + message); } catch { }
            }

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var settings = AppSettingsLoader.LoadOrDefault(baseDir, logger);
                var cameraMap = CameraMapLoader.LoadOrDefault(baseDir, logger);

                using (var mta = new MtaWorker(logger))
                using (var cameraHost = new CameraHost(mta, logger, cameraMap, settings))
                {
                   var adapter = new Photobox.CameraBridge.Ipc.BridgeWorkerAdapter(cameraHost, watchdog: null, settings: settings);

                    if (options.RefreshFirst)
                    {
                        Say("Refreshing cameras...");
                        await cameraHost.RefreshAsync().ConfigureAwait(false);
                    }

                    var cameras = cameraHost.GetCameraList();
                    Say("Found cameras: " + cameras.Count);
                    foreach (var cam in cameras)
                        Say($"  [{cam.Id}] {cam.DisplayName} | {cam.Manufacturer} {cam.Model} | Serial={cam.Serial} | Connected={cam.IsConnected}");

                    if (cameras.Count == 0)
                    {
                        Say("No cameras found.");
                        return 2;
                    }

                    bool selected;
                    if (!string.IsNullOrWhiteSpace(options.Serial))
                    {
                        Say("Selecting camera by serial: " + options.Serial);
                        selected = cameraHost.SelectCameraBySerial(options.Serial);
                    }
                    else if (options.CameraId.HasValue)
                    {
                        Say("Selecting camera by id: " + options.CameraId.Value);
                        selected = cameraHost.SelectCamera(options.CameraId.Value);
                    }
                    else
                    {
                        Say("Selecting first camera (id 0).");
                        selected = cameraHost.SelectCamera(0);
                    }

                    if (!selected)
                    {
                        Say("Camera selection failed.");
                        return 3;
                    }

                    var selectedId = cameraHost.GetSelectedCameraId();
                    var selectedCam = selectedId.HasValue
                        ? cameraHost.GetCameraList().FirstOrDefault(x => x.Id == selectedId.Value)
                        : null;

                    if (selectedCam != null)
                        Say($"Selected camera: [{selectedCam.Id}] {selectedCam.DisplayName} | Serial={selectedCam.Serial}");

                    var request = new Shared.CaptureRequestDto
                    {
                        Mode = "file",
                        FileName = options.FileName,
                        Path = options.Path,
                        Overwrite = options.Overwrite,
                        ApplySettings = string.Equals(options.Command, "capture-user", StringComparison.OrdinalIgnoreCase),
                        ResetAfterShoot = options.ResetAfterShoot,
                        Iso = options.Iso,
                        Shutter = options.Shutter,
                        WhiteBalance = options.WhiteBalance,
                        Aperture = options.Aperture,
                        Exposure = options.Exposure,
                    };

                    Say("Starting capture...");
                    Say($"  Mode={options.Command}, ApplySettings={request.ApplySettings}, ResetAfterShoot={request.ResetAfterShoot}");
                    Say($"  ISO={request.Iso ?? "<unchanged>"}, Shutter={request.Shutter ?? "<unchanged>"}, WB={request.WhiteBalance ?? "<unchanged>"}, Aperture={request.Aperture ?? "<unchanged>"}, Exposure={(request.Exposure.HasValue ? request.Exposure.Value.ToString(CultureInfo.InvariantCulture) : "<unchanged>")}");

                    var result = await adapter.CaptureToFileAsync(request, CancellationToken.None).ConfigureAwait(false);
                    if (result == null || !result.Ok || string.IsNullOrWhiteSpace(result.File))
                    {
                        Say("Capture failed without output file.");
                        return 4;
                    }

                    Say("Capture OK -> " + result.File);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                try { logger?.Error("Debug CLI failed", ex); } catch { }
                try { Console.Error.WriteLine(ex.ToString()); } catch { }
                return 10;
            }
        }

        private static bool HasArg(string[] args, params string[] names)
        {
            foreach (var raw in args ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (var name in names)
                {
                    if (string.Equals(raw, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static string? GetArgValue(string[] args, params string[] names)
        {
            args = args ?? Array.Empty<string>();

            for (int i = 0; i < args.Length; i++)
            {
                var raw = args[i];
                if (string.IsNullOrWhiteSpace(raw)) continue;

                foreach (var name in names)
                {
                    if (string.Equals(raw, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length)
                            return args[i + 1]?.Trim().Trim('"');
                        return null;
                    }

                    var prefix = name + "=";
                    if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return raw.Substring(prefix.Length).Trim().Trim('"');
                }
            }

            return null;
        }

        private static bool GetBoolArg(string[] args, bool defaultValue, params string[] names)
        {
            var raw = GetArgValue(args, names);
            if (raw == null)
                return HasArg(args, names) ? true : defaultValue;

            raw = raw.Trim();
            if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
                return false;

            return defaultValue;
        }

        private static int? GetIntArg(string[] args, params string[] names)
        {
            var raw = GetArgValue(args, names);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null;
        }

        private static double? GetDoubleArg(string[] args, params string[] names)
        {
            var raw = GetArgValue(args, names);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            raw = raw.Trim().Replace(',', '.');
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null;
        }

        private static string[] ApplyRestartArgs(string[] args)
        {
            args = args ?? Array.Empty<string>();

            int? waitPid = TryGetIntArg(args, "--waitforpid=");
            if (waitPid.HasValue)
            {
                try { Process.GetProcessById(waitPid.Value).WaitForExit(); }
                catch { }
            }

            int? delayMs = TryGetIntArg(args, "--delayms=");
            if (delayMs.HasValue && delayMs.Value > 0)
                Thread.Sleep(delayMs.Value);

            return args
                .Where(a => !a.StartsWith("--waitforpid=", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.StartsWith("--delayms=", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static int? TryGetIntArg(string[] args, string prefix)
        {
            var hit = args.FirstOrDefault(a => a != null && a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            var s = hit.Substring(prefix.Length).Trim();
            return int.TryParse(s, out var v) ? v : (int?)null;
        }



        private static Mutex EnsureSingleInstance(RingLogger log, out bool owned)
        {
            owned = false;

            var exePath = SafeGetExePath();
            var hash = Md5Hex(exePath ?? "Photobox.CameraBridge");
            var mutexName = @"Local\Photobox.CameraBridge_" + hash;

            KillOtherInstances(exePath, log, forceKill: false);

            var m = new Mutex(false, mutexName);
            try
            {
                var got = false;
                try
                {
                    got = m.WaitOne(TimeSpan.FromSeconds(10), false);
                    if (!got)
                    {
                        log?.Warn("OneInstance: existing instance did not exit in time -> forcing kill");
                        KillOtherInstances(exePath, log, forceKill: true);
                        got = m.WaitOne(TimeSpan.FromSeconds(10), false);
                    }
                }
                catch (AbandonedMutexException)
                {
                    got = true;
                }

                if (!got)
                {
                    log?.Error("OneInstance: mutex still owned by another process; continuing without single-instance lock", null);
                    try { m.Dispose(); } catch { }
                    return null;
                }

                owned = true;
                log?.Info("OneInstance: acquired mutex " + mutexName);
                return m;
            }
            catch (Exception ex)
            {
                log?.Error("OneInstance: failed to acquire mutex", ex);
                try { m.Dispose(); } catch { }
                return null;
            }
        }

        private static void KillOtherInstances(string exePath, RingLogger log, bool forceKill)
        {
            try
            {
                var cur = Process.GetCurrentProcess();
                var curId = cur.Id;
                var name = cur.ProcessName;

                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (p == null) continue;
                    if (p.Id == curId) { try { p.Dispose(); } catch { } continue; }

                    try
                    {
                        if (p.HasExited) continue;

                        var otherPath = SafeGetProcessPath(p);
                        if (!string.IsNullOrWhiteSpace(exePath))
                        {
                            if (string.IsNullOrWhiteSpace(otherPath))
                                continue;

                            if (!string.Equals(otherPath, exePath, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        log?.Info($"OneInstance: shutting down PID {p.Id}...");

                        var exited = false;
                        if (!forceKill)
                        {
                            try
                            {
                                if (p.CloseMainWindow())
                                    exited = p.WaitForExit(3000);
                            }
                            catch { }
                        }

                        if (!exited)
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(5000); } catch { }
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                log?.Warn("OneInstance: failed to enumerate/close instances: " + ex.Message);
            }
        }

        private static string SafeGetExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName; } catch { return null; }
        }

        private static string SafeGetProcessPath(Process p)
        {
            try { return p?.MainModule?.FileName; } catch { return null; }
        }

        private static string Md5Hex(string s)
        {
            try
            {
                using (var md5 = MD5.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
                    var hash = md5.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", "");
                }
            }
            catch
            {
                return (s ?? string.Empty).GetHashCode().ToString("X8");
            }
        }


        [DataContract]
private sealed class AppSettingsRoot
{
    // ✅ Root-Level: { "pipeName": "..." }
    [DataMember(Name = "pipeName")]
    public string? PipeName { get; set; }

    // optional legacy: { "ipcPipeName": "..." }
    [DataMember(Name = "ipcPipeName")]
    public string? IpcPipeName { get; set; }

    // optional: { "ipc": { "pipeName": "..." } }
    [DataMember(Name = "ipc")]
    public IpcSection? Ipc { get; set; }
}

[DataContract]
private sealed class IpcSection
{
    [DataMember(Name = "pipeName")]
    public string? PipeName { get; set; }
}

private static string? ReadPipeNameFromAppSettingsJson(string baseDir)
{
    try
    {
        var path = Path.Combine(baseDir, "appsettings.json");
        if (!File.Exists(path)) return null;

        using (var fs = File.OpenRead(path))
        {
            var ser = new DataContractJsonSerializer(typeof(AppSettingsRoot));
            var cfg = ser.ReadObject(fs) as AppSettingsRoot;

            // Priorität: ipc.pipeName > root.pipeName > ipcPipeName
            var name = cfg?.Ipc?.PipeName ?? cfg?.PipeName ?? cfg?.IpcPipeName;
            return NormalizePipeName(name);
        }
    }
    catch
    {
        return null; // best effort
    }
}

private static string? NormalizePipeName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return null;

    name = name.Trim();

    // falls jemand \\.\pipe\xyz in die config schreibt
    const string prefix = @"\\.\pipe\";
    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        name = name.Substring(prefix.Length);

    return string.IsNullOrWhiteSpace(name) ? null : name;
}
    }
}
