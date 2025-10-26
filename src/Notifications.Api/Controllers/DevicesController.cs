using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Api.Contracts.Requests;
using Notifications.Api.Contracts.Responses;
using Notifications.Api.Services;

namespace Notifications.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/notifications/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly IDeviceRegistrationService _deviceRegistrationService;
    private readonly ICurrentUserProvider _currentUserProvider;

    public DevicesController(
        IDeviceRegistrationService deviceRegistrationService,
        ICurrentUserProvider currentUserProvider)
    {
        _deviceRegistrationService = deviceRegistrationService;
        _currentUserProvider = currentUserProvider;
    }

    [HttpPost("register")]
    [ProducesResponseType(typeof(RegisterDeviceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterDeviceResponse>> RegisterDeviceAsync(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUserProvider.GetUserId();
        var endpointArn = await _deviceRegistrationService.RegisterDeviceAsync(userId, request, cancellationToken).ConfigureAwait(false);
        return Ok(new RegisterDeviceResponse(endpointArn));
    }

    [HttpPost("unregister")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnregisterDeviceAsync(
        [FromBody] UnregisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUserProvider.GetUserId();
        var deactivated = await _deviceRegistrationService.UnregisterDeviceAsync(userId, request, cancellationToken).ConfigureAwait(false);
        if (!deactivated)
        {
            return NotFound();
        }

        return NoContent();
    }
}
