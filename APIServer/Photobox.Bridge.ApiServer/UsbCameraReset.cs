// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: UsbCameraReset.cs
// Zweck: Setzt USB-Geräte der Kamera-Klassen (WPD/Camera/Image) per PnP-Deaktivierung/-Aktivierung zurück.
// Projekt: Photobox CameraBridge ApiServer
//
// Aufgaben:
// - präsente Kamera-Geräte klassenbasiert (WPD/Camera/Image) ermitteln, unabhängig von Modell/Hub
// - Geräte per CM_Disable_DevNode/CM_Enable_DevNode neu enumerieren lassen
using System.Management;
using System.Runtime.InteropServices;
using Serilog;

namespace Photobox.Bridge.ApiServer;

/// <summary>
/// Cycles present PnP devices classified as WPD/Camera/Image (standard classes for PTP/MTP cameras).
/// Devices are found dynamically via WMI by class, not by a fixed VID/PID/instance path, so this
/// works for any camera model and regardless of whether it's plugged directly or via a hub.
/// </summary>
public static class UsbCameraReset
{
    private static readonly string[] CameraPnpClasses = { "WPD", "Camera", "Image" };

    private const int CR_SUCCESS = 0;
    private static readonly TimeSpan DisableEnableGap = TimeSpan.FromMilliseconds(1500);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    public static (int found, int resetOk) ResetCameraClassDevices(Serilog.ILogger log)
    {
        var devices = FindCameraClassDevices(log);
        if (devices.Count == 0)
        {
            log.Information("UsbCameraReset: no WPD/Camera/Image devices currently present.");
            return (0, 0);
        }

        int ok = 0;
        foreach (var (name, id) in devices)
        {
            if (TryCycleDevice(name, id, log))
                ok++;
        }

        log.Information("UsbCameraReset: cycled {Ok}/{Total} camera-class device(s).", ok, devices.Count);
        return (devices.Count, ok);
    }

    private static List<(string Name, string Id)> FindCameraClassDevices(Serilog.ILogger log)
    {
        var result = new List<(string, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, PNPClass FROM Win32_PnPEntity");

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
        catch (Exception ex)
        {
            log.Warning(ex, "UsbCameraReset: WMI enumeration failed.");
        }

        return result;
    }

    private static bool TryCycleDevice(string name, string instanceId, Serilog.ILogger log)
    {
        try
        {
            var locateResult = CM_Locate_DevNodeW(out var devInst, instanceId, 0);
            if (locateResult != CR_SUCCESS)
            {
                log.Warning("UsbCameraReset: CM_Locate_DevNodeW failed ({Code}) for {Name} ({Id}).", locateResult, name, instanceId);
                return false;
            }

            var disableResult = CM_Disable_DevNode(devInst, 0);
            Thread.Sleep(DisableEnableGap);
            var enableResult = CM_Enable_DevNode(devInst, 0);

            if (disableResult == CR_SUCCESS && enableResult == CR_SUCCESS)
            {
                log.Information("UsbCameraReset: cycled {Name} ({Id}).", name, instanceId);
                return true;
            }

            log.Warning("UsbCameraReset: cycle incomplete for {Name} ({Id}). disable={D} enable={E}", name, instanceId, disableResult, enableResult);
            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "UsbCameraReset: exception cycling {Name} ({Id}).", name, instanceId);
            return false;
        }
    }
}
