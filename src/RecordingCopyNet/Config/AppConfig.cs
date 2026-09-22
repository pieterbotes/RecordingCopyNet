namespace RecordingCopyNet.Config;

public class AppConfig
{
    public int Port { get; set; } = 3900;
    public string DataDir { get; set; } = "data";
    public string DbPath { get; set; } = "data/app.db";
    public string TempDir { get; set; } = "data/temp";
    public string ZoomAuthUrl { get; set; } = "https://zoom.us/oauth/token";
    public string ZoomApiBase { get; set; } = "https://api.zoom.us/v2";
    public int DefaultDateRangeDays { get; set; } = 30;

    /// Resolves DataDir/DbPath/TempDir to absolute paths rooted at baseDir.
    /// Node resolves these with path.join(__dirname, ...); we do the same
    /// relative to the app's base directory so the app works regardless of cwd.
    public void ResolvePaths(string baseDir)
    {
        DataDir = Path.GetFullPath(Path.Combine(baseDir, DataDir));
        DbPath = Path.GetFullPath(Path.Combine(baseDir, DbPath));
        TempDir = Path.GetFullPath(Path.Combine(baseDir, TempDir));
    }
}
