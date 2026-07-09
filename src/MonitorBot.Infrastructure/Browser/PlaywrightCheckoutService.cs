using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using MonitorBot.Infrastructure.Captcha;

namespace MonitorBot.Infrastructure.Browser
{
    /// <summary>
    /// Stealth Playwright-based checkout that handles bot-detection, CAPTCHA solving,
    /// and account login for both Target and Walmart.
    /// </summary>
    public class PlaywrightCheckoutService : ICheckoutService
    {
        private readonly ILogger<PlaywrightCheckoutService> _logger;
        private readonly CaptchaSolverService _captcha;
        private readonly ISettingsService _settings;

        // Site key constants (these are publicly visible in page source)
        private const string TargetRecaptchaSiteKey  = "6LfC6HkUAAAAALxGBnQoKiocFa4qDGFqAGlqYxqH";
        private const string WalmartRecaptchaSiteKey = "6LflLsoUAAAAADfVFQxGxKfn4O8eIOXgakT5SxpA";

        private static readonly Regex TargetTcinRegex = new(@"/-/A-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WalmartItemIdRegex = new(@"/ip/[^/]+/(\d+)", RegexOptions.Compiled);

        public PlaywrightCheckoutService(
            ILogger<PlaywrightCheckoutService> logger,
            CaptchaSolverService captcha,
            ISettingsService settings)
        {
            _logger   = logger;
            _captcha  = captcha;
            _settings = settings;
        }

        public async Task<CheckoutResult> CheckoutAsync(
            MonitorTask task,
            UserProfile profile,
            SiteAccount? account,
            MonitorResult monitorResult,
            CancellationToken ct = default)
        {
            var result = new CheckoutResult { TaskId = task.Id };
            var url = task.TargetUrl.ToLowerInvariant();

            var isTarget  = url.Contains("target.com");
            var isWalmart = url.Contains("walmart.com");

            if (!isTarget && !isWalmart)
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Playwright checkout only supports Target and Walmart.";
                return result;
            }

            _logger.LogInformation("Starting Playwright checkout for {Site}: {Url}",
                isTarget ? "Target" : "Walmart", task.TargetUrl);

            IPlaywright? playwright = null;
            IBrowser?    browser   = null;

            try
            {
                playwright = await Playwright.CreateAsync();

                // Launch stealth Chromium — args suppress common bot-detection signals
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = _settings.Current.HeadlessBrowser,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-blink-features=AutomationControlled",
                        "--disable-dev-shm-usage",
                        "--disable-web-security",
                        "--disable-features=IsolateOrigins,site-per-process"
                    }
                });

                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                "Chrome/124.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    Locale = "en-US",
                    TimezoneId = "America/New_York",
                    ExtraHTTPHeaders = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["Accept-Language"] = "en-US,en;q=0.9"
                    }
                });

                // Patch navigator.webdriver so sites can't detect automation
                await context.AddInitScriptAsync(@"
                    Object.defineProperty(navigator, 'webdriver', { get: () => false });
                    Object.defineProperty(navigator, 'plugins', { get: () => [1,2,3,4,5] });
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                    window.chrome = { runtime: {} };
                ");

                var page = await context.NewPageAsync();

                if (isTarget)
                    result = await CheckoutTargetAsync(page, task, profile, account, ct);
                else
                    result = await CheckoutWalmartAsync(page, task, profile, account, ct);
            }
            catch (OperationCanceledException)
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Checkout cancelled.";
            }
            catch (Exception ex)
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Playwright checkout error");
            }
            finally
            {
                if (browser != null) await browser.CloseAsync();
                playwright?.Dispose();
            }

            return result;
        }

        // ?? TARGET ??????????????????????????????????????????????????????????

        private async Task<CheckoutResult> CheckoutTargetAsync(
            IPage page, MonitorTask task, UserProfile profile, SiteAccount? account, CancellationToken ct)
        {
            var result = new CheckoutResult { TaskId = task.Id };

            // Login if account provided
            if (account != null)
            {
                var loggedIn = await LoginTargetAsync(page, account, ct);
                if (!loggedIn)
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = $"Target login failed for {account.Email}";
                    return result;
                }
            }

            // Navigate to product
            await page.GotoAsync(task.TargetUrl, new PageGotoOptions { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // Add to cart
            var addToCart = page.Locator("[data-test='shoppingCartButton'], button:has-text('Add to cart')").First;
            if (!await addToCart.IsVisibleAsync())
            {
                result.Status = CheckoutStatus.OutOfStock;
                result.ErrorMessage = "Add to cart button not visible — item may be out of stock.";
                return result;
            }

            await addToCart.ClickAsync();
            await page.WaitForTimeoutAsync(2000);

            // Go to checkout
            await page.GotoAsync("https://www.target.com/co-cart");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // Click checkout
            var checkout = page.Locator("button:has-text('Check out'), a:has-text('Check out')").First;
            if (await checkout.IsVisibleAsync())
                await checkout.ClickAsync();

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // Fill shipping (guest or continue)
            await FillTargetShippingAsync(page, profile, account);

            // Fill payment
            await FillTargetPaymentAsync(page, profile);

            // Place order
            var placeOrder = page.Locator("button:has-text('Place your order'), button:has-text('Place order')").First;
            if (!await placeOrder.IsVisibleAsync())
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Place order button not found.";
                return result;
            }

            await placeOrder.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var orderId = await ExtractTargetOrderIdAsync(page);
            if (!string.IsNullOrEmpty(orderId))
            {
                result.IsSuccess = true;
                result.Status    = CheckoutStatus.Success;
                result.OrderId   = orderId;
                _logger.LogInformation("Target Playwright order placed: {OrderId}", orderId);
            }
            else
            {
                result.Status = CheckoutStatus.CardDeclined;
                result.ErrorMessage = "Order not confirmed — check card details or review page.";
            }

            return result;
        }

        private async Task<bool> LoginTargetAsync(IPage page, SiteAccount account, CancellationToken ct)
        {
            _logger.LogInformation("Playwright: logging into Target as {Email}", account.Email);
            await page.GotoAsync("https://www.target.com/account/login", new PageGotoOptions { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            await page.FillAsync("#username", account.Email);
            await page.FillAsync("#password", account.Password);
            await page.ClickAsync("button[type='submit'], button:has-text('Sign in')");

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitForTimeoutAsync(3000);

            // Check for OTP page
            var otpField = page.Locator("input[name='code'], input[placeholder*='code']").First;
            if (await otpField.IsVisibleAsync())
            {
                _logger.LogInformation("Target OTP page detected");
                if (_captcha.IsConfigured)
                    await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

                // Wait for user or email code — if IMAP configured it would be handled
                // by TargetLoginService before we even hit this path; this is a fallback
                await page.WaitForTimeoutAsync(30000);
            }

            var currentUrl = page.Url;
            var success = currentUrl.Contains("target.com") && !currentUrl.Contains("/login");
            _logger.LogInformation("Target Playwright login {Status}", success ? "succeeded" : "failed");
            return success;
        }

        private async Task FillTargetShippingAsync(IPage page, UserProfile profile, SiteAccount? account)
        {
            var addr = profile.ShippingAddress;

            // Guest email if no account
            if (account == null)
            {
                var emailField = page.Locator("input[name='email'], input[type='email']").First;
                if (await emailField.IsVisibleAsync())
                    await emailField.FillAsync(profile.Email);
            }

            var firstName = page.Locator("input[name='firstName'], input[placeholder*='First']").First;
            if (await firstName.IsVisibleAsync()) await firstName.FillAsync(profile.FirstName);

            var lastName = page.Locator("input[name='lastName'], input[placeholder*='Last']").First;
            if (await lastName.IsVisibleAsync()) await lastName.FillAsync(profile.LastName);

            var line1 = page.Locator("input[name='addressLine1'], input[placeholder*='Address']").First;
            if (await line1.IsVisibleAsync()) await line1.FillAsync(addr.Line1);

            var city = page.Locator("input[name='city']").First;
            if (await city.IsVisibleAsync()) await city.FillAsync(addr.City);

            var state = page.Locator("select[name='state']").First;
            if (await state.IsVisibleAsync()) await state.SelectOptionAsync(addr.State);

            var zip = page.Locator("input[name='zip'], input[name='postalCode']").First;
            if (await zip.IsVisibleAsync()) await zip.FillAsync(addr.ZipCode);

            var continueBtn = page.Locator("button:has-text('Continue'), button:has-text('Save & continue')").First;
            if (await continueBtn.IsVisibleAsync())
                await continueBtn.ClickAsync();

            await page.WaitForTimeoutAsync(2000);
        }

        private async Task FillTargetPaymentAsync(IPage page, UserProfile profile)
        {
            var pay = profile.Payment;

            var cardNumber = page.Locator("input[name='cardNumber'], input[placeholder*='Card number']").First;
            if (await cardNumber.IsVisibleAsync()) await cardNumber.FillAsync(pay.CardNumber);

            var expiry = page.Locator("input[name='expiry'], input[placeholder*='MM/YY']").First;
            if (await expiry.IsVisibleAsync()) await expiry.FillAsync($"{pay.ExpiryMonth}/{pay.ExpiryYear}");

            var cvv = page.Locator("input[name='cvv'], input[placeholder*='CVV']").First;
            if (await cvv.IsVisibleAsync()) await cvv.FillAsync(pay.Cvv);

            var saveBtn = page.Locator("button:has-text('Save'), button:has-text('Continue')").First;
            if (await saveBtn.IsVisibleAsync())
                await saveBtn.ClickAsync();

            await page.WaitForTimeoutAsync(2000);
        }

        private async Task<string?> ExtractTargetOrderIdAsync(IPage page)
        {
            try
            {
                var confirmUrl = page.Url;
                var m = Regex.Match(confirmUrl, @"/order-confirmation/([^/?]+)");
                if (m.Success) return m.Groups[1].Value;

                var bodyText = await page.InnerTextAsync("body");
                m = Regex.Match(bodyText, @"[Oo]rder\s+#?\s*([A-Z0-9\-]{6,})");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        // ?? WALMART ?????????????????????????????????????????????????????????

        private async Task<CheckoutResult> CheckoutWalmartAsync(
            IPage page, MonitorTask task, UserProfile profile, SiteAccount? account, CancellationToken ct)
        {
            var result = new CheckoutResult { TaskId = task.Id };

            // Login if account provided
            if (account != null)
            {
                var loggedIn = await LoginWalmartAsync(page, account, ct);
                if (!loggedIn)
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = $"Walmart login failed for {account.Email}";
                    return result;
                }
            }

            // Navigate to product
            await page.GotoAsync(task.TargetUrl, new PageGotoOptions { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            // Add to cart
            var addToCart = page.Locator("button[data-automation-id='add-to-cart-button'], button:has-text('Add to cart')").First;
            if (!await addToCart.IsVisibleAsync())
            {
                result.Status = CheckoutStatus.OutOfStock;
                result.ErrorMessage = "Add to cart button not visible — item may be out of stock.";
                return result;
            }

            await addToCart.ClickAsync();
            await page.WaitForTimeoutAsync(2000);

            // Go to checkout
            await page.GotoAsync("https://www.walmart.com/cart");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            var checkout = page.Locator("button:has-text('Continue to checkout'), a:has-text('Continue to checkout')").First;
            if (await checkout.IsVisibleAsync())
                await checkout.ClickAsync();

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            // Fill shipping
            await FillWalmartShippingAsync(page, profile, account);

            // Fill payment
            await FillWalmartPaymentAsync(page, profile);

            // Place order
            var placeOrder = page.Locator("button:has-text('Place order'), button:has-text('Review your order')").First;
            if (!await placeOrder.IsVisibleAsync())
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Place order button not found.";
                return result;
            }

            await placeOrder.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var orderId = await ExtractWalmartOrderIdAsync(page);
            if (!string.IsNullOrEmpty(orderId))
            {
                result.IsSuccess = true;
                result.Status    = CheckoutStatus.Success;
                result.OrderId   = orderId;
                _logger.LogInformation("Walmart Playwright order placed: {OrderId}", orderId);
            }
            else
            {
                result.Status = CheckoutStatus.CardDeclined;
                result.ErrorMessage = "Order not confirmed — check card details or review page.";
            }

            return result;
        }

        private async Task<bool> LoginWalmartAsync(IPage page, SiteAccount account, CancellationToken ct)
        {
            _logger.LogInformation("Playwright: logging into Walmart as {Email}", account.Email);
            await page.GotoAsync("https://www.walmart.com/account/login", new PageGotoOptions { Timeout = 30000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            await page.FillAsync("input[name='email']", account.Email);
            await page.ClickAsync("button:has-text('Continue'), button[type='submit']");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var passwordField = page.Locator("input[name='password']").First;
            if (await passwordField.IsVisibleAsync())
                await passwordField.FillAsync(account.Password);

            await page.ClickAsync("button[type='submit'], button:has-text('Sign in')");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitForTimeoutAsync(3000);

            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            var currentUrl = page.Url;
            var success = currentUrl.Contains("walmart.com") && !currentUrl.Contains("/login");
            _logger.LogInformation("Walmart Playwright login {Status}", success ? "succeeded" : "failed");
            return success;
        }

        private async Task FillWalmartShippingAsync(IPage page, UserProfile profile, SiteAccount? account)
        {
            var addr = profile.ShippingAddress;

            if (account == null)
            {
                var emailField = page.Locator("input[name='email'], input[type='email']").First;
                if (await emailField.IsVisibleAsync())
                    await emailField.FillAsync(profile.Email);
            }

            var firstName = page.Locator("input[name='firstName']").First;
            if (await firstName.IsVisibleAsync()) await firstName.FillAsync(profile.FirstName);

            var lastName = page.Locator("input[name='lastName']").First;
            if (await lastName.IsVisibleAsync()) await lastName.FillAsync(profile.LastName);

            var line1 = page.Locator("input[name='addressLineOne'], input[placeholder*='address']").First;
            if (await line1.IsVisibleAsync()) await line1.FillAsync(addr.Line1);

            var city = page.Locator("input[name='city']").First;
            if (await city.IsVisibleAsync()) await city.FillAsync(addr.City);

            var state = page.Locator("select[name='state']").First;
            if (await state.IsVisibleAsync()) await state.SelectOptionAsync(addr.State);

            var zip = page.Locator("input[name='postalCode'], input[name='zipCode']").First;
            if (await zip.IsVisibleAsync()) await zip.FillAsync(addr.ZipCode);

            var continueBtn = page.Locator("button:has-text('Continue'), button:has-text('Deliver to this address')").First;
            if (await continueBtn.IsVisibleAsync())
                await continueBtn.ClickAsync();

            await page.WaitForTimeoutAsync(2000);
        }

        private async Task FillWalmartPaymentAsync(IPage page, UserProfile profile)
        {
            var pay = profile.Payment;

            var cardNumber = page.Locator("input[name='cardNumber'], input[placeholder*='Card number']").First;
            if (await cardNumber.IsVisibleAsync()) await cardNumber.FillAsync(pay.CardNumber);

            var expMonth = page.Locator("select[name='expiryMonth']").First;
            if (await expMonth.IsVisibleAsync()) await expMonth.SelectOptionAsync(pay.ExpiryMonth);

            var expYear = page.Locator("select[name='expiryYear']").First;
            if (await expYear.IsVisibleAsync()) await expYear.SelectOptionAsync(pay.ExpiryYear);

            var cvv = page.Locator("input[name='cvv'], input[name='securityCode']").First;
            if (await cvv.IsVisibleAsync()) await cvv.FillAsync(pay.Cvv);

            var saveBtn = page.Locator("button:has-text('Continue'), button:has-text('Save & continue')").First;
            if (await saveBtn.IsVisibleAsync())
                await saveBtn.ClickAsync();

            await page.WaitForTimeoutAsync(2000);
        }

        private async Task<string?> ExtractWalmartOrderIdAsync(IPage page)
        {
            try
            {
                var m = Regex.Match(page.Url, @"/order-confirmation/([^/?]+)");
                if (m.Success) return m.Groups[1].Value;

                var bodyText = await page.InnerTextAsync("body");
                m = Regex.Match(bodyText, @"[Oo]rder\s+#?\s*([0-9\-]{6,})");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        // ?? CAPTCHA ??????????????????????????????????????????????????????????

        private async Task SolveCaptchaIfPresentAsync(
            IPage page, string siteDomain, string siteKey, CancellationToken ct)
        {
            if (!_captcha.IsConfigured) return;

            try
            {
                // Check for reCAPTCHA iframe
                var recaptchaFrame = page.Frames.FirstOrDefault(f =>
                    f.Url.Contains("recaptcha") || f.Url.Contains("google.com/recaptcha"));

                // Check for hCaptcha iframe
                var hcaptchaFrame = page.Frames.FirstOrDefault(f =>
                    f.Url.Contains("hcaptcha.com"));

                if (recaptchaFrame == null && hcaptchaFrame == null)
                {
                    // Check via DOM presence
                    var recaptchaEl = page.Locator(".g-recaptcha, [data-sitekey]").First;
                    var hcaptchaEl  = page.Locator(".h-captcha").First;

                    if (!await recaptchaEl.IsVisibleAsync() && !await hcaptchaEl.IsVisibleAsync())
                        return; // No captcha on page
                }

                _logger.LogInformation("CAPTCHA detected on {Domain} — solving via 2Captcha", siteDomain);

                string? token;
                if (hcaptchaFrame != null)
                    token = await _captcha.SolveHCaptchaAsync(siteKey, page.Url, ct);
                else
                    token = await _captcha.SolveRecaptchaV2Async(siteKey, page.Url, ct);

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("2Captcha returned no token — proceeding anyway");
                    return;
                }

                // Inject the solved token into the page
                await page.EvaluateAsync(@$"
                    document.querySelectorAll('[name=""g-recaptcha-response""]').forEach(el => {{
                        el.value = '{token}';
                    }});
                    document.querySelectorAll('[name=""h-captcha-response""]').forEach(el => {{
                        el.value = '{token}';
                    }});
                    if (typeof ___grecaptcha_cfg !== 'undefined') {{
                        Object.entries(___grecaptcha_cfg.clients || {{}}).forEach(([id, client]) => {{
                            const cb = client?.U?.U?.callback || client?.aa?.callback;
                            if (typeof cb === 'function') cb('{token}');
                        }});
                    }}
                ");

                _logger.LogInformation("Captcha token injected successfully");
                await page.WaitForTimeoutAsync(1000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Captcha solve/inject error — continuing");
            }
        }
    }
}
