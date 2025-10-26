# Notifications service

This repository contains a .NET 8 worker-enabled Web API that implements the FISA notification delivery pattern using PostgreSQL outbox + AWS SNS.

## Projects

- `src/Notifications.Api` – ASP.NET Core Web API exposing device registration endpoints and hosting the outbox background worker.

## Getting started

1. Update `appsettings.json` (or user secrets) with the correct PostgreSQL connection string and AWS resource ARNs.
2. Ensure the `user_devices` and `outbox_events` tables follow the DDL in the specification (`fisa_notifications_be_net_dev_kit_sns_lambda_outbox_v_1_0.md`).
3. Run database migrations before starting the API.
4. Build and run the API:

   ```bash
   dotnet build
   dotnet run --project src/Notifications.Api
   ```

The hosted service `OutboxWorker` will continuously poll `outbox_events`, publish notifications to SNS (FIFO aware), and mark records as delivered or scheduled for retry.
