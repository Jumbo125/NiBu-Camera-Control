// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: Dtos.cs
// Zweck: Definiert die gemeinsamen DTOs für HTTP-API und Worker-IPC.
// Projekt: Photobox CameraBridge Shared
//
// Aufgaben:
// - Status-, Kamera- und Settings-Modelle bereitstellen
// - Request-/Response-DTOs für Auswahl, Refresh, Capture und LiveView definieren
// - serialisierbare DataContracts für den Austausch zwischen API-Server und Worker zentral bündeln
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Photobox.Bridge.Shared;

namespace Photobox.Bridge.WorkerIpc;

public sealed class BridgeIpcServer : IDisposable
{
    private readonly string _pipeName;
    private readonly IBridgeWorker _worker;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public BridgeIpcServer(IBridgeWorker worker, string? pipeName = null, Action<string>? log = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeNames.CommandPipe : pipeName;
        _log = log;
    }

    public void Start()
    {
        if (_cts != null) throw new InvalidOperationException("Already started.");

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log?.Invoke($"IPC: listening on named pipe '{_pipeName}'");
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _acceptLoop?.Wait(1500); } catch { }
        try { _cts?.Dispose(); } catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _log?.Invoke("IPC: client connected");

                await HandleClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.Invoke("IPC: accept loop error: " + ex);
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PipeRequest? req;
            try
            {
                var json = await ReadMessageAsync(stream, ct).ConfigureAwait(false);
                if (json == null) return; // disconnect
                req = IpcJson.Deserialize<PipeRequest>(json);
            }
            catch (EndOfStreamException) { return; }
            catch (IOException) { return; }
            catch (Exception ex)
            {
                _log?.Invoke("IPC: failed to read request: " + ex);
                return;
            }

            PipeResponse res;
            try
            {
                res = await DispatchAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                res = new PipeResponse
                {
                    Id = req.Id ?? "",
                    Ok = false,
                    ErrorCode = ErrorCodes.InternalError,
                    ErrorMessage = ex.Message
                };
            }

            try
            {
                var resBytes = IpcJson.Serialize(res);
                await WriteMessageAsync(stream, resBytes, ct).ConfigureAwait(false);
            }
            catch (IOException) { return; }
        }
    }

    private async Task<PipeResponse> DispatchAsync(PipeRequest req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Cmd))
        {
            return new PipeResponse { Id = req?.Id ?? "", Ok = false, ErrorCode = ErrorCodes.InvalidRequest, ErrorMessage = "missing cmd" };
        }

        string id = req.Id ?? "";

        try
        {
            switch (req.Cmd)
            {
                case Commands.StatusGet:
                {
                    var dto = await _worker.GetStatusAsync(ct).ConfigureAwait(false);
                    return Ok(id, dto);
                }

                case Commands.CamerasList:
                {
                    var list = await _worker.GetCamerasAsync(ct).ConfigureAwait(false);
                    return Ok(id, list);
                }

                case Commands.CameraSelect:
                {
                    var dto = ReadPayload<SelectRequestDto>(req.Payload);
                    var ok = await _worker.SelectCameraAsync(dto, ct).ConfigureAwait(false);
                    return Ok(id, new OkDto { Ok = ok });
                }

                case Commands.CameraRefresh:
                {
                    var dto = ReadPayload<RefreshRequestDto>(req.Payload);
                    await _worker.RefreshAsync(dto, ct).ConfigureAwait(false);
                    return Ok(id, new OkDto { Ok = true });
                }

                case Commands.LiveViewStart:
                    await _worker.StartLiveViewAsync(ct).ConfigureAwait(false);
                    return Ok(id, new OkDto { Ok = true });

                case Commands.LiveViewStop:
                    await _worker.StopLiveViewAsync(ct).ConfigureAwait(false);
                    return Ok(id, new OkDto { Ok = true });

                case Commands.LiveViewFpsGet:
                {
                    var fps = await _worker.GetLiveViewFpsAsync(ct).ConfigureAwait(false);
                    return Ok(id, new LiveViewFpsDto { Fps = fps });
                }

                case Commands.LiveViewFpsSet:
                {
                    var dto = ReadPayload<LiveViewFpsDto>(req.Payload);
                    await _worker.SetLiveViewFpsAsync(dto.Fps, ct).ConfigureAwait(false);
                    return Ok(id, new OkDto { Ok = true });
                }

                case Commands.SettingsGet:
                {
                    var settings = await _worker.GetSettingsAsync(ct).ConfigureAwait(false);
                    return Ok(id, settings);
                }

                case Commands.SettingsSet:
                {
                    var patch = ReadPayload<CameraSettingsDtoPartial>(req.Payload);
                    var ok = await _worker.SetSettingsAsync(patch, ct).ConfigureAwait(false);
                    return Ok(id, new OkDto { Ok = ok });
                }

                case Commands.Capture:
                {
                    var cap = ReadPayload<CaptureRequestDto>(req.Payload);
                    var mode = (cap.Mode ?? "file").Trim().ToLowerInvariant();
                    if (mode == "jpeg")
                    {
                        var jpg = await _worker.CaptureJpegAsync(cap, ct).ConfigureAwait(false);
                        var payload = new CaptureJpegResultDto { JpegBase64 = Convert.ToBase64String(jpg ?? Array.Empty<byte>()) };
                        return Ok(id, payload);
                    }
                    else
                    {
                        var result = await _worker.CaptureToFileAsync(cap, ct).ConfigureAwait(false);
                        return Ok(id, result);
                    }
                }

                case Commands.WatchdogGet:
                {
                    var wd = await _worker.GetWatchdogAsync(ct).ConfigureAwait(false);
                    return Ok(id, wd);
                }

                case Commands.WatchdogSet:
                {
                    var dto = ReadPayload<WatchdogDto>(req.Payload);
                    var wd = await _worker.SetWatchdogAsync(dto.Enabled, ct).ConfigureAwait(false);
                    return Ok(id, wd);
                }

                case Commands.FrameWaitNext:
                {
                    var dto = ReadPayload<FrameWaitRequestDto>(req.Payload);
                    var (seq, jpeg, lastUtc) = await _worker.WaitNextFrameAsync(dto.LastSeq, dto.TimeoutMs, ct).ConfigureAwait(false);
                    var payload = new FrameWaitResponseDto
                    {
                        Seq = seq,
                        LastFrameUtc = lastUtc,
                        JpegBase64 = (jpeg == null || jpeg.Length == 0) ? null : Convert.ToBase64String(jpeg)
                    };
                    return Ok(id, payload);
                }

                default:
                    return new PipeResponse { Id = id, Ok = false, ErrorCode = ErrorCodes.NotFound, ErrorMessage = "unknown cmd: " + req.Cmd };
            }
        }
        catch (OperationCanceledException)
        {
            return new PipeResponse { Id = id, Ok = false, ErrorCode = ErrorCodes.Timeout, ErrorMessage = "cancelled" };
        }
        catch (Exception ex)
        {
            // Let your real implementation map errors more specifically.
            return new PipeResponse { Id = id, Ok = false, ErrorCode = ErrorCodes.InternalError, ErrorMessage = ex.Message };
        }
    }

    private static T ReadPayload<T>(string? payload) where T : new()
    {
        if (string.IsNullOrWhiteSpace(payload)) return new T();
        return IpcJson.DeserializeFromString<T>(payload);
    }

    private static PipeResponse Ok<T>(string id, T payload)
    {
        return new PipeResponse
        {
            Id = id,
            Ok = true,
            Payload = payload == null ? null : IpcJson.SerializeToString(payload)
        };
    }

    private static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        int read = await ReadExactAsync(stream, lenBuf, 0, 4, ct).ConfigureAwait(false);
        if (read == 0) return null;

        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Invalid message length: " + len);

        var buf = new byte[len];
        await ReadExactAsync(stream, buf, 0, len, ct).ConfigureAwait(false);
        return buf;
    }

    private static async Task WriteMessageAsync(Stream stream, byte[] jsonBytes, CancellationToken ct)
    {
        var lenBuf = BitConverter.GetBytes(jsonBytes.Length);
        await stream.WriteAsync(lenBuf, 0, lenBuf.Length, ct).ConfigureAwait(false);
        await stream.WriteAsync(jsonBytes, 0, jsonBytes.Length, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0) return 0;
                throw new EndOfStreamException();
            }
            total += n;
        }
        return total;
    }
}
