using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface IUpdateService
    {
        Task<bool> CheckForUpdateAsync();
        Task<string> GetLatestVersionAsync();
        Task DownloadAndInstallAsync();
    }
}
