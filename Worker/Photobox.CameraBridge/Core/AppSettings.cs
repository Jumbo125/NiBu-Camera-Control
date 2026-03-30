// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: AppSettings.cs
// Zweck: Definiert die grundlegenden Laufzeit- und Standardwerte des Workers.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - LiveView-FPS konfigurieren
// - KeepAlive-Verhalten festlegen
// - WIA-Geräte optional aktivieren
// - Standard-Capture-Ordner bereitstellen
using System.Runtime.Serialization;

namespace Photobox.CameraBridge.Core
{
    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Name = "liveViewFps")]
        public int LiveViewFps { get; set; } = 20;

        // Wenn du KeepAlive wirklich noch verwendest (z.B. Timer/Watchdog), lassen wir es drin.
        // Falls nicht genutzt: kannst du es später entfernen.
        [DataMember(Name = "keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 60;

        // Falls du WIA (z.B. digiCamControl Virtual Webcam) brauchst:
        [DataMember(Name = "loadWiaDevices")]
        public bool LoadWiaDevices { get; set; } = false;

        [DataMember(Name = "defaultCaptureFolder")]
        public string DefaultCaptureFolder { get; set; } = @"C:\Photobox\captures";
    }
}
