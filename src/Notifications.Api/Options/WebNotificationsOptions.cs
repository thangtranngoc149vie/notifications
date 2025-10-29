using System.ComponentModel.DataAnnotations;

namespace Notifications.Api.Options;

public sealed class WebNotificationsOptions
{
    public const string ConfigurationSectionName = "WebNotifications";

    public bool Enabled { get; set; }

    [Required]
    [StringLength(200)]
    public string HubPath { get; set; } = "/hubs/notifications";

    [Required]
    [StringLength(100)]
    public string BroadcastMethod { get; set; } = "notificationReceived";

    [Required]
    [StringLength(100)]
    public string UserGroupPrefix { get; set; } = "user-";

    /// <summary>
    /// When true, the worker only pushes to SignalR if the envelope declares a matching channel tag.
    /// </summary>
    public bool RequireChannelTag { get; set; } = true;

    [StringLength(50)]
    public string ChannelTag { get; set; } = "web";

    /// <summary>
    /// Number of notifications sent in a single SignalR dispatch batch.
    /// </summary>
    [Range(1, 500)]
    public int MaxBatchSize { get; set; } = 100;
}
