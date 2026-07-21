using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Monitoring
{
    public class HttpProductChecker : IProductChecker
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<HttpProductChecker> _logger;
        private readonly HttpStockChecker _httpStockChecker;

        // ?? Title ??????????????????????????????????????????????????????
        // 1) og:title meta tag  2) <title> tag
        private static readonly Regex OgTitleRegex = new(
            @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TitleTagRegex = new(
            @"<title[^>]*>([^<]+)</title>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ?? Price ??????????????????????????????????????????????????????
        // Priority order: og meta ? JSON-LD "price" ? data-price attr ? visible currency symbol
        private static readonly Regex OgPriceRegex = new(
            @"<meta[^>]+property=[""'](?:og|product):price:amount[""'][^>]+content=[""']([0-9]+(?:[.,][0-9]{1,2})?)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex JsonLdPriceRegex = new(
            @"""price""\s*:\s*[""']?([0-9]+(?:[.,][0-9]{1,2})?)[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DataPriceRegex = new(
            @"data-price=[""']([0-9]+(?:[.,][0-9]{1,2})?)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CurrencyPriceRegex = new(
            @"(?:[\$£€¥])\s*([0-9]{1,6}(?:[.,][0-9]{1,2})?)",
            RegexOptions.Compiled);

        // ?? JSON-LD availability ???????????????????????????????????????
        private static readonly Regex JsonLdAvailRegex = new(
            @"""availability""\s*:\s*[""']?(?:https?://schema\.org/)?(\w+)[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ?? Site-specific availability patterns ????????????????????????
        // Target embeds availability_status in page JS
        private static readonly Regex TargetAvailStatusRegex = new(
            @"""availability_status""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TargetIsOosRegex = new(
            @"""is_out_of_stock""\s*:\s*(true|false)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TargetLocationAvailRegex = new(
            @"""location_available_to_promise_quantity""\s*:\s*(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Walmart embeds availabilityStatus in page JS
        private static readonly Regex WalmartAvailRegex = new(
            @"""availabilityStatus""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex WalmartAvailV2Regex = new(
            @"""availabilityStatusV2""[^}]*""value""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public HttpProductChecker(
            IHttpClientFactory httpClientFactory,
            ILogger<HttpProductChecker> logger,
            HttpStockChecker httpStockChecker)
        {
            _httpClientFactory = httpClientFactory;
            _logger            = logger;
            _httpStockChecker  = httpStockChecker;
        }

        public async Task<MonitorResult> CheckAsync(MonitorTask task, CancellationToken ct = default)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                var url = task.TargetUrl ?? "";

                // Route known sites through HttpStockChecker (pure HTTP API, no browser).
                // Also route Walmart tasks that have no URL but have SKU/OfferIdOverride set.
                var isWalmartByIds = string.IsNullOrWhiteSpace(url)
                    && (!string.IsNullOrWhiteSpace(task.Sku) || !string.IsNullOrWhiteSpace(task.OfferIdOverride));

                if (isWalmartByIds                                                       ||
                    url.Contains("target.com", StringComparison.OrdinalIgnoreCase)      ||
                    url.Contains("walmart.com", StringComparison.OrdinalIgnoreCase)      ||
                    url.Contains("bestbuy.com", StringComparison.OrdinalIgnoreCase)      ||
                    url.Contains("costco.com", StringComparison.OrdinalIgnoreCase)       ||
                    url.Contains("samsclub.com", StringComparison.OrdinalIgnoreCase)     ||
                    url.Contains("pokemoncenter.com", StringComparison.OrdinalIgnoreCase)||
                    url.Contains("p-bandai.com", StringComparison.OrdinalIgnoreCase))
                    return await _httpStockChecker.CheckAsync(task, ct);

                // Generic HTML fallback for other sites
                using var client = _httpClientFactory.CreateClient("monitor");
                using var request = BuildRequest(task.TargetUrl);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

                result.IsSuccess = response.IsSuccessStatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    _logger.LogWarning("Task {Name} got {Status}", task.Name, (int)response.StatusCode);
                    return result;
                }

                var html = await response.Content.ReadAsStringAsync(ct);

                result.Title = ExtractTitle(html);
                result.Price = ExtractPrice(html);
                result.IsAvailable = DetermineAvailability(task, html, result.Price);

                _logger.LogDebug("Task {Name} — available={Avail} price={Price} title={Title}",
                    task.Name, result.IsAvailable, result.Price, result.Title);
            }
            catch (TaskCanceledException)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Request timed out";
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Check failed for task {Name}", task.Name);
            }
            return result;
        }

        // ?? TARGET API ???????????????????????????????????????????????????????
        // Tries two APIs: fulfillment API (shipping availability nationwide) then Redsky fallback.
        private async Task<MonitorResult> CheckTargetApiAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl, IsSuccess = false };

            var tcin = ExtractTargetTcin(task.TargetUrl);
            if (string.IsNullOrEmpty(tcin))
                return await CheckHtmlFallbackAsync(task, ct);

            // ?? Try 1: fulfillment API (checks shipping availability, not store-specific) ??
            var fulfillmentUrl = $"https://api.target.com/fulfillment_aggregator/v1/fiats/{tcin}" +
                                 $"?key=eb2551e4accc14f38cc42d32fbc2b2ea" +
                                 $"&nearby=10001&limit=20&requested_quantity=1&radius=50";

            try
            {
                using var client = _httpClientFactory.CreateClient("monitor");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.target.com");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", task.TargetUrl);

                var json = await client.GetStringAsync(fulfillmentUrl, ct);
                var obj  = Newtonsoft.Json.Linq.JObject.Parse(json);

                // Check shipping (online) availability first
                var shipping = obj["shipping"];
                var shippingAvail = shipping?["availability_status"]?.ToString()?.ToUpperInvariant() ?? string.Empty;

                // Check if any store has it, or if online shipping is available
                var locations = obj["locations"] as Newtonsoft.Json.Linq.JArray;
                var anyInStock = false;
                if (locations != null)
                {
                    foreach (var loc in locations)
                    {
                        var locStatus = loc["availability_status"]?.ToString()?.ToUpperInvariant() ?? string.Empty;
                        if (locStatus is "IN_STOCK" or "LIMITED" or "BACKORDER")
                        {
                            anyInStock = true;
                            break;
                        }
                    }
                }

                result.IsSuccess   = true;
                result.IsAvailable = shippingAvail is "IN_STOCK" or "LIMITED" or "BACKORDER" || anyInStock;

                _logger.LogDebug("Target fulfillment API: TCIN={Tcin} shipping={Shipping} anyStore={Any} available={Avail}",
                    tcin, shippingAvail, anyInStock, result.IsAvailable);

                // Also grab title/price from Redsky in background if we got a result
                if (result.IsSuccess)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var redskyUrl = "https://redsky.target.com/redsky_aggregations/v1/web/pdp_client_v1" +
                                            $"?key=9f36aeafbe60771e321a7cc95a78140772ab3e96&tcin={tcin}" +
                                            $"&store_id=3991&zip=10001&state=NY&latitude=40.7128&longitude=-74.0060" +
                                            $"&scheduled_delivery_store_id=3991&pricing_store_id=3991";
                            using var c2 = _httpClientFactory.CreateClient("monitor");
                            var j2 = Newtonsoft.Json.Linq.JObject.Parse(await c2.GetStringAsync(redskyUrl, ct));
                            var product = j2["data"]?["product"];
                            result.Title = product?["item"]?["product_description"]?["title"]?.ToString();
                            var priceStr = product?["price"]?["current_retail"]?.ToString();
                            if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Number,
                                System.Globalization.CultureInfo.InvariantCulture, out var p))
                                result.Price = p;
                        }
                        catch { /* title/price is optional */ }
                    }, ct);

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Target fulfillment API failed for TCIN={Tcin}, trying Redsky", tcin);
            }

            // ?? Try 2: Redsky PDP API ????????????????????????????????????????
            var redskyApiUrl = "https://redsky.target.com/redsky_aggregations/v1/web/pdp_client_v1" +
                               $"?key=9f36aeafbe60771e321a7cc95a78140772ab3e96&tcin={tcin}" +
                               $"&store_id=3991&zip=10001&state=NY&latitude=40.7128&longitude=-74.0060" +
                               $"&scheduled_delivery_store_id=3991&pricing_store_id=3991";

            try
            {
                using var client = _httpClientFactory.CreateClient("monitor");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.target.com");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", task.TargetUrl);

                var json = await client.GetStringAsync(redskyApiUrl, ct);
                var obj  = Newtonsoft.Json.Linq.JObject.Parse(json);

                var product = obj["data"]?["product"];
                result.Title = product?["item"]?["product_description"]?["title"]?.ToString();

                var avail  = product?["availability"];
                var status = avail?["availability_status"]?.ToString()?.ToUpperInvariant() ?? string.Empty;
                var isOos  = avail?["is_out_of_stock"]?.ToObject<bool>();

                // Also check online_available flag which is more reliable than availability_status
                var onlineAvailable = product?["availability"]?["online_available"]?.ToObject<bool>();

                result.IsSuccess   = true;
                result.IsAvailable = onlineAvailable == true
                                  || status is "IN_STOCK" or "LIMITED" or "BACKORDER"
                                  || (isOos == false && status != "OUT_OF_STOCK" && status != "UNAVAILABLE");

                var priceStr = product?["price"]?["current_retail"]?.ToString();
                if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var p))
                    result.Price = p;

                _logger.LogDebug("Target Redsky API: TCIN={Tcin} status={Status} onlineAvail={Online} available={Avail}",
                    tcin, status, onlineAvailable, result.IsAvailable);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Target Redsky API also failed for TCIN={Tcin}, falling back to HTML", tcin);
                return await CheckHtmlFallbackAsync(task, ct);
            }

            return result;
        }

        // ?? WALMART ITEM API ?????????????????????????????????????????????????
        // Uses Walmart's internal item API — returns clean JSON, not blocked by PerimeterX.
        private async Task<MonitorResult> CheckWalmartApiAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl, IsSuccess = false };

            var itemId = ExtractWalmartItemId(task.TargetUrl);
            if (string.IsNullOrEmpty(itemId))
                return await CheckHtmlFallbackAsync(task, ct);

            var apiUrl = $"https://www.walmart.com/api/2/page/comp/store/a1/product/v1" +
                         $"?itemId={itemId}&storeId=2648&fetchCriteo=false";

            try
            {
                using var client = _httpClientFactory.CreateClient("monitor");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.walmart.com");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", task.TargetUrl);

                var json = await client.GetStringAsync(apiUrl, ct);
                var obj  = Newtonsoft.Json.Linq.JObject.Parse(json);

                var item = obj["item"]
                        ?? obj["payload"]?["item"]
                        ?? obj["data"]?["product"];

                result.Title = item?["name"]?.ToString() ?? item?["productName"]?.ToString();

                var statusRaw = item?["availabilityStatus"]?.ToString()
                             ?? item?["inventory"]?["availabilityStatus"]?.ToString()
                             ?? string.Empty;

                result.IsSuccess   = true;
                result.IsAvailable = statusRaw.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                                  || statusRaw.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase);

                var priceStr = item?["priceInfo"]?["currentPrice"]?["price"]?.ToString()
                            ?? item?["price"]?.ToString();
                if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var p))
                    result.Price = p;

                _logger.LogDebug("Walmart API: ItemId={ItemId} status={Status} available={Avail}",
                    itemId, statusRaw, result.IsAvailable);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Walmart API failed for ItemId={ItemId}, falling back to HTML", itemId);
                return await CheckHtmlFallbackAsync(task, ct);
            }

            return result;
        }

        // ?? HTML FALLBACK ????????????????????????????????????????????????????
        private async Task<MonitorResult> CheckHtmlFallbackAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = _httpClientFactory.CreateClient("monitor");
                using var request = BuildRequest(task.TargetUrl);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

                result.IsSuccess = response.IsSuccessStatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    result.ErrorMessage = $"HTTP {(int)response.StatusCode}";
                    return result;
                }

                var html = await response.Content.ReadAsStringAsync(ct);
                result.Title       = ExtractTitle(html);
                result.Price       = ExtractPrice(html);
                result.IsAvailable = DetermineAvailability(task, html, result.Price);
            }
            catch (Exception ex)
            {
                result.IsSuccess    = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        // ?? ID EXTRACTORS ????????????????????????????????????????????????????
        private static readonly Regex TargetTcinRegex    = new(@"/-/A-(\d+)",          RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TargetTcinAltRegex = new(@"[?&]preselect=(\d+)", RegexOptions.Compiled);
        private static readonly Regex WalmartItemIdRegex = new(@"/ip/[^/]+/(\d+)",     RegexOptions.Compiled);

        private static string? ExtractTargetTcin(string url)
        {
            var m = TargetTcinRegex.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = TargetTcinAltRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string? ExtractWalmartItemId(string url)
        {
            var m = WalmartItemIdRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ?? Request builder — realistic browser request ????????????????
        private static HttpRequestMessage BuildRequest(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            return req;
        }

        // ?? Title extraction ???????????????????????????????????????????
        private static string? ExtractTitle(string html)
        {
            // Try og:title first — more descriptive on product pages
            var m = OgTitleRegex.Match(html);
            if (m.Success) return Decode(m.Groups[1].Value);

            m = TitleTagRegex.Match(html);
            return m.Success ? Decode(m.Groups[1].Value) : null;
        }

        // ?? Price extraction ???????????????????????????????????????????
        private static decimal? ExtractPrice(string html)
        {
            // 1. og/product meta tag (most reliable)
            var m = OgPriceRegex.Match(html);
            if (m.Success && TryParsePrice(m.Groups[1].Value, out var p1)) return p1;

            // 2. JSON-LD structured data
            m = JsonLdPriceRegex.Match(html);
            if (m.Success && TryParsePrice(m.Groups[1].Value, out var p2)) return p2;

            // 3. data-price attribute (common in Shopify/WooCommerce)
            m = DataPriceRegex.Match(html);
            if (m.Success && TryParsePrice(m.Groups[1].Value, out var p3)) return p3;

            // 4. Visible currency symbol fallback
            m = CurrencyPriceRegex.Match(html);
            if (m.Success && TryParsePrice(m.Groups[1].Value, out var p4)) return p4;

            return null;
        }

        private static bool TryParsePrice(string raw, out decimal price) =>
            decimal.TryParse(
                raw.Replace(",", "."),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out price) && price > 0;

        // ?? Availability detection ?????????????????????????????????????
        private static bool DetermineAvailability(MonitorTask task, string html, decimal? price)
        {
            var lower = html.ToLowerInvariant();
            var url   = (task.TargetUrl ?? "").ToLowerInvariant();

            // ?? Keyword mode ??????????????????????????????????????????
            if (task.Type == MonitorType.Keyword && !string.IsNullOrWhiteSpace(task.Keyword))
                return lower.Contains(task.Keyword.ToLowerInvariant());

            // ?? Price threshold mode ??????????????????????????????????
            if (task.DetectionMode == DetectionMode.PriceThreshold
                && task.PriceThreshold.HasValue && price.HasValue)
                return price <= task.PriceThreshold;

            // ?? TARGET.COM — read embedded JSON ??????????????????????
            if (url.Contains("target.com"))
            {
                // Primary: availability_status field
                var statusMatch = TargetAvailStatusRegex.Match(html);
                if (statusMatch.Success)
                {
                    var status = statusMatch.Groups[1].Value.ToLowerInvariant();
                    // IN_STOCK, LIMITED, BACKORDER = available; OUT_OF_STOCK, UNAVAILABLE = not
                    if (status == "in_stock" || status == "limited" || status == "backorder")
                        return true;
                    if (status == "out_of_stock" || status == "unavailable")
                        return false;
                }

                // Secondary: is_out_of_stock boolean
                var oosMatch = TargetIsOosRegex.Match(html);
                if (oosMatch.Success)
                    return oosMatch.Groups[1].Value.ToLowerInvariant() == "false";

                // Tertiary: location quantity > 0
                var qtyMatch = TargetLocationAvailRegex.Match(html);
                if (qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out var qty))
                    return qty > 0;

                // Target fallback — treat as unavailable if no signal found
                return false;
            }

            // ?? WALMART.COM — read embedded JS JSON ???????????????????
            if (url.Contains("walmart.com"))
            {
                var wm = WalmartAvailV2Regex.Match(html);
                if (!wm.Success) wm = WalmartAvailRegex.Match(html);
                if (wm.Success)
                {
                    var status = wm.Groups[1].Value.ToLowerInvariant();
                    if (status == "in_stock" || status == "available")  return true;
                    if (status == "out_of_stock" || status == "unavailable") return false;
                }
            }

            // ?? JSON-LD Schema.org (generic sites) ???????????????????
            var jsonLdAvail = JsonLdAvailRegex.Match(html);
            if (jsonLdAvail.Success)
            {
                var val = jsonLdAvail.Groups[1].Value.ToLowerInvariant();
                if (val == "instock" || val == "limitedavailability" || val == "presale")
                    return true;
                if (val == "outofstock" || val == "discontinued" || val == "soldout")
                    return false;
            }

            // ?? Generic text signals ??????????????????????????????????
            bool hasOosSignal =
                lower.Contains("out of stock") ||
                lower.Contains("out-of-stock") ||
                lower.Contains("outofstock") ||
                lower.Contains("sold out") ||
                lower.Contains("soldout") ||
                lower.Contains("currently unavailable") ||
                lower.Contains("not available") ||
                lower.Contains("no longer available") ||
                lower.Contains("temporarily out") ||
                lower.Contains("backordered") ||
                lower.Contains("notify me when available") ||
                lower.Contains("email me when available") ||
                lower.Contains("join waitlist") ||
                lower.Contains("join the waitlist");

            bool hasInStockSignal =
                lower.Contains("add to cart") ||
                lower.Contains("add to bag") ||
                lower.Contains("add to basket") ||
                lower.Contains("buy now") ||
                lower.Contains("buy it now") ||
                lower.Contains("in stock") ||
                lower.Contains("ships in") ||
                lower.Contains("order now") ||
                lower.Contains("available for purchase");

            if (hasInStockSignal && !hasOosSignal) return true;
            if (hasOosSignal) return false;

            // ?? SKU mode ??????????????????????????????????????????????
            if (task.Type == MonitorType.Sku && !string.IsNullOrWhiteSpace(task.Sku))
                return lower.Contains(task.Sku.ToLowerInvariant());

            // Default: no signal found — assume unavailable
            return false;
        }

        private static string Decode(string raw) =>
            WebUtility.HtmlDecode(raw.Trim());
    }
}
