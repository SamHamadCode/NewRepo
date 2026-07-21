using System;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly ILogStore _logStore;
        private readonly IWalmartBrowserStockChecker? _browser;

        private static readonly Regex ItemIdRegex = new(
            @"/ip/[^/]+/(\d+)", RegexOptions.Compiled);
        private static readonly Regex CsrfRegex = new(
            @"""csp""\s*:\s*\{[^}]*""token""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NextDataRegex = new(
            @"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>",
            RegexOptions.Compiled);

        public WalmartCheckoutService(
            IHttpClientFactory httpClientFactory,
            ILogger<WalmartCheckoutService> logger,
            WalmartLoginService loginService,
            ILogStore logStore,
            IWalmartBrowserStockChecker? browser = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _loginService = loginService;
            _logStore = logStore;
            _browser = browser;
        }

        private void Log(string level, string message, string? taskId = null) =>
            _logStore.Add(new LogEntry
            {
                Level    = level,
                Category = "Checkout",
                Message  = message,
                TaskId   = taskId
            });

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

                // ── Step 2: Add to cart ────────────────────────────────────
                onStatus?.Invoke(MonitorTaskStatus.AddingToCart);

                // itemId: SKU field first, then extract from URL
                var itemId = !string.IsNullOrWhiteSpace(task.Sku)
                    ? task.Sku!.Trim()
                    : ExtractItemId(task.TargetUrl ?? string.Empty);

                if (string.IsNullOrEmpty(itemId))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "No item ID found. Enter the Walmart item ID in the SKU field (e.g. 5037629800).";
                    return result;
                }

                _logger.LogDebug("Walmart item ID: {ItemId}", itemId);

                // If OfferIdOverride is set we have everything we need — skip the page fetch entirely.
                // FetchTokensAsync hits walmart.com over HTTP which gets 412/521 from Cloudflare.
                string? csrfToken, cartId, offerId;
                if (!string.IsNullOrWhiteSpace(task.OfferIdOverride))
                {
                    csrfToken = null;
                    cartId    = Guid.NewGuid().ToString("N");
                    offerId   = null; // OfferIdOverride used below as atcId
                    Log("INFO", $"[Walmart] SKU+OfferID mode — skipping page fetch (cartId={cartId})");
                }
                else if (!string.IsNullOrWhiteSpace(task.TargetUrl))
                {
                    (csrfToken, cartId, offerId) = await FetchTokensAsync(client, task.TargetUrl, ct);
                }
                else
                {
                    csrfToken = null;
                    cartId    = Guid.NewGuid().ToString("N");
                    offerId   = null;
                    Log("INFO", $"[Walmart] No URL or OfferID — proceeding with guest cartId={cartId}");
                }

                if (string.IsNullOrEmpty(cartId))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Could not obtain cart session from Walmart.";
                    return result;
                }

                // Use real offerId from page if available, otherwise fall back to itemId
                // Priority: manual OfferIdOverride > page-extracted offerId > numeric itemId
                var atcId = !string.IsNullOrWhiteSpace(task.OfferIdOverride)
                    ? task.OfferIdOverride!.Trim()
                    : !string.IsNullOrEmpty(offerId) ? offerId : itemId;
                Log("INFO", $"[Walmart] ATC id={atcId} (itemId={itemId})");

                bool addedToCart;
                // Browser ATC bypasses Cloudflare 521
                if (_browser != null && !string.IsNullOrEmpty(atcId))
                {
                    Log("INFO", "[Walmart] Using browser ATC");
                    var (browserCartId, atcErr) = await _browser.AddToCartAsync(itemId, atcId, quantity, ct);
                    if (browserCartId != null)
                    {
                        cartId = browserCartId;
                        addedToCart = true;
                        Log("INFO", $"[Walmart] Browser ATC OK — cartId={cartId}");
                    }
                    else
                    {
                        Log("WARN", $"[Walmart] Browser ATC failed ({atcErr}) — falling back to HTTP");
                        addedToCart = await AddToCartAsync(client, cartId!, atcId, quantity, csrfToken, ct);
                    }
                }
                else
                {
                    addedToCart = await AddToCartAsync(client, cartId!, atcId, quantity, csrfToken, ct);
                }

                if (!addedToCart)
                {
                    result.Status = CheckoutStatus.OutOfStock;
                    result.ErrorMessage = "Add-to-cart failed — item may have sold out.";
                    return result;
                }

                Log("INFO", $"[Walmart] ATC OK — item={atcId} x{quantity} cartId={cartId}");

                // ── Step 3: Place order via browser (bypasses Cloudflare/PX 412) ──
                onStatus?.Invoke(MonitorTaskStatus.PlacingOrder);

                string? orderId;
                if (_browser != null)
                {
                    var (browsOrderId, browsErr) = await _browser.BrowserCheckoutAsync(cartId!, profile, ct);
                    if (!string.IsNullOrEmpty(browsOrderId))
                    {
                        orderId = browsOrderId;
                    }
                    else
                    {
                        Log("WARN", $"[Walmart] Browser checkout failed ({browsErr}) — falling back to HTTP");
                        orderId = await PlaceOrderViaHttpAsync(client, cartId!, profile, csrfToken, ct);
                    }
                }
                else
                {
                    orderId = await PlaceOrderViaHttpAsync(client, cartId!, profile, csrfToken, ct);
                }

                if (string.IsNullOrEmpty(orderId))
                {
                    result.Status = CheckoutStatus.CardDeclined;
                    result.ErrorMessage = "Order submission failed — check card details.";
                    return result;
                }

                result.IsSuccess = true;
                result.Status = CheckoutStatus.Success;
                result.OrderId = orderId;
                Log("INFO", $"[Walmart] Order placed! orderId={orderId}");
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
                Log("WARN", $"[Walmart] Checkout error: {ex.Message}");
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

        private async Task<(string? csrfToken, string? cartId, string? offerId)> FetchTokensAsync(
            HttpClient client, string productUrl, CancellationToken ct)
        {
            try
            {
                var html = await client.GetStringAsync(productUrl, ct);

                string? csrf = null;
                var csrfMatch = CsrfRegex.Match(html);
                if (csrfMatch.Success) csrf = csrfMatch.Groups[1].Value;

                // Extract offerId by parsing the __NEXT_DATA__ JSON block directly.
                // Walmart embeds it in a <script id="__NEXT_DATA__"> tag.
                string? offerId = null;
                var ndMatch = NextDataRegex.Match(html);
                if (ndMatch.Success)
                {
                    try
                    {
                        var nd = JObject.Parse(ndMatch.Groups[1].Value);
                        // Try known paths in order of reliability
                        offerId =
                            nd.SelectToken("props.pageProps.initialData.data.product.offers.primary.offerId")?.ToString()
                         ?? nd.SelectToken("props.pageProps.initialData.data.product.buyBox.products[0].offerId")?.ToString()
                         ?? nd.SelectToken("props.pageProps.initialData.data.idml.offerId")?.ToString();

                        // Last resort: collect ALL offerId values and pick the longest (most specific)
                        if (string.IsNullOrEmpty(offerId))
                        {
                            var all = new System.Collections.Generic.List<string>();
                            foreach (var t in nd.Descendants())
                                if (t is JProperty p && p.Name == "offerId" && p.Value.Type == JTokenType.String)
                                {
                                    var v = p.Value.ToString();
                                    if (v.Length >= 10) all.Add(v);
                                }
                            offerId = all.Count > 0
                                ? all.OrderByDescending(x => x.Length).First()
                                : null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse __NEXT_DATA__ for offerId");
                    }
                }

                Log("INFO", $"[Walmart] Page fetched — csrf={(csrf != null ? "found" : "missing")} offerId={(offerId ?? "missing")}");

                // Walmart guest cart ID is a client-generated GUID
                var cartId = Guid.NewGuid().ToString("N");

                return (csrf, cartId, offerId);
            }
            catch (Exception ex)
            {
                Log("WARN", $"[Walmart] Failed to fetch page tokens: {ex.Message}");
                _logger.LogWarning(ex, "Failed to fetch Walmart tokens");
                return (null, null, null);
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
            var body = await resp.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 400 ? body[..400] : body;
            if (!resp.IsSuccessStatusCode)
            {
                Log("WARN", $"[Walmart] ATC HTTP {(int)resp.StatusCode}: {snippet}");
                _logger.LogWarning("Add-to-cart HTTP {Status}", (int)resp.StatusCode);
                return false;
            }

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
            var body = await resp.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 400 ? body[..400] : body;
            if (!resp.IsSuccessStatusCode)
            {
                Log("WARN", $"[Walmart] Contract HTTP {(int)resp.StatusCode}: {snippet}");
                _logger.LogWarning("Contract HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

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
            var body = await resp.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 400 ? body[..400] : body;
            if (!resp.IsSuccessStatusCode)
            {
                Log("WARN", $"[Walmart] PlaceOrder HTTP {(int)resp.StatusCode}: {snippet}");
                _logger.LogWarning("Place-order HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            Log("INFO", $"[Walmart] PlaceOrder response: {snippet}");
            var json = JObject.Parse(body);
            return json["order"]?["id"]?.ToString()
                ?? json["orderId"]?.ToString();
        }

        /// <summary>HTTP fallback: contract then order in two HTTP calls.</summary>
        private async Task<string?> PlaceOrderViaHttpAsync(
            HttpClient client, string cartId, UserProfile profile,
            string? csrfToken, CancellationToken ct)
        {
            var contractId = await CreateContractAsync(client, cartId, profile, csrfToken, ct);
            if (string.IsNullOrEmpty(contractId)) return null;
            Log("INFO", $"[Walmart] HTTP Contract: {contractId}");
            return await PlaceOrderAsync(client, contractId, profile, csrfToken, ct);
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
