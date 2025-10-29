using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Api.Hubs;
using Notifications.Api.Models;
using Notifications.Api.Options;

namespace Notifications.Api.Services.Web;

public sealed class SignalRWebNotificationPublisher : IWebNotificationPublisher
{
    private readonly IHubContext<NotificationsHub> _hubContext;
    private readonly IOptionsMonitor<WebNotificationsOptions> _optionsMonitor;
    private readonly ILogger<SignalRWebNotificationPublisher> _logger;

    public SignalRWebNotificationPublisher(
        IHubContext<NotificationsHub> hubContext,
        IOptionsMonitor<WebNotificationsOptions> optionsMonitor,
        ILogger<SignalRWebNotificationPublisher> logger)
    {
        _hubContext = hubContext;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public bool IsEnabled => _optionsMonitor.CurrentValue.Enabled;

    public bool ShouldHandle(NotificationEnvelope envelope)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            return false;
        }

        if (envelope.Recipients is null || envelope.Recipients.Count == 0)
        {
            _logger.LogWarning("Skipping SignalR delivery for event {EventId} because it has no recipients.", envelope.EventId);
            return false;
        }

        if (!options.RequireChannelTag)
        {
            return true;
        }

        var channels = envelope.Channels;
        if (channels is null || channels.Count == 0)
        {
            return false;
        }

        foreach (var channel in channels)
        {
            if (string.Equals(channel, options.ChannelTag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task PublishAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            return;
        }

        var recipients = envelope.Recipients;
        if (recipients is null || recipients.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>(Math.Min(recipients.Count, options.MaxBatchSize));
        foreach (var recipient in recipients)
        {
            var groupName = string.Concat(options.UserGroupPrefix, recipient.ToString("N"));
            tasks.Add(_hubContext.Clients.Group(groupName)
                .SendAsync(options.BroadcastMethod, envelope, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
