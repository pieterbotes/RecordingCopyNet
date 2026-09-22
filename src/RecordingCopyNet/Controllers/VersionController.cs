using Microsoft.AspNetCore.Mvc;
using RecordingCopyNet.Config;
using RecordingCopyNet.Models;

namespace RecordingCopyNet.Controllers;

[ApiController]
[Route("api/version")]
public class VersionController : ControllerBase
{
    [HttpGet]
    public ActionResult<VersionResponse> Get() => new VersionResponse(AppVersion.Current);
}
