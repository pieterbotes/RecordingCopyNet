using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ICredentialStore _store;

    public SettingsController(ICredentialStore store) => _store = store;

    [HttpGet]
    public ActionResult<SettingsResponse> Get()
    {
        var settings = _store.Load(CredentialType.Settings) ?? new Dictionary<string, string?>();
        return Ok(new SettingsResponse(settings, _store.Exists(CredentialType.Zoom), _store.Exists(CredentialType.Google)));
    }
}
