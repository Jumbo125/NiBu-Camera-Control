// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: BridgeSettings.cs
// Zweck: Definiert die Konfigurationswerte für HTTP-Bindung, MJPEG-Pfad, Auth und Pipe-Namen.
// Projekt: Photobox CameraBridge ApiServer
namespace Photobox.Bridge.ApiServer;

public sealed class BridgeSettings
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8052;
    public string MjpegPath { get; set; } = "/live.mjpg";
    public string AuthKey { get; set; } = "";
    public string PipeName { get; set; } = "PhotoboxBridge.Cmd";
}
