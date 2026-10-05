// ======================= AdvancedForm.cs =======================
// Drop-in replacement for your AdvancedForm with:
// ✅ UI strings loaded from launcher\ui\strings.{lang}.json
// ✅ Placeholder support via UiStrings.T(key, vars)
// ✅ No auto-refresh (refresh only on open + refresh button + after actions)
// ✅ Full Install: Port-Eingabe -> schreibt launcher\install_port.txt -> ruft install.bat (ELEVATED)
// ✅ Install/Uninstall Warnung (Ja/Nein) via strings.json
// ✅ Watchdog Start/Stop Buttons (Task bleibt im Scheduler: enable+run / end+disable) (ELEVATED)
// ✅ Firewall/Task Install/Uninstall Buttons laufen per UAC (ELEVATED, UseShellExecute=true)
// ✅ Checks laufen NICHT elevated und immer mit /nopause
// ✅ Manual Install: zusätzlicher Button "Desktop Shortcut" -> create_open_app_shortcut.bat (non-admin)
// ✅ Edit Ports(Caddy/PHP) -> nutzt bestehenden PortDialog mit Flags und ruft edit_ports.bat elevated
// ✅ NEW: Kiosk-Autostart nur noch als Hinweis + anklickbarer Link zum Windows-Autostart-Ordner
// ✅ Fenster startet maximiert; linker Bereich ist scrollbar; Manual-Install-Frame ist höher

using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using NiBuLauncher.Services;

namespace NiBuLauncher;

public sealed class AdvancedForm : Form
{
    private readonly string _baseDir;
    private readonly ScriptRunner _runner;

    private volatile bool _closing;
    private readonly CancellationTokenSource _cts = new();

    private string _lang;
    private UiStrings _ui;

    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly DataGridView _statusGrid = new()
    {
        Dock = DockStyle.Top,
        Height = 180,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    // UI refs (for language updates)
    private GroupBox? _g1;
    private GroupBox? _g2;
    private GroupBox? _g3;
    private GroupBox? _g4;

    private Label? _lblHeaderTitle;
    private Label? _lblConsole;
    private Label? _lblKioskAutostartHint;
    private LinkLabel? _lnkOpenStartupFolder;

    private Button? _btnRefresh;
    private Button? _btnUnblock;
    private Button? _btnFullInstall;

    private Button? _btnFirewall;
    private Button? _btnTaskInstall;
    private Button? _btn_editPort;

    private Button? _btnShortcut;

    private Button? _btnWinTweaks;

    private Button? _btnWatchdogStart;
    private Button? _btnWatchdogStop;

    private Button? _btnRemoveAll;
    private Button? _btnFirewallUninstall;
    private Button? _btnTaskUninstall;

    private Button? _btnWindowstweaksUninstall;

    public AdvancedForm(string baseDir)
    {
        _baseDir = baseDir;
        _runner = new ScriptRunner(_baseDir, AppendLog);

        var available = UiStrings.DiscoverLanguages(_baseDir);
        _lang = UiStrings.LoadLanguage(_baseDir, available);
        _ui = UiStrings.Load(_baseDir, _lang);

        Text = _ui.T("window.advanced.title");
        MinimumSize = new Size(1020, 680);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        WindowState = FormWindowState.Maximized;

        BuildUi();
        ApplyLanguageToUi();

        Shown += async (_, _) =>
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Advanced UI gestartet. BaseDir: {_baseDir}");
            await RefreshAdvancedStatusAsync(_cts.Token); // once on open
        };

        FormClosing += (_, _) =>
        {
            _closing = true;
            try
            {
                _cts.Cancel();
            }
            catch { }
        };
    }

    // called from MainForm when language changes
    public void SetLanguage(string lang)
    {
        _lang = (lang ?? "de").Trim().ToLowerInvariant();
        _ui = UiStrings.Load(_baseDir, _lang);
        ApplyLanguageToUi();
    }

    private void BuildUi()
    {
        _statusGrid.Columns.Clear();
        _statusGrid.Columns.Add(
            "key",
            _ui.T("adv.grid.key", UiStrings.Vars(("fallback", "Bereich")))
        );
        _statusGrid.Columns.Add(
            "state",
            _ui.T("adv.grid.state", UiStrings.Vars(("fallback", "Status")))
        );
        _statusGrid.Columns.Add(
            "details",
            _ui.T("adv.grid.details", UiStrings.Vars(("fallback", "Details")))
        );
        _statusGrid.Columns[0].FillWeight = 30;
        _statusGrid.Columns[1].FillWeight = 18;
        _statusGrid.Columns[2].FillWeight = 52;

        _statusGrid.Rows.Clear();
        _statusGrid.Rows.Add("?", "?", "");
        _statusGrid.Rows.Add("?", "?", "");
        _statusGrid.Rows.Add("?", "?", "");

        Button Btn(out Button? field, Func<Task> onClick)
        {
            var b = new Button
            {
                Width = 230,
                Height = 36,
                Margin = new Padding(0, 0, 0, 10),
            };
            field = b;
            b.Click += async (_, _) =>
            {
                b.Enabled = false;
                try
                {
                    await onClick();
                }
                catch (Exception ex)
                {
                    AppendLog("[EX] " + ex);
                }
                finally
                {
                    b.Enabled = true;
                    await RefreshAdvancedStatusAsync();
                }
            };
            return b;
        }

        FlowLayoutPanel VButtons() =>
            new()
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10),
            };

        GroupBox Group(out GroupBox? field, Control inner, int height)
        {
            var g = new GroupBox
            {
                Dock = DockStyle.Top,
                Height = height,
                Padding = new Padding(8),
                Controls = { inner },
            };
            field = g;
            return g;
        }

        // Frame 1 (Setup)
        var f1 = VButtons();
        f1.Controls.Add(
            Btn(
                out _btnUnblock,
                () => RunScriptAsync("launcher\\unblock.bat", "/nopause", timeoutSeconds: 60)
            )
        );
        f1.Controls.Add(Btn(out _btnFullInstall, FullInstallWithPortsAsync));
        var g1 = Group(out _g1, f1, 160);

        // Frame 2 (Manual Install)
        var f2 = VButtons();
        f2.Controls.Add(
            Btn(
                out _btnFirewall,
                () =>
                    RunScriptAdminAsync(
                        "launcher\\firewall_install.bat",
                        "/nopause",
                        timeoutSeconds: 30
                    )
            )
        );
        f2.Controls.Add(
            Btn(
                out _btnTaskInstall,
                () =>
                    RunScriptAdminAsync(
                        "launcher\\task_install.bat",
                        "/nopause",
                        timeoutSeconds: 30
                    )
            )
        );
        f2.Controls.Add(Btn(out _btn_editPort, EditCaddyPhpPortsAsync));
        f2.Controls.Add(Btn(out _btnWinTweaks, WindowsTweaksWithWarningAsync));
        f2.Controls.Add(
            Btn(
                out _btnShortcut,
                () => RunScriptAsync("launcher\\create_open_app_shortcut.bat", "/nopause")
            )
        );

        _lblKioskAutostartHint = new Label
        {
            AutoSize = false,
            Width = 250,
            Height = 62,
            Margin = new Padding(3, 4, 3, 6),
        };

        _lnkOpenStartupFolder = new LinkLabel
        {
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 10),
        };
        _lnkOpenStartupFolder.LinkClicked += (_, _) => OpenStartupFolderWithInfo();

        f2.Controls.Add(_lblKioskAutostartHint);
        f2.Controls.Add(_lnkOpenStartupFolder);

        var g2 = Group(out _g2, f2, 340);

        // Frame 3 (Watchdog) - ADMIN (schtasks)
        var f3 = VButtons();
        f3.Controls.Add(
            Btn(
                out _btnWatchdogStart,
                () => RunScriptAdminAsync("launcher\\task_start.bat", "/nopause")
            )
        );
        f3.Controls.Add(
            Btn(
                out _btnWatchdogStop,
                () => RunScriptAdminAsync("launcher\\task_stop.bat", "/nopause")
            )
        );
        var g3 = Group(out _g3, f3, 160);

        // Frame 4 (Remove) - ADMIN for most
        var f4 = VButtons();
        f4.Controls.Add(Btn(out _btnRemoveAll, UninstallWithWarningAsync));
        f4.Controls.Add(
            Btn(
                out _btnFirewallUninstall,
                () => RunScriptAdminAsync("launcher\\firewall_uninstall.bat", "/nopause")
            )
        );
        f4.Controls.Add(
            Btn(
                out _btnTaskUninstall,
                () => RunScriptAdminAsync("launcher\\task_uninstall.bat", "/nopause")
            )
        );
        f4.Controls.Add(
            Btn(
                out _btnWindowstweaksUninstall,
                () => RunScriptAdminAsync("launcher\\windowsTweaks_uninstall.bat", "/nopause")
            )
        );
        var g4 = Group(out _g4, f4, 220);

        var left = new Panel
        {
            Dock = DockStyle.Left,
            Width = 320,
            AutoScroll = true,
        };
        left.Controls.Add(g4);
        left.Controls.Add(g3);
        left.Controls.Add(g2);
        left.Controls.Add(g1);

        // Header (Install Status + Refresh)
        var header = new Panel { Dock = DockStyle.Top, Height = 30 };

        _lblHeaderTitle = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _btnRefresh = new Button
        {
            Dock = DockStyle.Right,
            Width = 90,
            Height = 26,
        };

        _btnRefresh.Click += async (_, _) =>
        {
            if (_btnRefresh is null || !_btnRefresh.Enabled)
                return;

            _btnRefresh.Enabled = false;
            var old = _btnRefresh.Text;
            _btnRefresh.Text = _ui.T("btn.refreshing", UiStrings.Vars(("fallback", "…")));

            AppendLog(
                _ui.T(
                    "log.advRefreshStart",
                    UiStrings.Vars(
                        ("time", DateTime.Now.ToString("HH:mm:ss")),
                        ("fallback", $"[{DateTime.Now:HH:mm:ss}] Refresh…")
                    )
                )
            );

            try
            {
                await RefreshAdvancedStatusAsync();

                string Row(int i) =>
                    $"{_statusGrid.Rows[i].Cells[0].Value}: {_statusGrid.Rows[i].Cells[1].Value} | {_statusGrid.Rows[i].Cells[2].Value}";
                var summary = $"{Row(0)} ; {Row(1)} ; {Row(2)}";

                AppendLog(
                    _ui.T(
                        "log.advRefreshDone",
                        UiStrings.Vars(
                            ("time", DateTime.Now.ToString("HH:mm:ss")),
                            ("summary", summary),
                            ("fallback", $"[{DateTime.Now:HH:mm:ss}] Refresh OK: {summary}")
                        )
                    )
                );
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] Advanced Refresh: FEHLER -> {ex.Message}");
            }
            finally
            {
                _btnRefresh.Text = old;
                _btnRefresh.Enabled = true;
            }
        };

        header.Controls.Add(_btnRefresh);
        header.Controls.Add(_lblHeaderTitle);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        _lblConsole = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(0, 10, 10, 0),
        };

        // Order: Fill first, then top items, so headers remain visible
        right.Controls.Add(_logBox);
        right.Controls.Add(_lblConsole);
        right.Controls.Add(_statusGrid);
        right.Controls.Add(header);

        Controls.Add(right);
        Controls.Add(left);
    }

    private void ApplyLanguageToUi()
    {
        Text = _ui.T("window.advanced.title", UiStrings.Vars(("fallback", "Advanced")));

        if (_g1 != null)
            _g1.Text = _ui.T("adv.frame1", UiStrings.Vars(("fallback", "Setup")));
        if (_g2 != null)
            _g2.Text = _ui.T("adv.frame2", UiStrings.Vars(("fallback", "Install")));
        if (_g3 != null)
            _g3.Text = _ui.T("adv.frame3", UiStrings.Vars(("fallback", "Watchdog")));
        if (_g4 != null)
            _g4.Text = _ui.T("adv.frame4", UiStrings.Vars(("fallback", "Remove")));

        if (_btnUnblock != null)
            _btnUnblock.Text = _ui.T(
                "adv.btn.unblock",
                UiStrings.Vars(("fallback", "Unblock Files"))
            );
        if (_btnFullInstall != null)
            _btnFullInstall.Text = _ui.T(
                "adv.btn.fullInstall",
                UiStrings.Vars(("fallback", "Full Install"))
            );

        if (_btnFirewall != null)
            _btnFirewall.Text = _ui.T(
                "adv.btn.firewall",
                UiStrings.Vars(("fallback", "Firewall Install"))
            );
        if (_btnTaskInstall != null)
            _btnTaskInstall.Text = _ui.T(
                "adv.btn.task",
                UiStrings.Vars(("fallback", "Task Install"))
            );
        if (_btn_editPort != null)
            _btn_editPort.Text = _ui.T(
                "adv.btn.editPort",
                UiStrings.Vars(("fallback", "Edit Ports(Caddy/PHP)"))
            );
        if (_btnShortcut != null)
            _btnShortcut.Text = _ui.T(
                "adv.btn.shortcut",
                UiStrings.Vars(("fallback", "Desktop-Verknüpfung erstellen"))
            );

        if (_lblKioskAutostartHint != null)
            _lblKioskAutostartHint.Text = _ui.T(
                "adv.lbl.kioskAutostartHint",
                UiStrings.Vars(
                    (
                        "fallback",
                        "Für Autostart bitte die Verknüpfung 'NibuBox_Autostart.lnk' manuell in den Windows-Autostart-Ordner kopieren."
                    )
                )
            );

        if (_lnkOpenStartupFolder != null)
            _lnkOpenStartupFolder.Text = _ui.T(
                "adv.link.openStartupFolder",
                UiStrings.Vars(("fallback", "Autostart-Ordner öffnen"))
            );

        if (_btnWatchdogStart != null)
            _btnWatchdogStart.Text = _ui.T(
                "adv.btn.watchdogStart",
                UiStrings.Vars(("fallback", "Watchdog Start"))
            );
        if (_btnWatchdogStop != null)
            _btnWatchdogStop.Text = _ui.T(
                "adv.btn.watchdogStop",
                UiStrings.Vars(("fallback", "Watchdog Stop"))
            );

        if (_btnRemoveAll != null)
            _btnRemoveAll.Text = _ui.T(
                "adv.btn.removeAll",
                UiStrings.Vars(("fallback", "Uninstall"))
            );
        if (_btnFirewallUninstall != null)
            _btnFirewallUninstall.Text = _ui.T(
                "adv.btn.firewallUninstall",
                UiStrings.Vars(("fallback", "Firewall Uninstall"))
            );
        if (_btnTaskUninstall != null)
            _btnTaskUninstall.Text = _ui.T(
                "adv.btn.taskUninstall",
                UiStrings.Vars(("fallback", "Task Uninstall"))
            );
        if (_btnWindowstweaksUninstall != null)
            _btnWindowstweaksUninstall.Text = _ui.T(
                "adv.btn.btnWindowstweaksUninstall",
                UiStrings.Vars(("fallback", "WindowsTweaks Uninstall"))
            );

        if (_lblHeaderTitle != null)
            _lblHeaderTitle.Text = _ui.T(
                "label.installStatus",
                UiStrings.Vars(("fallback", "Install Status"))
            );
        if (_btnRefresh != null)
            _btnRefresh.Text = _ui.T("btn.refresh", UiStrings.Vars(("fallback", "Refresh")));
        if (_lblConsole != null)
            _lblConsole.Text = _ui.T("label.console", UiStrings.Vars(("fallback", "Console")));

        if (_btnWinTweaks != null)
            _btnWinTweaks.Text = _ui.T(
                "adv.btn.winTweaks",
                UiStrings.Vars(("fallback", "Windows-Kiosk Anpassungen"))
            );

        // Grid row titles
        if (_statusGrid.Rows.Count >= 3)
        {
            _statusGrid.Rows[0].Cells[0].Value = _ui.T(
                "adv.row.configs",
                UiStrings.Vars(("fallback", "Configs"))
            );
            _statusGrid.Rows[1].Cells[0].Value = _ui.T(
                "adv.row.firewall",
                UiStrings.Vars(("fallback", "Firewall"))
            );
            _statusGrid.Rows[2].Cells[0].Value = _ui.T(
                "adv.row.task",
                UiStrings.Vars(("fallback", "Task"))
            );
        }
    }

    private void AppendLog(string line)
    {
        if (_closing || IsDisposed)
            return;
        try
        {
            if (InvokeRequired)
            {
                if (!IsHandleCreated || _closing || IsDisposed)
                    return;
                BeginInvoke(new Action<string>(AppendLog), line);
                return;
            }
            if (_logBox.IsDisposed || _closing)
                return;
            _logBox.AppendText(line + Environment.NewLine);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // Non-admin run (captures via ScriptRunner) + optional Timeout/Detached
    private async Task RunScriptAsync(
        string relativePath,
        string? args = null,
        int? timeoutSeconds = 15,
        bool detached = false
    )
    {
        if (_closing || _cts.IsCancellationRequested)
            return;

        var fullPath = Path.GetFullPath(Path.Combine(_baseDir, relativePath));
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ACTION: {relativePath} {args}".Trim());

        if (detached)
            await _runner.RunBatchDetachedAsync(fullPath, args ?? "");
        else
            await _runner.RunBatchAsync(fullPath, args ?? "", timeoutSeconds, _cts.Token);
    }

    // Admin run (UAC) + capture by redirecting into logs\admin_*.log
    private async Task RunScriptAdminAsync(
        string relativePath,
        string? args = null,
        int timeoutSeconds = 180
    )
    {
        if (_closing || _cts.IsCancellationRequested)
            return;

        var fullPath = Path.GetFullPath(Path.Combine(_baseDir, relativePath));
        var name = Path.GetFileName(fullPath);

        var logsDir = Path.Combine(_baseDir, "logs");
        Directory.CreateDirectory(logsDir);

        var outFile = Path.Combine(
            logsDir,
            $"admin_{Path.GetFileNameWithoutExtension(fullPath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        );

        // IMPORTANT: UseShellExecute=true for Verb=runas (UAC)
        // Capture via cmd redirection to file (since RedirectStandardOutput is not possible with UAC)
        var cmdArgs = $"/c \"\"{fullPath}\" {args ?? ""} > \"{outFile}\" 2>&1\"";

        AppendLog($"[{DateTime.Now:HH:mm:ss}] ACTION(ADMIN): {relativePath} {args}".Trim());
        AppendLog($"[{DateTime.Now:HH:mm:ss}] -> Output: {outFile}");

        try
        {
            using var p = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    WorkingDirectory = Path.GetDirectoryName(fullPath)!,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
            );

            if (p == null)
            {
                AppendLog(
                    $"[{DateTime.Now:HH:mm:ss}] [ERR] Admin-Start fehlgeschlagen (Process=null)."
                );
                return;
            }

            var exitTask = p.WaitForExitAsync();
            var finished = await Task.WhenAny(
                exitTask,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), _cts.Token)
            );

            if (finished != exitTask)
            {
                AppendLog(
                    $"[{DateTime.Now:HH:mm:ss}] [TIMEOUT] {name} läuft länger als {timeoutSeconds}s -> Kill"
                );
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch { }
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [DONE] {name} -> Timeout");
                return;
            }

            await exitTask;

            if (File.Exists(outFile))
            {
                var text = File.ReadAllText(outFile);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    AppendLog("----- admin output begin -----");
                    AppendLog(text.TrimEnd());
                    AppendLog("----- admin output end -----");
                }
            }

            AppendLog($"[{DateTime.Now:HH:mm:ss}] [DONE] {name} -> ExitCode={p.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [INFO] UAC abgebrochen.");
        }
    }

    private void SetRow(int idx, string state, string details)
    {
        if (_closing || _statusGrid.IsDisposed)
            return;
        if (idx < 0 || idx >= _statusGrid.Rows.Count)
            return;
        _statusGrid.Rows[idx].Cells[1].Value = state;
        _statusGrid.Rows[idx].Cells[2].Value = details;
    }

    private async Task RefreshAdvancedStatusAsync(CancellationToken token = default)
    {
        if (_closing || token.IsCancellationRequested)
            return;
        if (!await _refreshLock.WaitAsync(0))
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Advanced Refresh: übersprungen (läuft bereits)");
            return;
        }

        try
        {
            if (token.IsCancellationRequested)
                return;
            await UpdateInstallStatusAsync();
            if (token.IsCancellationRequested)
                return;
            await UpdateFirewallStatusAsync();
            if (token.IsCancellationRequested)
                return;
            await UpdateTaskStatusAsync();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task UpdateInstallStatusAsync()
    {
        var r = await RunCheckAsync("launcher\\check_install.bat");
        SetRow(0, r.State, r.Details);
    }

    private async Task UpdateTaskStatusAsync()
    {
        var full = Path.GetFullPath(Path.Combine(_baseDir, "launcher\\check_task.bat"));
        var res = await _runner.RunBatchCaptureAsync(full, "/nopause");

        if (res.ExitCode != 0)
        {
            SetRow(
                2,
                "FEHLER",
                string.IsNullOrWhiteSpace(res.Stdout) ? res.Summary : res.Stdout.Trim()
            );
            return;
        }

        var text = res.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetRow(2, "FEHLER", "check_task.bat hat keinen Output geliefert");
            return;
        }

        // Try JSON first
        try
        {
            var tc = JsonSerializer.Deserialize<TaskCheck>(
                text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            if (tc != null)
            {
                var installed = tc.Installed;
                var enabled = tc.Enabled;
                var running = tc.Running;

                var state = installed
                    ? (running ? "RUNNING" : (enabled ? "READY" : "DISABLED"))
                    : "NICHT INSTALLIERT";

                var details =
                    $"installed={(installed ? "yes" : "no")}; enabled={(enabled ? "yes" : "no")}; running={(running ? "yes" : "no")}";
                if (!string.IsNullOrWhiteSpace(tc.TaskName))
                    details = $"{tc.TaskName} | {details}";

                SetRow(2, installed ? "OK" : "NEIN", $"{state} | {details}");
                return;
            }
        }
        catch
        {
            // ignore and fallback to text
        }

        // Fallback: use plain text
        SetRow(2, "OK", text);
    }

    private async Task UpdateFirewallStatusAsync()
    {
        var full = Path.GetFullPath(Path.Combine(_baseDir, "launcher\\check_firewall.bat"));
        var res = await _runner.RunBatchCaptureAsync(full, "/nopause");

        if (res.ExitCode != 0)
        {
            SetRow(1, "FEHLER", res.Summary);
            return;
        }

        var jsonText = res.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            SetRow(1, "FEHLER", "check_firewall.bat hat keinen Output geliefert");
            return;
        }

        try
        {
            var fw = JsonSerializer.Deserialize<FirewallCheck>(
                jsonText,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            if (fw == null)
            {
                SetRow(1, "?", "keine Daten");
                return;
            }

            var parts = fw.Ports.Select(p =>
            {
                var port = p.Port?.ToString() ?? "?";
                var ok = p.Freigegeben ? "ok" : "fail";
                var err = string.IsNullOrWhiteSpace(p.Error) ? "" : $"({p.Error})";
                return $"{p.Name}:{port}={ok}{err}";
            });

            var allNotFound =
                fw.Ports.Count > 0
                && fw.Ports.All(p =>
                    string.Equals(p.Error, "not found", StringComparison.OrdinalIgnoreCase)
                );
            var state = fw.AllOk ? "OK" : (allNotFound ? "NICHT INSTALLIERT" : "TEILWEISE");

            SetRow(1, state, string.Join("; ", parts));
        }
        catch
        {
            SetRow(1, "FEHLER", "Firewall JSON konnte nicht geparst werden");
        }
    }

    private async Task<(string State, string Details)> RunCheckAsync(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_baseDir, relativePath));
        var res = await _runner.RunBatchCaptureAsync(full, "/nopause");

        return res.ExitCode switch
        {
            0 => (_ui.T("state.yes", UiStrings.Vars(("fallback", "JA"))), res.Stdout.Trim()),
            1 => (_ui.T("state.no", UiStrings.Vars(("fallback", "NEIN"))), res.Stdout.Trim()),
            _ => (
                _ui.T("state.error", UiStrings.Vars(("fallback", "FEHLER"))),
                string.IsNullOrWhiteSpace(res.Stdout) ? res.Summary : res.Stdout.Trim()
            ),
        };
    }

    // ----------------------------
    // Confirm Dialog (Install/Uninstall Warnings)
    // ----------------------------
    private bool ConfirmFromUi(
        string titleKey,
        string msgKey,
        IDictionary<string, string>? vars = null
    )
    {
        var title = _ui.T(titleKey, UiStrings.Vars(("fallback", "Warnung")));
        var msg = _ui.T(msgKey, vars ?? UiStrings.Vars(("fallback", msgKey)));

        var yesText = _ui.T("btn.yes", UiStrings.Vars(("fallback", "Ja")));
        var noText = _ui.T("btn.no", UiStrings.Vars(("fallback", "Nein")));

        using var dlg = new ConfirmDialog(title, msg, yesText, noText);
        return dlg.ShowDialog(this) == DialogResult.OK;
    }

    private sealed class ConfirmDialog : Form
    {
        public ConfirmDialog(string title, string message, string yesText, string noText)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 600;
            Height = 260;

            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                Text = message,
                Padding = new Padding(14),
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
            };

            var btnYes = new Button
            {
                Text = yesText,
                DialogResult = DialogResult.OK,
                Width = 120,
                Height = 34,
            };
            var btnNo = new Button
            {
                Text = noText,
                DialogResult = DialogResult.Cancel,
                Width = 120,
                Height = 34,
            };

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 70,
                Padding = new Padding(12),
            };
            bottom.Controls.Add(btnYes);
            bottom.Controls.Add(btnNo);

            Controls.Add(lbl);
            Controls.Add(bottom);

            AcceptButton = btnYes;
            CancelButton = btnNo;
        }
    }

    private async Task UninstallWithWarningAsync()
    {
        var ok = ConfirmFromUi(
            "confirm.uninstall.title",
            "confirm.uninstall.msg",
            UiStrings.Vars(
                (
                    "fallback",
                    "Durch die Deinstallation werden sämtliche Konfigurationsdateien gelöscht.\n\nWollen Sie fortfahren?"
                )
            )
        );

        if (!ok)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Deinstallation abgebrochen.");
            return;
        }

        await RunScriptAdminAsync("launcher\\uninstall.bat", "/nopause", timeoutSeconds: 300);
    }

    // ----------------------------
    // Ports + Dialog
    // ----------------------------
    private sealed record Ports(int Caddy, int Php, int BridgeApi, int Python);

    private sealed class PortDialog : Form
    {
        public Ports Result { get; private set; }

        public PortDialog(UiStrings ui, Ports initial, bool showApi = true, bool showPython = true)
        {
            Text = ui.T("adv.port.title", UiStrings.Vars(("fallback", "Ports für Full Install")));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 420;
            Height = 260;

            NumericUpDown Num(int v) =>
                new()
                {
                    Minimum = 1,
                    Maximum = 65535,
                    Value = Math.Clamp(v, 1, 65535),
                    Width = 120,
                };

            var nCaddy = Num(initial.Caddy);
            var nPhp = Num(initial.Php);
            var nApi = Num(initial.BridgeApi);
            var nPy = Num(initial.Python);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
                AutoSize = true,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));

            void AddRow(string label, Control ctrl)
            {
                var r = grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                grid.Controls.Add(
                    new Label
                    {
                        Text = label,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                    },
                    0,
                    r
                );
                grid.Controls.Add(ctrl, 1, r);
            }

            AddRow(ui.T("adv.port.caddy", UiStrings.Vars(("fallback", "Caddy Port"))), nCaddy);
            AddRow(ui.T("adv.port.php", UiStrings.Vars(("fallback", "PHP Port"))), nPhp);

            if (showApi)
                AddRow(
                    ui.T("adv.port.api", UiStrings.Vars(("fallback", "API-Server (Bridge) Port"))),
                    nApi
                );

            if (showPython)
                AddRow(ui.T("adv.port.python", UiStrings.Vars(("fallback", "Python Port"))), nPy);

            var btnOk = new Button
            {
                Text = ui.T("btn.ok", UiStrings.Vars(("fallback", "OK"))),
                DialogResult = DialogResult.OK,
                Width = 90,
            };
            var btnCancel = new Button
            {
                Text = ui.T("btn.cancel", UiStrings.Vars(("fallback", "Abbrechen"))),
                DialogResult = DialogResult.Cancel,
                Width = 90,
            };

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 45,
                Padding = new Padding(12),
            };
            bottom.Controls.Add(btnOk);
            bottom.Controls.Add(btnCancel);

            Controls.Add(grid);
            Controls.Add(bottom);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            btnOk.Click += (_, _) =>
            {
                Result = new Ports(
                    (int)nCaddy.Value,
                    (int)nPhp.Value,
                    (int)nApi.Value,
                    (int)nPy.Value
                );
            };

            Result = initial;

            if (!showApi && !showPython)
                Height = 200;
            else if (!showApi || !showPython)
                Height = 230;
        }
    }

    private Ports LoadPortsForDialog()
    {
        int caddy = 8050,
            php = 8051,
            bridge = 8052,
            py = 8053;

        var p = Path.Combine(_baseDir, "launcher", "port_Settings.txt");
        if (File.Exists(p))
        {
            try
            {
                foreach (var raw in File.ReadAllLines(p))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;

                    var key = line[..idx].Trim().ToUpperInvariant();
                    var val = line[(idx + 1)..].Trim();

                    if (!int.TryParse(val, out var n))
                        continue;
                    if (n < 1 || n > 65535)
                        continue;

                    if (key == "CADDY_PORT")
                        caddy = n;
                    else if (key == "PHP_PORT")
                        php = n;
                    else if (key == "BRIDGE_PORT" || key == "API_PORT")
                        bridge = n;
                    else if (key == "PY_PORT" || key == "PYTHON_PORT")
                        py = n;
                }
            }
            catch { }
        }

        return new Ports(caddy, php, bridge, py);
    }

    private void SavePortsForDialog(Ports ports)
    {
        var launcherDir = Path.Combine(_baseDir, "launcher");
        Directory.CreateDirectory(launcherDir);

        var path = Path.Combine(launcherDir, "port_Settings.txt");
        var text =
            $"CADDY_PORT={ports.Caddy}\r\n"
            + $"PHP_PORT={ports.Php}\r\n"
            + $"BRIDGE_PORT={ports.BridgeApi}\r\n"
            + $"PY_PORT={ports.Python}\r\n";

        File.WriteAllText(path, text, Encoding.UTF8);
        AppendLog($"[{DateTime.Now:HH:mm:ss}] port_Settings.txt gespeichert: {path}");
    }

    // ----------------------------
    // Full Install with Ports (ELEVATED)
    // ----------------------------
    private async Task FullInstallWithPortsAsync()
    {
        var ok = ConfirmFromUi(
            "confirm.install.title",
            "confirm.install.msg",
            UiStrings.Vars(
                (
                    "fallback",
                    "Durch die Installation werden vorhandene Einstellungen überschrieben.\nWollen Sie fortfahren?"
                ),
                ("files", "booth\\config\\config\\config.json")
            )
        );

        if (!ok)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Full Install abgebrochen (Warnung).");
            return;
        }

        var ports = LoadPortsForDialog();

        using var dlg = new PortDialog(_ui, ports);
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Full Install abgebrochen.");
            return;
        }

        ports = dlg.Result;

        var launcherDir = Path.GetFullPath(Path.Combine(_baseDir, "launcher"));
        var defaultConfigDir = Path.Combine(launcherDir, "defaultConfig");

        Directory.CreateDirectory(launcherDir);
        Directory.CreateDirectory(defaultConfigDir);

        var caddyPhpTemplatePath = Path.Combine(launcherDir, "caddy_php_port.template.json");
        var serverConfigTemplatePath = Path.Combine(
            defaultConfigDir,
            "server_config.template.json"
        );
        var apiServerSettingsTemplatePath = Path.Combine(
            defaultConfigDir,
            "ApiServer_settings.template.json"
        );

        try
        {
            var generatedFiles = WriteInstallPortConfigsFromTemplates(
                caddyPhpTemplatePath,
                serverConfigTemplatePath,
                apiServerSettingsTemplatePath,
                ports
            );

            AppendLog($"[{DateTime.Now:HH:mm:ss}] Install-Konfiguration vorbereitet:");
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {generatedFiles.CaddyPhpPath}");
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {generatedFiles.ServerConfigPath}");
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {generatedFiles.ApiServerSettingsPath}");
            AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] Ports: Caddy={ports.Caddy} PHP={ports.Php} API={ports.BridgeApi} Python={ports.Python}"
            );

            await RunScriptAdminAsync("launcher\\install.bat", "/nopause", timeoutSeconds: 600);
        }
        catch (Exception ex)
        {
            AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] Full Install Fehler beim Schreiben der Port-Konfiguration: {ex.Message}"
            );
            MessageBox.Show(
                this,
                ex.Message,
                "Install-Konfiguration konnte nicht vorbereitet werden",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private static GeneratedInstallFiles WriteInstallPortConfigsFromTemplates(
        string caddyPhpTemplatePath,
        string serverConfigTemplatePath,
        string apiServerSettingsTemplatePath,
        Ports ports
    )
    {
        var caddyPhpPath = CreateFromTemplate(
            caddyPhpTemplatePath,
            ("__CADDY_PORT__", ports.Caddy),
            ("__PHP_PORT__", ports.Php)
        );

        var serverConfigPath = CreateFromTemplate(
            serverConfigTemplatePath,
            ("__BRIDGE_PORT__", ports.BridgeApi),
            ("__PY_PORT__", ports.Python)
        );

        var apiServerSettingsPath = CreateFromTemplate(
            apiServerSettingsTemplatePath,
            ("__BRIDGE_PORT__", ports.BridgeApi)
        );

        return new GeneratedInstallFiles(caddyPhpPath, serverConfigPath, apiServerSettingsPath);
    }

    private static string CreateFromTemplate(
        string templatePath,
        params (string Placeholder, int Value)[] replacements
    )
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Template-Datei nicht gefunden.", templatePath);

        var content = File.ReadAllText(templatePath, Encoding.UTF8);

        foreach (var (placeholder, value) in replacements)
        {
            content = ReplacePlaceholderRequired(
                content,
                placeholder,
                value.ToString(CultureInfo.InvariantCulture),
                templatePath
            );
        }

        var outputPath = GetGeneratedPathFromTemplate(templatePath);
        File.WriteAllText(
            outputPath,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        return outputPath;
    }

    private static string ReplacePlaceholderRequired(
        string content,
        string placeholder,
        string value,
        string templatePath
    )
    {
        if (!content.Contains(placeholder))
        {
            throw new InvalidOperationException(
                $"Platzhalter '{placeholder}' wurde in '{templatePath}' nicht gefunden."
            );
        }

        return content.Replace(placeholder, value);
    }

    private static string GetGeneratedPathFromTemplate(string templatePath)
    {
        var fileName = Path.GetFileName(templatePath);
        const string marker = ".template.";

        var idx = fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            throw new InvalidOperationException(
                $"Der Dateiname '{fileName}' enthält kein '.template.' und kann nicht in eine Zieldatei umgewandelt werden."
            );
        }

        var generatedFileName = fileName.Remove(idx, ".template".Length);
        return Path.Combine(Path.GetDirectoryName(templatePath)!, generatedFileName);
    }

    private sealed record GeneratedInstallFiles(
        string CaddyPhpPath,
        string ServerConfigPath,
        string ApiServerSettingsPath
    );

    // ----------------------------
    // Edit only Caddy/PHP (ELEVATED)
    // ----------------------------
    private async Task EditCaddyPhpPortsAsync()
    {
        var initial = LoadPortsForDialog();

        using var dlg = new PortDialog(_ui, initial, showApi: false, showPython: false);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        var p = dlg.Result;

        SavePortsForDialog(p);

        await RunScriptAdminAsync(
            "launcher\\edit_ports.bat",
            $"/caddy {p.Caddy} /php {p.Php} /nopause",
            timeoutSeconds: 30
        );
    }

    private void OpenStartupFolderWithInfo()
    {
        try
        {
            var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

            MessageBox.Show(
                this,
                _ui.T(
                    "msg.startupFolderInfo.body",
                    UiStrings.Vars(
                        (
                            "fallback",
                            "Bitte kopieren Sie die Verknüpfung 'NibuBox_Autostart.lnk' manuell in den geöffneten Windows-Autostart-Ordner.\n\nFalls die Verknüpfung noch nicht existiert, führen Sie zuerst 'Desktop-Verknüpfung erstellen' aus."
                        )
                    )
                ),
                _ui.T(
                    "msg.startupFolderInfo.title",
                    UiStrings.Vars(("fallback", "Autostart manuell einrichten"))
                ),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );

            AppendLog($"[{DateTime.Now:HH:mm:ss}] Öffne Autostart-Ordner: {startupDir}");

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = '"' + startupDir + '"',
                    UseShellExecute = true,
                }
            );
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [EX] Autostart-Ordner öffnen: {ex.Message}");

            MessageBox.Show(
                this,
                ex.Message,
                _ui.T(
                    "msg.startupFolderOpenFailed.title",
                    UiStrings.Vars(("fallback", "Autostart-Ordner konnte nicht geöffnet werden"))
                ),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    // ----------------------------
    // Models for checks
    // ----------------------------
    private sealed class FirewallCheck
    {
        public bool AllOk { get; set; }
        public List<FirewallPort> Ports { get; set; } = new();
    }

    private sealed class FirewallPort
    {
        public string Name { get; set; } = "";
        public int? Port { get; set; }
        public bool Freigegeben { get; set; }
        public string? Error { get; set; }
    }

    private sealed class TaskCheck
    {
        public bool Installed { get; set; }
        public bool Enabled { get; set; }
        public bool Running { get; set; }
        public string TaskName { get; set; } = "";
    }

    private async Task WindowsTweaksWithWarningAsync()
    {
        var ok = ConfirmFromUi(
            "confirm.winTweaks.title",
            "confirm.winTweaks.msg",
            UiStrings.Vars(
                (
                    "fallback",
                    @"Hinweis: Diese Aktion ändert Windows-Einstellungen (Registry/Policies). Administratorrechte sind erforderlich. Manche Änderungen werden erst nach Ab-/Anmelden oder Neustart wirksam.

Es werden folgende Einstellungen gesetzt:

Sperrbildschirm deaktivieren (Policy: NoLockScreen=1)
Microsoft Consumer Features deaktivieren (Policy: DisableWindowsConsumerFeatures=1)
Toast-Benachrichtigungen für aktuellen Benutzer deaktivieren (ToastEnabled=0 unter HKCU)
Toast-Benachrichtigungen per Policy sperren (systemweit) (NoToastApplicationNotification=1)

Nur ausführen, wenn du diese Anpassungen möchtest."
                )
            )
        );

        if (!ok)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Windows Tweaks abgebrochen.");
            return;
        }

        await RunScriptAdminAsync("launcher\\windows_tweaks.bat", "/nopause", timeoutSeconds: 180);
    }
}
