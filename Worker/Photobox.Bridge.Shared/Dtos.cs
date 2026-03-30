using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Photobox.Bridge.Shared;

// -----------------------------
// HTTP API DTOs
// -----------------------------

[DataContract]
public sealed class StatusDto
{
    [DataMember(Name = "httpRunning", Order = 1)]
    public bool HttpRunning { get; set; }

    [DataMember(Name = "liveViewRunning", Order = 2)]
    public bool LiveViewRunning { get; set; }

    [DataMember(Name = "selected", Order = 3, EmitDefaultValue = true)]
    public string? Selected { get; set; }

    [DataMember(Name = "manufacturer", Order = 4, EmitDefaultValue = true)]
    public string? Manufacturer { get; set; }

    [DataMember(Name = "model", Order = 5, EmitDefaultValue = true)]
    public string? Model { get; set; }

    [DataMember(Name = "serial", Order = 6, EmitDefaultValue = true)]
    public string? Serial { get; set; }

    [DataMember(Name = "httpRunningSinceUtc", Order = 7, EmitDefaultValue = true)]
    public string? HttpRunningSinceUtc { get; set; }

    [DataMember(Name = "httpUptimeSeconds", Order = 8)]
    public long HttpUptimeSeconds { get; set; }

    [DataMember(Name = "streamRunning", Order = 9)]
    public bool StreamRunning { get; set; }

    [DataMember(Name = "streamClients", Order = 10)]
    public int StreamClients { get; set; }

    [DataMember(Name = "streamSendingFrames", Order = 11)]
    public bool StreamSendingFrames { get; set; }

    [DataMember(Name = "framesActive", Order = 12)]
    public bool FramesActive { get; set; }

    [DataMember(Name = "framesTotal", Order = 13)]
    public long FramesTotal { get; set; }

    [DataMember(Name = "frameAgeMs", Order = 14, EmitDefaultValue = true)]
    public long? FrameAgeMs { get; set; }

    [DataMember(Name = "lastFrameUtc", Order = 15, EmitDefaultValue = true)]
    public string? LastFrameUtc { get; set; }

    [DataMember(Name = "source", Order = 16, EmitDefaultValue = true)]
    public StreamSourceDto? Source { get; set; }

    // Optional extension
    [DataMember(Name = "watchdogEnabled", Order = 17, EmitDefaultValue = true)]
    public bool? WatchdogEnabled { get; set; }
}

[DataContract]
public sealed class StreamSourceDto
{
    [DataMember(Name = "serial", Order = 1, EmitDefaultValue = true)]
    public string? Serial { get; set; }

    [DataMember(Name = "id", Order = 2, EmitDefaultValue = true)]
    public int? Id { get; set; }
}

[DataContract]
public sealed class OkDto
{
    [DataMember(Name = "ok", Order = 1)]
    public bool Ok { get; set; }
}

[DataContract]
public sealed class WatchdogDto
{
    [DataMember(Name = "enabled", Order = 1)]
    public bool Enabled { get; set; }
}

// -----------------------------
// Worker-facing DTOs (IPC)
// -----------------------------

[DataContract]
public sealed class WorkerStatusDto
{
    [DataMember(Name = "liveViewRunning", Order = 1)]
    public bool LiveViewRunning { get; set; }

    [DataMember(Name = "selected", Order = 2, EmitDefaultValue = true)]
    public string? Selected { get; set; }

    [DataMember(Name = "manufacturer", Order = 3, EmitDefaultValue = true)]
    public string? Manufacturer { get; set; }

    [DataMember(Name = "model", Order = 4, EmitDefaultValue = true)]
    public string? Model { get; set; }

    [DataMember(Name = "serial", Order = 5, EmitDefaultValue = true)]
    public string? Serial { get; set; }

    [DataMember(Name = "framesTotal", Order = 6)]
    public long FramesTotal { get; set; }

    [DataMember(Name = "frameAgeMs", Order = 7, EmitDefaultValue = true)]
    public long? FrameAgeMs { get; set; }

    [DataMember(Name = "lastFrameUtc", Order = 8, EmitDefaultValue = true)]
    public string? LastFrameUtc { get; set; }

    [DataMember(Name = "source", Order = 9, EmitDefaultValue = true)]
    public StreamSourceDto? Source { get; set; }

    [DataMember(Name = "watchdogEnabled", Order = 10, EmitDefaultValue = true)]
    public bool? WatchdogEnabled { get; set; }
}

[DataContract]
public sealed class CameraInfoDto
{
    [DataMember(Name = "id", Order = 1)]
    public int Id { get; set; }

    [DataMember(Name = "displayName", Order = 2, EmitDefaultValue = true)]
    public string? DisplayName { get; set; }

    [DataMember(Name = "manufacturer", Order = 3, EmitDefaultValue = true)]
    public string? Manufacturer { get; set; }

    [DataMember(Name = "model", Order = 4, EmitDefaultValue = true)]
    public string? Model { get; set; }

    [DataMember(Name = "serial", Order = 5, EmitDefaultValue = true)]
    public string? Serial { get; set; }

    [DataMember(Name = "port", Order = 6, EmitDefaultValue = true)]
    public string? Port { get; set; }

    [DataMember(Name = "isConnected", Order = 7)]
    public bool IsConnected { get; set; }
}

[DataContract]
public sealed class CameraSettingsDto
{
    [DataMember(Name = "iso", Order = 1, EmitDefaultValue = true)]
    public string? Iso { get; set; }

    [DataMember(Name = "shutter", Order = 2, EmitDefaultValue = true)]
    public string? Shutter { get; set; }

    [DataMember(Name = "whiteBalance", Order = 3, EmitDefaultValue = true)]
    public string? WhiteBalance { get; set; }

    // ✅ NEU
    [DataMember(Name = "aperture", Order = 4, EmitDefaultValue = true)]
    public string? Aperture { get; set; }

    // ✅ NEU
    [DataMember(Name = "exposure", Order = 5, EmitDefaultValue = true)]
    public double? Exposure { get; set; }

    [DataMember(Name = "isoOptions", Order = 6, EmitDefaultValue = true)]
    public List<string> IsoOptions { get; set; } = new List<string>();

    [DataMember(Name = "shutterOptions", Order = 7, EmitDefaultValue = true)]
    public List<string> ShutterOptions { get; set; } = new List<string>();

    [DataMember(Name = "whiteBalanceOptions", Order = 8, EmitDefaultValue = true)]
    public List<string> WhiteBalanceOptions { get; set; } = new List<string>();


    [DataMember(Name = "apertureOptions", Order = 9, EmitDefaultValue = true)]
    public List<string> ApertureOptions { get; set; } = new List<string>();

    [DataMember(Name = "exposureOptions", Order = 10, EmitDefaultValue = true)]
    public List<string> ExposureOptions { get; set; } = new List<string>();
}


[DataContract]
public sealed class LiveViewFpsDto
{
    [DataMember(Name = "fps", Order = 1)]
    public int Fps { get; set; }
}

[DataContract]
public sealed class SelectRequestDto
{
    [DataMember(Name = "serial", Order = 1, EmitDefaultValue = false)]
    public string? Serial { get; set; }

    [DataMember(Name = "id", Order = 2, EmitDefaultValue = false)]
    public int? Id { get; set; }
}

[DataContract]
public sealed class RefreshRequestDto
{
    [DataMember(Name = "timeoutMs", Order = 1)]
    public int TimeoutMs { get; set; } = 4000;
}

[DataContract]
public sealed class CaptureRequestDto
{
    /// <summary>"file" (default) or "jpeg"</summary>
    [DataMember(Name = "mode", Order = 1, EmitDefaultValue = true)]
    public string? Mode { get; set; }

    [DataMember(Name = "overwrite", Order = 2)]
    public bool Overwrite { get; set; }

    /// <summary>Optional. If overwrite=true, should be set.</summary>
    [DataMember(Name = "fileName", Order = 3, EmitDefaultValue = false)]
    public string? FileName { get; set; }

    [DataMember(Name = "path", Order = 4, EmitDefaultValue = false)]
    public string? Path { get; set; }

    [DataMember(Name = "applySettings", Order = 5)]
    public bool ApplySettings { get; set; }

    [DataMember(Name = "resetAfterShoot", Order = 6)]
    public bool ResetAfterShoot { get; set; } = true;

    [DataMember(Name = "iso", Order = 7, EmitDefaultValue = false)]
    public string? Iso { get; set; }

    [DataMember(Name = "shutter", Order = 8, EmitDefaultValue = false)]
    public string? Shutter { get; set; }

    [DataMember(Name = "whiteBalance", Order = 9, EmitDefaultValue = false)]
    public string? WhiteBalance { get; set; }

    // ✅ NEU
    [DataMember(Name = "aperture", Order = 10, EmitDefaultValue = false)]
    public string? Aperture { get; set; }

    // ✅ NEU
    [DataMember(Name = "exposure", Order = 11, EmitDefaultValue = false)]
    public double? Exposure { get; set; }
}

[DataContract]
public sealed class CaptureFileResultDto
{
    [DataMember(Name = "ok", Order = 1)]
    public bool Ok { get; set; }

    [DataMember(Name = "file", Order = 2, EmitDefaultValue = true)]
    public string? File { get; set; }
}

[DataContract]
public sealed class CaptureJpegResultDto
{
    [DataMember(Name = "contentType", Order = 1)]
    public string ContentType { get; set; } = "image/jpeg";

    [DataMember(Name = "jpegBase64", Order = 2)]
    public string JpegBase64 { get; set; } = "";
}

[DataContract]
public sealed class FrameWaitRequestDto
{
    [DataMember(Name = "lastSeq", Order = 1)]
    public long LastSeq { get; set; }

    [DataMember(Name = "timeoutMs", Order = 2)]
    public int TimeoutMs { get; set; } = 1500;
}

[DataContract]
public sealed class FrameWaitResponseDto
{
    [DataMember(Name = "seq", Order = 1)]
    public long Seq { get; set; }

    [DataMember(Name = "jpegBase64", Order = 2, EmitDefaultValue = false)]
    public string? JpegBase64 { get; set; }

    [DataMember(Name = "lastFrameUtc", Order = 3, EmitDefaultValue = true)]
    public string? LastFrameUtc { get; set; }
}
