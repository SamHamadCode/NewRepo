using System.Collections.Generic;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface ILogStore
    {
        void Add(LogEntry entry);
        IReadOnlyList<LogEntry> GetRecent(int count = 500);
        Task FlushAsync();
        Task PurgeOldAsync(int retentionDays);
    }
}
