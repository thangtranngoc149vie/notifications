using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Notifications.Api.Data;
using Notifications.Api.Infrastructure;
using Notifications.Api.Models;
using Notifications.Api.Options;
using Notifications.Api.Services.Web;

namespace Notifications.Api.Workers;

public sealed class OutboxWorker : BackgroundService
{
    private const string QuerySql = """
SELECT id, event_type, payload, created_at, published_at, failed_attempts, next_retry_at, last_error
FROM public.outbox_events
WHERE published_at IS NULL
  AND (next_retry_at IS NULL OR next_retry_at <= now())
ORDER BY created_at
FOR UPDATE SKIP LOCKED
LIMIT @BatchSize;
""";

    private const string MarkPublishedSql = """
UPDATE public.outbox_events
SET published_at = @PublishedAt,
    failed_attempts = 0,
    next_retry_at = NULL,
    last_error = NULL
WHERE id = @Id;
""";

    private const string MarkFailedSql = """
UPDATE public.outbox_events
SET failed_attempts = @FailedAttempts,
    next_retry_at = @NextRetryAt,
    last_error = @LastError
WHERE id = @Id;
""";

    private readonly INpgsqlConnectionFactory _connectionFactory;
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly IOptionsMonitor<OutboxWorkerOptions> _workerOptions;
    private readonly IOptionsMonitor<AwsOptions> _awsOptions;
    private readonly ILogger<OutboxWorker> _logger;
    private readonly IWebNotificationPublisher? _webNotificationPublisher;
    private readonly IClock _clock;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public OutboxWorker(
        INpgsqlConnectionFactory connectionFactory,
        IAmazonSimpleNotificationService snsClient,
        IOptionsMonitor<OutboxWorkerOptions> workerOptions,
        IOptionsMonitor<AwsOptions> awsOptions,
        ILogger<OutboxWorker> logger,
        IClock clock,
        IWebNotificationPublisher? webNotificationPublisher = null)
    {
        _connectionFactory = connectionFactory;
        _snsClient = snsClient;
        _workerOptions = workerOptions;
        _awsOptions = awsOptions;
        _logger = logger;
        _clock = clock;
        _webNotificationPublisher = webNotificationPublisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_workerOptions.CurrentValue.PollIntervalMs), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while processing outbox batch");
                await Task.Delay(TimeSpan.FromMilliseconds(_workerOptions.CurrentValue.PollIntervalMs), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Outbox worker stopped");
    }

    private async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var awsOptions = _awsOptions.CurrentValue;
        var snsEnabled = !string.IsNullOrWhiteSpace(awsOptions.TopicArn);
        var webPublisher = _webNotificationPublisher;
        var webEnabled = webPublisher?.IsEnabled == true;

        if (!snsEnabled && !webEnabled)
        {
            _logger.LogWarning("No delivery channel configured. Outbox worker will pause.");
            await Task.Delay(TimeSpan.FromMilliseconds(_workerOptions.CurrentValue.PollIntervalMs), cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var records = (await connection.QueryAsync<OutboxEventRecord>(new CommandDefinition(
            QuerySql,
            new { BatchSize = _workerOptions.CurrentValue.BatchSize },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        if (records.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var record in records)
        {
            await ProcessRecordAsync(record, connection, transaction, awsOptions, snsEnabled, webPublisher, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessRecordAsync(
        OutboxEventRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AwsOptions awsOptions,
        bool snsEnabled,
        IWebNotificationPublisher? webPublisher,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<NotificationEnvelope>(record.Payload, _serializerOptions)
                ?? throw new InvalidOperationException($"Outbox event {record.Id} does not contain a valid notification envelope.");

            if (envelope.Recipients is null || envelope.Recipients.Count == 0)
            {
                throw new InvalidOperationException($"Outbox event {record.Id} has no recipients.");
            }

            await PublishAsync(record, envelope, awsOptions, snsEnabled, webPublisher, cancellationToken).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                MarkPublishedSql,
                new { Id = record.Id, PublishedAt = _clock.UtcNow },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish outbox event {EventId}", record.Id);
            await MarkFailureAsync(record, connection, transaction, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(
        OutboxEventRecord record,
        NotificationEnvelope envelope,
        AwsOptions awsOptions,
        bool snsEnabled,
        IWebNotificationPublisher? webPublisher,
        CancellationToken cancellationToken)
    {
        if (snsEnabled)
        {
            var messageAttributes = BuildMessageAttributes(envelope);

            if (awsOptions.Fifo)
            {
                await foreach (var request in BuildFifoRequestsAsync(record, envelope, awsOptions, messageAttributes, cancellationToken))
                {
                    await _snsClient.PublishAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var request = new PublishRequest
                {
                    TopicArn = awsOptions.TopicArn,
                    Message = record.Payload,
                    MessageAttributes = messageAttributes
                };

                await _snsClient.PublishAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _logger.LogDebug("Skipping SNS publish for event {EventId} because the topic ARN is not configured.", record.Id);
        }

        if (webPublisher is not null && webPublisher.ShouldHandle(envelope))
        {
            await webPublisher.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<PublishRequest> BuildFifoRequestsAsync(
        OutboxEventRecord record,
        NotificationEnvelope envelope,
        AwsOptions awsOptions,
        Dictionary<string, MessageAttributeValue> messageAttributes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var node = JsonNode.Parse(record.Payload) as JsonObject
            ?? throw new InvalidOperationException($"Unable to parse payload for outbox event {record.Id}");

        foreach (var recipient in envelope.Recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clone = node.DeepClone().AsObject();
            clone["recipients"] = new JsonArray(JsonValue.Create(recipient));
            var message = clone.ToJsonString(_serializerOptions);

            yield return new PublishRequest
            {
                TopicArn = awsOptions.TopicArn,
                Message = message,
                MessageAttributes = messageAttributes,
                MessageGroupId = $"user-{recipient}",
                MessageDeduplicationId = $"{record.Id:N}-{recipient:N}"
            };
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, MessageAttributeValue> BuildMessageAttributes(NotificationEnvelope envelope)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
        {
            ["event_type"] = new MessageAttributeValue { DataType = "String", StringValue = envelope.EventType }
        };

        if (!string.IsNullOrWhiteSpace(envelope.CorrelationId))
        {
            attributes["correlation_id"] = new MessageAttributeValue { DataType = "String", StringValue = envelope.CorrelationId };
        }

        return attributes;
    }

    private async Task MarkFailureAsync(
        OutboxEventRecord record,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var options = _workerOptions.CurrentValue;
        var attempts = record.FailedAttempts + 1;
        var delaySeconds = CalculateDelaySeconds(attempts, options);
        var nextRetry = _clock.UtcNow.AddSeconds(delaySeconds);

        await connection.ExecuteAsync(new CommandDefinition(
            MarkFailedSql,
            new
            {
                Id = record.Id,
                FailedAttempts = attempts,
                NextRetryAt = nextRetry,
                LastError = exception.Message
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (attempts >= options.MaxRetryAttempts)
        {
            _logger.LogWarning("Outbox event {EventId} reached max retry attempts", record.Id);
        }
    }

    private static double CalculateDelaySeconds(int attempts, OutboxWorkerOptions options)
    {
        if (attempts <= 0)
        {
            return options.BaseRetrySeconds;
        }

        var exponential = options.BaseRetrySeconds * Math.Pow(2, Math.Max(0, attempts - 1));
        return Math.Min(options.MaxBackoffSeconds, exponential);
    }
}
