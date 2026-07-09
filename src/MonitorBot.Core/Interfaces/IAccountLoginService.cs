using System.Threading;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface IAccountLoginService
    {
        /// <summary>
        /// Logs into the site and returns a cookie header string to use in subsequent requests.
        /// Returns null if login failed.
        /// </summary>
        Task<string?> LoginAsync(SiteAccount account, CancellationToken ct = default);
    }
}
