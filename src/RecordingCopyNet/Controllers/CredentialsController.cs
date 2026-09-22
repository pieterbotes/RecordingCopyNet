using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Data;
using RecordingCopyNet.Models;
using RecordingCopyNet.Services.Google;
using RecordingCopyNet.Services.Zoom;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/credentials")]
public class CredentialsController : ControllerBase
{
    private readonly ICredentialStore _store;
    private readonly IZoomAuthService _zoomAuth;
    private readonly IGoogleAuthService _googleAuth;
    private readonly IZoomWebSocketController _wsController;

    public CredentialsController(ICredentialStore store, IZoomAuthService zoomAuth, IGoogleAuthService googleAuth, IZoomWebSocketController wsController)
    {
        _store = store;
        _zoomAuth = zoomAuth;
        _googleAuth = googleAuth;
        _wsController = wsController;
    }

    [HttpGet("{type}")]
    public ActionResult<ConfiguredResponse> GetConfigured(string type)
    {
        if (!CredentialSchema.TryParse(type, out var parsed))
            return BadRequest(new ErrorResponse($"Unknown credential type: {type}"));

        return Ok(new ConfiguredResponse(_store.Exists(parsed)));
    }

    [HttpPost("{type}")]
    public ActionResult<OkResponse> Save(string type, [FromBody] Dictionary<string, string?> body)
    {
        if (!CredentialSchema.TryParse(type, out var parsed))
            return BadRequest(new ErrorResponse($"Unknown credential type: {type}"));

        try
        {
            _store.Save(parsed, body);

            if (parsed == CredentialType.Zoom) _zoomAuth.ClearTokenCache();
            if (parsed == CredentialType.Google) _googleAuth.ClearDriveClient();
            if (parsed == CredentialType.Settings)
            {
                _googleAuth.ClearDriveClient();
                _wsController.StopConnectionAsync().GetAwaiter().GetResult();
                _wsController.StartConnectionAsync().GetAwaiter().GetResult();
            }

            return Ok(new OkResponse(true));
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    [HttpDelete("{type}")]
    public ActionResult<OkResponse> Delete(string type)
    {
        if (!CredentialSchema.TryParse(type, out var parsed))
            return BadRequest(new ErrorResponse($"Unknown credential type: {type}"));

        _store.Delete(parsed);
        if (parsed == CredentialType.Zoom) _zoomAuth.ClearTokenCache();
        if (parsed == CredentialType.Google) _googleAuth.ClearDriveClient();
        if (parsed == CredentialType.Settings) _googleAuth.ClearDriveClient();

        return Ok(new OkResponse(true));
    }
}
