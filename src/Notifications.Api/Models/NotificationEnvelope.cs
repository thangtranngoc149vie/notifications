using System.Text.Json.Serialization;

namespace Notifications.Api.Models;

public sealed class NotificationEnvelope
{
    [JsonPropertyName("event_id")]
    public Guid EventId { get; set; }

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("org_id")]
    public Guid? OrgId { get; set; }

    [JsonPropertyName("recipients")]
    public IReadOnlyCollection<Guid> Recipients { get; set; } = Array.Empty<Guid>();

    [JsonPropertyName("channels")]
    public IReadOnlyCollection<string>? Channels { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("deep_link")]
    public string? DeepLink { get; set; }

    [JsonPropertyName("extras")]
    public Dictionary<string, object>? Extras { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }
}
