using System;
using System;
using System.IO;
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

        /// <summary>
        /// Returns a per-account persistent browser profile directory.
        /// Storing cookies/session data here means Target/Walmart remember the browser
        /// between runs, preventing forced re-login on every checkout attempt.
        /// </summary>
        private static string GetProfileDir(SiteAccount? account)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MonitorBot", "BrowserProfiles");

            var profileName = account != null
                ? $"account_{account.Id}"
                : "guest";

            var dir = Path.Combine(baseDir, profileName);
            Directory.CreateDirectory(dir);
            return dir;
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
            IBrowserContext? context = null;

            try
            {
                playwright = await Playwright.CreateAsync();

                // Use a persistent profile directory so cookies/session survive between runs.
                // Each account gets its own profile so sessions don't conflict.
                var profileDir = GetProfileDir(account);

                // Prefer the user's real Chrome install — much harder to fingerprint than bundled Chromium
                var chromePaths = new[]
                {
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\Application\chrome.exe")
                };
                var realChrome = chromePaths.FirstOrDefault(File.Exists);

                context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = _settings.Current.HeadlessBrowser,
                        ExecutablePath = realChrome, // null = use bundled Chromium as fallback
                        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                    "Chrome/124.0.0.0 Safari/537.36",
                        ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                        Locale = "en-US",
                        TimezoneId = "America/New_York",
                        Args = new[]
                        {
                            "--no-sandbox",
                            "--disable-blink-features=AutomationControlled",
                            "--disable-dev-shm-usage",
                            "--disable-features=IsolateOrigins,site-per-process",
                            "--disable-infobars",
                            "--password-store=basic",
                            "--use-mock-keychain"
                        },
                        ExtraHTTPHeaders = new System.Collections.Generic.Dictionary<string, string>
                        {
                            ["Accept-Language"] = "en-US,en;q=0.9"
                        },
                        IgnoreDefaultArgs = new[] { "--enable-automation", "--enable-blink-features=IdleDetection" }
                    });

                // Patch navigator.webdriver on every page in this context
                await context.AddInitScriptAsync(@"
                    // Hide webdriver flag
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

                    // Fake plugin list like a real browser
                    Object.defineProperty(navigator, 'plugins', { get: () => {
                        return [
                            { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format' },
                            { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '' },
                            { name: 'Native Client', filename: 'internal-nacl-plugin', description: '' }
                        ];
                    }});

                    // Real language list
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });

                    // Spoof chrome runtime so sites see it as a real Chrome
                    window.chrome = {
                        app: { isInstalled: false, InstallState: { DISABLED: 'disabled', INSTALLED: 'installed', NOT_INSTALLED: 'not_installed' }, RunningState: { CANNOT_RUN: 'cannot_run', READY_TO_RUN: 'ready_to_run', RUNNING: 'running' } },
                        runtime: { OnInstalledReason: {}, OnRestartRequiredReason: {}, PlatformArch: {}, PlatformNaclArch: {}, PlatformOs: {}, RequestUpdateCheckStatus: {} },
                        loadTimes: function() {},
                        csi: function() {}
                    };

                    // Permissions API — real Chrome returns 'granted' for notifications
                    const originalQuery = window.navigator.permissions.query;
                    window.navigator.permissions.query = (parameters) =>
                        parameters.name === 'notifications'
                            ? Promise.resolve({ state: Notification.permission })
                            : originalQuery(parameters);
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
                if (context != null) await context.CloseAsync();
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

                // Brief pause to let session cookies fully settle before navigating
                await page.WaitForTimeoutAsync(2000);
            }

            // Navigate to product — session cookies from login carry over in the same context
            await page.GotoAsync(task.TargetUrl, new PageGotoOptions
            {
                Timeout = 30000,
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
            await page.WaitForTimeoutAsync(3000);

            // If Target redirected back to login, session was lost
            if (page.Url.Contains("/login") || page.Url.Contains("create_session"))
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Target session expired after navigation — try again";
                return result;
            }

            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // Wait for add-to-cart button to render (JS-loaded)
            try
            {
                await page.WaitForSelectorAsync(
                    "[data-test='shoppingCartButton'], button:has-text('Add to cart')",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch { }

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
            await page.WaitForTimeoutAsync(2000);
            await HandleTargetReauthModalAsync(page, account, ct);
            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // Click checkout
            var checkout = page.Locator("button:has-text('Check out'), a:has-text('Check out')").First;
            if (await checkout.IsVisibleAsync())
                await checkout.ClickAsync();

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitForTimeoutAsync(2000);
            await HandleTargetReauthModalAsync(page, account, ct);
            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // If checkout page is already fully pre-filled (saved payment on file), skip shipping/payment steps
            var placeOrder = page.Locator("button:has-text('Place your order'), button:has-text('Place order')").First;
            if (!await placeOrder.IsVisibleAsync())
            {
                // Fill shipping (guest or continue)
                await FillTargetShippingAsync(page, profile, account);

                // Fill payment
                await FillTargetPaymentAsync(page, profile);
            }
            if (!await placeOrder.IsVisibleAsync())
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Place order button not found.";
                return result;
            }

            await placeOrder.ClickAsync();

            // Handle CVV confirmation modal that Target may show after clicking Place your order
            await HandleTargetCvvModalAsync(page, profile);

            // Wait for Target's confirmation page
            // Can take up to 30s depending on payment processing
            try
            {
                await page.WaitForURLAsync(
                    url => url.Contains("order-confirmation") || url.Contains("order-details"),
                    new PageWaitForURLOptions { Timeout = 30000 });
            }
            catch
            {
                // URL may not change on some flows — wait for DOM to settle
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await page.WaitForTimeoutAsync(5000);
            }

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
                // Check if still on checkout page (button click may have failed)
                var currentUrl = page.Url;
                if (currentUrl.Contains("/checkout") || currentUrl.Contains("/co-"))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Place order click did not navigate — order may not have been submitted.";
                }
                else
                {
                    result.Status = CheckoutStatus.CardDeclined;
                    result.ErrorMessage = "Order not confirmed — check card details or review page.";
                }
            }

            return result;
        }

        /// <summary>
        /// Tries each CSS selector in order and clicks the first one that is visible.
        /// Returns true if a button was clicked, false if none matched.
        /// </summary>
        private static async Task<bool> TryClickFirstVisibleAsync(IPage page, params string[] selectors)
        {
            foreach (var selector in selectors)
            {
                try
                {
                    var el = page.Locator(selector).First;
                    if (await el.IsVisibleAsync())
                    {
                        await el.ClickAsync();
                        return true;
                    }
                }
                catch { /* selector may not exist — try next */ }
            }
            return false;
        }

        /// <summary>
        /// Handles the "Confirm CVV" modal Target shows when placing an order with a saved card.
        /// Enters the CVV from the user's profile and clicks Confirm.
        /// </summary>
        private async Task HandleTargetCvvModalAsync(IPage page, UserProfile profile)
        {
            try
            {
                // Wait briefly to see if the CVV modal appears
                await page.WaitForTimeoutAsync(2000);

                var cvvInput = page.Locator("input[id*='cvv'], input[placeholder*='CVV'], input[placeholder*='cvv'], input[aria-label*='CVV'], input[aria-label*='cvv']").First;
                if (!await cvvInput.IsVisibleAsync())
                    return; // No CVV modal — proceed normally

                _logger.LogInformation("CVV confirmation modal detected — entering CVV");

                if (string.IsNullOrWhiteSpace(profile.Payment.Cvv))
                {
                    _logger.LogWarning("CVV not set in profile — cannot confirm CVV modal");
                    return;
                }

                await cvvInput.ClickAsync();
                await cvvInput.FillAsync(profile.Payment.Cvv);
                await page.WaitForTimeoutAsync(500);

                // Click the Confirm button
                var confirmBtn = page.Locator("button:has-text('Confirm')").First;
                if (await confirmBtn.IsVisibleAsync())
                    await confirmBtn.ClickAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CVV modal handling failed — continuing anyway");
            }
        }

        /// <summary>
        /// Handles the inline re-authentication modal Target shows on cart/checkout pages.
        /// It appears as a slide-in panel with "Enter your password" option.
        /// </summary>
        private async Task HandleTargetReauthModalAsync(IPage page, SiteAccount? account, CancellationToken ct)
        {
            if (account == null) return;
            try
            {
                // Check if the reauth modal is visible (contains "Sign in to your account" heading)
                var modalHeading = page.GetByText("Sign in to your account").First;
                if (!await modalHeading.IsVisibleAsync()) return;

                _logger.LogInformation("Target reauth modal detected — signing in again");

                // Click "Enter your password"
                var enterPw = page.GetByText("Enter your password", new() { Exact = true });
                if (await enterPw.IsVisibleAsync())
                    await enterPw.ClickAsync();
                else
                {
                    var enterPwFallback = page.Locator("*:has-text('Enter your password')").Last;
                    if (await enterPwFallback.IsVisibleAsync())
                        await enterPwFallback.ClickAsync();
                }

                await page.WaitForTimeoutAsync(1500);

                // Fill password with human-like typing to avoid bot detection
                var passwordInput = page.Locator("input[type='password'], input[autocomplete='current-password']").First;
                if (await passwordInput.IsVisibleAsync())
                {
                    await passwordInput.ClickAsync();
                    await page.WaitForTimeoutAsync(400);
                    await passwordInput.TypeAsync(account.Password, new LocatorTypeOptions { Delay = 90 });
                    await page.WaitForTimeoutAsync(600);

                    // Try multiple selectors for the sign-in submit button in the reauth modal
                    var signInBtn =
                        await TryClickFirstVisibleAsync(page,
                            "button:has-text('Sign in with password')",
                            "button:has-text('Sign In with password')",
                            "button:has-text('Sign in')",
                            "button[type='submit']");

                    if (!signInBtn)
                        await passwordInput.PressAsync("Enter");

                    // If Target shows "Something went wrong" banner, wait and retry once
                    await page.WaitForTimeoutAsync(2000);
                    var errorBanner = page.Locator("text='Something went wrong on our end'").First;
                    if (await errorBanner.IsVisibleAsync())
                    {
                        _logger.LogWarning("Target reauth: 'Something went wrong' — waiting 4s and retrying");
                        await page.WaitForTimeoutAsync(4000);
                        // Clear and retype password
                        var pwRetry = page.Locator("input[type='password'], input[autocomplete='current-password']").First;
                        if (await pwRetry.IsVisibleAsync())
                        {
                            await pwRetry.ClickAsync();
                            await page.Keyboard.PressAsync("Control+A");
                            await page.WaitForTimeoutAsync(200);
                            await pwRetry.TypeAsync(account.Password, new LocatorTypeOptions { Delay = 110 });
                            await page.WaitForTimeoutAsync(700);
                            var retried = await TryClickFirstVisibleAsync(page,
                                "button:has-text('Sign in with password')",
                                "button:has-text('Sign In with password')",
                                "button:has-text('Sign in')",
                                "button[type='submit']");
                            if (!retried)
                                await pwRetry.PressAsync("Enter");
                        }
                    }
                }

                await page.WaitForTimeoutAsync(3000);

                // Dismiss Circle prompt if it appears again
                var dontJoin = page.GetByText("Don't join", new() { Exact = true });
                var maybeLater = page.GetByText("Maybe later", new() { Exact = true });
                if (await dontJoin.IsVisibleAsync())
                    await dontJoin.ClickAsync();
                else if (await maybeLater.IsVisibleAsync())
                    await maybeLater.ClickAsync();

                await page.WaitForTimeoutAsync(1500);
                _logger.LogInformation("Target reauth modal handled");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Target reauth modal handling error — continuing");
            }
        }

        private async Task<bool> LoginTargetAsync(IPage page, SiteAccount account, CancellationToken ct)
        {
            _logger.LogInformation("Playwright: logging into Target as {Email}", account.Email);

            await page.GotoAsync("https://www.target.com/account/login",
                new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });

            await page.WaitForTimeoutAsync(2000);

            // ?? Already logged in? ????????????????????????????????????????
            // If the persistent profile has a valid session, Target redirects
            // straight to /account — no need to go through the login flow.
            if (!page.Url.Contains("/login") && !page.Url.Contains("create_session"))
            {
                _logger.LogInformation("Target: already logged in via persistent session — URL: {Url}", page.Url);
                return true;
            }

            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            // ?? Step 1: Enter email ???????????????????????????????????????
            try
            {
                await page.WaitForSelectorAsync(
                    "input[id='username'], input[type='email'], input[autocomplete='email']",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                _logger.LogWarning("Target login: email field not found");
                return false;
            }

            var emailInput = page.Locator("input[id='username'], input[type='email'], input[autocomplete='email']").First;
            await emailInput.ClickAsync();
            await page.WaitForTimeoutAsync(300);
            await emailInput.TypeAsync(account.Email, new LocatorTypeOptions { Delay = 75 });

            var continueBtn = page.Locator("button[type='submit'], button:has-text('Continue'), button:has-text('Next')").First;
            await continueBtn.ClickAsync();
            await page.WaitForTimeoutAsync(2500);

            // ?? Step 2: Method selection screen ??????????????????????????
            // Target shows "Use a passkey / Enter your password / Get a code"
            // Click "Enter your password" — could be any element type (div, li, button, a)
            try
            {
                await page.WaitForTimeoutAsync(1500);

                // Try GetByText first — matches any element regardless of tag
                var enterPwByText = page.GetByText("Enter your password", new() { Exact = true });
                if (await enterPwByText.IsVisibleAsync())
                {
                    _logger.LogInformation("Target: clicking 'Enter your password'");
                    await enterPwByText.ClickAsync();
                    await page.WaitForTimeoutAsync(2000);
                }
                else
                {
                    // Fallback: any clickable element containing the text
                    var enterPwFallback = page.Locator("*:has-text('Enter your password')").Last;
                    if (await enterPwFallback.IsVisibleAsync())
                    {
                        await enterPwFallback.ClickAsync();
                        await page.WaitForTimeoutAsync(2000);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Target: method selection click failed — continuing");
            }

            // ?? Step 3: Enter password ????????????????????????????????????
            try
            {
                await page.WaitForSelectorAsync(
                    "input[type='password'], input[id='password'], input[autocomplete='current-password']",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                _logger.LogWarning("Target login: password field did not appear");
                return false;
            }

            var passwordInput = page.Locator("input[type='password'], input[id='password'], input[autocomplete='current-password']").First;
            await passwordInput.ClickAsync();
            await page.WaitForTimeoutAsync(300);
            await passwordInput.TypeAsync(account.Password, new LocatorTypeOptions { Delay = 80 });

            // Check "Keep me signed in" to persist the session
            try
            {
                var keepSignedIn = page.Locator("input[type='checkbox'][id*='keep'], input[type='checkbox'][name*='keep'], label:has-text('Keep me signed in')").First;
                if (await keepSignedIn.IsVisibleAsync())
                    await keepSignedIn.CheckAsync();
            }
            catch { }

            await SolveCaptchaIfPresentAsync(page, "target.com", TargetRecaptchaSiteKey, ct);

            var signInBtn = page.Locator("button:has-text('Sign in with password'), button[type='submit'], button:has-text('Sign in')").First;
            await signInBtn.ClickAsync();

            // Wait — Target loads "Still loading..." then Circle upsell or account page
            await page.WaitForTimeoutAsync(5000);

            // ?? Step 4: Dismiss "Still loading..." overlay if present ?????
            try
            {
                await page.WaitForSelectorAsync(
                    "text=Still loading",
                    new PageWaitForSelectorOptions { Timeout = 5000, State = WaitForSelectorState.Hidden });
            }
            catch { /* overlay may not appear or already gone */ }

            // ?? Step 5: Dismiss Target Circle join prompt ?????????????????
            try
            {
                var dontJoin   = page.GetByText("Don't join", new() { Exact = true });
                var maybeLater = page.GetByText("Maybe later", new() { Exact = true });

                if (await dontJoin.IsVisibleAsync())
                {
                    _logger.LogInformation("Target Circle prompt — clicking 'Don't join'");
                    await dontJoin.ClickAsync();
                    await page.WaitForTimeoutAsync(2000);
                }
                else if (await maybeLater.IsVisibleAsync())
                {
                    _logger.LogInformation("Target Circle prompt — clicking 'Maybe later'");
                    await maybeLater.ClickAsync();
                    await page.WaitForTimeoutAsync(2000);
                }
            }
            catch { }

            // ?? Step 5: Handle OTP if prompted ????????????????????????????
            var otpInput = page.Locator("input[name='code'], input[autocomplete='one-time-code']").First;
            if (await otpInput.IsVisibleAsync())
            {
                _logger.LogInformation("Target OTP challenge detected");

                if (account.UseEmailVerification)
                {
                    _logger.LogInformation("Waiting up to 60s for OTP (IMAP auto-fetch not in Playwright path)");
                    await page.WaitForTimeoutAsync(60000);
                }
                else
                {
                    _logger.LogInformation("Waiting 60s for manual OTP entry in browser window");
                    await page.WaitForTimeoutAsync(60000);
                }

                var otpSubmit = page.Locator("button[type='submit'], button:has-text('Verify'), button:has-text('Continue')").First;
                if (await otpSubmit.IsVisibleAsync())
                    await otpSubmit.ClickAsync();

                await page.WaitForTimeoutAsync(3000);
            }

            var finalUrl = page.Url;

            // The Circle upsell page, account page, or any target.com page that
            // isn't the username-entry step all count as successful login.
            // The initial email step URL contains "create_session_request_username".
            var stillOnEmailStep = finalUrl.Contains("create_session_request_username");
            var success = finalUrl.Contains("target.com") && !stillOnEmailStep;

            _logger.LogInformation("Target Playwright login {Status} — URL: {Url}",
                success ? "succeeded" : "failed", finalUrl);
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

            // ?? Check if card is already saved ????????????????????????????
            // If a saved card is already selected, just click Save and continue
            var savedCard = page.Locator("[data-test='saved-payment'], input[type='radio'][value*='card']:checked").First;
            var cardAlreadySaved = await savedCard.IsVisibleAsync();

            if (!cardAlreadySaved)
            {
                // ?? Select "Credit or debit card" radio button ?????????????
                var creditRadio = page.Locator("input[type='radio'] + label:has-text('Credit or debit card'), " +
                                               "label:has-text('Credit or debit card')").First;
                if (await creditRadio.IsVisibleAsync())
                {
                    await creditRadio.ClickAsync();
                    await page.WaitForTimeoutAsync(1500);
                }
                else
                {
                    // Try clicking the radio button directly next to the label
                    var creditRadioDirect = page.Locator("input[type='radio'][value*='credit'], input[type='radio'][id*='credit']").First;
                    if (await creditRadioDirect.IsVisibleAsync())
                    {
                        await creditRadioDirect.ClickAsync();
                        await page.WaitForTimeoutAsync(1500);
                    }
                    else
                    {
                        // Last resort — click the row that contains the text
                        var creditRow = page.GetByText("Credit or debit card").First;
                        if (await creditRow.IsVisibleAsync())
                        {
                            await creditRow.ClickAsync();
                            await page.WaitForTimeoutAsync(1500);
                        }
                    }
                }

                // ?? Fill card number ???????????????????????????????????????
                var cardNumber = page.Locator("input[name='cardNumber'], input[id*='cardNumber'], input[placeholder*='Card number'], input[autocomplete='cc-number']").First;
                if (await cardNumber.IsVisibleAsync())
                    await cardNumber.FillAsync(pay.CardNumber);

                await page.WaitForTimeoutAsync(500);

                // ?? Fill expiry ????????????????????????????????????????????
                // Try combined MM/YY field first, then separate month/year fields
                var expiryCombined = page.Locator("input[name='expiry'], input[placeholder*='MM/YY'], input[autocomplete='cc-exp']").First;
                if (await expiryCombined.IsVisibleAsync())
                {
                    await expiryCombined.FillAsync($"{pay.ExpiryMonth}/{pay.ExpiryYear}");
                }
                else
                {
                    var expMonth = page.Locator("select[name='expirationMonth'], input[name='expiryMonth']").First;
                    if (await expMonth.IsVisibleAsync()) await expMonth.FillAsync(pay.ExpiryMonth);

                    var expYear = page.Locator("select[name='expirationYear'], input[name='expiryYear']").First;
                    if (await expYear.IsVisibleAsync()) await expYear.FillAsync(pay.ExpiryYear);
                }

                // ?? Fill CVV ???????????????????????????????????????????????
                var cvv = page.Locator("input[name='cvv'], input[id*='cvv'], input[autocomplete='cc-csc'], input[placeholder*='CVV'], input[placeholder*='CVC']").First;
                if (await cvv.IsVisibleAsync())
                    await cvv.FillAsync(pay.Cvv);

                await page.WaitForTimeoutAsync(500);
            }
            else
            {
                _logger.LogInformation("Target: saved card already selected, skipping card entry");
            }

            // ?? Click Save and continue ????????????????????????????????????
            var saveBtn = page.Locator("button:has-text('Save and continue'), button:has-text('Save & continue'), button:has-text('Continue')").First;
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

            // If Walmart checkout is already pre-filled (saved payment), skip straight to place order
            var placeOrder = page.Locator("button:has-text('Place order'), button:has-text('Review your order')").First;
            if (!await placeOrder.IsVisibleAsync())
            {
                // Fill shipping
                await FillWalmartShippingAsync(page, profile, account);

                // Fill payment
                await FillWalmartPaymentAsync(page, profile);
            }

            // Re-locate after possible page changes
            placeOrder = page.Locator("button:has-text('Place order'), button:has-text('Review your order')").First;
            if (!await placeOrder.IsVisibleAsync())
            {
                result.Status = CheckoutStatus.Failed;
                result.ErrorMessage = "Place order button not found.";
                return result;
            }

            await placeOrder.ClickAsync();

            // Wait up to 30s for Walmart's order confirmation page
            try
            {
                await page.WaitForURLAsync(
                    url => url.Contains("thank-you") || url.Contains("order-confirmation") || url.Contains("/account/order/"),
                    new PageWaitForURLOptions { Timeout = 30000 });
            }
            catch
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await page.WaitForTimeoutAsync(5000);
            }

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

            await page.GotoAsync("https://www.walmart.com/account/login",
                new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });

            await page.WaitForTimeoutAsync(2000);

            // ?? Already logged in? ????????????????????????????????????????
            if (!page.Url.Contains("/login"))
            {
                _logger.LogInformation("Walmart: already logged in via persistent session — URL: {Url}", page.Url);
                return true;
            }

            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            // Dismiss passkey / "Use password instead" prompt if present
            await DismissPasskeyPromptAsync(page);

            // ?? Step 1: Email ?????????????????????????????????????????????
            try
            {
                await page.WaitForSelectorAsync(
                    "input[name='email'], input[type='email'], input[autocomplete='email']",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                _logger.LogWarning("Walmart login: email field not found");
                return false;
            }

            var emailInput = page.Locator("input[name='email'], input[type='email'], input[autocomplete='email']").First;
            await emailInput.ClickAsync();
            await page.WaitForTimeoutAsync(300);
            await emailInput.TypeAsync(account.Email, new LocatorTypeOptions { Delay = 75 });

            var continueBtn = page.Locator("button:has-text('Continue'), button[type='submit']").First;
            await continueBtn.ClickAsync();
            await page.WaitForTimeoutAsync(2000);

            // ?? Step 2: Password ??????????????????????????????????????????
            try
            {
                await page.WaitForSelectorAsync(
                    "input[type='password'], input[name='password'], input[autocomplete='current-password']",
                    new PageWaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                _logger.LogWarning("Walmart login: password field did not appear");
                return false;
            }

            var passwordInput = page.Locator("input[type='password'], input[name='password']").First;
            await passwordInput.ClickAsync();
            await page.WaitForTimeoutAsync(300);
            await passwordInput.TypeAsync(account.Password, new LocatorTypeOptions { Delay = 80 });

            await SolveCaptchaIfPresentAsync(page, "walmart.com", WalmartRecaptchaSiteKey, ct);

            var signInBtn = page.Locator("button:has-text('Sign in with password'), button[type='submit'], button:has-text('Sign in')").First;
            await signInBtn.ClickAsync();

            try
            {
                await page.WaitForURLAsync(url => !url.Contains("/login"), new PageWaitForURLOptions { Timeout = 15000 });
            }
            catch { }

            await page.WaitForTimeoutAsync(2000);

            // ?? Step 3: OTP if prompted ???????????????????????????????????
            var otpInput = page.Locator("input[name='code'], input[autocomplete='one-time-code'], input[placeholder*='code']").First;
            if (await otpInput.IsVisibleAsync())
            {
                _logger.LogInformation("Walmart OTP challenge detected — waiting 60s for code");
                await page.WaitForTimeoutAsync(60000);

                var otpSubmit = page.Locator("button[type='submit'], button:has-text('Verify'), button:has-text('Continue')").First;
                if (await otpSubmit.IsVisibleAsync())
                    await otpSubmit.ClickAsync();

                await page.WaitForTimeoutAsync(3000);
            }

            var finalUrl = page.Url;
            var success  = finalUrl.Contains("walmart.com") && !finalUrl.Contains("/login");
            _logger.LogInformation("Walmart Playwright login {Status} — URL: {Url}",
                success ? "succeeded" : "failed", finalUrl);
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

        // ?? PASSKEY PROMPT DISMISSAL ?????????????????????????????????????????
        // Target and Walmart show a "Use passkey or password?" modal on login pages.
        // We always want password — click the appropriate link to dismiss it.
        private static async Task DismissPasskeyPromptAsync(IPage page)
        {
            try
            {
                // Give the prompt up to 3 seconds to appear
                await page.WaitForTimeoutAsync(1500);

                // Common selectors for "Use password instead" / "Sign in a different way" links
                var usePassword = page.Locator(
                    "button:has-text('Use password'), " +
                    "button:has-text('Use a password'), " +
                    "a:has-text('Use password'), " +
                    "button:has-text('Sign in a different way'), " +
                    "button:has-text('More options'), " +
                    "[data-test='use-password-link'], " +
                    "button:has-text('Cancel')").First;

                if (await usePassword.IsVisibleAsync())
                    await usePassword.ClickAsync();

                // Also handle browser-level credential dialogs by pressing Escape
                await page.Keyboard.PressAsync("Escape");
                await page.WaitForTimeoutAsync(500);
            }
            catch { /* prompt may not appear — that's fine */ }
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
