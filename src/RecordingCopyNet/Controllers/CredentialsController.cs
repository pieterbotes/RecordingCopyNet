using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<CredentialsController> _logger;

    public CredentialsController(ICredentialStore store, IZoomAuthService zoomAuth, IGoogleAuthService googleAuth,
        IZoomWebSocketController wsController, ILogger<CredentialsController>? logger = null)
    {
        _store = store;
        _zoomAuth = zoomAuth;
        _googleAuth = googleAuth;
        _wsController = wsController;
        _logger = logger ?? NullLogger<CredentialsController>.Instance;
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

        var existing = _store.Load(parsed);
        var missing = FindMissingRequiredFields(parsed, body, existing);
        if (missing.Count > 0)
            return BadRequest(new ErrorResponse($"Missing required field(s): {string.Join(", ", missing)}"));

        try
        {
            _store.Save(parsed, body);
        }
        catch (Exception ex)
        {
            // The save itself is what the caller cares most about — report its failure,
            // and only its failure, as a 400.
            return BadRequest(new ErrorResponse(ex.Message));
        }

        if (parsed == CredentialType.Zoom) _zoomAuth.ClearTokenCache();
        if (parsed == CredentialType.Google) _googleAuth.ClearDriveClient();
        if (parsed == CredentialType.Settings)
        {
            _googleAuth.ClearDriveClient();
            try
            {
                // The credential save above already succeeded. A WebSocket restart
                // hiccup here is a separate concern and must not be reported back to
                // the caller as a failed credential save.
                _wsController.StopConnectionAsync().GetAwaiter().GetResult();
                _wsController.StartConnectionAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart the Zoom WebSocket connection after settings save.");
            }
        }

        return Ok(new OkResponse(true));
    }

    // Required fields (CredentialFieldDef.Required) must end up non-null after this save:
    // either the incoming body supplies a non-null value, or the field was already
    // present with a non-null value in the stored record (partial-update/rotation case).
    private static List<string> FindMissingRequiredFields(
        CredentialType type, IReadOnlyDictionary<string, string?> body, IReadOnlyDictionary<string, string?>? existing)
    {
        var missing = new List<string>();
        foreach (var def in CredentialSchema.Fields[type])
        {
            if (!def.Required) continue;

            string? effective = null;
            if (body.TryGetValue(def.Name, out var bodyValue))
                effective = bodyValue;
            else if (existing != null && existing.TryGetValue(def.Name, out var existingValue))
                effective = existingValue;

            if (effective is null) missing.Add(def.Name);
        }
        return missing;
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
