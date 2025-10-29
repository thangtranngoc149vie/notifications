using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Notifications.Api.Contracts.Requests;

public sealed class RegisterDeviceRequest
{
    [Required]
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("fcm_token")]
    public string? FcmToken { get; set; }

    [JsonPropertyName("apns_token")]
    public string? ApnsToken { get; set; }

    [JsonPropertyName("device_model")]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("app_version")]
    public string? AppVersion { get; set; }
}
