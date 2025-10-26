using System.Text.Json.Serialization;

namespace Notifications.Api.Models;

public sealed class OutboxEventRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; init; }

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("failed_attempts")]
    public int FailedAttempts { get; init; }

    [JsonPropertyName("next_retry_at")]
    public DateTimeOffset? NextRetryAt { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }
}
