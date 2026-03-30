// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: StartupOptions.cs
// Zweck: Definiert und parst die Startoptionen des Workers.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - CLI-Flags für Headless-, Tray- und Auto-Verhalten auswerten
// - Kameraauswahl und FPS-Overrides übernehmen
// - Startkonfiguration zentral verfügbar machen

using System;

namespace Photobox.CameraBridge.Core
{
    public sealed class StartupOptions
    {
        public bool Headless { get; set; }

        public bool AutoRefresh { get; set; } = true;
        public bool AutoStartHttp { get; set; } = true;

        public bool AutoSelectCamera { get; set; } = true;
        public int? AutoSelectCameraId { get; set; }

        // "Auto stream" = LiveView automatisch starten
        public bool AutoStartLiveView { get; set; } = false;

        // optional: fps override per CLI
        public int? LiveViewFps { get; set; }

        // Default: aus (ändert bestehendes Verhalten nicht)
        public bool TrayIcon { get; set; } = false;

        // Default: aus (nur wenn gesetzt: bestehende Instanz beenden & übernehmen)
        public bool OneInstance { get; set; } = false;

        public static StartupOptions Parse(string[] args, RingLogger log = null)
        {
            var o = new StartupOptions();
            if (args == null) return o;

            foreach (var raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var a = raw.Trim();

                if (Eq(a, "--headless") || Eq(a, "--no-ui")) { o.Headless = true; continue; }

                if (Eq(a, "--no-auto-refresh") || Eq(a, "--no-refresh")) { o.AutoRefresh = false; continue; }
                if (Eq(a, "--auto-refresh") || Eq(a, "--refresh")) { o.AutoRefresh = true; continue; }

                if (Eq(a, "--no-auto-http") || Eq(a, "--no-http")) { o.AutoStartHttp = false; continue; }
                if (Eq(a, "--auto-http") || Eq(a, "--http")) { o.AutoStartHttp = true; continue; }

                if (Eq(a, "--no-auto-select") || Eq(a, "--no-select"))
                {
                    o.AutoSelectCamera = false;
                    o.AutoSelectCameraId = null;
                    continue;
                }

                if (Eq(a, "--auto-select") || Eq(a, "--select")) { o.AutoSelectCamera = true; continue; }

                if (TryInt(a, "--select=", out var id) || TryInt(a, "--camera=", out id))
                {
                    o.AutoSelectCamera = true;
                    o.AutoSelectCameraId = id;
                    continue;
                }

                if (Eq(a, "--auto-liveview") || Eq(a, "--liveview")) { o.AutoStartLiveView = true; continue; }
                if (Eq(a, "--no-auto-liveview") || Eq(a, "--no-liveview")) { o.AutoStartLiveView = false; continue; }

                if (TryInt(a, "--fps=", out var fps))
                {
                    o.LiveViewFps = fps;
                    continue;
                }

                if (Eq(a, "--tray")) { o.TrayIcon = true; continue; }
                if (Eq(a, "--no-tray")) { o.TrayIcon = false; continue; }

                // Single instance (nur wenn du das explizit setzt)
                if (Eq(a, "--one_instanz") || Eq(a, "--one-instance") || Eq(a, "--single-instance"))
                {
                    o.OneInstance = true;
                    continue;
                }

                log?.Warn("Unknown arg ignored: " + a);
            }

            return o;
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool TryInt(string a, string prefix, out int value)
        {
            value = 0;
            if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(a.Substring(prefix.Length).Trim(), out value);
        }
    }
}
