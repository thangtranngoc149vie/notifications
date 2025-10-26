# FISA Notifications — BE .NET Dev Kit (SNS + Lambda + Outbox) v1.0

> **Mục tiêu**: Tài liệu hợp nhất, một nơi đủ để Dev BE .NET code **push notifications** tới thiết bị (FCM/APNs) cho hệ thống FISA, theo kiến trúc **PostgreSQL + Outbox + AWS SNS + Lambda** và tuỳ chọn **SignalR** cho web realtime. Dev ~1 năm kinh nghiệm có thể triển khai ngay.

---

## 1) Kiến trúc tổng quan

```
(FISA .NET Services) ──► PostgreSQL (outbox_events)
       │
       ├── Outbox Worker .NET ► SNS Topic (FIFO: fisa-notify.fifo)
       │                              ├─ SQS (Archive/Audit)
       │                              ├─ Lambda: delivery-mobile (Python) ─► SNS Mobile Push ─► FCM/APNs ─► Devices
       │                              └─ Lambda: delivery-web (Optional) ─► SignalR/Redis/API GW WS
       │
       └── (Option nhanh) App .NET publish trực tiếp SNS (vẫn ghi outbox để audit)
```

- **Decouple** bằng SNS (fan-out), **Lambda** làm dispatcher glue đến thiết bị.
- **FIFO theo user** khi cần thứ tự: `MessageGroupId = user-{user_id}`; `MessageDeduplicationId = outbox_event_id[-user]`.
- **Idempotency & Durable**: trước hết **ghi outbox**; Worker chịu trách nhiệm publish + retry.

---

## 2) DDL & Migration (PostgreSQL)

### 2.1 `user_devices` (đăng ký thiết bị)
```sql
CREATE TABLE IF NOT EXISTS user_devices (
  id uuid PRIMARY KEY DEFAULT uuid_generate_v4(),
  user_id uuid NOT NULL,
  platform varchar(10) NOT NULL,        -- 'ios' | 'android' | 'web'
  fcm_token text,
  apns_token text,
  sns_endpoint_arn text,
  is_active boolean DEFAULT true,
  device_model text,
  app_version text,
  last_seen_at timestamptz DEFAULT now(),
  created_at timestamptz DEFAULT now(),
  updated_at timestamptz
);
CREATE INDEX IF NOT EXISTS idx_user_devices_user ON user_devices(user_id, is_active);
```

### 2.2 Outbox (bổ sung cột nếu thiếu)
```sql
ALTER TABLE public.outbox_events
  ADD COLUMN IF NOT EXISTS published_at timestamptz,
  ADD COLUMN IF NOT EXISTS failed_attempts int NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS next_retry_at timestamptz,
  ADD COLUMN IF NOT EXISTS last_error text;

CREATE INDEX IF NOT EXISTS idx_outbox_pending
  ON public.outbox_events (id)
  WHERE published_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_outbox_retry
  ON public.outbox_events (next_retry_at)
  WHERE published_at IS NULL;
```

---

## 3) Chuẩn Envelope (thông điệp chuẩn)
```json
{
  "event_id": "uuid-outbox-id",
  "event_type": "comment.added | work_item.assigned | ticket.sla_breach_soon | ...",
  "org_id": "uuid-org",
  "recipients": ["uuid-user-1", "uuid-user-2"],
  "title": "Tiêu đề ngắn",
  "body": "Nội dung tóm tắt",
  "icon": "comment|bell|warning|...",
  "severity": "info|success|warn|error",
  "deep_link": "fisa://work-item/xxxx?tab=comments",
  "extras": { "work_item_id": "...", "ticket_id": "...", "priority": "high" },
  "created_at": "2025-10-26T14:05:00Z",
  "correlation_id": "trace-id"
}
```
- **Bắt buộc**: `event_id`, `event_type`, `recipients`, `title|body`.
- **FIFO**: dùng `event_id` (hoặc `event_id-userId`) làm `MessageDeduplicationId`.

---

## 4) API BE .NET: Đăng ký/huỷ đăng ký thiết bị

### 4.1 OpenAPI mini
```yaml
paths:
  /api/v1/notifications/devices/register:
    post:
      summary: Register device token & bind SNS endpoint
      security: [{ bearerAuth: [] }]
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [platform]
              properties:
                platform: { type: string, enum: [ios, android, web] }
                fcm_token: { type: string }
                apns_token: { type: string }
                device_model: { type: string }
                app_version: { type: string }
      responses:
        '200':
          description: OK
          content:
            application/json:
              schema:
                type: object
                properties:
                  endpoint_arn: { type: string }
  /api/v1/notifications/devices/unregister:
    post:
      summary: Unregister device endpoint
      security: [{ bearerAuth: [] }]
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required: [endpoint_arn]
              properties:
                endpoint_arn: { type: string }
      responses:
        '200': { description: OK }
components:
  securitySchemes:
    bearerAuth:
      type: http
      scheme: bearer
      bearerFormat: JWT
```

### 4.2 Controller mẫu (.NET 8 + Dapper)
```csharp
[ApiController]
[Route("api/v1/notifications/devices")]
public class DevicesController : ControllerBase
{
    private readonly IDbConnectionFactory _db;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly ILogger<DevicesController> _log;
    private readonly string _gcmAppArn; // arn:aws:sns:...:app/GCM/fisa-android
    private readonly string _apnsAppArn; // arn:aws:sns:...:app/APNS/fisa-ios

    public DevicesController(IDbConnectionFactory db, IAmazonSimpleNotificationService sns, IOptions<AwsOptions> awsOpt, ILogger<DevicesController> log)
    { _db = db; _sns = sns; _gcmAppArn = awsOpt.Value.GcmAppArn; _apnsAppArn = awsOpt.Value.ApnsAppArn; _log = log; }

    [HttpPost("register")]
    [Authorize]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceReq req)
    {
        var userId = User.GetUserId();
        string platformArn = req.Platform?.ToLowerInvariant() == "ios" ? _apnsAppArn : _gcmAppArn;

        var create = await _sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = platformArn,
            Token = req.Platform == "ios" ? req.ApnsToken : req.FcmToken,
            CustomUserData = userId.ToString()
        });

        using var conn = _db.Get();
        var arn = await conn.ExecuteScalarAsync<string>(@"
            INSERT INTO user_devices(user_id, platform, fcm_token, apns_token, sns_endpoint_arn, device_model, app_version, is_active)
            VALUES (@user_id, @platform, @fcm, @apns, @arn, @model, @appv, true)
            ON CONFLICT (user_id, fcm_token)
            DO UPDATE SET sns_endpoint_arn = EXCLUDED.sns_endpoint_arn, is_active = true, updated_at = now()
            RETURNING sns_endpoint_arn;",
            new { user_id = userId, platform = req.Platform, fcm = req.FcmToken, apns = req.ApnsToken, arn = create.EndpointArn, model = req.DeviceModel, appv = req.AppVersion });

        return Ok(new { endpoint_arn = arn });
    }

    [HttpPost("unregister")]
    [Authorize]
    public async Task<IActionResult> Unregister([FromBody] UnregisterDeviceReq req)
    {
        await _sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = req.EndpointArn });
        using var conn = _db.Get();
        await conn.ExecuteAsync("UPDATE user_devices SET is_active = false, updated_at = now() WHERE sns_endpoint_arn = @arn;", new { arn = req.EndpointArn });
        return Ok();
    }
}

public record RegisterDeviceReq(string Platform, string? FcmToken, string? ApnsToken, string? DeviceModel, string? AppVersion);
public record UnregisterDeviceReq(string EndpointArn);
```

---

## 5) Producer Pattern (ghi outbox & publish)

### 5.1 Ghi outbox (trong transaction domain)
```sql
INSERT INTO outbox_events(aggregate, aggregate_id, event_type, payload)
VALUES ('work_item', :work_item_id, 'comment.added', :payload::jsonb)
RETURNING id;
```

### 5.2 Publish trực tiếp (tuỳ chọn) hoặc để Worker xử lý
```csharp
var sns = new AmazonSimpleNotificationServiceClient();
var env = new { /* theo Envelope chuẩn */ };
var req = new PublishRequest {
  TopicArn = cfg.TopicArn,
  Message = JsonSerializer.Serialize(env),
  MessageGroupId = $"user-{assigneeId}",
  MessageDeduplicationId = outboxId.ToString()
};
await sns.PublishAsync(req);
```

> Khuyến nghị: **để Outbox Worker publish** nhằm đảm bảo retry và kiểm soát.

---

## 6) Outbox Worker (.NET 8 + Dapper)

### 6.1 Thuật toán (batch + `FOR UPDATE SKIP LOCKED`)
1) Transaction `READ COMMITTED`.
2) Select lô sự kiện chưa gửi/đến hạn retry:
```sql
SELECT id, payload
FROM public.outbox_events
WHERE published_at IS NULL
  AND (next_retry_at IS NULL OR next_retry_at <= now())
ORDER BY id
FOR UPDATE SKIP LOCKED
LIMIT @batch;
```
3) Với mỗi bản ghi: deserialize **envelope**, publish SNS.
   - FIFO per user: tách publish theo từng `recipient`, `MessageGroupId = user-{uid}`, `MessageDeduplicationId = "{event_id}-{uid}"`.
4) OK → `published_at = now()`; Fail → tăng `failed_attempts`, set `next_retry_at` theo **backoff**; ghi `last_error`.
5) Commit; nếu không có bản ghi → `Delay(pollInterval)`.

### 6.2 Backoff đề xuất
- Lần 1: +1m; L2: +5m; L3: +15m; L4: +60m; L5: +6h; >L5: +1d & alert.

### 6.3 Code mẫu BackgroundService
```csharp
public sealed class OutboxWorker : BackgroundService
{
    private readonly ILogger<OutboxWorker> _log;
    private readonly IDbConnectionFactory _db;
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly OutboxWorkerOptions _opt;

    public OutboxWorker(ILogger<OutboxWorker> log, IDbConnectionFactory db, IAmazonSimpleNotificationService sns, IOptions<OutboxWorkerOptions> opt)
    { _log = log; _db = db; _sns = sns; _opt = opt.Value; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("OutboxWorker started");
        while (!ct.IsCancellationRequested)
        {
            var handled = false;
            using var conn = _db.Get();
            using var tx = await conn.BeginTransactionAsync(ct);

            var items = await conn.QueryAsync<dynamic>(@"
                SELECT id, payload
                FROM public.outbox_events
                WHERE published_at IS NULL
                  AND (next_retry_at IS NULL OR next_retry_at <= now())
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT @batch;", new { batch = _opt.BatchSize }, tx);

            foreach (var it in items)
            {
                handled = true;
                try
                {
                    var env = JsonSerializer.Deserialize<Envelope>(it.payload);
                    if (env?.recipients != null && env.recipients.Any() && _opt.Fifo)
                    {
                        foreach (var uid in env.recipients)
                        {
                            var req = new PublishRequest {
                                TopicArn = _opt.TopicArn,
                                Message = JsonSerializer.Serialize(env),
                                MessageGroupId = $"user-{uid}",
                                MessageDeduplicationId = $"{env.event_id}-{uid}"
                            };
                            await _sns.PublishAsync(req, ct);
                        }
                    }
                    else
                    {
                        var req = new PublishRequest { TopicArn = _opt.TopicArn, Message = JsonSerializer.Serialize(env) };
                        if (_opt.Fifo) { req.MessageGroupId = "broadcast"; req.MessageDeduplicationId = env!.event_id.ToString(); }
                        await _sns.PublishAsync(req, ct);
                    }

                    await conn.ExecuteAsync("UPDATE public.outbox_events SET published_at = now(), last_error = NULL WHERE id = @id;", new { id = (long)it.id }, tx);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Publish failed for outbox {Id}", (long)it.id);
                    await conn.ExecuteAsync(@"
                        UPDATE public.outbox_events
                        SET failed_attempts = failed_attempts + 1,
                            next_retry_at   = COALESCE(next_retry_at, now()) + @add,
                            last_error      = left(@err, 2000)
                        WHERE id = @id;",
                        new { id = (long)it.id, add = TimeSpan.FromMinutes(BackoffMinutes(it.failed_attempts + 1)), err = ex.Message }, tx);
                }
            }

            await tx.CommitAsync(ct);
            if (!handled) await Task.Delay(_opt.PollIntervalMs, ct);
        }
    }

    private static double BackoffMinutes(int attempt) => attempt switch { 1 => 1, 2 => 5, 3 => 15, 4 => 60, 5 => 360, _ => 1440 };
}

public sealed class OutboxWorkerOptions
{
    public string TopicArn { get; set; } = default!;
    public bool Fifo { get; set; } = true;
    public int BatchSize { get; set; } = 100;
    public int PollIntervalMs { get; set; } = 800;
}

public record Envelope(Guid event_id, string event_type, Guid org_id, IEnumerable<Guid> recipients, string title, string body, string? deep_link, object? extras);
```

### 6.4 Healthcheck, Observability
- **/healthz** hoặc `IHealthCheck` (Hosted Worker).
- Serilog: enrich `correlation-id`, log publish OK/Fail, tổng số record xử lý.
- Metrics: số outbox/giây, latency `created_at→published_at`, retry counts.

---

## 7) Lambda delivery-mobile (Python 3.12)

- **Trigger**: SNS `fisa-notify(.fifo)`.
- **Nhiệm vụ**: nhận envelope → lấy danh sách `sns_endpoint_arn` theo `user_id` → publish **Mobile Push** (APNs/FCM) bằng SNS → xử lý endpoint lỗi (disable/refresh) → log CloudWatch → (tuỳ chọn) đẩy bản sao sang SQS archive.

**Payload builder**
```python
import json

def build_platform_payload(msg):
    apns = {
      "aps": {"alert": {"title": msg["title"], "body": msg["body"]}, "sound": "default"},
      "custom": {"deep_link": msg.get("deep_link"), "extras": msg.get("extras", {})}
    }
    gcm = {
      "notification": {"title": msg["title"], "body": msg["body"]},
      "data": {"deep_link": msg.get("deep_link"), **(msg.get("extras") or {})}
    }
    return { "default": msg["body"], "APNS": json.dumps(apns), "APNS_SANDBOX": json.dumps(apns), "GCM": json.dumps(gcm) }
```

**Handler skeleton**
```python
import json, boto3
sns = boto3.client('sns')

def handler(event, context):
    for rec in event['Records']:
        msg = json.loads(rec['Sns']['Message'])
        for user_id in msg.get('recipients', []):
            for ep in get_devices(user_id):
                try:
                    sns.publish(TargetArn=ep, MessageStructure='json', Message=json.dumps(build_platform_payload(msg)))
                except Exception as e:
                    handle_endpoint_error(ep, e)
    return {"ok": True}
```

> `get_devices` có thể gọi BE internal API hoặc RDS Data API truy vấn `user_devices`.

---

## 8) Cấu hình & Triển khai AWS (Checklist)
1) **SNS Platform Applications**: GCM/FCM (Android), APNs (iOS).  
2) **SNS Topics**: `fisa-notify.fifo` (bật content-dedup), optional `fisa-notify` (standard).  
3) **SQS** (archive) & subscription từ topic.  
4) **Lambda** delivery-mobile (Python 3.12) + trigger SNS; (Optional) delivery-web.  
5) **IAM**: Lambda policy `sns:Publish`, `sns:CreatePlatformEndpoint`, `sns:Get/SetEndpointAttributes`; quyền gọi BE hoặc RDS Data API.  
6) **SSM Parameter Store/Secrets**: FCM key, BE base URL, DB creds (nếu Data API).  
7) **CloudWatch**: alarms cho Errors/Throttles; log retention 14–30 ngày.

---

## 9) appsettings mẫu & DI (.NET)

**appsettings.json**
```json
{
  "Aws": {
    "TopicArn": "arn:aws:sns:ap-southeast-1:123456789012:fisa-notify.fifo",
    "GcmAppArn": "arn:aws:sns:ap-southeast-1:123456789012:app/GCM/fisa-android",
    "ApnsAppArn": "arn:aws:sns:ap-southeast-1:123456789012:app/APNS/fisa-ios",
    "Fifo": true
  },
  "OutboxWorker": { "BatchSize": 100, "PollIntervalMs": 800 }
}
```

**Program.cs**
```csharp
builder.Services.AddSingleton<IAmazonSimpleNotificationService>(new AmazonSimpleNotificationServiceClient());
builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection("Aws"));
builder.Services.Configure<OutboxWorkerOptions>(builder.Configuration.GetSection("OutboxWorker"));

// Background worker
builder.Services.AddHostedService<OutboxWorker>();

// Serilog, Authentication/JWT, Dapper connection factory ...
```

```csharp
public sealed class AwsOptions { public string TopicArn { get; set; } = default!; public string GcmAppArn { get; set; } = default!; public string ApnsAppArn { get; set; } = default!; public bool Fifo { get; set; } }
```

---

## 10) Test Plan (Smoke & Integration)
- **Smoke**
  1) Register device Android/iOS → trả `endpoint_arn`.
  2) Tạo comment vào work item nơi user A là assignee → outbox record → Worker publish → thiết bị A nhận push & mở đúng **deep_link**.
  3) Đổi token/disable endpoint → Lambda handle & cập nhật `user_devices`.
  4) 10 sự kiện liên tiếp tới cùng user → **thứ tự đúng** trên thiết bị (FIFO per user).
  5) SQS archive nhận bản sao message.
- **Integration**
  - Dùng LocalStack mock SNS; test idempotency (dedupe); test retry/backoff bằng cách giả lập lỗi mạng.

---

## 11) Bảo mật, Hiệu năng, Chi phí
- **JWT + RBAC/ABAC** cho endpoints register/unregister.
- Không đưa dữ liệu nhạy cảm trong payload push; chỉ meta + deep link.
- Limit số thiết bị active mỗi user (ví dụ ≤5) + rate limit đăng ký.
- Với ~300 users: 1–2 Lambda + 1 topic FIFO đủ; chi phí thấp, scale tuyến tính theo số event.

---

## 12) DoD (Definition of Done)
- [ ] API register/unregister (Serilog, AuthN/Z, validation, rate limit) ✅  
- [ ] Outbox Worker chạy nền, batch + SKIP LOCKED, retry/backoff, idempotent ✅  
- [ ] Publish đúng FIFO per user (nếu bật) ✅  
- [ ] Lambda delivery-mobile hoạt động; xử lý endpoint lỗi ✅  
- [ ] Deep link mở đúng màn hình app ✅  
- [ ] SQS archive nhận bản sao message ✅  
- [ ] OpenAPI + README chạy local; ≥1 integration test (mock SNS) ✅

---

## 13) Phụ lục: Mẫu mã nguồn rút gọn

- **Controller**: xem mục 4.2 (Register/Unregister).
- **OutboxWorker**: xem mục 6.3 (BackgroundService).
- **Envelope record**: xem mục 6.3.
- **Lambda Python**: xem mục 7.

> **Ghi chú triển khai**: Nếu muốn đẩy realtime web, thêm **delivery-web** Lambda để gọi SignalR/Redis/API GW WS. Với FISA traffic hiện tại, SignalR + Redis là đủ.

