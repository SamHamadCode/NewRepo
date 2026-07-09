using System;

namespace MonitorBot.Core.Models
{
    public class MonitorResult
    {
        public Guid TaskId { get; set; }
        public bool IsAvailable { get; set; }
        public decimal? Price { get; set; }
        public string? Title { get; set; }
        public string? Url { get; set; }
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
