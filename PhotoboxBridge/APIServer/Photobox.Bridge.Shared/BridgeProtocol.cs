// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: BridgeProtocol.cs
// Zweck: Definiert das grundlegende Named-Pipe-Protokoll zwischen API-Server und Worker.
// Projekt: Photobox CameraBridge Shared
//
// Aufgaben:
// - Request- und Response-Hüllen für IPC bereitstellen
// - Standard-Pipe-Namen zentral definieren
// - stabile Fehlercodes für die Kommunikation festlegen
using System.Runtime.Serialization;

namespace Photobox.Bridge.Shared;

/// <summary>
/// Length-prefixed UTF-8 JSON message on a single named pipe.
/// Payload is itself JSON (as a string), so the net48 side can deserialize strongly typed per command.
/// </summary>
[DataContract]
public sealed class PipeRequest
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = "";

    [DataMember(Name = "cmd", Order = 2)]
    public string Cmd { get; set; } = "";

    /// <summary>
    /// JSON string (e.g. "{\"fps\":15}") or null/empty.
    /// </summary>
    [DataMember(Name = "payload", Order = 3, EmitDefaultValue = false)]
    public string? Payload { get; set; }
}

[DataContract]
public sealed class PipeResponse
{
    [DataMember(Name = "id", Order = 1)]
    public string Id { get; set; } = "";

    [DataMember(Name = "ok", Order = 2)]
    public bool Ok { get; set; }

    /// <summary>
    /// Stable short error code (e.g. "no_camera", "device_busy", "timeout").
    /// </summary>
    [DataMember(Name = "errorCode", Order = 3, EmitDefaultValue = false)]
    public string? ErrorCode { get; set; }

    [DataMember(Name = "errorMessage", Order = 4, EmitDefaultValue = false)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// JSON string payload.
    /// </summary>
    [DataMember(Name = "payload", Order = 5, EmitDefaultValue = false)]
    public string? Payload { get; set; }
}

public static class PipeNames
{
    // Keep your original plan.
    public const string CommandPipe = "PhotoboxBridge.Cmd";
}

public static class ErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string NoCamera = "no_camera";
    public const string NotFound = "not_found";
    public const string DeviceBusy = "device_busy";
    public const string CannotFocus = "cannot_focus";
    public const string Timeout = "timeout";
    public const string CaptureFailed = "capture_failed";
    public const string RefreshTimeout = "refresh_timeout";
    public const string RefreshFailed = "refresh_failed";
    public const string InvalidRequest = "invalid_request";
    public const string InternalError = "internal_error";
}
