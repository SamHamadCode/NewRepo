using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Browser
{
    /// <summary>
    /// Uses a real Playwright browser to check product availability.
    /// Needed for Target and Walmart since their availability data is loaded
    /// dynamically via JavaScript after page load — not present in static HTML.
    /// </summary>
    public class PlaywrightStockChecker
    {
        private readonly ILogger<PlaywrightStockChecker> _logger;
        private readonly ISettingsService _settings;

        public PlaywrightStockChecker(
            ILogger<PlaywrightStockChecker> logger,
            ISettingsService settings)
        {
            _logger   = logger;
            _settings = settings;
        }

        public async Task<MonitorResult> CheckAsync(MonitorTask task, CancellationToken ct = default)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };

            IPlaywright? playwright = null;
            IBrowser?    browser   = null;

            try
            {
                playwright = await Playwright.CreateAsync();

                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true, // Always headless for monitoring — runs every 10s
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-blink-features=AutomationControlled",
                        "--disable-dev-shm-usage"
                    }
                });

                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    Locale       = "en-US"
                });

                await context.AddInitScriptAsync(
                    "Object.defineProperty(navigator, 'webdriver', { get: () => false });");

                var page = await context.NewPageAsync();

                var url = task.TargetUrl.ToLowerInvariant();

                if (url.Contains("target.com"))
                    result = await CheckTargetAsync(page, task, ct);
                else if (url.Contains("walmart.com"))
                    result = await CheckWalmartAsync(page, task, ct);
                else
                    result = await CheckGenericAsync(page, task, ct);
            }
            catch (OperationCanceledException)
            {
                result.IsSuccess   = false;
                result.ErrorMessage = "Check cancelled";
            }
            catch (Exception ex)
            {
                result.IsSuccess   = false;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Playwright stock check failed for task {Name}", task.Name);
            }
            finally
            {
                if (browser != null) await browser.CloseAsync();
                playwright?.Dispose();
            }

            return result;
        }

        // ?? TARGET ???????????????????????????????????????????????????????????
        private async Task<MonitorResult> CheckTargetAsync(IPage page, MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };

            await page.GotoAsync(task.TargetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30000
            });

            // Wait for the add-to-cart or sold-out button to appear (JS-rendered)
            try
            {
                await page.WaitForSelectorAsync(
                    "[data-test='shoppingCartButton'], [data-test='soldOutButton'], " +
                    "button:has-text('Add to cart'), button:has-text('Sold out'), " +
                    "button:has-text('Out of stock')",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                // Timeout waiting for button — page may still have useful info
            }

            result.Title = await page.TitleAsync();

            // Check for add-to-cart button — most reliable signal
            var addToCart = page.Locator("[data-test='shoppingCartButton'], button:has-text('Add to cart')").First;
            var soldOut   = page.Locator(
                "[data-test='soldOutButton'], " +
                "button:has-text('Sold out'), " +
                "button:has-text('Out of stock'), " +
                "button:has-text('Not available'), " +
                "[data-test='ndsb'], " +           // "Not delivered/shipped" button
                "[data-test='preOrderButton']"      // pre-order = not currently in stock
            ).First;
            // Also check for "Unavailable" / "Not available near" text blocks Target shows
            var unavailableText = page.Locator(
                "text='Not available near you', " +
                "text='Unavailable', " +
                "[data-test='storeLocationUnavailable'], " +
                "[data-test='fulfillment-cell-unavailable']"
            ).First;

            var addVisible       = await addToCart.IsVisibleAsync();
            var soldVisible      = await soldOut.IsVisibleAsync();
            var unavailVisible   = await unavailableText.IsVisibleAsync();

            // Make sure the add-to-cart button is actually enabled (not disabled)
            bool addEnabled = false;
            if (addVisible)
            {
                var disabled = await addToCart.GetAttributeAsync("disabled");
                addEnabled = disabled == null; // null means attribute not present ? enabled
            }

            result.IsSuccess   = true;
            result.IsAvailable = addEnabled && !soldVisible && !unavailVisible;

            // Try extract price
            try
            {
                var priceEl = page.Locator("[data-test='product-price'], [class*='price']").First;
                if (await priceEl.IsVisibleAsync())
                {
                    var priceText = await priceEl.InnerTextAsync();
                    var digits = System.Text.RegularExpressions.Regex.Match(priceText, @"[\d]+\.[\d]{2}|[\d]+");
                    if (digits.Success && decimal.TryParse(digits.Value,
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var p))
                        result.Price = p;
                }
            }
            catch { /* price is optional */ }

            _logger.LogDebug("Target Playwright check: addVisible={Add} addEnabled={Enabled} soldVisible={Sold} unavailVisible={Unavail} available={Avail}",
                addVisible, addEnabled, soldVisible, unavailVisible, result.IsAvailable);

            return result;
        }

        // ?? WALMART ??????????????????????????????????????????????????????????
        private async Task<MonitorResult> CheckWalmartAsync(IPage page, MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };

            await page.GotoAsync(task.TargetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30000
            });

            try
            {
                await page.WaitForSelectorAsync(
                    "button[data-automation-id='add-to-cart-button'], " +
                    "button:has-text('Add to cart'), button:has-text('Out of stock')",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch { }

            result.Title = await page.TitleAsync();

            var addToCart = page.Locator("button[data-automation-id='add-to-cart-button'], button:has-text('Add to cart')").First;
            var outOfStock = page.Locator("button:has-text('Out of stock'), [class*='out-of-stock']").First;

            var addVisible = await addToCart.IsVisibleAsync();
            var oosVisible = await outOfStock.IsVisibleAsync();

            result.IsSuccess   = true;
            result.IsAvailable = addVisible && !oosVisible;

            _logger.LogDebug("Walmart Playwright check: addVisible={Add} oosVisible={Oos} available={Avail}",
                addVisible, oosVisible, result.IsAvailable);

            return result;
        }

        // ?? GENERIC ??????????????????????????????????????????????????????????
        private async Task<MonitorResult> CheckGenericAsync(IPage page, MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };

            await page.GotoAsync(task.TargetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30000
            });

            result.Title = await page.TitleAsync();
            var bodyText = (await page.InnerTextAsync("body")).ToLowerInvariant();

            var hasOos = bodyText.Contains("out of stock") || bodyText.Contains("sold out")
                      || bodyText.Contains("unavailable") || bodyText.Contains("notify me");
            var hasAtc = bodyText.Contains("add to cart") || bodyText.Contains("buy now")
                      || bodyText.Contains("in stock");

            result.IsSuccess   = true;
            result.IsAvailable = hasAtc && !hasOos;

            return result;
        }
    }
}
