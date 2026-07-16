using System.Threading;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    /// <summary>
    /// Executes Target's checkout flow inside an embedded browser.
    /// This interface exists so MonitorBot.Infrastructure can call the
    /// browser-based checkout without taking a direct dependency on MonitorBot.App.
    /// The implementation lives in MonitorBot.App and is registered at startup.
    /// </summary>
    public interface ITargetBrowserCheckout
    {
        /// <summary>
        /// Runs the full Target checkout (init session + set address + set payment + place order)
        /// inside an embedded Chromium browser using the provided session cookies.
        /// </summary>
        Task<(string? orderId, string? error)> RunAsync(
            string rawCookies,
            string tcin,
            int quantity,
            string apiKey,
            string cartId,
            UserProfile profile,
            CancellationToken ct);
    }
}
