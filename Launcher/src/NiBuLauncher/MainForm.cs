// ======================= MainForm.cs =======================
// Updated version with:
// - UI strings loaded from launcher\ui\strings.{lang}.json
// - Auto-discover languages by scanning launcher\ui for strings.*.json
// - Language dropdown (subtle) next to Refresh button
// - Placeholder support in logs
// - Saves selected language to launcher\ui\lang.txt
// - Layout fix: DPI scaling + maximized start

using NiBuLauncher.Services;
using System.Diagnostics;

namespace NiBuLauncher;

public sealed class MainForm : Form
{
    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly DataGridView _statusGrid = new()
    {
        Dock = DockStyle.Top,
        Height = 140,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    private readonly ScriptRunner _runner;
    private readonly StatusChecker _status;

    private List<UiStrings.LangInfo> _availableLangs = new();
    private string _lang;
    private UiStrings _ui;

    private StatusSnapshot? _lastSnapshot;
    private readonly Dictionary<string, DateTime> _lastFailLog = new();
    private readonly SemaphoreSlim _statusLock = new(1, 1);

    private AdvancedForm? _advanced;

    // UI refs
    private GroupBox? _grpControl;
    private Button? _btnStart, _btnStop, _btnRestart, _btnOpenApp, _btnOpenLogs, _btnAdvanced;
    private Label? _lblStatus, _lblConsole;
    private Button? _btnRefresh;
    private ComboBox? _cmbLang;

    // Tray refs
    private ToolStripMenuItem? _trayOpen, _trayStart, _trayRestart, _trayStop, _trayOpenApp, _trayLogs, _trayExit;

    public MainForm()
    {
        _availableLangs = UiStrings.DiscoverLanguages(AppContext.BaseDirectory);
        _lang = UiStrings.LoadLanguage(AppContext.BaseDirectory, _availableLangs);
        _ui = UiStrings.Load(AppContext.BaseDirectory, _lang);

        Text = _ui.T("window.main.title");
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        WindowState = FormWindowState.Maximized;

        try
        {
            var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ico != null)
            {
                Icon = ico;
                _tray.Icon = ico;
            }
        }
        catch { }

        _runner = new ScriptRunner(AppContext.BaseDirectory, AppendLog);
        _status = new StatusChecker(AppContext.BaseDirectory);

        BuildUi();
        BuildTray();
        ApplyLanguageToUi();

        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] UI gestartet. BaseDir: {AppContext.BaseDirectory}");
            _timer.Start();
            await RefreshStatusAsync();
        };

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };
    }

    private void BuildUi()
    {
        _statusGrid.Columns.Clear();
        _statusGrid.Columns.Add("svc", _ui.T("grid.service", UiStrings.Vars(("fallback", "Service"))));
        _statusGrid.Columns.Add("proc", _ui.T("grid.process", UiStrings.Vars(("fallback", "Process"))));
        _statusGrid.Columns.Add("health", _ui.T("grid.health", UiStrings.Vars(("fallback", "Health"))));
        _statusGrid.Columns[0].FillWeight = 35;
        _statusGrid.Columns[1].FillWeight = 32;
        _statusGrid.Columns[2].FillWeight = 33;

        _statusGrid.Rows.Clear();
        _statusGrid.Rows.Add("Caddy", "?", "?");
        _statusGrid.Rows.Add("PHP", "?", "?");
        _statusGrid.Rows.Add("Bridge API", "?", "?");
        _statusGrid.Rows.Add("Python", "?", "?");

        Button Btn(out Button? field, Func<Task> onClick)
        {
            var b = new Button
            {
                Width = 240,
                Height = 36,
                Margin = new Padding(0, 0, 0, 10),
            };
            field = b;

            b.Click += async (_, _) =>
            {
                b.Enabled = false;
                try { await onClick(); }
                catch (Exception ex) { AppendLog("[EX] " + ex); }
                finally { b.Enabled = true; }
            };
            return b;
        }

        FlowLayoutPanel VerticalButtons() => new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10),
        };

        GroupBox Group(out GroupBox? field, Control inner) =>
            field = new GroupBox
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(10),
                Controls = { inner },
            };

        var leftButtons = VerticalButtons();
        leftButtons.Controls.Add(Btn(out _btnStart, () => RunScriptAsync("launcher\\start.bat", args: "/nopause", timeoutSeconds: 90)));
        leftButtons.Controls.Add(Btn(out _btnStop, () => RunStopAsync()));
        leftButtons.Controls.Add(Btn(out _btnRestart, () => RunRestartAsync()));
        leftButtons.Controls.Add(Btn(out _btnOpenApp, () => RunScriptAsync("launcher\\open_app.bat")));
        leftButtons.Controls.Add(Btn(out _btnOpenLogs, () => OpenLogsAsync()));
        leftButtons.Controls.Add(Btn(out _btnAdvanced, () => OpenAdvancedAsync()));

        _grpControl = Group(out _grpControl, leftButtons);

        var left = new Panel { Dock = DockStyle.Left, Width = 300 };
        left.Controls.Add(_grpControl);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

        // Status Header: Label + language dropdown + refresh
        var statusHeader = new Panel { Dock = DockStyle.Top, Height = 30 };

        _lblStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

        _btnRefresh = new Button { Dock = DockStyle.Right, Width = 90, Height = 26 };
        _btnRefresh.Click += async (_, _) => await ManualRefreshAsync();

        _cmbLang = new ComboBox
        {
            Dock = DockStyle.Right,
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
        };
        _cmbLang.SelectedIndexChanged += (_, _) => OnLanguageChanged();

        statusHeader.Controls.Add(_btnRefresh);
        statusHeader.Controls.Add(_cmbLang);
        statusHeader.Controls.Add(_lblStatus);

        _lblConsole = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 10, 10, 0) };

        right.Controls.Add(_logBox);
        right.Controls.Add(_lblConsole);
        right.Controls.Add(_statusGrid);
        right.Controls.Add(statusHeader);

        Controls.Add(right);
        Controls.Add(left);
    }

    private void BuildTray()
    {
        _tray.Text = "NiBu Photobooth Launcher";
        _tray.Visible = true;

        var menu = new ContextMenuStrip();

        _trayOpen = new ToolStripMenuItem();
        _trayOpen.Click += (_, _) => ShowFromTray();

        _trayStart = new ToolStripMenuItem();
        _trayStart.Click += async (_, _) => await RunScriptAsync("launcher\\start.bat", args: "/nopause", timeoutSeconds: 90);

        _trayRestart = new ToolStripMenuItem();
        _trayRestart.Click += async (_, _) => await RunRestartAsync();

        _trayStop = new ToolStripMenuItem();
        _trayStop.Click += async (_, _) => await RunStopAsync();

        _trayOpenApp = new ToolStripMenuItem();
        _trayOpenApp.Click += async (_, _) => await RunScriptAsync("launcher\\open_app.bat");

        _trayLogs = new ToolStripMenuItem();
        _trayLogs.Click += async (_, _) => await OpenLogsAsync();

        _trayExit = new ToolStripMenuItem();
        _trayExit.Click += (_, _) => ExitApp();

        menu.Items.Add(_trayOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayStart);
        menu.Items.Add(_trayRestart);
        menu.Items.Add(_trayStop);
        menu.Items.Add(_trayOpenApp);
        menu.Items.Add(_trayLogs);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayExit);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private async Task ManualRefreshAsync()
    {
        if (_btnRefresh is null || !_btnRefresh.Enabled) return;

        _btnRefresh.Enabled = false;
        var oldText = _btnRefresh.Text;
        _btnRefresh.Text = _ui.T("btn.refreshing");

        AppendLog(_ui.T("log.manualRefreshStart", UiStrings.Vars(("time", DateTime.Now.ToString("HH:mm:ss")))));

        _timer.Stop();
        try
        {
            await RefreshStatusAsync();

            string Row(int i) => $"{_statusGrid.Rows[i].Cells[0].Value}: {_statusGrid.Rows[i].Cells[1].Value}/{_statusGrid.Rows[i].Cells[2].Value}";
            var summary = $"{Row(0)} | {Row(1)} | {Row(2)} | {Row(3)}";

            AppendLog(_ui.T("log.manualRefreshDone", UiStrings.Vars(
                ("time", DateTime.Now.ToString("HH:mm:ss")),
                ("summary", summary)
            )));
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Manual Refresh: FEHLER -> {ex.Message}");
        }
        finally
        {
            _timer.Start();
            _btnRefresh.Text = oldText;
            _btnRefresh.Enabled = true;
        }
    }

    private void LoadLanguagesIntoDropdown()
    {
        if (_cmbLang == null) return;

        _availableLangs = UiStrings.DiscoverLanguages(AppContext.BaseDirectory);
        _cmbLang.Items.Clear();
        foreach (var li in _availableLangs)
            _cmbLang.Items.Add(li);

        // select current
        for (int i = 0; i < _cmbLang.Items.Count; i++)
        {
            if (_cmbLang.Items[i] is UiStrings.LangInfo it &&
                it.Code.Equals(_lang, StringComparison.OrdinalIgnoreCase))
            {
                _cmbLang.SelectedIndex = i;
                return;
            }
        }

        if (_cmbLang.Items.Count > 0)
            _cmbLang.SelectedIndex = 0;
    }

    private void OnLanguageChanged()
    {
        if (_cmbLang?.SelectedItem is not UiStrings.LangInfo li) return;

        var newLang = li.Code.Trim().ToLowerInvariant();
        if (string.Equals(newLang, _lang, StringComparison.OrdinalIgnoreCase))
            return;

        _lang = newLang;
        UiStrings.SaveLanguage(AppContext.BaseDirectory, _lang);
        _ui = UiStrings.Load(AppContext.BaseDirectory, _lang);

        ApplyLanguageToUi();

        if (_advanced != null && !_advanced.IsDisposed)
            _advanced.SetLanguage(_lang);
    }

    private void ApplyLanguageToUi()
    {
        Text = _ui.T("window.main.title");

        if (_grpControl != null) _grpControl.Text = _ui.T("group.control");

        if (_btnStart != null) _btnStart.Text = _ui.T("btn.start");
        if (_btnStop != null) _btnStop.Text = _ui.T("btn.stop");
        if (_btnRestart != null) _btnRestart.Text = _ui.T("btn.restart");
        if (_btnOpenApp != null) _btnOpenApp.Text = _ui.T("btn.openApp");
        if (_btnOpenLogs != null) _btnOpenLogs.Text = _ui.T("btn.openLogs");
        if (_btnAdvanced != null) _btnAdvanced.Text = _ui.T("btn.advanced");

        if (_lblStatus != null) _lblStatus.Text = _ui.T("label.status");
        if (_lblConsole != null) _lblConsole.Text = _ui.T("label.console");

        if (_btnRefresh != null) _btnRefresh.Text = _ui.T("btn.refresh");

        // Tray
        if (_trayOpen != null) _trayOpen.Text = _ui.T("tray.open");
        if (_trayStart != null) _trayStart.Text = _ui.T("btn.start");
        if (_trayRestart != null) _trayRestart.Text = _ui.T("btn.restart");
        if (_trayStop != null) _trayStop.Text = _ui.T("btn.stop");
        if (_trayOpenApp != null) _trayOpenApp.Text = _ui.T("btn.openApp");
        if (_trayLogs != null) _trayLogs.Text = _ui.T("tray.logs");
        if (_trayExit != null) _trayExit.Text = _ui.T("tray.exit");

        // (re)load languages into dropdown with meta names
        LoadLanguagesIntoDropdown();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _tray.BalloonTipTitle = "NiBu Launcher";
        _tray.BalloonTipText = "Läuft im Tray (Doppelklick zum Öffnen).";
        _tray.ShowBalloonTip(1000);
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLog), line);
            return;
        }
        _logBox.AppendText(line + Environment.NewLine);
    }

    private async Task OpenLogsAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);

        AppendLog(_ui.T("log.openLogs", UiStrings.Vars(
            ("time", DateTime.Now.ToString("HH:mm:ss")),
            ("dir", dir)
        )));

        await Task.Yield();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = '"' + dir + '"',
            UseShellExecute = true,
        });
    }

    private async Task RunScriptAsync(
        string relativePath,
        string? args = null,
        int? timeoutSeconds = 15,
        bool detached = false)
    {
        var fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ACTION: {relativePath} {args}".Trim());

        if (detached)
            await _runner.RunBatchDetachedAsync(fullPath, args ?? "");
        else
            await _runner.RunBatchAsync(fullPath, args ?? "", timeoutSeconds);

        await RefreshStatusAsync();
    }

    private async Task RunStopAsync()
    {
        await PauseWatchdogAsync(TimeSpan.FromMinutes(10), "GUI Stop");
        await RunScriptAsync("launcher\\stop.bat", args: "/nopause");
    }

    private string WatchdogPauseFile => Path.Combine(AppContext.BaseDirectory, "launcher", "watchdog_pause.json");

    private async Task PauseWatchdogAsync(TimeSpan duration, string reason)
    {
        try
        {
            var dir = Path.GetDirectoryName(WatchdogPauseFile)!;
            Directory.CreateDirectory(dir);
            var until = DateTimeOffset.UtcNow.Add(duration);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { untilUtc = until.ToString("o"), reason });
            await File.WriteAllTextAsync(WatchdogPauseFile, payload);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] WATCHDOG PAUSE: {duration.TotalMinutes:0.##} min | reason={reason} | untilUtc={until:O}");
        }
        catch (Exception ex)
        {
            AppendLog($"[WARN] WATCHDOG PAUSE konnte nicht gesetzt werden: {ex.Message}");
        }
    }

    private async Task RunRestartAsync()
    {
        await PauseWatchdogAsync(TimeSpan.FromMinutes(2), "GUI Restart");
        await RunScriptAsync("launcher\\stop.bat", args: "/nopause");
        await RunScriptAsync("launcher\\start.bat", args: "/nopause", timeoutSeconds: 90);
    }

    private Task OpenAdvancedAsync()
    {
        if (_advanced == null || _advanced.IsDisposed)
            _advanced = new AdvancedForm(AppContext.BaseDirectory);

        _advanced.SetLanguage(_lang);
        if (!_advanced.Visible)
            _advanced.Show();

        _advanced.WindowState = FormWindowState.Maximized;
        _advanced.BringToFront();
        _advanced.Activate();
        return Task.CompletedTask;
    }

    private async Task RefreshStatusAsync()
    {
        if (!await _statusLock.WaitAsync(0))
            return;

        try
        {
            var snapshot = await _status.GetSnapshotAsync();
            ApplyRow(0, snapshot.Caddy);
            ApplyRow(1, snapshot.Php);
            ApplyRow(2, snapshot.Bridge);
            ApplyRow(3, snapshot.Python);

            LogStatus(snapshot);
            _lastSnapshot = snapshot;
        }
        catch (Exception ex)
        {
            AppendLog("[STATUS-EX] " + ex.Message);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private void ApplyRow(int rowIndex, ServiceState state)
    {
        if (rowIndex < 0 || rowIndex >= _statusGrid.Rows.Count)
            return;

        var row = _statusGrid.Rows[rowIndex];
        row.Cells[1].Value = state.ProcessRunning ? "RUNNING" : "STOPPED";
        row.Cells[2].Value = state.HealthOk ? "OK" : "FAIL";
        row.Cells[2].ToolTipText = state.Details ?? "";
        row.Cells[1].Style.ForeColor = state.ProcessRunning ? Color.DarkGreen : Color.DarkRed;
        row.Cells[2].Style.ForeColor = state.HealthOk ? Color.DarkGreen : Color.DarkRed;
    }

    private void LogStatus(StatusSnapshot snapshot)
    {
        LogOne("Caddy", _lastSnapshot?.Caddy, snapshot.Caddy);
        LogOne("PHP", _lastSnapshot?.Php, snapshot.Php);
        LogOne("Bridge API", _lastSnapshot?.Bridge, snapshot.Bridge);
        LogOne("Python", _lastSnapshot?.Python, snapshot.Python);
    }

    private void LogOne(string name, ServiceState? prev, ServiceState cur)
    {
        var now = DateTime.Now;

        if (prev is null || prev.Value.ProcessRunning != cur.ProcessRunning || prev.Value.HealthOk != cur.HealthOk)
        {
            AppendLog($"[{now:HH:mm:ss}] STATUS {name}: PROC={(cur.ProcessRunning ? "RUNNING" : "STOPPED")}, HEALTH={(cur.HealthOk ? "OK" : "FAIL")}");
            if (!cur.HealthOk && !string.IsNullOrWhiteSpace(cur.Details))
                AppendLog($"[{now:HH:mm:ss}]   {cur.Details}");
            return;
        }

        if (!cur.HealthOk)
        {
            if (!_lastFailLog.TryGetValue(name, out var last) || (now - last).TotalSeconds >= 10)
            {
                _lastFailLog[name] = now;
                AppendLog($"[{now:HH:mm:ss}] STATUS {name}: HEALTH=FAIL");
                if (!string.IsNullOrWhiteSpace(cur.Details))
                    AppendLog($"[{now:HH:mm:ss}]   {cur.Details}");
            }
        }
    }
}
