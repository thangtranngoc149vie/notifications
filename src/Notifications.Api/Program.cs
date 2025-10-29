using Amazon.SimpleNotificationService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Notifications.Api.Data;
using Notifications.Api.Hubs;
using Notifications.Api.Infrastructure;
using Notifications.Api.Options;
using Notifications.Api.Services;
using Notifications.Api.Services.Web;
using Notifications.Api.Workers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Notifications API",
        Version = "v1"
    });

    options.AddSecurityDefinition("bearerAuth", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = "bearerAuth"
            }
        }] = Array.Empty<string>()
    });
});

var hubPath = builder.Configuration.GetValue<string>($"{WebNotificationsOptions.ConfigurationSectionName}:HubPath") ?? "/hubs/notifications";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["Authentication:Authority"];
        if (!string.IsNullOrWhiteSpace(authority))
        {
            options.Authority = authority;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = true
            };
        }
        else
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = false
            };
        }

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var requestPath = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && requestPath.StartsWithSegments(hubPath, StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();

builder.Services.Configure<AwsOptions>(builder.Configuration.GetSection("Aws"));
builder.Services.Configure<OutboxWorkerOptions>(builder.Configuration.GetSection("OutboxWorker"));
builder.Services.Configure<WebNotificationsOptions>(builder.Configuration.GetSection(WebNotificationsOptions.ConfigurationSectionName));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddScoped<ICurrentUserProvider, CurrentUserProvider>();
builder.Services.AddScoped<IDeviceRegistrationService, DeviceRegistrationService>();
builder.Services.AddSingleton<IWebNotificationPublisher, SignalRWebNotificationPublisher>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

var webOptions = app.Services.GetRequiredService<IOptions<WebNotificationsOptions>>().Value;
if (webOptions.Enabled)
{
    app.MapHub<NotificationsHub>(webOptions.HubPath).RequireAuthorization();
}

app.Run();
