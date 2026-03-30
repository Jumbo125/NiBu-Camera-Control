// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: BridgeApiEndpointMappings.cs
// Zweck: Registriert die HTTP-Endpunkte der Bridge-API und verbindet sie mit Worker, Stream und Health-Status.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - API-Routen für Status, Kamera, LiveView, Settings, Capture und Watchdog bereitstellen
// - HTTP-Requests in IPC-Aufrufe an den Worker übersetzen
// - Responses, Streams und Fehler als HTTP-Ergebnisse ausgeben

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Photobox.Bridge.Shared;
using Serilog;

namespace Photobox.Bridge.ApiServer;

internal static class BridgeApiEndpointMappings
{
    internal static void MapBridgeApiEndpoints(
        this WebApplication app,
        BridgeSettings settings,
        DateTime startedUtc)
    {
        app.MapGet("/", () => Results.Text($"OK. MJPEG: {settings.MjpegPath}\n", "text/plain"));

        app.MapGet(
            settings.MjpegPath,
            async (HttpContext ctx, BridgePipeClient ipc, StreamState stream) =>
            {
                await MjpegStreamer.StreamAsync(ctx, ipc, stream, ctx.RequestAborted);
            }
        )
            .WithTags("LiveView");

        app.MapGet(
            "/api/status",
            async (BridgePipeClient ipc, StreamState stream, WorkerHealthState health) =>
            {
                WorkerStatusDto? w = null;
                try
                {
                    w = await ipc.CallAsync<WorkerStatusDto>(
                        Commands.StatusGet,
                        null,
                        CancellationToken.None
                    );
                }
                catch
                {
                }

                var now = DateTime.UtcNow;
                var hs = health.Snapshot();

                var dto = new StatusDto
                {
                    HttpRunning = true,
                    LiveViewRunning = w?.LiveViewRunning ?? false,
                    Selected = w?.Selected,
                    Manufacturer = w?.Manufacturer,
                    Model = w?.Model,
                    Serial = w?.Serial,

                    HttpRunningSinceUtc = startedUtc.ToString("o"),
                    HttpUptimeSeconds = (long)Math.Max(0, (now - startedUtc).TotalSeconds),

                    StreamRunning = true,
                    StreamClients = stream.Clients,
                    StreamSendingFrames = stream.SendingFrames,

                    FramesActive = (w?.FrameAgeMs ?? long.MaxValue) < 2500,
                    FramesTotal = w?.FramesTotal ?? 0,
                    FrameAgeMs = w?.FrameAgeMs,
                    LastFrameUtc = w?.LastFrameUtc,
                    Source = w?.Source,
                    WatchdogEnabled = w?.WatchdogEnabled,
                };

                return Results.Json(
                    new
                    {
                        dto.HttpRunning,
                        dto.LiveViewRunning,
                        dto.Selected,
                        dto.Manufacturer,
                        dto.Model,
                        dto.Serial,
                        dto.HttpRunningSinceUtc,
                        dto.HttpUptimeSeconds,
                        dto.StreamRunning,
                        dto.StreamClients,
                        dto.StreamSendingFrames,
                        dto.FramesActive,
                        dto.FramesTotal,
                        dto.FrameAgeMs,
                        dto.LastFrameUtc,
                        dto.Source,
                        dto.WatchdogEnabled,

                        workerReachable = hs.Reachable,
                        workerLastOkUtc = hs.LastOkUtc?.ToString("o"),
                        workerLastError = hs.LastError,
                    },
                    Program.JsonOpts
                );
            }
        );

        app.MapGet(
            "/api/worker/reachable",
            (WorkerHealthState health) =>
            {
                var hs = health.Snapshot();
                return Results.Json(
                    new
                    {
                        reachable = hs.Reachable,
                        lastOkUtc = hs.LastOkUtc?.ToString("o"),
                        lastError = hs.LastError,
                    },
                    Program.JsonOpts
                );
            }
        );

        app.MapGet(
            "/api/worker/ping",
            async (BridgePipeClient ipc) =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    _ = await ipc.CallAsync<WorkerStatusDto>(
                        Commands.StatusGet,
                        null,
                        CancellationToken.None
                    );
                    return Results.Json(new { ok = true, ms = sw.ElapsedMilliseconds }, Program.JsonOpts);
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new
                        {
                            ok = false,
                            ms = sw.ElapsedMilliseconds,
                            error = ex.Message,
                        },
                        Program.JsonOpts
                    );
                }
            }
        );

        app.MapPost(
            "/api/worker/restart",
            async (WorkerProcessManager pm) =>
            {
                var res = await pm.EnsureStartedAsync("api_restart", CancellationToken.None);
                return Results.Json(res, Program.JsonOpts);
            }
        );

        app.MapGet(
            "/api/cameras",
            async (BridgePipeClient ipc) =>
            {
                var cams = await ipc.CallAsync<List<CameraInfoDto>>(
                    Commands.CamerasList,
                    null,
                    CancellationToken.None
                );
                return Results.Json(cams ?? new List<CameraInfoDto>(), Program.JsonOpts);
            }
        )
            .WithTags("Camera")
            .Produces<List<CameraInfoDto>>(StatusCodes.Status200OK, "application/json");

        app.MapPost(
            "/api/select",
            async (string? serial, int? id, BridgePipeClient ipc) =>
            {
                var okDto = await ipc.CallAsync<OkDto>(
                    Commands.CameraSelect,
                    new SelectRequestDto
                    {
                        Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
                        Id = id,
                    },
                    CancellationToken.None
                );

                var ok = okDto?.Ok ?? false;
                return ok ? Results.Text("ok") : Results.NotFound("not found");
            }
        )
            .WithTags("Camera")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces(StatusCodes.Status404NotFound, contentType: "text/plain");

        app.MapPost(
            "/api/refresh",
            async (int? timeoutMs, BridgePipeClient ipc) =>
            {
                await ipc.CallRawAsync(
                    Commands.CameraRefresh,
                    new RefreshRequestDto { TimeoutMs = timeoutMs.GetValueOrDefault(4000) },
                    CancellationToken.None
                );
                return Results.Text("ok");
            }
        )
            .WithTags("Camera")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain");

        app.MapPost(
            "/api/liveview/start",
            async (BridgePipeClient ipc) =>
            {
                await ipc.CallRawAsync(Commands.LiveViewStart, null, CancellationToken.None);
                return Results.Text("ok");
            }
        )
            .WithTags("LiveView")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain");

        app.MapPost(
            "/api/liveview/stop",
            async (BridgePipeClient ipc) =>
            {
                await ipc.CallRawAsync(Commands.LiveViewStop, null, CancellationToken.None);
                return Results.Text("ok");
            }
        )
            .WithTags("LiveView")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain");

        app.MapGet(
            "/api/liveview/fps",
            async (BridgePipeClient ipc) =>
            {
                var fps = await ipc.CallAsync<LiveViewFpsDto>(
                    Commands.LiveViewFpsGet,
                    null,
                    CancellationToken.None
                );
                return Results.Json(fps ?? new LiveViewFpsDto { Fps = 0 }, Program.JsonOpts);
            }
        )
            .WithTags("LiveView")
            .Produces<LiveViewFpsDto>(StatusCodes.Status200OK, "application/json");

        app.MapPost(
            "/api/liveview/fps",
            async (
                [FromQuery(Name = "fps")] int? fps,
                [FromQuery(Name = "value")] int? value,
                [FromBody] LiveViewFpsDto? body,
                BridgePipeClient ipc
            ) =>
            {
                var effectiveFps = fps ?? value ?? body?.Fps ?? 0;

                if (effectiveFps <= 0)
                    return Results.BadRequest("missing/invalid fps");

                await ipc.CallRawAsync(
                    Commands.LiveViewFpsSet,
                    new LiveViewFpsDto { Fps = effectiveFps },
                    CancellationToken.None
                );
                return Results.Text("ok");
            }
        )
            .WithTags("LiveView")
            .Accepts<LiveViewFpsDto>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces(StatusCodes.Status400BadRequest, contentType: "text/plain");

        app.MapGet(
            "/api/settings",
            async (BridgePipeClient ipc) =>
            {
                var settingsDto = await ipc.CallAsync<CameraSettingsDto>(
                    Commands.SettingsGet,
                    null,
                    CancellationToken.None
                );
                return Results.Json(settingsDto, Program.JsonOpts);
            }
        )
            .WithTags("Settings")
            .Produces<CameraSettingsDto>(StatusCodes.Status200OK, "application/json");

        app.MapPost(
            "/api/settings",
            async ([FromBody] CameraSettingsUpdateRequestDto request, BridgePipeClient ipc) =>
            {
                await ipc.CallRawAsync(
                    Commands.SettingsSet,
                    new
                    {
                        iso = request.Iso,
                        shutter = request.Shutter,
                        whiteBalance = request.WhiteBalance,
                    },
                    CancellationToken.None
                );
                return Results.Text("ok");
            }
        )
            .WithTags("Settings")
            .Accepts<CameraSettingsUpdateRequestDto>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain");

        app.MapPost(
            "/api/capture",
            async (
                HttpContext ctx,
                [FromBody] CaptureApiRequestDto? request,
                [FromQuery(Name = "startLiveViewAfterCapture")] bool? startLiveViewAfterCapture,
                [FromQuery(Name = "liveview")] bool? liveview,
                BridgePipeClient ipc
            ) => await HandleCaptureAsync(
                ctx,
                ipc,
                request,
                forceStartLiveViewAfterCapture: false,
                queryStartLiveViewAfterCapture: startLiveViewAfterCapture == true || liveview == true
            )
        )
            .WithTags("Capture")
            .Accepts<CaptureApiRequestDto>("application/json")
            .Produces<CaptureFileResultDto>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost(
            "/api/capture-liveview",
            async (HttpContext ctx, [FromBody] CaptureApiRequestDto? request, BridgePipeClient ipc) =>
                await HandleCaptureAsync(
                    ctx,
                    ipc,
                    request,
                    forceStartLiveViewAfterCapture: true,
                    queryStartLiveViewAfterCapture: false
                )
        )
            .WithTags("Capture")
            .Accepts<CaptureApiRequestDto>("application/json")
            .Produces<CaptureFileResultDto>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet(
            "/api/watchdog",
            async (BridgePipeClient ipc) =>
            {
                try
                {
                    var wd = await ipc.CallAsync<WatchdogDto>(
                        Commands.WatchdogGet,
                        null,
                        CancellationToken.None
                    );
                    return Results.Json(wd ?? new WatchdogDto { Enabled = false }, Program.JsonOpts);
                }
                catch
                {
                    return Results.Json(new WatchdogDto { Enabled = false }, Program.JsonOpts);
                }
            }
        )
            .WithTags("Watchdog")
            .Produces<WatchdogDto>(StatusCodes.Status200OK, "application/json");

        app.MapPost(
            "/api/watchdog",
            async ([FromQuery(Name = "enabled")] bool? enabled, BridgePipeClient ipc) =>
            {
                var effectiveEnabled = enabled ?? false;

                var wd = await ipc.CallAsync<WatchdogDto>(
                    Commands.WatchdogSet,
                    new WatchdogDto { Enabled = effectiveEnabled },
                    CancellationToken.None
                );

                return Results.Json(wd ?? new WatchdogDto { Enabled = effectiveEnabled }, Program.JsonOpts);
            }
        )
            .WithTags("Watchdog")
            .Produces<WatchdogDto>(StatusCodes.Status200OK, "application/json");
    }

    internal static bool IsPublic(HttpContext ctx, string mjpegPath)
    {
        if (HttpMethods.IsOptions(ctx.Request.Method))
            return true;

        var path = ctx.Request.Path.Value ?? "";
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            if (path == "/")
                return true;
            if (path.Equals("/docs", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/docs/index.html", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith("/docs/", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/swagger", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/swagger/index.html", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/openapi/v1.json", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/swagger/v1/swagger.json", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Equals(mjpegPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static string? GetKey(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Api-Key", out var v))
            return v.ToString();

        var auth = ctx.Request.Headers.Authorization.ToString();
        if (
            !string.IsNullOrWhiteSpace(auth)
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
            return auth.Substring("Bearer ".Length).Trim();

        return null;
    }

    internal static IResult MapBridgePipeError(BridgePipeException ex)
    {
        var title = string.Equals(ex.Command, Commands.Capture, StringComparison.Ordinal)
            ? "Capture failed"
            : "Bridge error";

        return Results.Problem(
            title: title,
            detail: string.IsNullOrWhiteSpace(ex.Message) ? ex.Code : ex.Message,
            type: ex.Code,
            statusCode: ex.HttpStatus,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ex.Code,
                ["command"] = ex.Command,
            }
        );
    }

    internal static IResult MapBridgeError(BridgeException be)
    {
        var status = be.Code switch
        {
            ErrorCodes.DeviceBusy => 409,
            ErrorCodes.NoCamera => 404,
            ErrorCodes.CannotFocus => 422,
            ErrorCodes.Timeout or ErrorCodes.RefreshTimeout => 504,
            _ => 500,
        };

        return Results.Problem(
            title: "Bridge error",
            detail: string.IsNullOrWhiteSpace(be.Message) ? be.Code : be.Message,
            type: be.Code,
            statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = be.Code,
            }
        );
    }

    private static async Task<IResult> HandleCaptureAsync(
        HttpContext ctx,
        BridgePipeClient ipc,
        CaptureApiRequestDto? request,
        bool forceStartLiveViewAfterCapture,
        bool queryStartLiveViewAfterCapture)
    {
        request ??= new CaptureApiRequestDto();

        var cap = request.ToCaptureRequestDto();
        var startLiveViewAfterCapture =
            forceStartLiveViewAfterCapture
            || queryStartLiveViewAfterCapture
            || request.HasAnyLiveViewFlag();

        var mode = (cap.Mode ?? "file").Trim().ToLowerInvariant();
        cap.Mode = mode == "jpeg" ? "jpeg" : "file";

        Log.Information(
            "HTTP {Path} start. trace={TraceId}, mode={Mode}, startLiveViewAfterCapture={StartLiveViewAfterCapture}",
            ctx.Request.Path.Value,
            ctx.TraceIdentifier,
            cap.Mode,
            startLiveViewAfterCapture
        );

        if (cap.Mode == "jpeg")
        {
            var jpeg = await ipc.CallAsync<CaptureJpegResultDto>(
                Commands.Capture,
                cap,
                ctx.RequestAborted
            );

            if (jpeg == null || string.IsNullOrWhiteSpace(jpeg.JpegBase64))
            {
                return Results.Problem(
                    title: "Capture failed",
                    detail: "Worker returned no JPEG data.",
                    type: "capture_failed",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }

            if (startLiveViewAfterCapture)
                await TryStartLiveViewAfterCaptureAsync(ipc, ctx.RequestAborted);

            var bytes = Convert.FromBase64String(jpeg.JpegBase64);
            return Results.File(bytes, "image/jpeg");
        }

        var res = await ipc.CallAsync<CaptureFileResultDto>(
            Commands.Capture,
            cap,
            ctx.RequestAborted
        );

        if (startLiveViewAfterCapture && (res?.Ok ?? false))
            await TryStartLiveViewAfterCaptureAsync(ipc, ctx.RequestAborted);

        return Results.Json(res ?? new CaptureFileResultDto { Ok = false }, Program.JsonOpts);
    }

    private static async Task TryStartLiveViewAfterCaptureAsync(BridgePipeClient ipc, CancellationToken ct)
    {
        try
        {
            await ipc.CallRawAsync(Commands.LiveViewStart, null, ct);
            Log.Information("LiveView restarted automatically after capture.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LiveView restart after capture failed.");
        }
    }
}
