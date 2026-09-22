namespace RecordingCopyNet.Services.Zoom;

public interface IZoomAuthService
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
    void ClearTokenCache();
}
