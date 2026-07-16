using System;
using MonitorBot.Core.Enums;

namespace MonitorBot.Core.Models
{
    public class MonitorTask
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public MonitorType Type { get; set; }
        public string TargetUrl { get; set; } = string.Empty;
        public string? Sku { get; set; }
        public string? Keyword { get; set; }
        public DetectionMode DetectionMode { get; set; }
        public decimal? PriceThreshold { get; set; }
        public int IntervalSeconds { get; set; } = 30;
        public int MaxRetries { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 5;
        public MonitorTaskStatus Status { get; set; } = MonitorTaskStatus.Idle;
        public string? ProfileId { get; set; }
        public string? AccountId { get; set; }
        public string? ProxyId { get; set; }
        public DateTime? LastChecked { get; set; }
        public DateTime? NextCheck { get; set; }
        public string? LastResult { get; set; }
        public int RetryCount { get; set; }
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Checkout
        public bool AutoCheckout { get; set; } = false;
        public int Quantity { get; set; } = 1;
        public string? CookieOverride { get; set; }   // Manually pasted browser cookies for anti-bot bypass
        public CheckoutStatus CheckoutStatus { get; set; } = CheckoutStatus.NotAttempted;
        public string? LastOrderId { get; set; }
        public string? CheckoutError { get; set; }
    }
}
