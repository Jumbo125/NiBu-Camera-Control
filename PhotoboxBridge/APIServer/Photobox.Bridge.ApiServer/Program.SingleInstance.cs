// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: Program.SingleInstance.cs
// Zweck: Implementiert Single-Instance- und Replace-Mechanik für den API-Server.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - benannte Mutex-/Event-Objekte anlegen oder öffnen
// - bestehende Instanzen erkennen
// - Replace-Signal an laufende Instanzen senden
// - geordnetes Beenden und Neustarten unterstützen
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Photobox.Bridge.ApiServer;

public static partial class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static string ToLocal(string name) => name.Replace(@"Global\", @"Local\");

    private static MutexSecurity BuildMutexSecurityForAuthenticatedUsers()
    {
        var sec = new MutexSecurity();
        var au = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        sec.AddAccessRule(
            new MutexAccessRule(
                au,
                MutexRights.Synchronize | MutexRights.Modify,
                AccessControlType.Allow
            )
        );
        return sec;
    }

    private static EventWaitHandleSecurity BuildEventSecurityForAuthenticatedUsers()
    {
        var sec = new EventWaitHandleSecurity();
        var au = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        sec.AddAccessRule(
            new EventWaitHandleAccessRule(
                au,
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                AccessControlType.Allow
            )
        );
        return sec;
    }

    private static bool TryCreateOrOpenMutex(string name, out Mutex? mutex)
    {
        mutex = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                mutex = MutexAcl.Create(
                    false,
                    name,
                    out _,
                    BuildMutexSecurityForAuthenticatedUsers()
                );
                return true;
            }

            mutex = new Mutex(false, name, out _);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                mutex = Mutex.OpenExisting(name);
                return true;
            }
            catch
            {
                mutex = null;
                return false;
            }
        }
        catch
        {
            mutex = null;
            return false;
        }
    }

    private static bool TryCreateOrOpenEvent(string name, out EventWaitHandle? ev)
    {
        ev = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ev = EventWaitHandleAcl.Create(
                    false,
                    EventResetMode.AutoReset,
                    name,
                    out _,
                    BuildEventSecurityForAuthenticatedUsers()
                );
                return true;
            }

            ev = new EventWaitHandle(false, EventResetMode.AutoReset, name, out _);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                ev = EventWaitHandle.OpenExisting(name);
                return true;
            }
            catch
            {
                ev = null;
                return false;
            }
        }
        catch
        {
            ev = null;
            return false;
        }
    }

    private static bool SetupNamedObjects(out Mutex? mutex, out EventWaitHandle? ev)
    {
        mutex = null;
        ev = null;

        Mutex? m1 = null;
        EventWaitHandle? e1 = null;

        var gMutex = OneInstanceMutexName;
        var gEvent = ReplaceEventName;

        if (TryCreateOrOpenMutex(gMutex, out m1) && TryCreateOrOpenEvent(gEvent, out e1))
        {
            mutex = m1;
            ev = e1;
            _mutexNameUsed = gMutex;
            _eventNameUsed = gEvent;
            return true;
        }

        try
        {
            m1?.Dispose();
        }
        catch { }
        try
        {
            e1?.Dispose();
        }
        catch { }

        Mutex? m2 = null;
        EventWaitHandle? e2 = null;

        var lMutex = ToLocal(OneInstanceMutexName);
        var lEvent = ToLocal(ReplaceEventName);

        if (TryCreateOrOpenMutex(lMutex, out m2) && TryCreateOrOpenEvent(lEvent, out e2))
        {
            mutex = m2;
            ev = e2;
            _mutexNameUsed = lMutex;
            _eventNameUsed = lEvent;
            return true;
        }

        try
        {
            m2?.Dispose();
        }
        catch { }
        try
        {
            e2?.Dispose();
        }
        catch { }

        return false;
    }

    private static void InitializeSingleInstanceOrReplace(string[] args)
    {
        var enabled =
            GetBoolArg(args, "--one-instance")
            ?? GetBoolArg(args, "--one_instance")
            ?? GetBoolArg(args, "--on-instance")
            ?? true;

        if (!enabled)
        {
            try
            {
                Console.WriteLine("[ApiServer] one-instance disabled via flag.");
            }
            catch { }
            return;
        }

        if (!SetupNamedObjects(out _oneInstanceMutex, out _replaceEvent))
        {
            try
            {
                Console.WriteLine("[ApiServer] one-instance setup failed -> continuing without replace mode.");
            }
            catch { }
            try
            {
                Log.Warning("one-instance setup failed -> continuing without replace mode.");
            }
            catch { }
            return;
        }

        try
        {
            _ownsMutex = _oneInstanceMutex!.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        catch
        {
            _ownsMutex = false;
        }

        try
        {
            Console.WriteLine(
                $"[ApiServer] one-instance enabled. ownsMutex={_ownsMutex} mutex={_mutexNameUsed} event={_eventNameUsed}"
            );
        }
        catch { }

        if (_ownsMutex)
        {
            StartReplaceListener();
            return;
        }

        try
        {
            Console.WriteLine("[ApiServer] Existing instance found. Requesting replace ...");
        }
        catch { }

        try
        {
            _replaceEvent?.Set();
        }
        catch { }

        var sw = Stopwatch.StartNew();
        var acquired = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                acquired = _oneInstanceMutex!.WaitOne(500);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            catch
            {
                acquired = false;
            }

            if (acquired)
            {
                _ownsMutex = true;
                break;
            }
        }

        if (!_ownsMutex)
        {
            try
            {
                Console.Error.WriteLine(
                    "[ApiServer] Replace timed out. Could not acquire one-instance mutex."
                );
            }
            catch { }
            Environment.Exit(2);
            return;
        }

        try
        {
            Console.WriteLine("[ApiServer] Replace successful. Starting new instance.");
        }
        catch { }

        StartReplaceListener();
    }

    private static void StartReplaceListener()
    {
        if (_replaceEvent == null)
            return;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    _replaceEvent.WaitOne();
                    await ExitProcessAsync("replaced_by_new_instance", 0);
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(250);
                    }
                    catch { }
                }
            }
        });
    }

    private static async Task ExitProcessAsync(string reason, int exitCode)
    {
        if (Interlocked.Exchange(ref _exitInProgress, 1) == 1)
            return;

        try
        {
            try
            {
                Console.WriteLine($"[ApiServer] Exiting. Reason={reason}");
            }
            catch { }
            try
            {
                Log.Information("ApiServer exiting. Reason={Reason}", reason);
            }
            catch { }

            try
            {
                await StopServerAsync();
            }
            catch { }

            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch { }

            try
            {
                if (_ownsMutex && _oneInstanceMutex != null)
                {
                    _oneInstanceMutex.ReleaseMutex();
                    _ownsMutex = false;
                }
            }
            catch { }

            try
            {
                _oneInstanceMutex?.Dispose();
            }
            catch { }
            try
            {
                _replaceEvent?.Dispose();
            }
            catch { }

            try
            {
                if (_uiMode)
                    Application.Exit();
            }
            catch { }
        }
        finally
        {
            Environment.Exit(exitCode);
        }
    }

    private static bool? GetBoolArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 >= args.Length)
                return true;

            var v = (args[i + 1] ?? "").Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1")
                return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0")
                return false;
            if (v.StartsWith("-", StringComparison.Ordinal))
                return true;
            return null;
        }

        return null;
    }

    private static string? NormalizePipeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        name = name.Trim();

        const string prefix = @"\\.\pipe\";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(prefix.Length);

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
