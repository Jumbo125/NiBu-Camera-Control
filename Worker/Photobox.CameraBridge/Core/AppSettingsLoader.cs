// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: AppSettingsLoader.cs
// Zweck: Lädt die Worker-Konfiguration aus appsettings.json oder verwendet Defaultwerte.
// Projekt: Photobox CameraBridge Worker

using System;
using System.IO;

namespace Photobox.CameraBridge.Core
{
    internal static class AppSettingsLoader
    {
        public static AppSettings LoadOrDefault(string baseDir, RingLogger logger)
        {
            var path = Path.Combine(baseDir, "appsettings.json");
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonUtil.LoadFile<AppSettings>(path);
                    logger.Info($"Loaded appsettings.json from {path}");
                    return cfg;
                }
                logger.Warn($"appsettings.json not found at {path}, using defaults.");
            }
            catch (Exception ex)
            {
                logger.Error("Failed to load appsettings.json, using defaults.", ex);
            }
            return new AppSettings();
        }
    }
}
