using RecordingCopyNet.Config;
using Xunit;

namespace RecordingCopyNet.Tests.Config;

public class AppConfigTests
{
    [Fact]
    public void DefaultsMatchNodeConfigJs()
    {
        var config = new AppConfig();

        Assert.Equal(3900, config.Port);
        Assert.Equal("https://zoom.us/oauth/token", config.ZoomAuthUrl);
        Assert.Equal("https://api.zoom.us/v2", config.ZoomApiBase);
        Assert.Equal(30, config.DefaultDateRangeDays);
    }

    [Fact]
    public void ResolvePathsMakesDataDirAbsoluteUnderBaseDir()
    {
        var config = new AppConfig { DataDir = "data", DbPath = "data/app.db", TempDir = "data/temp" };
        var baseDir = Path.Combine(Path.GetTempPath(), "rc-net-test-base");

        config.ResolvePaths(baseDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data")), config.DataDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data", "app.db")), config.DbPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "data", "temp")), config.TempDir);
    }
}
