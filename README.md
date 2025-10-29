# Notifications service

This repository contains a .NET 8 worker-enabled Web API that implements the FISA notification delivery pattern using PostgreSQL outbox + AWS SNS and optional SignalR web notifications.

## Projects

- `src/Notifications.Api` – ASP.NET Core Web API exposing device registration endpoints, hosting the outbox background worker, and serving a SignalR hub for realtime web notifications.

## SignalR web notifications

The API exposes an authenticated SignalR hub at `/hubs/notifications` (configurable through `WebNotifications` options). Connections join a per-user group named `user-{userId:N}` so outbox events can be fan-out to all active browser tabs for the same account.

- Enable the hub by setting `WebNotifications:Enabled` to `true` (enabled by default in `appsettings.Development.json`).
- The outbox worker delivers payloads to SignalR when the envelope includes the `channels` property containing `"web"` (configurable via `WebNotifications:ChannelTag`).
- Incoming JWT bearer tokens are accepted either via the standard `Authorization` header or the `access_token` query string for WebSocket negotiation, matching the SignalR guidance.

Clients should listen for the `notificationReceived` event and expect the payload to match the shared `NotificationEnvelope` schema defined in the specification.

## Getting started

1. Update `appsettings.json` (or user secrets) with the correct PostgreSQL connection string, AWS resource ARNs, and web notification preferences.
2. Ensure the `user_devices` and `outbox_events` tables follow the DDL in the specification (`fisa_notifications_be_net_dev_kit_sns_lambda_outbox_v_1_0.md`).
3. Run database migrations before starting the API.
4. Build and run the API:

   ```bash
   dotnet build
   dotnet run --project src/Notifications.Api
   ```

The hosted service `OutboxWorker` continuously polls `outbox_events`, publishes notifications to SNS (FIFO aware), and, when enabled, mirrors the same envelopes to SignalR clients subscribed to the `/hubs/notifications` hub before marking records as delivered or scheduling retries.
