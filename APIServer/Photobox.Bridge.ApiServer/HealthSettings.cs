// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: HealthSettings.cs
// Zweck: Definiert Konfigurationswerte für Health-Checks und Timeouts des Workers.
// Projekt: Photobox CameraBridge ApiServer
namespace Photobox.Bridge.ApiServer;

public sealed class HealthSettings
{
    public int IntervalMs { get; set; } = 2000;
    public int TimeoutMs { get; set; } = 800;
}
