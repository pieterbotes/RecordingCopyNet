using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RecordingCopyNet.Config;
using RecordingCopyNet.Data;
using RecordingCopyNet.Services.Zoom;
using RecordingCopyNet.Tests.TestHelpers;
using Xunit;

namespace RecordingCopyNet.Tests.Services.Zoom;

public class ZoomAuthServiceTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string?>? ZoomFields;
        public bool Exists(CredentialType type) => type == CredentialType.Zoom && ZoomFields != null;
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) { }
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) =>
            type == CredentialType.Zoom ? ZoomFields : null;
        public void Delete(CredentialType type) { }
    }

    private static ZoomAuthService BuildService(FakeHttpMessageHandler handler, FakeCredentialStore store)
    {
        var client = new HttpClient(handler);
        var config = Options.Create(new AppConfig { ZoomAuthUrl = "https://zoom.us/oauth/token" });
        return new ZoomAuthService(client, store, config);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsTokenFromResponse()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "secret" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "tok-123", expires_in = 3600 })
        });
        var service = BuildService(handler, store);

        var token = await service.GetAccessTokenAsync();

        Assert.Equal("tok-123", token);
        Assert.Single(handler.Requests);
        Assert.Equal("Basic", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesTokenAcrossCalls()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "secret" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "tok-123", expires_in = 3600 })
        });
        var service = BuildService(handler, store);

        await service.GetAccessTokenAsync();
        await service.GetAccessTokenAsync();

        Assert.Single(handler.Requests); // second call served from cache
    }

    [Fact]
    public async Task ClearTokenCache_ForcesRefetchOnNextCall()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "secret" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "tok-123", expires_in = 3600 })
        });
        var service = BuildService(handler, store);
        await service.GetAccessTokenAsync();

        service.ClearTokenCache();
        await service.GetAccessTokenAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWithBodyOnFailure()
    {
        var store = new FakeCredentialStore
        {
            ZoomFields = new() { ["account_id"] = "acct", ["client_id"] = "id", ["client_secret"] = "wrong" }
        };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid_client")
        });
        var service = BuildService(handler, store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccessTokenAsync());
        Assert.Contains("invalid_client", ex.Message);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenNoCredentialsConfigured()
    {
        var store = new FakeCredentialStore { ZoomFields = null };
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var service = BuildService(handler, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAccessTokenAsync());
    }
}
