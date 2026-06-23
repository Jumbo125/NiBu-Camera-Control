// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: WorkerHealthState.cs
// Zweck: Speichert den aktuellen Health-Zustand des Workers thread-sicher.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - letzten erfolgreichen Kontakt festhalten
// - Fehlerstatus und Fehlermeldung speichern
// - Snapshot für Statusabfragen bereitstellen
using System;
using Photobox.Bridge.Shared;

namespace Photobox.Bridge.ApiServer;

public sealed class WorkerHealthState
{
    private readonly object _lock = new();

    public bool Reachable { get; private set; }
    public DateTime? LastOkUtc { get; private set; }
    public DateTime? LastFailUtc { get; private set; }
    public string? LastError { get; private set; }

    private WorkerStatusDto? _lastStatus;

    public WorkerStatusDto? GetLastStatus()
    {
        lock (_lock) return _lastStatus;
    }

    public void SetOk(WorkerStatusDto? status = null)
    {
        lock (_lock)
        {
            Reachable = true;
            LastOkUtc = DateTime.UtcNow;
            LastError = null;
            if (status != null) _lastStatus = status;
        }
    }

   public void SetFail(Exception ex)
{
    lock (_lock)
    {
        Reachable = false;

        // Timeout/CancelAfter -> schöner Text
        if (ex is OperationCanceledException)
            LastError = "timeout/canceled";
        else
            LastError = ex.Message;

        // optional:
        // LastFailUtc = DateTime.UtcNow;
    }
}


    public (bool Reachable, DateTime? LastOkUtc, DateTime? LastFailUtc, string? LastError) Snapshot()
    {
        lock (_lock)
            return (Reachable, LastOkUtc, LastFailUtc, LastError);
    }
}
