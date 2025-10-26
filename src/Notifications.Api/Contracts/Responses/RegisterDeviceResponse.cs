using System.Text.Json.Serialization;

namespace Notifications.Api.Contracts.Responses;

public sealed class RegisterDeviceResponse
{
    public RegisterDeviceResponse(string endpointArn)
    {
        EndpointArn = endpointArn;
    }

    [JsonPropertyName("endpoint_arn")]
    public string EndpointArn { get; }
}
