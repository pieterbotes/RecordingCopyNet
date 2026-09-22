using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

// Regression coverage for finding #2: existing ZoomControllerTests assert at the
// ActionResult level, which can't catch an ASP.NET Core content-negotiation bug (a
// bare `string` return picked up by StringOutputFormatter instead of the JSON
// formatter). This spins up the real app via WebApplicationFactory and makes an
// actual HTTP call so the serialized Content-Type/body are observable.
public class ZoomControllerIntegrationTests : IClassFixture<ZoomControllerIntegrationTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        public readonly string TempDataDir = Path.Combine(Path.GetTempPath(), $"rc-net-it-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppConfig:DataDir"] = TempDataDir,
                    ["AppConfig:DbPath"] = Path.Combine(TempDataDir, "app.db"),
                    ["AppConfig:TempDir"] = Path.Combine(TempDataDir, "temp"),
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(TempDataDir))
            {
                try { Directory.Delete(TempDataDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private readonly Factory _factory;

    public ZoomControllerIntegrationTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task WebsocketStop_ReturnsApplicationJson_WithQuotedStatusString_NotBareTextPlain()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/zoom/websocket/stop", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("\"disconnected\"", body); // quoted JSON string, not the bare word `disconnected`
    }

    [Fact]
    public async Task WebsocketStart_ReturnsApplicationJson_WithQuotedStatusString()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/zoom/websocket/start", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("\"", body);
        Assert.EndsWith("\"", body);
    }
}

// Regression coverage for finding #11. NOTE: a genuinely malformed `AppConfig__Port`
// env var also breaks options-pattern binding of AppConfig.Port (an `int` property)
// for every other consumer of IOptions<AppConfig> (Db, ZoomAuthService, etc.), not
// just the Kestrel setup this finding scoped its fix to — so a full WebApplicationFactory
// boot with that value still fails host startup regardless of this fix (a separate,
// pre-existing constraint of AppConfig.Port being typed `int`; redesigning that is out
// of scope for this finding). This test instead verifies, in isolation, the exact
// TryParse-with-fallback expression Program.cs now uses in place of the unguarded
// int.Parse(port ?? "3900") that used to throw an unhandled FormatException.
public class PortConfigParsingTests
{
    private static int ParsePortOrDefault(string? port)
    {
        if (!int.TryParse(port, out var parsedPort)) parsedPort = 3900;
        return parsedPort;
    }

    [Theory]
    [InlineData(null, 3900)]
    [InlineData("", 3900)]
    [InlineData("not-a-number", 3900)]
    [InlineData("4001", 4001)]
    public void FallsBackToDefaultPort_OnlyWhenValueIsMissingOrNonNumeric(string? input, int expected)
    {
        Assert.Equal(expected, ParsePortOrDefault(input));
    }
}
