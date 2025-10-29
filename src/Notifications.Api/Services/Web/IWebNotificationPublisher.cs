using Notifications.Api.Models;

namespace Notifications.Api.Services.Web;

public interface IWebNotificationPublisher
{
    bool IsEnabled { get; }

    bool ShouldHandle(NotificationEnvelope envelope);

    Task PublishAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
