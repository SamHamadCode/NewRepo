using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface INotificationService
    {
        Task SendSuccessAsync(MonitorTask task, MonitorResult result);
        Task SendFailureAsync(MonitorTask task, string reason);
        Task SendDesktopAsync(string title, string message);
    }
}
