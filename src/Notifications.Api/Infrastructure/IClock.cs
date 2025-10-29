namespace Notifications.Api.Infrastructure;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
