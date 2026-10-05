// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: StreamState.cs
// Zweck: Hält den aktuellen Zustand des MJPEG-Streams thread-sicher vor.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - Anzahl verbundener Clients verfolgen
// - Status für aktives Frame-Senden bereitstellen
namespace Photobox.Bridge.ApiServer;

public sealed class StreamState
{
    private int _clients;
    private int _sending;

    public int Clients => Volatile.Read(ref _clients);
    public bool SendingFrames => Volatile.Read(ref _sending) != 0;

    public void ClientConnected() => Interlocked.Increment(ref _clients);
    public void ClientDisconnected() => Interlocked.Decrement(ref _clients);

    public void SetSending(bool sending) => Volatile.Write(ref _sending, sending ? 1 : 0);
}
