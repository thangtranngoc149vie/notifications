namespace Notifications.Api.Options;

public sealed class OutboxWorkerOptions
{
    public int BatchSize { get; set; } = 100;
    public int PollIntervalMs { get; set; } = 800;
    public int MaxRetryAttempts { get; set; } = 10;
    public int BaseRetrySeconds { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 300;
}
