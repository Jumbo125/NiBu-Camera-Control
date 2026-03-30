// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: CameraMapLoader.cs
// Zweck: Lädt die Kamera-Mappings aus camera-map.json oder verwendet Defaultwerte.
// Projekt: Photobox CameraBridge Worker

using System;
using System.IO;

namespace Photobox.CameraBridge.Core
{
    internal static class CameraMapLoader
    {
        public static CameraMap LoadOrDefault(string baseDir, RingLogger logger)
        {
            var path = Path.Combine(baseDir, "camera-map.json");
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonUtil.LoadFile<CameraMap>(path);
                    logger.Info($"Loaded camera-map.json from {path}");
                    return cfg;
                }
                logger.Warn($"camera-map.json not found at {path}, using defaults.");
            }
            catch (Exception ex)
            {
                logger.Error("Failed to load camera-map.json, using defaults.", ex);
            }
            return new CameraMap();
        }
    }
}