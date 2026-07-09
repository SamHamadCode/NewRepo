using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface ISettingsService
    {
        AppSettings Current { get; }
        Task LoadAsync();
        Task SaveAsync();
        Task ResetAsync();
    }
}
