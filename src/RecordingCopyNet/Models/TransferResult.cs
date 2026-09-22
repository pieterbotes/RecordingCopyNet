using System.Text.Json.Serialization;

namespace RecordingCopyNet.Models;

public record TransferResult(
    [property: JsonPropertyName("folderName")] string FolderName,
    [property: JsonPropertyName("folderId")] string? FolderId,
    [property: JsonPropertyName("filesUploaded")] int FilesUploaded);
