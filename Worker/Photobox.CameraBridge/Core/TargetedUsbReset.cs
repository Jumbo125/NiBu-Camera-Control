// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: TargetedUsbReset.cs
// Zweck: Setzt genau das USB-Gerät der zuletzt ausgewählten Kamera per PnP-Deaktivierung/
//        -Aktivierung zurück (CM_Disable_DevNode/CM_Enable_DevNode) - im Gegensatz zu
//        APIServer/UsbCameraReset.cs, das pauschal ALLE WPD/Camera/Image-Geräte cycled.
// Projekt: Photobox CameraBridge Worker
//
// Hintergrund: Ein offener SDK-Handle auf die Kamera verhindert, dass Windows den
// zugehörigen Devnode überhaupt deaktivieren kann (PnP-Veto "Gerät wird verwendet") -
// das erklärt, warum ein manuelles Deaktivieren im Geräte-Manager erst nach Beenden des
// Workers funktionierte. Ein reiner SDK-seitiger Reconnect (cam.Close() + neu verbinden)
// reicht bei einer wirklich wedged Kamera deshalb nicht - der USB-Treiberstack selbst
// muss neu enumeriert werden, was ein SDK-Handle-Close allein nicht auslöst.
//
// Voraussetzung: Der SDK-Handle MUSS vor dem Aufruf hier bereits geschlossen sein
// (siehe CameraHost.TryCloseSelectedCameraConnection), sonst schlägt CM_Disable_DevNode
// fehl, weil der Devnode noch als "in Benutzung" markiert ist.

using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace Photobox.CameraBridge.Core
{
    public static class TargetedUsbReset
    {
        private static readonly string[] CameraPnpClasses = { "WPD", "Camera", "Image" };
        private const int CR_SUCCESS = 0;
        private static readonly TimeSpan DisableEnableGap = TimeSpan.FromMilliseconds(1500);

        // Nach CM_Enable_DevNode ist der Devnode "enabled", aber der Windows Portable
        // Devices (WPD)-Dienst hat die MTP/PTP-Geräteregistrierung oft noch nicht
        // abgeschlossen - ein RefreshAsync() direkt danach fand im Log kurzzeitig noch
        // den alten (absterbenden) WPD-Eintrag neben dem neuen ("Found: 2 camera(s)")
        // und griff dabei manchmal den alten, bereits herunterfahrenden COM-Objekt-
        // Handle ("Shutdown bereits aufgerufen"). Kurze Settle-Pause, damit WPD
        // nachziehen kann, bevor der Aufrufer neu enumeriert.
        private static readonly TimeSpan EnableSettleDelay = TimeSpan.FromMilliseconds(2000);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

        /// <summary>
        /// Cycled genau das Kamera-Gerät, dessen PnP-DeviceID die übergebene Seriennummer enthält
        /// (die meisten Canon/Nikon-USB-Descriptoren enthalten sie im Instance-Pfad). Falls kein
        /// Match möglich ist (Modell exponiert die Seriennummer nicht) und genau ein Kamera-
        /// Klassen-Gerät präsent ist, wird dieses als Fallback genommen. Bei mehreren Kandidaten
        /// ohne eindeutigen Match wird NICHTS gecycled - Absicht: lieber gar nichts tun, als
        /// versehentlich die falsche Kamera/das falsche Gerät zu deaktivieren.
        /// </summary>
        public static bool TryResetBySerial(string serial, RingLogger log)
        {
            var devices = FindCameraClassDevices(log);
            if (devices.Count == 0)
            {
                log?.Warn("TargetedUsbReset: no WPD/Camera/Image devices currently present.");
                return false;
            }

            (string Name, string Id)? target = null;

            if (!string.IsNullOrWhiteSpace(serial))
            {
                foreach (var d in devices)
                {
                    if (d.Id.IndexOf(serial, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        target = d;
                        break;
                    }
                }
            }

            if (target == null)
            {
                if (devices.Count == 1)
                {
                    target = devices[0];
                    log?.Info("TargetedUsbReset: no serial match, but exactly one camera-class device present -> using it.");
                }
                else
                {
                    log?.Warn($"TargetedUsbReset: no unique device match for serial '{serial}' among {devices.Count} camera-class devices -> aborting (would not know which to cycle).");
                    return false;
                }
            }

            return TryCycleDevice(target.Value.Name, target.Value.Id, log);
        }

        private static List<(string Name, string Id)> FindCameraClassDevices(RingLogger log)
        {
            var result = new List<(string, string)>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, PNPClass FROM Win32_PnPEntity"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        var pnpClass = mo["PNPClass"] as string;
                        if (string.IsNullOrEmpty(pnpClass) || Array.IndexOf(CameraPnpClasses, pnpClass) < 0)
                            continue;

                        var id = mo["DeviceID"] as string;
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var name = mo["Name"] as string ?? id;
                        result.Add((name, id));
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Warn("TargetedUsbReset: WMI enumeration failed: " + ex.Message);
            }

            return result;
        }

        private static bool TryCycleDevice(string name, string instanceId, RingLogger log)
        {
            try
            {
                var locateResult = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
                if (locateResult != CR_SUCCESS)
                {
                    log?.Warn($"TargetedUsbReset: CM_Locate_DevNodeW failed ({locateResult}) for {name} ({instanceId}).");
                    return false;
                }

                var disableResult = CM_Disable_DevNode(devInst, 0);
                Thread.Sleep(DisableEnableGap);
                var enableResult = CM_Enable_DevNode(devInst, 0);

                if (disableResult == CR_SUCCESS && enableResult == CR_SUCCESS)
                {
                    Thread.Sleep(EnableSettleDelay);
                    log?.Info($"TargetedUsbReset: cycled {name} ({instanceId}).");
                    return true;
                }

                log?.Warn($"TargetedUsbReset: cycle incomplete for {name} ({instanceId}). disable={disableResult} enable={enableResult}");
                return false;
            }
            catch (Exception ex)
            {
                log?.Warn("TargetedUsbReset: exception cycling " + name + " (" + instanceId + "): " + ex.Message);
                return false;
            }
        }
    }
}
