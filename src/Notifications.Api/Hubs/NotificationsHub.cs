using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Api.Options;

namespace Notifications.Api.Hubs;

[Authorize]
public sealed class NotificationsHub : Hub
{
    private readonly IOptionsMonitor<WebNotificationsOptions> _optionsMonitor;
    private readonly ILogger<NotificationsHub> _logger;

    public NotificationsHub(
        IOptionsMonitor<WebNotificationsOptions> optionsMonitor,
        ILogger<NotificationsHub> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        if (!TryGetUserId(out var userId))
        {
            _logger.LogWarning("Rejecting SignalR connection {ConnectionId} because user id is missing or invalid.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var groupName = BuildUserGroup(userId);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetUserId(out var userId))
        {
            var groupName = BuildUserGroup(userId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName).ConfigureAwait(false);
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var principal = Context.User;
        var value = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal?.FindFirstValue("sub");

        return Guid.TryParse(value, out userId);
    }

    private string BuildUserGroup(Guid userId)
    {
        var options = _optionsMonitor.CurrentValue;
        return string.Concat(options.UserGroupPrefix, userId.ToString("N"));
    }
}
