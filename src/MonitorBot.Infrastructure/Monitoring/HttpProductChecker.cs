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

        public HttpProductChecker(IHttpClientFactory httpClientFactory, ILogger<HttpProductChecker> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<MonitorResult> CheckAsync(MonitorTask task, CancellationToken ct = default)
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
