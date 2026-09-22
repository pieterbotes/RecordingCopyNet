using Google.Apis.Drive.v3;

namespace RecordingCopyNet.Services.Google;

public interface IGoogleAuthService
{
    DriveService GetDriveService();
    void ClearDriveClient();
}
