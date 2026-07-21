using System.Threading;
using System.Threading;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface IWalmartBrowserStockChecker
    {
        Task<(bool isAvailable, string? status, string? title, decimal? price)> CheckAsync(
            string itemId,
            string? offerIdOverride,
            string? cookies,
            CancellationToken ct = default);

        Task<(string? cartId, string? error)> AddToCartAsync(
            string itemId,
            string offerId,
            int quantity,
            CancellationToken ct = default);

        /// <summary>
        /// Runs contract creation and order placement entirely inside the browser session
        /// (bypasses Cloudflare/PerimeterX 412 on HTTP clients).
        /// Returns (orderId, error).
        /// </summary>
        Task<(string? orderId, string? error)> BrowserCheckoutAsync(
            string cartId,
            UserProfile profile,
            CancellationToken ct = default);
    }
}
