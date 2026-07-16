using System;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Models;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Monitoring
{
    /// <summary>
    /// Pure HTTP stock checker for Target and Walmart.
    /// Uses Target's Redsky internal API and Walmart's embedded __NEXT_DATA__ JSON —
    /// the same approach used by bots like RefractBot and Stellar AIO.
    /// No browser / Playwright required.
    /// </summary>
    public class HttpStockChecker
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<HttpStockChecker> _logger;

        private static readonly Regex TargetTcinRegex = new(
            @"/-/A-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TargetTcinAltRegex = new(
            @"[?&]preselect=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WalmartItemIdRegex = new(
            @"/ip/[^/]+/(\d+)", RegexOptions.Compiled);
        private static readonly Regex WalmartNextDataRegex = new(
            @"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>",
            RegexOptions.Compiled);

        public HttpStockChecker(
            IHttpClientFactory httpClientFactory,
            ILogger<HttpStockChecker> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public Task<MonitorResult> CheckAsync(MonitorTask task, CancellationToken ct = default)
        {
            var url = task.TargetUrl ?? string.Empty;

            // Allow routing by SKU alone — derive site from URL or SKU prefix hints
            // If URL is blank but SKU is set, we can't monitor without a URL to hit.
            // Return a clear message instead of getting stuck in retry loop.
            if (string.IsNullOrWhiteSpace(url))
            {
                return Task.FromResult(new MonitorResult
                {
                    TaskId      = task.Id,
                    Url         = string.Empty,
                    IsSuccess   = false,
                    IsAvailable = false,
                    ErrorMessage = "No Target URL set. Enter the product URL in the TARGET URL field so the monitor knows what page to check."
                });
            }

            if (url.Contains("target.com", StringComparison.OrdinalIgnoreCase))
                return CheckTargetAsync(task, ct);

            if (url.Contains("walmart.com", StringComparison.OrdinalIgnoreCase))
                return CheckWalmartAsync(task, ct);

            if (url.Contains("bestbuy.com", StringComparison.OrdinalIgnoreCase))
                return CheckBestBuyAsync(task, ct);

            if (url.Contains("costco.com", StringComparison.OrdinalIgnoreCase))
                return CheckCostcoAsync(task, ct);

            if (url.Contains("samsclub.com", StringComparison.OrdinalIgnoreCase))
                return CheckSamsClubAsync(task, ct);

            if (url.Contains("pokemoncenter.com", StringComparison.OrdinalIgnoreCase))
                return CheckPokemonCenterAsync(task, ct);

            if (url.Contains("p-bandai.com", StringComparison.OrdinalIgnoreCase))
                return CheckPBandaiAsync(task, ct);

            return Task.FromResult(new MonitorResult
            {
                TaskId = task.Id,
                Url = task.TargetUrl,
                IsSuccess = false,
                ErrorMessage = "Site not yet supported by HttpStockChecker."
            });
        }

        // ?? TARGET ????????????????????????????????????????????????????????????
        // Target exposes a Redsky API that returns inventory JSON without any JS.
        // ?? TARGET ????????????????????????????????????????????????????????????
        // Redsky is blocked. We use two fallback strategies:
        //   1. Target's lightweight ATP (available-to-promise) endpoint
        //   2. Scrape the product page HTML for embedded JSON / button text
        private async Task<MonitorResult> CheckTargetAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                var tcin = ExtractTargetTcin(task.TargetUrl);
                if (string.IsNullOrEmpty(tcin))
                {
                    result.IsSuccess   = false;
                    result.ErrorMessage = "Could not extract TCIN from Target URL.";
                    return result;
                }

                using var client = BuildTargetClient();

                // ?? Strategy 1: lightweight ATP endpoint (no Redsky, no auth) ??
                var apiUrl = $"https://www.target.com/api/click_list/v1/available_to_promise" +
                             $"?key=ff457966e64d5e877fdbad070f276d18ecec4a01&tcin={tcin}";

                using var apiResp = await client.GetAsync(apiUrl, ct);
                if (apiResp.IsSuccessStatusCode)
                {
                    var apiJson = await apiResp.Content.ReadAsStringAsync(ct);
                    var root = JObject.Parse(apiJson);
                    var qty = root.SelectToken($"products.{tcin}.available_to_promise_quantity")?.Value<int>() ?? 0;
                    var statusStr = root.SelectToken($"products.{tcin}.availability_status")?.Value<string>() ?? string.Empty;
                    result.IsSuccess   = true;
                    result.IsAvailable = qty > 0
                                      || statusStr.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                                      || statusStr.Equals("LIMITED_STOCK", StringComparison.OrdinalIgnoreCase);
                    _logger.LogDebug("Target ATP: TCIN={T} qty={Q} status={S}", tcin, qty, statusStr);
                    return result;
                }

                _logger.LogDebug("Target ATP returned {C} — falling back to page scrape", (int)apiResp.StatusCode);

                // ?? Strategy 2: scrape product page HTML ??????????????????????
                using var pageResp = await client.GetAsync(task.TargetUrl, ct);
                if (!pageResp.IsSuccessStatusCode)
                {
                    result.IsSuccess   = false;
                    result.ErrorMessage = $"Target page returned {(int)pageResp.StatusCode}";
                    return result;
                }

                var html = await pageResp.Content.ReadAsStringAsync(ct);

                var availMatch = Regex.Match(html,
                    @"""availability_status""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var availStr = availMatch.Success ? availMatch.Groups[1].Value : string.Empty;

                var ldMatch  = Regex.Match(html,
                    @"""availability""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var ldAvail  = ldMatch.Success ? ldMatch.Groups[1].Value : string.Empty;

                var hasAtc   = Regex.IsMatch(html, @"Add to cart", RegexOptions.IgnoreCase);
                var isSoldOut = Regex.IsMatch(html,
                    @"Sold out|Out of stock|currently unavailable", RegexOptions.IgnoreCase);

                result.IsSuccess   = true;
                result.IsAvailable = availStr.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                                  || ldAvail.Contains("InStock", StringComparison.OrdinalIgnoreCase)
                                  || (hasAtc && !isSoldOut);

                var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success) result.Title = titleMatch.Groups[1].Value.Trim();

                _logger.LogDebug("Target page scrape: TCIN={T} avail={A} ld={L} atc={ATC} oos={OOS}",
                    tcin, availStr, ldAvail, hasAtc, isSoldOut);
            }
            catch (OperationCanceledException)
            {
                result.IsSuccess    = false;
                result.ErrorMessage = "Check cancelled.";
            }
            catch (Exception ex)
            {
                result.IsSuccess    = false;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Target check failed for task {Name}", task.Name);
            }

            return result;
        }

        // ?? WALMART ???????????????????????????????????????????????????????????
        // Walmart embeds the full product/inventory JSON in __NEXT_DATA__ on every
        // product page. Parsing it from raw HTML is instant and needs no JS engine.
        private async Task<MonitorResult> CheckWalmartAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = BuildWalmartClient();
                using var resp = await client.GetAsync(task.TargetUrl, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = $"Walmart page returned {(int)resp.StatusCode}";
                    return result;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);

                // Extract __NEXT_DATA__ JSON blob
                var m = WalmartNextDataRegex.Match(html);
                if (!m.Success)
                {
                    // Fallback: try regex on page HTML for availabilityStatus
                    result.IsSuccess = true;
                    var fallbackAvail = Regex.Match(html,
                        @"""availabilityStatus""\s*:\s*""([^""]+)""",
                        RegexOptions.IgnoreCase);
                    var fs = fallbackAvail.Success ? fallbackAvail.Groups[1].Value : string.Empty;
                    result.IsAvailable = fs.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                                      || fs.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase);
                    _logger.LogDebug("Walmart fallback check: status={Status} available={Avail}", fs, result.IsAvailable);
                    return result;
                }

                var root = JObject.Parse(m.Groups[1].Value);

                // Path: props.pageProps.initialData.data.product
                var product = root.SelectToken("props.pageProps.initialData.data.product");

                result.Title = product?.SelectToken("name")?.Value<string>();

                var priceInfo = product?.SelectToken("priceInfo.currentPrice.price")?.Value<decimal?>();
                if (priceInfo.HasValue) result.Price = priceInfo.Value;

                // availabilityStatus: IN_STOCK, OUT_OF_STOCK, etc.
                var availStr = product?.SelectToken("availabilityStatus")?.Value<string>()
                            ?? product?.SelectToken("fulfillmentOptions[0].availabilityStatus")?.Value<string>()
                            ?? string.Empty;

                result.IsSuccess = true;
                result.IsAvailable = availStr.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                                  || availStr.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase);

                _logger.LogDebug("Walmart NEXT_DATA check: status={Status} available={Avail}", availStr, result.IsAvailable);
            }
            catch (OperationCanceledException)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Check cancelled.";
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Walmart HTTP stock check failed for task {Name}", task.Name);
            }

            return result;
        }

        // ?? BEST BUY ??????????????????????????????????????????????????????????
        // Best Buy embeds product data in a window.__UGS_PRODUCT_DATA__ JSON blob
        // and also in <script type="application/ld+json">. We parse the page HTML.
        private async Task<MonitorResult> CheckBestBuyAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = BuildGenericClient("https://www.bestbuy.com");
                using var resp = await client.GetAsync(task.TargetUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = $"Best Buy returned {(int)resp.StatusCode}";
                    return result;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);

                // Best Buy renders availability as "Add to Cart" button enabled state
                // and also exposes it in the fulfillment JSON embedded in the page.
                var addToCartMatch = Regex.Match(html,
                    @"fulfillment[^}]{0,300}""buttonState""\s*:\s*""([^""]+)""",
                    RegexOptions.IgnoreCase);
                var buttonState = addToCartMatch.Success ? addToCartMatch.Groups[1].Value : string.Empty;

                // PURCHASED_RECENTLY / ADD_TO_CART / SOLD_OUT / COMING_SOON
                result.IsSuccess = true;
                result.IsAvailable = buttonState.Equals("ADD_TO_CART", StringComparison.OrdinalIgnoreCase)
                                  || buttonState.Equals("PRE_ORDER", StringComparison.OrdinalIgnoreCase);

                var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success) result.Title = titleMatch.Groups[1].Value.Trim();

                _logger.LogDebug("Best Buy check: buttonState={State} available={Avail}",
                    buttonState, result.IsAvailable);
            }
            catch (OperationCanceledException) { result.IsSuccess = false; result.ErrorMessage = "Cancelled."; }
            catch (Exception ex)
            {
                result.IsSuccess = false; result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Best Buy check failed for {Name}", task.Name);
            }
            return result;
        }

        // ?? COSTCO ????????????????????????????????????????????????????????????
        // Costco product pages embed availability in the page HTML and JSON-LD.
        private async Task<MonitorResult> CheckCostcoAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = BuildGenericClient("https://www.costco.com");
                using var resp = await client.GetAsync(task.TargetUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = $"Costco returned {(int)resp.StatusCode}";
                    return result;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);

                // Costco uses "Add to Cart" text in a button; out-of-stock shows "Out of Stock"
                var isOos = Regex.IsMatch(html, @"Out of Stock", RegexOptions.IgnoreCase);
                var hasAtc = Regex.IsMatch(html, @"Add to Cart", RegexOptions.IgnoreCase);

                // Also check JSON-LD availability
                var ldMatch = Regex.Match(html,
                    @"""availability""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var ldAvail = ldMatch.Success ? ldMatch.Groups[1].Value : string.Empty;

                result.IsSuccess = true;
                result.IsAvailable = (!isOos && hasAtc)
                                  || ldAvail.Contains("InStock", StringComparison.OrdinalIgnoreCase);

                var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success) result.Title = titleMatch.Groups[1].Value.Trim();

                _logger.LogDebug("Costco check: oos={Oos} atc={Atc} ldAvail={Avail} available={Result}",
                    isOos, hasAtc, ldAvail, result.IsAvailable);
            }
            catch (OperationCanceledException) { result.IsSuccess = false; result.ErrorMessage = "Cancelled."; }
            catch (Exception ex)
            {
                result.IsSuccess = false; result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Costco check failed for {Name}", task.Name);
            }
            return result;
        }

        // ?? SAM'S CLUB ????????????????????????????????????????????????????????
        // Sam's Club embeds product JSON in __NEXT_DATA__ like Walmart.
        private async Task<MonitorResult> CheckSamsClubAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = BuildGenericClient("https://www.samsclub.com");
                using var resp = await client.GetAsync(task.TargetUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = $"Sam's Club returned {(int)resp.StatusCode}";
                    return result;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);

                // Try __NEXT_DATA__ first
                var ndMatch = Regex.Match(html,
                    @"<script id=""__NEXT_DATA__"" type=""application/json"">([\s\S]*?)</script>",
                    RegexOptions.Compiled);

                if (ndMatch.Success)
                {
                    try
                    {
                        var root = JObject.Parse(ndMatch.Groups[1].Value);
                        var product = root.SelectToken("props.pageProps.initialData.data.product");
                        result.Title = product?.SelectToken("name")?.Value<string>();

                        var availStr = product?.SelectToken("availabilityStatus")?.Value<string>()
                                    ?? string.Empty;
                        result.IsSuccess = true;
                        result.IsAvailable = availStr.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                                          || availStr.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase);

                        _logger.LogDebug("Sam's Club NEXT_DATA: status={Status} available={Avail}",
                            availStr, result.IsAvailable);
                        return result;
                    }
                    catch { /* fall through to HTML parsing */ }
                }

                // Fallback: HTML scraping
                var isOos = Regex.IsMatch(html, @"Out of Stock|Unavailable", RegexOptions.IgnoreCase);
                var hasAtc = Regex.IsMatch(html, @"Add to Cart", RegexOptions.IgnoreCase);
                result.IsSuccess = true;
                result.IsAvailable = hasAtc && !isOos;

                var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success) result.Title = titleMatch.Groups[1].Value.Trim();
            }
            catch (OperationCanceledException) { result.IsSuccess = false; result.ErrorMessage = "Cancelled."; }
            catch (Exception ex)
            {
                result.IsSuccess = false; result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Sam's Club check failed for {Name}", task.Name);
            }
            return result;
        }

        // ?? POKEMON CENTER ????????????????????????????????????????????????????
        // Pokemon Center uses Shopify under the hood. The product JSON is available
        // at <url>.json which returns a clean availability response.
        private async Task<MonitorResult> CheckPokemonCenterAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = BuildGenericClient("https://www.pokemoncenter.com");

                // Try Shopify product.json endpoint first
                var baseUrl = task.TargetUrl.Split('?')[0].TrimEnd('/');
                var jsonUrl = baseUrl + ".json";

                using var jsonResp = await client.GetAsync(jsonUrl, ct);
                if (jsonResp.IsSuccessStatusCode)
                {
                    var json = await jsonResp.Content.ReadAsStringAsync(ct);
                    var root = JObject.Parse(json);
                    var product = root["product"];

                    result.Title = product?["title"]?.Value<string>();

                    // Any variant available?
                    var variants = product?["variants"] as JArray;
                    var anyAvailable = variants?.Any(v =>
                        v["available"]?.Value<bool>() == true) ?? false;

                    result.IsSuccess = true;
                    result.IsAvailable = anyAvailable;

                    _logger.LogDebug("Pokemon Center Shopify JSON: anyAvailable={Avail}", anyAvailable);
                    return result;
                }

                // Fallback: HTML scrape
                using var resp = await client.GetAsync(task.TargetUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = $"Pokemon Center returned {(int)resp.StatusCode}";
                    return result;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);
                var isOos = Regex.IsMatch(html, @"Sold Out|Out of Stock|Unavailable", RegexOptions.IgnoreCase);
                var hasAtc = Regex.IsMatch(html, @"Add to Cart|Add to Bag", RegexOptions.IgnoreCase);

                result.IsSuccess = true;
                result.IsAvailable = hasAtc && !isOos;

                var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success) result.Title = titleMatch.Groups[1].Value.Trim();

                _logger.LogDebug("Pokemon Center HTML: oos={Oos} atc={Atc} available={Avail}",
                    isOos, hasAtc, result.IsAvailable);
            }
            catch (OperationCanceledException) { result.IsSuccess = false; result.ErrorMessage = "Cancelled."; }
            catch (Exception ex)
            {
                result.IsSuccess = false; result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "Pokemon Center check failed for {Name}", task.Name);
            }
            return result;
        }

        // ?? P-BANDAI ??????????????????????????????????????????????????????????
        // Bandai Namco's official collector shop. The Nuxt.js page embeds product
        // state (soldOut, purchasable, stockStatus) in the HTML. We scrape those
        // flags plus JSON-LD availability as a fallback.
        private async Task<MonitorResult> CheckPBandaiAsync(MonitorTask task, CancellationToken ct)
        {
            var result = new MonitorResult { TaskId = task.Id, Url = task.TargetUrl };
            try
            {
                using var client = BuildGenericClient("https://p-bandai.com");
                using var resp = await client.GetAsync(task.TargetUrl, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = $"P-Bandai returned {(int)resp.StatusCode}";
                    return result;
                }

                var html = await resp.Content.ReadAsStringAsync(ct);

                // Nuxt embedded flags
                var soldOutMatch    = Regex.Match(html, @"""soldOut""\s*:\s*(true|false)",    RegexOptions.IgnoreCase);
                var purchasableMatch = Regex.Match(html, @"""purchasable""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                var stockMatch      = Regex.Match(html, @"""stockStatus""\s*:\s*""([^""]*)"" ", RegexOptions.IgnoreCase);

                bool soldOut     = soldOutMatch.Success     && soldOutMatch.Groups[1].Value.Equals("true",  StringComparison.OrdinalIgnoreCase);
                bool purchasable = purchasableMatch.Success && purchasableMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                var stockStatus  = stockMatch.Success ? stockMatch.Groups[1].Value : string.Empty;

                // JSON-LD availability fallback
                var ldMatch = Regex.Match(html, @"""availability""\s*:\s*""([^""]*)"" ", RegexOptions.IgnoreCase);
                var ldAvail = ldMatch.Success ? ldMatch.Groups[1].Value : string.Empty;

                // Plain HTML button fallback
                var hasAtc = Regex.IsMatch(html, @"Add to (Cart|Bag)|Buy Now",              RegexOptions.IgnoreCase);
                var hasOos = Regex.IsMatch(html, @"Sold Out|Out of Stock|Unavailable",       RegexOptions.IgnoreCase);

                result.IsSuccess = true;
                result.IsAvailable =
                    (purchasable && !soldOut) ||
                    stockStatus.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase) ||
                    ldAvail.Contains("InStock", StringComparison.OrdinalIgnoreCase) ||
                    (hasAtc && !hasOos && !soldOut);

                var titleMatch = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (titleMatch.Success) result.Title = titleMatch.Groups[1].Value.Trim();

                _logger.LogDebug(
                    "P-Bandai check: soldOut={SoldOut} purchasable={Purchasable} stock={Stock} available={Avail}",
                    soldOut, purchasable, stockStatus, result.IsAvailable);
            }
            catch (OperationCanceledException) { result.IsSuccess = false; result.ErrorMessage = "Cancelled."; }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "P-Bandai check failed for {Name}", task.Name);
            }
            return result;
        }

        // ?? Helpers ???????????????????????????????????????????????????????????

        private static string? ExtractTargetTcin(string url)
        {
            var m = TargetTcinRegex.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = TargetTcinAltRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        private HttpClient BuildTargetClient()
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
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.target.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.target.com/");
            return client;
        }

        private HttpClient BuildWalmartClient()
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
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            return client;
        }

        private HttpClient BuildGenericClient(string origin)
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
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", origin + "/");
            return client;
        }
    }
}
