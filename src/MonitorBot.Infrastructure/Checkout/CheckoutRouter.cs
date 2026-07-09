using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using MonitorBot.Infrastructure.Browser;

namespace MonitorBot.Infrastructure.Checkout
{
    /// <summary>
    /// Routes checkout to the correct site-specific service based on the task URL.
    /// When UsePlaywrightCheckout is enabled in settings, all checkouts go through
    /// the stealth Playwright service instead of the plain HTTP services.
    /// </summary>
    public class CheckoutRouter : ICheckoutService
    {
        private readonly WalmartCheckoutService _walmart;
        private readonly TargetCheckoutService _target;
        private readonly PlaywrightCheckoutService _playwright;
        private readonly ISettingsService _settings;
        private readonly ILogger<CheckoutRouter> _logger;

        public CheckoutRouter(
            WalmartCheckoutService walmart,
            TargetCheckoutService target,
            PlaywrightCheckoutService playwright,
            ISettingsService settings,
            ILogger<CheckoutRouter> logger)
        {
            _walmart    = walmart;
            _target     = target;
            _playwright = playwright;
            _settings   = settings;
            _logger     = logger;
        }

        public Task<CheckoutResult> CheckoutAsync(
            MonitorTask task,
            UserProfile profile,
            SiteAccount? account,
            MonitorResult monitorResult,
            CancellationToken ct = default)
        {
            var url = task.TargetUrl.ToLowerInvariant();

            // Playwright stealth mode — handles bot detection + captcha automatically
            if (_settings.Current.UsePlaywrightCheckout)
            {
                _logger.LogInformation("Routing checkout via Playwright (stealth mode)");
                return _playwright.CheckoutAsync(task, profile, account, monitorResult, ct);
            }

            if (url.Contains("walmart.com"))
            {
                _logger.LogInformation("Routing checkout to Walmart");
                return _walmart.CheckoutAsync(task, profile, account, monitorResult, ct);
            }

            if (url.Contains("target.com"))
            {
                _logger.LogInformation("Routing checkout to Target");
                return _target.CheckoutAsync(task, profile, account, monitorResult, ct);
            }

            // Unsupported site
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
