# Dịch vụ Notifications

## Tổng quan
Dự án cung cấp một Web API xây dựng trên .NET 8 dùng cho hệ thống thông báo của FISA. Ứng dụng kết hợp mô hình outbox với PostgreSQL, đẩy thông điệp qua AWS SNS, đồng thời hỗ trợ tùy chọn thông báo thời gian thực tới web thông qua SignalR. Tất cả endpoint REST đều yêu cầu xác thực JWT theo chuẩn Bearer.

## Thành phần và tính năng chính
- **API ASP.NET Core**: phơi bày nhóm API `/api/v1/notifications/devices` cho phép ứng dụng di động/web đăng ký hoặc hủy đăng ký thiết bị nhận push.
- **Tích hợp AWS SNS**: tạo và quản lý Platform Endpoint cho từng thiết bị, xuất bản thông điệp ra Topic (hỗ trợ cả FIFO với MessageGroupId theo người dùng).
- **Kho outbox trên PostgreSQL**: các bản ghi tại bảng `outbox_events` được khóa, đánh dấu retry và cập nhật trạng thái `published_at`, `failed_attempts`, `next_retry_at`.
- **Nền tảng SignalR (tùy chọn)**: hub `/hubs/notifications` cho phép trình duyệt nhận sự kiện `notificationReceived` nếu thông điệp có channel `web` (có thể cấu hình).
- **Worker nền OutboxWorker**: chạy liên tục, xử lý batch, gửi SNS/SignalR, áp dụng backoff lũy thừa cho retry và ghi log lỗi.
- **Hỗ trợ đa nền tảng thiết bị**: phân biệt iOS (APNs), Android (FCM), Web với ARN tương ứng; tự cập nhật token, model thiết bị, app version.

## API hiện có
| Method | Đường dẫn | Request body | Mô tả |
| --- | --- | --- | --- |
| `POST` | `/api/v1/notifications/devices/register` | `RegisterDeviceRequest` với thông tin nền tảng, token (FCM/APNs), model, version | Tạo hoặc cập nhật SNS endpoint và lưu thiết bị đang hoạt động trong `user_devices` cho người dùng hiện tại. Trả về `RegisterDeviceResponse` chứa `endpoint_arn`. |
| `POST` | `/api/v1/notifications/devices/unregister` | `UnregisterDeviceRequest` với `endpoint_arn` | Đánh dấu thiết bị không hoạt động, cố gắng xóa endpoint khỏi SNS. Trả về `204 No Content` hoặc `404` nếu không tìm thấy. |

> Ghi chú: thông tin người dùng được lấy từ JWT thông qua `ICurrentUserProvider` (yêu cầu claim `sub`).

## Luồng xử lý thông báo
1. Dịch vụ khác ghi một hàng vào `outbox_events` với payload theo schema `NotificationEnvelope`.
2. `OutboxWorker` khóa các bản ghi chưa xuất bản, deserialize payload và kiểm tra danh sách người nhận.
3. Với SNS:
   - Nếu Topic là FIFO, worker nhân bản payload cho từng người nhận, đặt `MessageGroupId` theo `user-{recipient}`.
   - Nếu Topic thường, gửi payload nguyên bản cùng các thuộc tính như `event_type`, `correlation_id`.
4. Với SignalR (khi bật): worker kiểm tra channel tag (`web` theo mặc định) rồi phát tới nhóm `user-{userId}` thông qua hub.
5. Khi gửi thành công, bản ghi được cập nhật `published_at`. Nếu lỗi, worker tăng `failed_attempts`, tính `next_retry_at` với backoff tối đa `MaxBackoffSeconds`.

## Cấu hình cần thiết
### CSDL PostgreSQL
- Bảng `user_devices` và `outbox_events` phải khớp mô tả trong tài liệu FISA Dev Kit.
- Biến cấu hình: `ConnectionStrings:Default` dùng cho factory `NpgsqlConnectionFactory`.

### AWS SNS (`AwsOptions`)
- `TopicArn`: ARN topic chính để worker xuất bản thông báo.
- `GcmAppArn`, `ApnsAppArn`, `WebAppArn`: ARN ứng dụng SNS cho từng nền tảng (web mặc định dùng FCM ARN nếu không đặt riêng).
- `Fifo`: `true` nếu Topic là FIFO để bật logic tách người nhận.

### Outbox worker (`OutboxWorkerOptions`)
- `BatchSize`: số bản ghi lấy mỗi vòng (mặc định 100).
- `PollIntervalMs`: thời gian nghỉ giữa các vòng xử lý khi không có dữ liệu (mặc định 800ms).
- `MaxRetryAttempts`, `BaseRetrySeconds`, `MaxBackoffSeconds`: tham số retry/backoff.

### Web Notifications (`WebNotifications`)
- `Enabled`: bật/tắt SignalR.
- `HubPath`: đường dẫn hub (mặc định `/hubs/notifications`).
- `BroadcastMethod`: tên phương thức client lắng nghe (`notificationReceived`).
- `UserGroupPrefix`: prefix nhóm người dùng (`user-`).
- `RequireChannelTag` & `ChannelTag`: kiểm soát channel cần khớp (mặc định `web`).
- `MaxBatchSize`: số thông báo gửi cho mỗi lần phát.

## Phạm vi & giới hạn hiện tại
- Tập trung vào đăng ký thiết bị, worker phát thông báo và truyền tải qua SNS/SignalR; chưa bao gồm API quản lý nội dung thông báo hay lịch sử đọc.
- Chưa xây dựng cơ chế quản trị người dùng, xác thực được giả định do gateway/dịch vụ ngoài xử lý.
- Việc đẩy vào bảng `outbox_events` nằm ngoài phạm vi dự án (các dịch vụ khác chịu trách nhiệm).

## Cách chạy dự án
1. Cập nhật `appsettings.json` (hoặc user secrets) với thông tin PostgreSQL, AWS và Web Notifications tương ứng môi trường.
2. Khởi tạo/migration CSDL theo tài liệu đặc tả.
3. Xây dựng và chạy API:
   ```bash
   dotnet build
   dotnet run --project src/Notifications.Api
   ```
4. Kết nối thử SignalR bằng token hợp lệ tới `https://<host>/hubs/notifications?access_token=<jwt>` và lắng nghe sự kiện `notificationReceived`.

## Ghi chú cập nhật
- **2024-05-20**: Bổ sung hạ tầng SignalR, cập nhật worker xử lý channel `web`, ghi chú cấu hình.
- **2024-05-18**: Hoàn thiện API đăng ký/hủy đăng ký thiết bị, worker outbox phát SNS với retry/backoff.
