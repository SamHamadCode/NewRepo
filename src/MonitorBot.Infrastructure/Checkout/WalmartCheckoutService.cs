using System;
using System.Collections.Generic;
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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Checkout
{
    /// <summary>
    /// Walmart checkout flow � mirrors RefractBot step-by-step:
    ///   1. LoggingIn    � authenticate with account (if assigned)
    ///   2. AddingToCart � add the exact requested quantity to the cart
    ///   3. PlacingOrder � contract + order submission
    /// </summary>
    public class WalmartCheckoutService : ICheckoutService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<WalmartCheckoutService> _logger;
        private readonly WalmartLoginService _loginService;

        private static readonly Regex ItemIdRegex = new(
            @"/ip/[^/]+/(\d+)", RegexOptions.Compiled);
        private static readonly Regex CsrfRegex = new(
            @"""csp""\s*:\s*\{[^}]*""token""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public WalmartCheckoutService(
            IHttpClientFactory httpClientFactory,
            ILogger<WalmartCheckoutService> logger,
            WalmartLoginService loginService)
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
            _logger.LogInformation("Starting Walmart checkout for task {Name}", task.Name);

            // Clamp quantity � never send 0 or an absurdly large number
            var quantity = Math.Max(1, Math.Min(task.Quantity, 10));

            try
            {
                // ── Step 1: Auth ───────────────────────────────────────────
                // Priority: harvested cookies → account login → guest
                string? sessionCookies = null;
                if (!string.IsNullOrWhiteSpace(task.CookieOverride))
                {
                    sessionCookies = task.CookieOverride;
                    _logger.LogInformation("Using harvested cookies for Walmart checkout");
                }
                else if (account != null)
                {
                    onStatus?.Invoke(MonitorTaskStatus.LoggingIn);
                    _logger.LogInformation("Attempting Walmart account login: {Email}", account.Email);
                    sessionCookies = await _loginService.LoginAsync(account, ct);
                    if (sessionCookies == null)
                    {
                        result.Status = CheckoutStatus.Failed;
                        result.ErrorMessage =
                            $"Login failed for {account.Email}. " +
                            "Walmart blocks automated logins — use 'Auto-Harvest Cookies' in the task editor instead.";
                        return result;
                    }
                    _logger.LogInformation("Walmart login successful");
                }
                else
                {
                    _logger.LogInformation("No account or cookies — proceeding as Walmart guest checkout");
                }

                using var client = BuildClient(sessionCookies, null);

                // ?? Step 2: Add to cart ????????????????????????????????????
                onStatus?.Invoke(MonitorTaskStatus.AddingToCart);

                var itemId = ExtractItemId(task.TargetUrl);
                if (string.IsNullOrEmpty(itemId))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Could not extract Walmart item ID from URL.";
                    return result;
                }

                _logger.LogDebug("Walmart item ID: {ItemId}", itemId);

                var (csrfToken, cartId) = await FetchTokensAsync(client, task.TargetUrl, ct);
                if (string.IsNullOrEmpty(cartId))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Could not obtain cart session from Walmart.";
                    return result;
                }

                var addedToCart = await AddToCartAsync(client, cartId, itemId, quantity, csrfToken, ct);
                if (!addedToCart)
                {
                    result.Status = CheckoutStatus.OutOfStock;
                    result.ErrorMessage = "Add-to-cart failed � item may have sold out.";
                    return result;
                }

                _logger.LogInformation("Added item {ItemId} �{Qty} to cart {CartId}", itemId, quantity, cartId);

                // ?? Step 3: Place order ????????????????????????????????????
                onStatus?.Invoke(MonitorTaskStatus.PlacingOrder);

                var contractId = await CreateContractAsync(client, cartId, profile, csrfToken, ct);
                if (string.IsNullOrEmpty(contractId))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Failed to create checkout contract.";
                    return result;
                }

                var orderId = await PlaceOrderAsync(client, contractId, profile, csrfToken, ct);
                if (string.IsNullOrEmpty(orderId))
                {
                    result.Status = CheckoutStatus.CardDeclined;
                    result.ErrorMessage = "Order submission failed � check card details.";
                    return result;
                }

                result.IsSuccess = true;
                result.Status = CheckoutStatus.Success;
                result.OrderId = orderId;
                _logger.LogInformation("Walmart order placed! OrderId={OrderId}", orderId);
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
                _logger.LogWarning(ex, "Checkout error for task {Name}", task.Name);
            }

            return result;
        }

        // ?? Helpers ???????????????????????????????????????????????????????????

        private HttpClient BuildClient(string? sessionCookies = null, string? cookieOverride = null)
        {
            // IMPORTANT: UseCookies=false so the manually-injected Cookie header
            // is not stripped by the handler's own cookie management.
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
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.walmart.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.walmart.com/");

            // Prefer account session cookies; fall back to manually pasted cookies
            var cookies = !string.IsNullOrWhiteSpace(sessionCookies) ? sessionCookies : cookieOverride;
            if (!string.IsNullOrEmpty(cookies))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies);

            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private static string? ExtractItemId(string url)
        {
            var m = ItemIdRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        private async Task<(string? csrfToken, string? cartId)> FetchTokensAsync(
            HttpClient client, string productUrl, CancellationToken ct)
        {
            try
            {
                var html = await client.GetStringAsync(productUrl, ct);

                string? csrf = null;
                var csrfMatch = CsrfRegex.Match(html);
                if (csrfMatch.Success) csrf = csrfMatch.Groups[1].Value;

                // Walmart guest cart ID is a client-generated GUID
                var cartId = Guid.NewGuid().ToString("N");

                return (csrf, cartId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Walmart tokens");
                return (null, null);
            }
        }

        private async Task<bool> AddToCartAsync(
            HttpClient client, string cartId, string itemId,
            int quantity, string? csrfToken, CancellationToken ct)
        {
            var url = $"https://www.walmart.com/api/v3/cart/guest/{cartId}/items";

            var payload = new JObject
            {
                ["items"] = new JArray
                {
                    new JObject
                    {
                        ["offerId"]   = itemId,
                        ["quantity"]  = quantity,
                        ["addMethod"] = "DEFAULT"
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            AddApiHeaders(req, csrfToken);

            var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Add-to-cart HTTP {Status}", (int)resp.StatusCode);
                return false;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JObject.Parse(body);
            return json["cart"]?["count"]?.Value<int>() > 0;
        }

        private async Task<string?> CreateContractAsync(
            HttpClient client, string cartId, UserProfile profile,
            string? csrfToken, CancellationToken ct)
        {
            var url = "https://www.walmart.com/api/v3/checkout/guest/contract";

            var addr = profile.ShippingAddress;
            var payload = new JObject
            {
                ["cartId"] = cartId,
                ["customer"] = new JObject
                {
                    ["firstName"] = profile.FirstName,
                    ["lastName"]  = profile.LastName,
                    ["email"]     = profile.Email,
                    ["phone"]     = profile.Phone
                },
                ["shippingAddress"] = new JObject
                {
                    ["firstName"]      = profile.FirstName,
                    ["lastName"]       = profile.LastName,
                    ["addressLineOne"] = addr.Line1,
                    ["addressLineTwo"] = addr.Line2,
                    ["city"]           = addr.City,
                    ["state"]          = addr.State,
                    ["postalCode"]     = addr.ZipCode,
                    ["country"]        = addr.Country.Length == 2 ? addr.Country : "US"
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            AddApiHeaders(req, csrfToken);

            var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Contract HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JObject.Parse(body);
            return json["contractId"]?.ToString();
        }

        private async Task<string?> PlaceOrderAsync(
            HttpClient client, string contractId, UserProfile profile,
            string? csrfToken, CancellationToken ct)
        {
            var url = "https://www.walmart.com/api/v3/checkout/guest/order";

            var pay = profile.Payment;
            var payload = new JObject
            {
                ["contractId"] = contractId,
                ["payment"] = new JObject
                {
                    ["paymentType"]  = "CREDITCARD",
                    ["cardType"]     = GuessCardType(pay.CardNumber),
                    ["cardNumber"]   = pay.CardNumber,
                    ["cardHolder"]   = $"{profile.FirstName} {profile.LastName}",
                    ["expiryMonth"]  = pay.ExpiryMonth,
                    ["expiryYear"]   = pay.ExpiryYear,
                    ["cvv"]          = pay.Cvv,
                    ["billingAddress"] = new JObject
                    {
                        ["firstName"]      = profile.FirstName,
                        ["lastName"]       = profile.LastName,
                        ["addressLineOne"] = profile.BillingAddress.Line1.Length > 0
                                             ? profile.BillingAddress.Line1
                                             : profile.ShippingAddress.Line1,
                        ["city"]           = profile.BillingAddress.City.Length > 0
                                             ? profile.BillingAddress.City
                                             : profile.ShippingAddress.City,
                        ["state"]          = profile.BillingAddress.State.Length > 0
                                             ? profile.BillingAddress.State
                                             : profile.ShippingAddress.State,
                        ["postalCode"]     = profile.BillingAddress.ZipCode.Length > 0
                                             ? profile.BillingAddress.ZipCode
                                             : profile.ShippingAddress.ZipCode,
                        ["country"]        = "US"
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            AddApiHeaders(req, csrfToken);

            var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Place-order HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var json = JObject.Parse(body);
            return json["order"]?["id"]?.ToString()
                ?? json["orderId"]?.ToString();
        }

        private static void AddApiHeaders(HttpRequestMessage req, string? csrfToken)
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("WM_PAGE_URL", "https://www.walmart.com/checkout");
            req.Headers.TryAddWithoutValidation("WM_QOS.CORRELATION_ID", Guid.NewGuid().ToString());
            req.Headers.TryAddWithoutValidation("WM_SVC.NAME", "walmart-client");
            if (!string.IsNullOrEmpty(csrfToken))
                req.Headers.TryAddWithoutValidation("WM_CSRF_TOKEN", csrfToken);
        }

        private static string GuessCardType(string number)
        {
            if (string.IsNullOrWhiteSpace(number)) return "VISA";
            var n = number.Replace(" ", "");
            if (n.StartsWith("4"))                                          return "VISA";
            if (n.StartsWith("51") || n.StartsWith("52") ||
                n.StartsWith("53") || n.StartsWith("54") ||
                n.StartsWith("55") || n.StartsWith("2"))                    return "MASTERCARD";
            if (n.StartsWith("34") || n.StartsWith("37"))                   return "AMEX";
            if (n.StartsWith("6011") || n.StartsWith("65"))                 return "DISCOVER";
            return "VISA";
        }
    }
}
