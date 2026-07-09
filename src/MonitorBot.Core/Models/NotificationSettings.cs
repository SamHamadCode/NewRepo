using System.Collections.Generic;
using MonitorBot.Core.Enums;

namespace MonitorBot.Core.Models
{
    public class NotificationWebhook
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public NotificationChannel Channel { get; set; }
        public string Url { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool OnSuccess { get; set; } = true;
        public bool OnFailure { get; set; } = false;
    }

    public class NotificationSettings
    {
        public bool DesktopEnabled { get; set; } = true;
        public bool DesktopOnSuccess { get; set; } = true;
        public bool DesktopOnFailure { get; set; } = true;
        public List<NotificationWebhook> Webhooks { get; set; } = new();
    }
}
