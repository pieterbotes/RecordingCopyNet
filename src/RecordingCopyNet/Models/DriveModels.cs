namespace RecordingCopyNet.Models;

public record DriveFolderInfo(string Id, string Name, string? DriveId);
public record DriveFileInfo(string Id, string Name, long? Size);
public record DriveInfo(string Id, string Name);
