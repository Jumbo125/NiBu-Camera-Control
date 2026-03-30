// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: CameraMap.cs
// Zweck: Definiert Kamera-Mappings und Treiber-Overrides für bekannte Modelle.
// Projekt: Photobox CameraBridge Worker

using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Photobox.CameraBridge.Core
{
    [DataContract]
    public sealed class CameraMap
    {
        [DataMember(Name = "overrides")]
        public Dictionary<string, string> Overrides { get; set; } = new Dictionary<string, string>();

        [DataMember(Name = "nikonDefaultDriver")]
        public string NikonDefaultDriver { get; set; } = "CameraControl.Devices.Nikon.NikonD600Base";
    }
}
