using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Checkout
{
    /// <summary>
    /// Target checkout flow � mirrors RefractBot step-by-step:
    ///   1. LoggingIn    � authenticate with account (if assigned)
    ///   2. AddingToCart � POST to carts.target.com with exact quantity
    ///   3. PlacingOrder � POST checkout order with shipping + payment
    /// </summary>
    public class TargetCheckoutService : ICheckoutService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TargetCheckoutService> _logger;
        private readonly TargetLoginService _loginService;

        private static readonly Regex TcinRegex = new(
            @"/-/A-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TcinAltRegex = new(
            @"[?&]preselect=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public TargetCheckoutService(
            IHttpClientFactory httpClientFactory,
            ILogger<TargetCheckoutService> logger,
            TargetLoginService loginService)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _loginService = loginService;
        }

        public async Task<CheckoutResult> CheckoutAsync(
            MonitorTask task,
            UserProfile profile,
            SiteAccount? account,
            MonitorResult monitorResult,
            Action<MonitorTaskStatus>? onStatus = null,
            CancellationToken ct = default)
        {
            var result = new CheckoutResult { TaskId = task.Id };
            _logger.LogInformation("Starting Target checkout for task {Name}", task.Name);

            // Clamp quantity � never send 0 or an absurdly large number
            var quantity = Math.Max(1, Math.Min(task.Quantity, 10));

            try
            {
                // Use SKU field first (most reliable), fall back to parsing URL
                var tcin = !string.IsNullOrWhiteSpace(task.Sku)
                    ? task.Sku!.Trim()
                    : ExtractTcin(task.TargetUrl);

                if (string.IsNullOrEmpty(tcin))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Could not determine TCIN. Enter the numeric item ID in the SKU field (e.g. 12856565).";
                    return result;
                }

                _logger.LogDebug("Target TCIN: {Tcin}", tcin);

                // ── Step 1: Auth ───────────────────────────────────────────
                // Priority: harvested cookies → account login → guest
                string? sessionCookies = null;
                if (!string.IsNullOrWhiteSpace(task.CookieOverride))
                {
                    sessionCookies = task.CookieOverride;
                    _logger.LogInformation("Using harvested cookies for Target checkout");
                }
                else if (account != null)
                {
                    onStatus?.Invoke(MonitorTaskStatus.LoggingIn);
                    _logger.LogInformation("Attempting Target account login: {Email}", account.Email);
                    sessionCookies = await _loginService.LoginAsync(account, ct);
                    if (sessionCookies == null)
                    {
                        result.Status = CheckoutStatus.Failed;
                        result.ErrorMessage =
                            $"Login failed for {account.Email}. " +
                            "Target blocks automated logins — use 'Auto-Harvest Cookies' in the task editor instead.";
                        return result;
                    }
                    _logger.LogInformation("Target login successful");
                }
                else
                {
                    _logger.LogInformation("No account or cookies — proceeding as guest");
                }

                using var client = BuildClient(sessionCookies, null);

                // ── Step 2: Add to cart ────────────────────────────────────
                onStatus?.Invoke(MonitorTaskStatus.AddingToCart);
                var (atcOk, atcError) = await AddToCartAsync(client, tcin, quantity, sessionCookies, ct);
                if (!atcOk)
                {
                    result.Status       = CheckoutStatus.Failed;
                    result.ErrorMessage = atcError;
                    return result;
                }

                _logger.LogInformation("Added TCIN {Tcin} �{Qty} to Target cart", tcin, quantity);

                // ?? Step 3: Place order ????????????????????????????????????
                onStatus?.Invoke(MonitorTaskStatus.PlacingOrder);
                var orderId = await PlaceOrderAsync(client, tcin, profile, quantity, ct);
                if (string.IsNullOrEmpty(orderId))
                {
                    result.Status = CheckoutStatus.CardDeclined;
                    result.ErrorMessage = "Order submission failed � check card details.";
                    return result;
                }

                result.IsSuccess = true;
                result.Status = CheckoutStatus.Success;
                result.OrderId = orderId;
                _logger.LogInformation("Target order placed! OrderId={OrderId}", orderId);
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
                _logger.LogWarning(ex, "Target checkout error for task {Name}", task.Name);
            }

            return result;
        }

        private static string? ExtractTcin(string url)
        {
            var m = TcinRegex.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = TcinAltRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        private HttpClient BuildClient(string? sessionCookies = null, string? cookieOverride = null)
        {
            // UseCookies=false so our injected Cookie header is not stripped
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                       | DecompressionMethods.Deflate
                                       | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                UseCookies = false
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.target.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.target.com/");

            var cookies = !string.IsNullOrWhiteSpace(sessionCookies) ? sessionCookies : cookieOverride;
            if (!string.IsNullOrEmpty(cookies))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies);

                // Target's cart API requires the accessToken as a Bearer token
                // in the Authorization header — without it you get ERR_AUTH_DENIED
                var accessToken = ExtractCookieValue(cookies, "accessToken")
                               ?? ExtractCookieValue(cookies, "target_access_token");
                if (!string.IsNullOrEmpty(accessToken))
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", $"Bearer {accessToken}");
            }

            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private async Task<(bool ok, string error)> AddToCartAsync(
            HttpClient client, string tcin, int quantity, string? rawCookies, CancellationToken ct)
        {
            // Endpoint and key captured directly from Target's browser DevTools (July 2025)
            const string apiKey      = "9f36aeafbe60771e321a7cc95a78140772ab3e96";
            const string fieldGroups = "CART%2CCART_ITEMS%2CSUMMARY";
            var url = $"https://carts.target.com/web_checkouts/v1/cart_items" +
                      $"?field_groups={fieldGroups}&key={apiKey}";

            // Payload matches exactly what Target's web app sends (channel_id "10" = web/digital)
            var payload = new JObject
            {
                ["cart_item"] = new JObject
                {
                    ["item_channel_id"] = "10",
                    ["tcin"]            = tcin,
                    ["quantity"]        = quantity
                },
                ["cart_type"]       = "REGULAR",
                ["channel_id"]      = "10",
                ["fulfillment"]     = new JObject
                {
                    ["type"]        = "SHIP",
                    ["ship_method"] = "STANDARD"
                },
                ["shopping_context"] = "DIGITAL"
            };
            var payloadStr = payload.ToString(Newtonsoft.Json.Formatting.None);

            _logger.LogDebug("Target ATC payload: {P}", payloadStr);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payloadStr, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("X-Application-Name", "web");

            var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 600 ? body[..600] : body;

            _logger.LogDebug("Target ATC POST HTTP {S} tcin={T}: {B}",
                (int)resp.StatusCode, tcin, snippet);

            if (resp.IsSuccessStatusCode)
                return (true, string.Empty);

            string lastError;
            try
            {
                var j = JObject.Parse(body);
                lastError = $"HTTP {(int)resp.StatusCode}: " +
                    (j["message"]?.ToString()
                  ?? j["error"]?.ToString()
                  ?? j["errors"]?[0]?["message"]?.ToString()
                  ?? snippet);
            }
            catch { lastError = $"HTTP {(int)resp.StatusCode}: {snippet}"; }

            _logger.LogWarning("Target ATC failed: {E}", lastError);
            return (false, $"ATC failed — {lastError}");
        }

        /// <summary>Extracts a single cookie value from a raw Cookie header string.</summary>
        private static string? ExtractCookieValue(string? rawCookies, string name)
        {
            if (string.IsNullOrWhiteSpace(rawCookies)) return null;
            foreach (var part in rawCookies.Split(';'))
            {
                var kv = part.Trim();
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                if (kv[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(kv[(eq + 1)..].Trim());
            }
            return null;
        }

        private async Task<string?> PlaceOrderAsync(
            HttpClient client, string tcin, UserProfile profile, int quantity, CancellationToken ct)
        {
            var url = "https://carts.target.com/web_checkouts/v1/checkout?key=9f36aeafbe60771e321a7cc95a78140772ab3e96";

            var addr = profile.ShippingAddress;
            var pay  = profile.Payment;

            var payload = new JObject
            {
                ["addresses"] = new JArray
                {
                    new JObject
                    {
                        ["address_type"]  = "SHIPPING",
                        ["first_name"]    = profile.FirstName,
                        ["last_name"]     = profile.LastName,
                        ["email_address"] = profile.Email,
                        ["phone"]         = profile.Phone,
                        ["line1"]         = addr.Line1,
                        ["line2"]         = addr.Line2,
                        ["city"]          = addr.City,
                        ["state"]         = addr.State,
                        ["zip_code"]      = addr.ZipCode,
                        ["country_code"]  = "US"
                    }
                },
                ["payment_instructions"] = new JArray
                {
                    new JObject
                    {
                        ["payment_type"]    = "CREDITCARD",
                        ["card_number"]     = pay.CardNumber,
                        ["expiration_date"] = $"{pay.ExpiryMonth}/{pay.ExpiryYear}",
                        ["cvv"]             = pay.Cvv,
                        ["billing_address"] = new JObject
                        {
                            ["first_name"] = profile.FirstName,
                            ["last_name"]  = profile.LastName,
                            ["line1"]      = profile.BillingAddress.Line1.Length > 0
                                             ? profile.BillingAddress.Line1 : addr.Line1,
                            ["city"]       = profile.BillingAddress.City.Length > 0
                                             ? profile.BillingAddress.City : addr.City,
                            ["state"]      = profile.BillingAddress.State.Length > 0
                                             ? profile.BillingAddress.State : addr.State,
                            ["zip_code"]   = profile.BillingAddress.ZipCode.Length > 0
                                             ? profile.BillingAddress.ZipCode : addr.ZipCode,
                            ["country_code"] = "US"
                        }
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Target place-order HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JObject.Parse(body);
            return json["order_id"]?.ToString()
                ?? json["id"]?.ToString()
                ?? Guid.NewGuid().ToString("N")[..12];
        }
    }
}
