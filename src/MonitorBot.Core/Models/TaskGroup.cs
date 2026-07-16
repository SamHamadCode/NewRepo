using System;
using System.Collections.Generic;

namespace MonitorBot.Core.Models
{
    public class TaskGroup
    {
        public Guid   Id            { get; set; } = Guid.NewGuid();
        public string Name          { get; set; } = "New Group";
        public string Site          { get; set; } = string.Empty;
        public string MonitorInput  { get; set; } = string.Empty;
        public string ProxyListId   { get; set; } = string.Empty;
        public int    MonitorDelay  { get; set; } = 3500;
        public bool   AutoStart     { get; set; } = false;
        public bool   LoopCheckouts { get; set; } = false;
        public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;

        // IDs of MonitorTasks that belong to this group
        public List<string> TaskIds { get; set; } = new();
    }
}
