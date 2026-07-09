using System.Threading;
using System.Threading.Tasks;

namespace MonitorBot.Core.Interfaces
{
    public interface IBrowserSessionService
    {
        Task<bool> ConnectAsync(string port, CancellationToken ct = default);
        Task<string?> GetCookiesAsync(string domain, CancellationToken ct = default);
        bool IsConnected { get; }
        void Disconnect();
    }
}
