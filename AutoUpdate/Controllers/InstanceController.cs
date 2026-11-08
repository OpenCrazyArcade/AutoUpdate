using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Managers;

namespace OpenCrazyArcade.AutoUpdate.Controllers;

/// <summary>
/// The launcher's commands about itself, for another launcher process (see <see cref="InstanceControl"/>): bring the
/// window up, or act on an <c>opencrazyarcade://</c> link. Reached only over the control named pipe. Whatever arrives
/// is still validated here and confirmed by the player in the window before it changes anything.
/// </summary>
[ApiController]
[Route("instance")]
public sealed class InstanceController(RemoteCommands remote, ILogger<InstanceController> logger) : ControllerBase
{
    public sealed record LinkRequest(string Uri);

    [HttpPost("activate")]
    public IActionResult Activate()
    {
        logger.LogInformation("[INSTANCE] yeu cau hien cua so tu instance khac");
        remote.Submit("ACTIVATE");
        return Accepted();
    }

    [HttpPost("link")]
    [RequestSizeLimit(16 * 1024)]
    public IActionResult Link([FromBody] LinkRequest request)
    {
        string uri = request?.Uri;
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > SingleInstance.MaxLine || !ProtocolHandler.IsLink(uri))
            return BadRequest();

        remote.Submit("URI\t" + uri);
        return Accepted();
    }
}
