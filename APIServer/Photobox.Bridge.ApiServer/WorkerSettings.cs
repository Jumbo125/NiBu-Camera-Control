// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: WorkerSettings.cs
// Zweck: Definiert die Konfiguration für Worker-Pfad, Startargumente und Autostart-Verhalten.
// Projekt: Photobox CameraBridge ApiServer
namespace Photobox.Bridge.ApiServer;
public sealed class WorkerSettings
{
    public string? ExePath { get; set; }
    public string? Args { get; set; }

    public bool AutoStartOnBoot { get; set; } = true;
    public bool AutoStartWhenUnreachable { get; set; } = true;

    public int StartCooldownMs { get; set; } = 8000; // min Abstand zwischen Startversuchen
    public int FailThreshold { get; set; } = 3;      // wie viele Checks offline, bevor Autostart versucht wird
}
