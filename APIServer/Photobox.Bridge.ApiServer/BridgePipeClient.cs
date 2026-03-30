// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: BridgePipeClient.cs
// Zweck: Stellt den Named-Pipe-Client für die Kommunikation mit dem Worker bereit.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - Pipe-Verbindung aufbauen und wiederverwenden
// - IPC-Requests serialisieren und senden
// - IPC-Responses lesen und deserialisieren
// - Worker-/Pipe-Fehler in API-taugliche Exceptions übersetzen
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Photobox.Bridge.Shared;

namespace Photobox.Bridge.ApiServer;

public sealed class BridgePipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BridgePipeClient(string pipeName)
    {
        _pipeName = NormalizePipeName(pipeName) ?? PipeNames.CommandPipe;
    }

    public BridgePipeClient(IOptions<BridgeSettings> settings)
        : this(settings?.Value?.PipeName ?? PipeNames.CommandPipe)
    {
    }

    public async ValueTask DisposeAsync()
    {
        try { _pipe?.Dispose(); } catch { }
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return;

        try { _pipe?.Dispose(); } catch { }
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(2000, ct);
        _pipe.ReadMode = PipeTransmissionMode.Byte;
    }

    public async Task<T?> CallAsync<T>(string cmd, object? payloadObj, CancellationToken ct)
    {
        var res = await CallRawAsync(cmd, payloadObj, ct);
        if (string.IsNullOrWhiteSpace(res.Payload)) return default;
        return JsonSerializer.Deserialize<T>(res.Payload!, JsonOpts);
    }

    public async Task<PipeResponse> CallRawAsync(string cmd, object? payloadObj, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);

            var req = new PipeRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Cmd = cmd,
                Payload = payloadObj == null ? null : JsonSerializer.Serialize(payloadObj, JsonOpts)
            };

            var reqBytes = JsonSerializer.SerializeToUtf8Bytes(req, JsonOpts);
            await WriteMessageAsync(_pipe!, reqBytes, ct);

            var resBytes = await ReadMessageAsync(_pipe!, ct)
                ?? throw new BridgePipeException("pipe_disconnected", "Worker pipe disconnected.", 503, cmd);

            var res = JsonSerializer.Deserialize<PipeResponse>(resBytes, JsonOpts)
                ?? throw new BridgePipeException("invalid_pipe_response", "Invalid IPC response.", 502, cmd);

            if (!res.Ok)
                throw CreatePipeException(cmd, res);

            return res;
        }
        catch
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BridgePipeException CreatePipeException(string cmd, PipeResponse res)
    {
        var code = string.IsNullOrWhiteSpace(res.ErrorCode)
            ? ErrorCodes.InternalError
            : res.ErrorCode!;

        var message = string.IsNullOrWhiteSpace(res.ErrorMessage)
            ? code
            : res.ErrorMessage!;

        if (IsCanonUnableToFocus(cmd, message))
            return new BridgePipeException("camera_unable_to_focus", message, 409, cmd);

        var httpStatus = code switch
        {
            ErrorCodes.InvalidRequest => 400,
            ErrorCodes.NotFound => 404,
            ErrorCodes.DeviceBusy => 409,
            ErrorCodes.CannotFocus => 422,
            ErrorCodes.Timeout or ErrorCodes.RefreshTimeout => 504,
            _ => 500,
        };

        return new BridgePipeException(code, message, httpStatus, cmd);
    }

    private static bool IsCanonUnableToFocus(string cmd, string? message)
    {
        if (!string.Equals(cmd, Commands.Capture, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Unable to focus", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot focus", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed to focus", StringComparison.OrdinalIgnoreCase);
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

    private static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        int read = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
        if (read == 0) return null;

        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid message length: " + len);

        var buf = new byte[len];
        await ReadExactAsync(stream, buf, 0, len, ct);
        return buf;
    }

    private static async Task WriteMessageAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        var lenBuf = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(lenBuf, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
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

public class BridgeException : Exception
{
    public string Code { get; }

    public BridgeException(string code, string message)
        : base(string.IsNullOrWhiteSpace(message) ? code : message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? ErrorCodes.InternalError : code;
    }
}

public sealed class BridgePipeException : BridgeException
{
    public int HttpStatus { get; }
    public string? Command { get; }

    public BridgePipeException(string code, string message, int httpStatus = 500, string? command = null)
        : base(code, message)
    {
        HttpStatus = httpStatus;
        Command = command;
    }
}
