using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Photobox.Bridge.Shared;

namespace Photobox.Bridge.WorkerIpc;

/// <summary>
/// Implement this in your net48 CameraWorker (wrap your existing CameraHost, FrameHub, Watchdog...).
/// The IPC server will expose these methods via Named Pipes.
/// </summary>
public interface IBridgeWorker
{
    Task<WorkerStatusDto> GetStatusAsync(CancellationToken ct);
    Task<List<CameraInfoDto>> GetCamerasAsync(CancellationToken ct);

    Task<bool> SelectCameraAsync(SelectRequestDto req, CancellationToken ct);
    Task RefreshAsync(RefreshRequestDto req, CancellationToken ct);

    Task StartLiveViewAsync(CancellationToken ct);
    Task StopLiveViewAsync(CancellationToken ct);

    Task<int> GetLiveViewFpsAsync(CancellationToken ct);
    Task SetLiveViewFpsAsync(int fps, CancellationToken ct);

    Task<CameraSettingsDto?> GetSettingsAsync(CancellationToken ct);
    Task<bool> SetSettingsAsync(CameraSettingsDtoPartial patch, CancellationToken ct);

    Task<CaptureFileResultDto> CaptureToFileAsync(CaptureRequestDto req, CancellationToken ct);
    Task<byte[]> CaptureJpegAsync(CaptureRequestDto req, CancellationToken ct);

    Task<WatchdogDto> GetWatchdogAsync(CancellationToken ct);
    Task<WatchdogDto> SetWatchdogAsync(bool enabled, CancellationToken ct);

    /// <summary>
    /// Wait until a frame newer than lastSeq is available or timeout triggers.
    /// Return seq + optional jpeg (base64 is added by IPC layer).
    /// </summary>
    Task<(long Seq, byte[]? Jpeg, string? LastFrameUtc)> WaitNextFrameAsync(long lastSeq, int timeoutMs, CancellationToken ct);
}

/// <summary>
/// For /api/settings POST: only a partial update is required.
/// </summary>
[System.Runtime.Serialization.DataContract]
public sealed class CameraSettingsDtoPartial
{
    [System.Runtime.Serialization.DataMember(Name = "iso", Order = 1, EmitDefaultValue = false)]
    public string? Iso { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "shutter", Order = 2, EmitDefaultValue = false)]
    public string? Shutter { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "whiteBalance", Order = 3, EmitDefaultValue = false)]
    public string? WhiteBalance { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "aperture", Order = 4, EmitDefaultValue = false)]
    public string? Aperture { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "exposure", Order = 5, EmitDefaultValue = false)]
    public double? Exposure { get; set; }

}
