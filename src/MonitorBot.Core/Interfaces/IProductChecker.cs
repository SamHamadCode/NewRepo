using System.Threading;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface IProductChecker
    {
        Task<MonitorResult> CheckAsync(MonitorTask task, CancellationToken ct = default);
    }
}
