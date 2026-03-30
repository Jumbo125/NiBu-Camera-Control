// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: Commands.cs
// Zweck: Enthält die zentralen IPC-Command-Namen für Worker- und API-Kommunikation.
// Projekt: Photobox CameraBridge Shared
//
// Aufgaben:
// - Command-Strings zentral definieren
// - einheitliche Namen für Status, Kamera, LiveView, Settings, Capture und Watchdog bereitstellen
// - typsichere Wiederverwendung statt verteilter String-Literale ermöglichen
namespace Photobox.Bridge.Shared;

public static class Commands
{
    public const string StatusGet = "status.get";
    public const string CamerasList = "cameras.list";
    public const string CameraSelect = "camera.select";
    public const string CameraRefresh = "camera.refresh";

    public const string LiveViewStart = "liveview.start";
    public const string LiveViewStop = "liveview.stop";
    public const string LiveViewFpsGet = "liveview.fps.get";
    public const string LiveViewFpsSet = "liveview.fps.set";

    public const string SettingsGet = "settings.get";
    public const string SettingsSet = "settings.set";

    public const string Capture = "capture";

    public const string WatchdogGet = "watchdog.get";
    public const string WatchdogSet = "watchdog.set";

    public const string FrameWaitNext = "frame.wait_next";
}
