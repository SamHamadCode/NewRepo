using System;

namespace MonitorBot.Core.Models
{
    public class ExportBundle
    {
        public string Version { get; set; } = "1.0";
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
        public MonitorTask[]? Tasks { get; set; }
        public UserProfile[]? Profiles { get; set; }
        public SiteAccount[]? Accounts { get; set; }
        public ProxyEntry[]? Proxies { get; set; }
        public NotificationSettings? Notifications { get; set; }
    }
}
