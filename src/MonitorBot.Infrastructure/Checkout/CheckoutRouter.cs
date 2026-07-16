using System;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Checkout
{
    /// <summary>
    /// Routes checkout to the correct site-specific HTTP service based on the task URL.
    /// Pure HTTP checkout — same approach as RefractBot and Stellar AIO.
    /// </summary>
    public class CheckoutRouter : ICheckoutService
    {
        private readonly WalmartCheckoutService _walmart;
        private readonly TargetCheckoutService _target;
        private readonly ILogger<CheckoutRouter> _logger;

        public CheckoutRouter(
            WalmartCheckoutService walmart,
            TargetCheckoutService target,
            ILogger<CheckoutRouter> logger)
        {
            _walmart = walmart;
            _target  = target;
            _logger  = logger;
        }

        public Task<CheckoutResult> CheckoutAsync(
            MonitorTask task,
            UserProfile profile,
            SiteAccount? account,
            MonitorResult monitorResult,
            Action<MonitorTaskStatus>? onStatus = null,
            CancellationToken ct = default)
        {
            var url = task.TargetUrl.ToLowerInvariant();

            if (url.Contains("walmart.com"))
            {
                _logger.LogInformation("Routing checkout to Walmart");
                return _walmart.CheckoutAsync(task, profile, account, monitorResult, onStatus, ct);
            }

            if (url.Contains("target.com"))
            {
                _logger.LogInformation("Routing checkout to Target");
                return _target.CheckoutAsync(task, profile, account, monitorResult, onStatus, ct);
            }

            _logger.LogWarning("No checkout service available for URL: {Url}", task.TargetUrl);
            return Task.FromResult(new CheckoutResult
            {
                TaskId       = task.Id,
                IsSuccess    = false,
                Status       = CheckoutStatus.Failed,
                ErrorMessage = $"Auto checkout is not supported for this site yet.\nURL: {task.TargetUrl}"
            });
        }
    }
}
