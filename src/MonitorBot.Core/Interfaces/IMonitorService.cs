using System;
using System.Threading;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface IMonitorService
    {
        event EventHandler<MonitorResult>? ResultReceived;
        event EventHandler<MonitorTask>? TaskStatusChanged;

        Task StartTaskAsync(MonitorTask task, CancellationToken ct = default);
        Task StopTaskAsync(Guid taskId);
        Task StopAllAsync();
        bool IsRunning(Guid taskId);
    }
}
