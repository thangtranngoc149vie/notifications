using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Notifications.Api.Contracts.Requests;

public sealed class UnregisterDeviceRequest
{
    [Required]
    [JsonPropertyName("endpoint_arn")]
    public string EndpointArn { get; set; } = string.Empty;
}
