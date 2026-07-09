using System;
using MonitorBot.Core.Enums;

namespace MonitorBot.Core.Models
{
    public class ProxyEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public ProxyType Type { get; set; } = ProxyType.Http;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
