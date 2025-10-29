namespace Notifications.Api.Options;

public sealed class AwsOptions
{
    public string TopicArn { get; set; } = string.Empty;
    public string? GcmAppArn { get; set; }
    public string? ApnsAppArn { get; set; }
    public string? WebAppArn { get; set; }
    public bool Fifo { get; set; }
}
