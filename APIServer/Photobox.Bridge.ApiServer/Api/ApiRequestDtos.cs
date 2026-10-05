// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: ApiRequestDtos.cs
// Zweck: Definiert die API-spezifischen Request-DTOs für Settings- und Capture-Aufrufe.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - HTTP-Request-Modelle für API-Endpunkte bereitstellen
// - LiveView-bezogene Capture-Flags auswerten
// - API-Requests in Shared-Capture-DTOs überführen

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Photobox.Bridge.Shared;

namespace Photobox.Bridge.ApiServer;

internal sealed class CameraSettingsUpdateRequestDto
{
    public string? Iso { get; set; }

    public string? Shutter { get; set; }

    public string? WhiteBalance { get; set; }
}

internal sealed class CaptureApiRequestDto
{
    public string? Mode { get; set; }

    public bool Overwrite { get; set; }

    public string? FileName { get; set; }

    public string? Path { get; set; }

    public bool ApplySettings { get; set; }

    public bool ResetAfterShoot { get; set; } = true;

    public string? Iso { get; set; }

    public string? Shutter { get; set; }

    public string? WhiteBalance { get; set; }

    public string? Aperture { get; set; }

    public double? Exposure { get; set; }

    public bool? StartLiveViewAfterCapture { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public bool HasAnyLiveViewFlag() =>
        StartLiveViewAfterCapture == true
        || ReadExtensionBool("restartLiveViewAfterCapture")
        || ReadExtensionBool("captureAndLiveView")
        || ReadExtensionBool("capturePlusLiveView");

    public CaptureRequestDto ToCaptureRequestDto() =>
        new()
        {
            Mode = Mode,
            Overwrite = Overwrite,
            FileName = FileName,
            Path = Path,
            ApplySettings = ApplySettings,
            ResetAfterShoot = ResetAfterShoot,
            Iso = Iso,
            Shutter = Shutter,
            WhiteBalance = WhiteBalance,
            Aperture = Aperture,
            Exposure = Exposure,
        };

    private bool ReadExtensionBool(string propertyName)
    {
        if (ExtensionData == null || !ExtensionData.TryGetValue(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                || value.GetString() == "1",
            JsonValueKind.Number => value.TryGetInt32(out var i) && i != 0,
            _ => false,
        };
    }
}
