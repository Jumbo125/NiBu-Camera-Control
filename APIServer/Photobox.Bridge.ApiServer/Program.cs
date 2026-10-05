// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: Program.cs
// Zweck: Einstiegspunkt des API-Servers sowie Grundinitialisierung für UI-, Tray- und Konsolenbetrieb.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - Startparameter übernehmen
// - Single-Instance-Logik initialisieren
// - Konsolenfenster konfigurieren
// - UI-/Headless-Modus wählen
// - Serverstart anstoßen
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Serilog;

namespace Photobox.Bridge.ApiServer;

public static partial class Program
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly object Gate = new();
    private static WebApplication? _app;
    private static NotifyIcon? _tray;

    private static bool _uiMode;
    private static string[] _args = Array.Empty<string>();

    private static Mutex? _oneInstanceMutex;
    private static EventWaitHandle? _replaceEvent;
    private static bool _ownsMutex;
    private static int _exitInProgress;

    private static string _mutexNameUsed = "";
    private static string _eventNameUsed = "";

    private const string OneInstanceMutexName = @"Global\Photobox.CameraBridge.ApiServer.Mutex";
    private const string ReplaceEventName = @"Global\Photobox.CameraBridge.ApiServer.Replace";

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [STAThread]
    public static void Main(string[] args)
    {
        _args = args ?? Array.Empty<string>();

        InitializeSingleInstanceOrReplace(_args);

        var showConsole = GetBoolArg(_args, "--window_console") ?? true;
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero)
            ShowWindow(h, showConsole ? SW_SHOW : SW_HIDE);

        var sessionId = Process.GetCurrentProcess().SessionId;
        var trayEnabled = GetBoolArg(_args, "--tray") ?? true;
        var headless = GetBoolArg(_args, "--headless") ?? false;

        _uiMode = (sessionId != 0) && trayEnabled && !headless;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject?.ToString() ?? "Unhandled exception";
            try
            {
                if (_uiMode)
                    MessageBox.Show(msg, "ApiServer crashed");
                else
                    Console.Error.WriteLine(msg);
            }
            catch { }

            try
            {
                Log.Error("{Msg}", msg);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var msg = e.Exception?.ToString() ?? "Unobserved task exception";
            try
            {
                if (_uiMode)
                    MessageBox.Show(msg, "ApiServer crashed");
                else
                    Console.Error.WriteLine(msg);
            }
            catch { }

            try
            {
                Log.Error(e.Exception, "Unobserved task exception");
            }
            catch { }
        };

        if (_uiMode)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetupTray();
            _ = StartServerAsync();

            Application.Run();
            return;
        }

        _ = StartServerAsync();

        try
        {
            Console.CancelKeyPress += async (_, e) =>
            {
                e.Cancel = true;
                await ExitProcessAsync("ctrl_c", 0);
            };
        }
        catch { }

        Thread.Sleep(Timeout.Infinite);
    }
}
