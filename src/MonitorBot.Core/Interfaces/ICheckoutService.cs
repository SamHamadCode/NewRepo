using System;
using System.Threading;
using System.Threading.Tasks;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface ICheckoutService
    {
        /// <param name="onStatus">
        /// Called at each checkout phase so the UI can show
        /// LoggingIn ? AddingToCart ? PlacingOrder in real time.
        /// </param>
        Task<CheckoutResult> CheckoutAsync(
            MonitorTask task,
            UserProfile profile,
            SiteAccount? account,
            MonitorResult monitorResult,
            Action<MonitorTaskStatus>? onStatus = null,
            CancellationToken ct = default);
    }
}
