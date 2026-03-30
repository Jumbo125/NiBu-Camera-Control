// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: MjpegStreamer.cs
// Zweck: Streamt LiveView-Frames des Workers als MJPEG-HTTP-Stream an Clients.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - Frames per IPC vom Worker abrufen
// - JPEG-Daten dekodieren
// - multipart/x-mixed-replace Antworten schreiben
// - Stream-Status aktualisieren
using System.Text;
using Photobox.Bridge.Shared;

namespace Photobox.Bridge.ApiServer;

public static class MjpegStreamer
{
    private static readonly byte[] Boundary = Encoding.ASCII.GetBytes("--frame\r\n");
    private static readonly byte[] CrLf = Encoding.ASCII.GetBytes("\r\n");

    public static async Task StreamAsync(HttpContext ctx, BridgePipeClient ipc, StreamState streamState, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers.Expires = "0";
        ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

        streamState.ClientConnected();
        try
        {
            long lastSeq = -1;

            while (!ct.IsCancellationRequested)
            {
                FrameWaitResponseDto? frame;
                try
                {
                    frame = await ipc.CallAsync<FrameWaitResponseDto>(
                        Commands.FrameWaitNext,
                        new FrameWaitRequestDto { LastSeq = lastSeq, TimeoutMs = 1500 },
                        ct);
                }
                catch (BridgeException)
                {
                    // Worker down or handler failed -> backoff a bit
                    await Task.Delay(250, ct);
                    continue;
                }

                if (frame == null || frame.Seq == lastSeq || string.IsNullOrWhiteSpace(frame.JpegBase64))
                {
                    continue;
                }

                byte[] jpg;
                try { jpg = Convert.FromBase64String(frame.JpegBase64); }
                catch { continue; }

                streamState.SetSending(true);

                await ctx.Response.Body.WriteAsync(Boundary, ct);

                var header = $"Content-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n";
                await ctx.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(header), ct);

                await ctx.Response.Body.WriteAsync(jpg, ct);
                await ctx.Response.Body.WriteAsync(CrLf, ct);
                await ctx.Response.Body.FlushAsync(ct);

                lastSeq = frame.Seq;
            }
        }
        finally
        {
            streamState.SetSending(false);
            streamState.ClientDisconnected();
        }
    }
}
