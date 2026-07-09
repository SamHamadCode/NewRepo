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
    /// Target guest checkout flow:
    ///   1. Fetch product page  ? extract TCIN (Target item ID)
    ///   2. POST /v3/cart       ? add to cart
    ///   3. POST /v3/checkout/orders ? create order
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
            CancellationToken ct = default)
        {
            var result = new CheckoutResult { TaskId = task.Id };
            _logger.LogInformation("Starting Target checkout for task {Name}", task.Name);

            try
            {
                var tcin = ExtractTcin(task.TargetUrl);
                if (string.IsNullOrEmpty(tcin))
                {
                    result.Status = CheckoutStatus.Failed;
                    result.ErrorMessage = "Could not extract Target TCIN from URL.";
                    return result;
                }

                _logger.LogDebug("Target TCIN: {Tcin}", tcin);

                // ?? Login if account is assigned ?????????????????????
                string? sessionCookies = null;
                if (account != null)
                {
                    _logger.LogInformation("Logging into Target account: {Email}", account.Email);
                    sessionCookies = await _loginService.LoginAsync(account, ct);
                    if (sessionCookies == null)
                    {
                        result.Status = CheckoutStatus.Failed;
                        result.ErrorMessage = $"Login failed for account: {account.Email}";
                        return result;
                    }
                    _logger.LogInformation("Target login successful, proceeding with authenticated checkout");
                }
                else
                {
                    _logger.LogInformation("No account assigned — proceeding as guest checkout");
                }

                using var client = BuildClient(sessionCookies);

                // Step 1: Add to cart
                var cartResult = await AddToCartAsync(client, tcin, task.Quantity, ct);
                if (!cartResult)
                {
                    result.Status = CheckoutStatus.OutOfStock;
                    result.ErrorMessage = "Add-to-cart failed — item may be out of stock.";
                    return result;
                }

                _logger.LogInformation("Added TCIN {Tcin} to Target cart", tcin);

                // Step 2: Place order
                var orderId = await PlaceOrderAsync(client, tcin, profile, task.Quantity, ct);
                if (string.IsNullOrEmpty(orderId))
                {
                    result.Status = CheckoutStatus.CardDeclined;
                    result.ErrorMessage = "Order submission failed — check card details.";
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

        private HttpClient BuildClient(string? sessionCookies = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                       | DecompressionMethods.Deflate
                                       | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.target.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.target.com/");

            // Inject session cookies from login if available
            if (!string.IsNullOrEmpty(sessionCookies))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", sessionCookies);

            client.Timeout = TimeSpan.FromSeconds(20);
            return client;
        }

        private async Task<bool> AddToCartAsync(
            HttpClient client, string tcin, int quantity, CancellationToken ct)
        {
            var url = "https://carts.target.com/web_checkouts/v1/cart_items?field_groups=CART%2CCART_ITEMS&key=ff457966e64d5e877fdbad070f276d18ecec4a01";

            var payload = new JObject
            {
                ["cart_item"] = new JObject
                {
                    ["tcin"]               = tcin,
                    ["quantity"]           = quantity,
                    ["item_channel_id"]    = "WEB",
                    ["relationship_type"]  = "OwnedBrand"
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var resp = await client.SendAsync(req, ct);
            _logger.LogDebug("Target add-to-cart: {Status}", (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }

        private async Task<string?> PlaceOrderAsync(
            HttpClient client, string tcin, UserProfile profile, int quantity, CancellationToken ct)
        {
            var url = "https://carts.target.com/web_checkouts/v1/checkout?key=ff457966e64d5e877fdbad070f276d18ecec4a01";

            var addr = profile.ShippingAddress;
            var pay = profile.Payment;

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
