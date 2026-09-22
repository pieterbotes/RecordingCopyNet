using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Controllers;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using Xunit;

namespace RecordingCopyNet.Tests.Controllers;

public class SettingsControllerTests
{
    private class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<CredentialType, Dictionary<string, string?>> Saved = new();
        public bool Exists(CredentialType type) => Saved.ContainsKey(type);
        public void Save(CredentialType type, IReadOnlyDictionary<string, string?> fields) => Saved[type] = new(fields);
        public IReadOnlyDictionary<string, string?>? Load(CredentialType type) => Saved.GetValueOrDefault(type);
        public void Delete(CredentialType type) => Saved.Remove(type);
    }

    [Fact]
    public void Get_ReturnsEmptySettingsAndFalseFlags_WhenNothingConfigured()
    {
        var controller = new SettingsController(new FakeCredentialStore());

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = (SettingsResponse)ok.Value!;
        Assert.Empty(body.Settings);
        Assert.False(body.ZoomConfigured);
        Assert.False(body.GoogleConfigured);
    }

    [Fact]
    public void Get_ReturnsStoredSettingsAndConfiguredFlags()
    {
        var store = new FakeCredentialStore();
        store.Save(CredentialType.Settings, new Dictionary<string, string?> { ["google_folder_id"] = "abc" });
        store.Save(CredentialType.Zoom, new Dictionary<string, string?> { ["client_id"] = "x" });
        var controller = new SettingsController(store);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = (SettingsResponse)ok.Value!;
        Assert.Equal("abc", body.Settings["google_folder_id"]);
        Assert.True(body.ZoomConfigured);
        Assert.False(body.GoogleConfigured);
    }
}
