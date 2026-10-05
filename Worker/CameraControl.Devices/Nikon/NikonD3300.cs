#region Licence


// Distributed under MIT License
// ===========================================================
//
// digiCamControl - DSLR camera remote control open source software
// Copyright (C) 2014 Duka Istvan
// Modifications Copyright (c) 2026 Andreas Rottmann
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

#endregion

using System.Runtime.InteropServices;
using System.Threading;
using CameraControl.Devices.Classes;

namespace CameraControl.Devices.Nikon
{
    public class NikonD3300 : NikonD600Base
    {
        public override bool Init(DeviceDescriptor deviceDescriptor)
        {
            bool res = base.Init(deviceDescriptor);

            // D3300 konservativer behandeln:
            // D600Base behalten für LiveView/Layout,
            // aber Features auf das Nötige reduzieren.
            Capabilities.Clear();
            Capabilities.Add(CapabilityEnum.LiveView);
            Capabilities.Add(CapabilityEnum.CaptureInRam);

            // Erst später wieder aktivieren, falls wirklich getestet:
            // Capabilities.Add(CapabilityEnum.RecordMovie);

            CaptureInSdRam = false;
            return res;
        }

        public override void CapturePhoto()
        {
            Monitor.Enter(Locker);
            try
            {
                IsBusy = true;
                DeviceReady();

                ErrorCodes.GetException(
                    CaptureInSdRam
                        ? ExecuteWithNoData(CONST_CMD_InitiateCaptureRecInSdram, 0xFFFFFFFF)
                        : ExecuteWithNoData(CONST_CMD_InitiateCapture)
                );
            }
            catch (COMException comException)
            {
                IsBusy = false;
                ErrorCodes.GetException(comException);
            }
            catch
            {
                IsBusy = false;
                throw;
            }
            finally
            {
                Monitor.Exit(Locker);
            }
        }
    }
}