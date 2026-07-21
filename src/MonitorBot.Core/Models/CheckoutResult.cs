using System;
using MonitorBot.Core.Enums;

namespace MonitorBot.Core.Models
{
    public class CheckoutResult
    {
        public Guid TaskId { get; set; }
        public bool IsSuccess { get; set; }
        public CheckoutStatus Status { get; set; }
        public string? OrderId { get; set; }
        public string? ErrorMessage { get; set; }
        public decimal? ChargedAmount { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>True when cookies are expired or missing and a fresh harvest is needed.</summary>
        public bool NeedsReHarvest { get; set; }
    }
}
