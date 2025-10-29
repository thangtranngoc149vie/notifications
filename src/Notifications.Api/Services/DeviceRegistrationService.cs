using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Api.Contracts.Requests;
using Notifications.Api.Data;
using Notifications.Api.Infrastructure;
using Notifications.Api.Options;

namespace Notifications.Api.Services;

public interface IDeviceRegistrationService
{
    Task<string> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken cancellationToken);

    Task<bool> UnregisterDeviceAsync(Guid userId, UnregisterDeviceRequest request, CancellationToken cancellationToken);
}

public sealed class DeviceRegistrationService : IDeviceRegistrationService
{
    private readonly INpgsqlConnectionFactory _connectionFactory;
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly IOptionsMonitor<AwsOptions> _awsOptionsMonitor;
    private readonly ILogger<DeviceRegistrationService> _logger;
    private readonly IClock _clock;

    public DeviceRegistrationService(
        INpgsqlConnectionFactory connectionFactory,
        IAmazonSimpleNotificationService snsClient,
        IOptionsMonitor<AwsOptions> awsOptionsMonitor,
        ILogger<DeviceRegistrationService> logger,
        IClock clock)
    {
        _connectionFactory = connectionFactory;
        _snsClient = snsClient;
        _awsOptionsMonitor = awsOptionsMonitor;
        _logger = logger;
        _clock = clock;
    }

    public async Task<string> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _awsOptionsMonitor.CurrentValue;
        var platform = request.Platform?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new ArgumentException("Platform is required", nameof(request));
        }

        string token = platform switch
        {
            "ios" => request.ApnsToken ?? throw new ArgumentException("APNs token is required for iOS devices", nameof(request)),
            "android" => request.FcmToken ?? throw new ArgumentException("FCM token is required for Android devices", nameof(request)),
            "web" => request.FcmToken ?? request.ApnsToken ?? throw new ArgumentException("A device token is required", nameof(request)),
            _ => throw new ArgumentException($"Unsupported platform '{request.Platform}'", nameof(request))
        };

        var platformArn = platform switch
        {
            "ios" => options.ApnsAppArn,
            "android" => options.GcmAppArn,
            "web" => options.WebAppArn ?? options.GcmAppArn,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(platformArn))
        {
            throw new InvalidOperationException($"Platform application ARN is not configured for platform '{platform}'.");
        }

        var endpointResponse = await _snsClient.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = platformArn,
            Token = token,
            CustomUserData = userId.ToString()
        }, cancellationToken).ConfigureAwait(false);

        var endpointArn = endpointResponse.EndpointArn;

        // Ensure the endpoint is enabled and up to date.
        await _snsClient.SetEndpointAttributesAsync(new SetEndpointAttributesRequest
        {
            EndpointArn = endpointArn,
            Attributes = new Dictionary<string, string>
            {
                ["Token"] = token,
                ["Enabled"] = "true"
            }
        }, cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new
        {
            UserId = userId,
            Platform = platform,
            request.FcmToken,
            request.ApnsToken,
            EndpointArn = endpointArn,
            request.DeviceModel,
            request.AppVersion
        };

        const string updateSql = """
UPDATE user_devices
SET platform = @Platform,
    fcm_token = @FcmToken,
    apns_token = @ApnsToken,
    sns_endpoint_arn = @EndpointArn,
    device_model = @DeviceModel,
    app_version = @AppVersion,
    is_active = true,
    last_seen_at = now(),
    updated_at = now()
WHERE user_id = @UserId
  AND (
        sns_endpoint_arn = @EndpointArn
        OR (@FcmToken IS NOT NULL AND fcm_token IS NOT DISTINCT FROM @FcmToken)
        OR (@ApnsToken IS NOT NULL AND apns_token IS NOT DISTINCT FROM @ApnsToken)
      );
""";

        var affected = await connection.ExecuteAsync(new CommandDefinition(updateSql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            const string insertSql = """
INSERT INTO user_devices (
    id,
    user_id,
    platform,
    fcm_token,
    apns_token,
    sns_endpoint_arn,
    device_model,
    app_version,
    is_active,
    last_seen_at,
    created_at,
    updated_at)
VALUES (
    @Id,
    @UserId,
    @Platform,
    @FcmToken,
    @ApnsToken,
    @EndpointArn,
    @DeviceModel,
    @AppVersion,
    true,
    now(),
    now(),
    now());
""";

            var insertParameters = new
            {
                Id = Guid.NewGuid(),
                parameters.UserId,
                parameters.Platform,
                parameters.FcmToken,
                parameters.ApnsToken,
                parameters.EndpointArn,
                parameters.DeviceModel,
                parameters.AppVersion
            };

            await connection.ExecuteAsync(new CommandDefinition(insertSql, insertParameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Registered device for user {UserId} with endpoint {Endpoint}", userId, endpointArn);

        return endpointArn;
    }

    public async Task<bool> UnregisterDeviceAsync(Guid userId, UnregisterDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const string deactivateSql = """
UPDATE user_devices
SET is_active = false,
    updated_at = now(),
    last_seen_at = @Now
WHERE user_id = @UserId
  AND sns_endpoint_arn = @EndpointArn;
""";

        var affected = await connection.ExecuteAsync(new CommandDefinition(deactivateSql, new
        {
            UserId = userId,
            request.EndpointArn,
            Now = _clock.UtcNow
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            return false;
        }

        try
        {
            await _snsClient.DeleteEndpointAsync(new DeleteEndpointRequest
            {
                EndpointArn = request.EndpointArn
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            _logger.LogWarning("SNS endpoint {Endpoint} was not found during unregister", request.EndpointArn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete SNS endpoint {Endpoint}", request.EndpointArn);
        }

        return true;
    }
}
