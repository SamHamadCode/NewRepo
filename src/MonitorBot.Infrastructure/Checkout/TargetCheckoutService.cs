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
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Checkout
{
    public class TargetCheckoutService : ICheckoutService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TargetCheckoutService> _logger;
        private readonly TargetLoginService _loginService;
        private readonly ILogStore _logStore;
        private readonly ITargetBrowserCheckout? _browserCheckout;

        private static readonly Regex TcinRegex = new(
            @"/-/A-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TcinAltRegex = new(
            @"[?&]preselect=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ApiKeyRegex = new(
            @"[""']([0-9a-f]{40})[""']", RegexOptions.Compiled);

        // Cached so we only fetch once per app run, refreshed on 401
        private static string _cachedApiKey = "ff457966e64d5e877fdbad070f276d18ecec4a01";
        private static DateTime _apiKeyFetchedAt = DateTime.MinValue;

        public TargetCheckoutService(
            IHttpClientFactory httpClientFactory,
            ILogger<TargetCheckoutService> logger,
            TargetLoginService loginService,
            ILogStore logStore,
            ITargetBrowserCheckout? browserCheckout = null)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _loginService = loginService;
            _logStore = logStore;
            _browserCheckout = browserCheckout;
        }

        /// <summary>Writes a message directly to the Activity Log UI.</summary>
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

                // Log which auth path we're taking — visible in Activity Log
                Log("INFO", string.IsNullOrWhiteSpace(sessionCookies)
                    ? "[Target] Auth path: GUEST (no account/cookies)"
                    : "[Target] Auth path: SESSION cookies present");

                // Use whatever token is available in the cookies.
                // BuildClient extracts accessToken / target_access_token automatically.
                var mergedCookies = sessionCookies ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(sessionCookies))
                {
                    var id2tok = ExtractCookieValue(sessionCookies, "target_access_token");
                    var mi6tok = ExtractCookieValue(sessionCookies, "accessToken");
                    if (id2tok != null)
                        Log("INFO", $"[Target] Using ID2 token (target_access_token, len={id2tok.Length})");
                    else if (mi6tok != null)
                        Log("INFO", $"[Target] Using MI6 token (accessToken, len={mi6tok.Length}) — cookie-only mode (no Bearer header)");
                    else
                        Log("WARN", "[Target] No auth token found in cookies");
                }

                using var client = BuildClient(mergedCookies, null);

                // ── Step 2: Add to cart ────────────────────────────────────
                onStatus?.Invoke(MonitorTaskStatus.AddingToCart);
                var (atcOk, cartId, atcError) = await AddToCartAsync(client, tcin, quantity, mergedCookies, ct);
                if (!atcOk)
                {
                    // 401 means the cookies have expired — signal the caller to re-harvest
                    var needsReHarvest = atcError != null && atcError.Contains("401");
                    result.Status          = CheckoutStatus.Failed;
                    result.ErrorMessage    = atcError;
                    result.NeedsReHarvest  = needsReHarvest;
                    if (needsReHarvest)
                        Log("WARN", $"[Target] Cookies expired (401) — requesting re-harvest", task.Id.ToString());
                    return result;
                }

                _logger.LogInformation("Added TCIN {Tcin} x{Qty} to Target cart (cartId={CartId})", tcin, quantity, cartId);

                // ── Step 3: Place order ────────────────────────────────────
                onStatus?.Invoke(MonitorTaskStatus.PlacingOrder);
                string? orderId, orderError;
                var hasId2Token = !string.IsNullOrEmpty(ExtractCookieValue(mergedCookies, "target_access_token"));
                if (_browserCheckout != null && hasId2Token)
                {
                    // Use embedded browser checkout — requires a real ID2 token
                    Log("INFO", "[Target] Using browser checkout (ID2 token path)");
                    var apiKey = await FetchApiKeyAsync(client, ct);
                    (orderId, orderError) = await _browserCheckout.RunAsync(
                        mergedCookies, tcin, quantity, apiKey, cartId!, profile, ct);
                }
                else
                {
                    if (_browserCheckout != null && !hasId2Token)
                        Log("INFO", "[Target] No ID2 token — using HTTP checkout (cookie-only/MI6 path)");
                    else
                        Log("INFO", "[Target] Using HTTP checkout");
                    (orderId, orderError) = await PlaceOrderAsync(client, cartId!, tcin, profile, quantity, ct);
                }
                if (string.IsNullOrEmpty(orderId))
                {
                    result.Status = CheckoutStatus.CardDeclined;
                    result.ErrorMessage = string.IsNullOrWhiteSpace(orderError)
                        ? "Order submission failed — check card details."
                        : $"CardDeclined: {orderError}";
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

        /// <summary>
        /// Registers a guest identity with Target's auth service and returns the
        /// Set-Cookie string that must be forwarded on the checkout request.
        /// Target returns 403 INVALID_GUEST_STATUS without this step for guest checkouts.
        /// </summary>
        private async Task<(bool ok, string? cookies, string error)> RegisterGuestAsync(
            HttpClient client, string cartId, UserProfile profile, CancellationToken ct)
        {
            try
            {
                // Target's guest registration endpoint — creates an anonymous identity
                // bound to the current browser session.
                const string url = "https://guestidentity.target.com/v1/guest/token";

                var payload = new JObject
                {
                    ["channel"]    = "WEB",
                    ["cart_id"]    = cartId,
                    ["email"]      = profile.Email
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(
                        payload.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                var resp = await client.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                Log("INFO", $"[Target] Guest register HTTP {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");

                if (resp.IsSuccessStatusCode)
                {
                    // Collect Set-Cookie headers to forward
                    var setCookies = string.Join("; ",
                        resp.Headers.TryGetValues("Set-Cookie", out var vals)
                            ? System.Linq.Enumerable.Select(vals, v => v.Split(';')[0])
                            : System.Array.Empty<string>());
                    return (true, setCookies.Length > 0 ? setCookies : null, string.Empty);
                }

                // Non-2xx — parse error but don't hard-fail; PlaceOrder may still work
                // if the cart already has a valid session attached
                string err;
                try { err = JObject.Parse(body)["message"]?.ToString() ?? body; }
                catch { err = body; }
                Log("WARN", $"[Target] Guest register failed (non-fatal): {err}");
                return (true, null, string.Empty); // soft-fail: let PlaceOrder try anyway
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Guest registration request failed");
                return (true, null, string.Empty); // soft-fail
            }
        }

        /// <summary>
        /// Builds an HttpClient that uses a live CookieContainer so Target
        /// can set visitorId / session cookies automatically via Set-Cookie.
        /// Returns both the client and the container so cookies can be read back.
        /// </summary>
        private (HttpClient client, CookieContainer jar) BuildCookieClient()
        {
            var jar = new CookieContainer();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                       | DecompressionMethods.Deflate
                                       | DecompressionMethods.Brotli,
                AllowAutoRedirect  = true,
                UseCookies         = true,
                CookieContainer    = jar
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.Timeout = TimeSpan.FromSeconds(30);
            return (client, jar);
        }

        /// <summary>
        /// Visits target.com so the server sets visitorId + session cookies
        /// into our CookieContainer, which Target requires for guest checkout.
        /// </summary>
        private async Task BootstrapSessionAsync(
            HttpClient client, CancellationToken ct)
        {
            try
            {
                await client.GetAsync("https://www.target.com", ct);
                Log("INFO", "[Target] Session bootstrap complete (visitorId set)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Target session bootstrap failed (non-fatal)");
            }
        }

        /// <summary>
        /// Fetches a guest Bearer token from Target's auth service.
        /// This is required on every guest checkout — without it the checkout
        /// endpoint returns 403 INVALID_GUEST_STATUS.
        /// Mirrors exactly what TargetLoginService does in its Step 1.
        /// </summary>
        /// <summary>
        /// GETs the checkout endpoint to initialise a guest session against the cart.
        /// Target's server registers the Bearer token identity with the cart during this
        /// call. Without it, the subsequent checkout POST returns 403 INVALID_GUEST_STATUS.
        /// </summary>
        private async Task InitCheckoutSessionAsync(
            HttpClient client, string cartId, CancellationToken ct)
        {
            try
            {
                var apiKey = await FetchApiKeyAsync(client, ct);
                var url = $"https://carts.target.com/web_checkouts/v1/checkout" +
                          $"?cart_id={cartId}&field_groups=CHECKOUT%2CCART&key={apiKey}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                var resp = await client.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log("INFO", $"[Target] Init checkout session HTTP {(int)resp.StatusCode}: " +
                            $"{(body.Length > 200 ? body[..200] : body)}");
            }
            catch (Exception ex)
            {
                // Non-fatal — log and let PlaceOrder try anyway
                _logger.LogWarning(ex, "InitCheckoutSessionAsync failed");
                Log("WARN", $"[Target] Init checkout session failed (non-fatal): {ex.Message}");
            }
        }

        private async Task<(bool ok, string? token, string error)> FetchGuestTokenAsync(
            string? existingCookies, CancellationToken ct)
        {
            var (token, status, body) = await _loginService.GetGuestTokenAsync(existingCookies, ct);
            Log(string.IsNullOrEmpty(token) ? "WARN" : "INFO",
                $"[Target] gsp.target.com HTTP {status}: {(body.Length > 400 ? body[..400] : body)}");
            if (string.IsNullOrEmpty(token))
                return (false, null, $"Guest token failed: HTTP {status} — {(body.Length > 200 ? body[..200] : body)}");
            return (true, token, string.Empty);
        }

        private static string? ExtractTcin(string url)
        {
            var m = TcinRegex.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = TcinAltRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Fetches the current Target API key from their web app bundle.
        /// The key is a 40-char hex string embedded in Target's JS.
        /// Falls back to the cached value if the fetch fails.
        /// </summary>
        private async Task<string> FetchApiKeyAsync(HttpClient client, CancellationToken ct)
        {
            // Refresh at most once per hour
            if ((DateTime.UtcNow - _apiKeyFetchedAt).TotalMinutes < 5)
                return _cachedApiKey;

            try
            {
                // Target embeds the API key in their main page config script
                var html = await client.GetStringAsync("https://www.target.com", ct);
                var match = ApiKeyRegex.Match(html);
                if (match.Success)
                {
                    _cachedApiKey = match.Groups[1].Value;
                    _apiKeyFetchedAt = DateTime.UtcNow;
                    _logger.LogInformation("Target API key refreshed: {Key}", _cachedApiKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not refresh Target API key, using cached value");
            }

            return _cachedApiKey;
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
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site",     "same-site");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode",     "cors");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest",     "empty");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-CH-UA",          "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-CH-UA-Mobile",   "?0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-CH-UA-Platform", "\"Windows\"");

            var cookies = !string.IsNullOrWhiteSpace(sessionCookies) ? sessionCookies : cookieOverride;
            if (!string.IsNullOrEmpty(cookies))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies);

                // Only set Authorization: Bearer when we have the ID2 token (target_access_token).
                // MI6 (accessToken) is NOT accepted as a bearer by carts.target.com — the endpoints
                // are cookie-only when only MI6 is available, so we omit the header entirely in
                // that case and let the Cookie header carry the session.
                var id2Token = ExtractCookieValue(cookies, "target_access_token");
                if (!string.IsNullOrEmpty(id2Token))
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", $"Bearer {id2Token}");
            }

            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private async Task<(bool ok, string? cartId, string error)> AddToCartAsync(
            HttpClient client, string tcin, int quantity, string? rawCookies, CancellationToken ct)
        {
            const string fieldGroups = "CART%2CCART_ITEMS%2CSUMMARY";

            // Fetch the live API key; on 401 invalidate cache and retry once
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt == 1)
                {
                    _apiKeyFetchedAt = DateTime.MinValue; // force refresh
                    _logger.LogWarning("Target ATC 401 — forcing API key refresh");
                }

                var apiKey = await FetchApiKeyAsync(client, ct);
                var url = $"https://carts.target.com/web_checkouts/v1/cart_items" +
                          $"?field_groups={fieldGroups}&key={apiKey}";

                var payload = new JObject
                {
                    ["cart_item"] = new JObject
                    {
                        ["item_channel_id"] = "10",
                        ["tcin"]            = tcin,
                        ["quantity"]        = quantity
                    },
                    ["cart_type"]        = "REGULAR",
                    ["channel_id"]       = "10",
                    ["fulfillment"]      = new JObject
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
                        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

                        var resp = await client.SendAsync(req, ct);
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        var snippet = body.Length > 600 ? body[..600] : body;

                        _logger.LogDebug("Target ATC POST HTTP {S} tcin={T}: {B}",
                            (int)resp.StatusCode, tcin, snippet);

                        // 401 on first attempt = stale key; loop will refresh and retry
                        if ((int)resp.StatusCode == 401 && attempt == 0)
                            continue;

                        if (resp.IsSuccessStatusCode)
                        {
                            string? cartId = null;
                            try
                            {
                                var j = JObject.Parse(body);
                                cartId = j["cart_id"]?.ToString()
                                      ?? j["id"]?.ToString()
                                      ?? j["cart"]?["id"]?.ToString();
                            }
                            catch { }
                            _logger.LogWarning("Target ATC succeeded, cartId={CartId}, rawBody={Body}", cartId, snippet);
                            Log("INFO", $"[Target] ATC OK — cartId={cartId ?? "NULL"} | body={snippet}");
                            return (true, cartId, string.Empty);
                        }

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
                        Log("WARN", $"[Target] ATC failed — {lastError}");
                        return (false, null, $"ATC failed — {lastError}");
                    }

                        return (false, null, "ATC failed — could not authenticate with Target API after key refresh");
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

        private async Task<(string? orderId, string? error)> PlaceOrderAsync(
            HttpClient client, string? cartId, string tcin, UserProfile profile, int quantity, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cartId))
                return (null, "cart_id was not returned by Add-to-Cart — cannot submit order.");

            var apiKey  = await FetchApiKeyAsync(client, ct);
            var baseUrl = $"https://carts.target.com/web_checkouts/v1/checkout?key={apiKey}";

            var addr = profile.ShippingAddress;
            var pay  = profile.Payment;

            Log("INFO", $"[Target] Profile addr: '{addr.Line1}', '{addr.City}', '{addr.State}', '{addr.ZipCode}' | email='{profile.Email}'");

            // ── Helpers ───────────────────────────────────────────────────
            static string CheckoutError(string body)
            {
                try
                {
                    var j = JObject.Parse(body);
                    return j["message"]?.ToString()
                        ?? j["error"]?.ToString()
                        ?? j["errors"]?[0]?["message"]?.ToString()
                        ?? j["checkout_error"]?["message"]?.ToString()
                        ?? j["fault"]?["faultstring"]?.ToString()
                        ?? body;
                }
                catch { return body; }
            }

            async Task<(bool ok, string body)> Send(HttpMethod method, string url, JObject? payload)
            {
                using var r = new HttpRequestMessage(method, url);
                r.Headers.TryAddWithoutValidation("Accept",              "application/json");
                r.Headers.TryAddWithoutValidation("X-Api-Key",           apiKey);
                r.Headers.TryAddWithoutValidation("X-Application-Name",  "web");
                r.Headers.TryAddWithoutValidation("Origin",              "https://www.target.com");
                r.Headers.TryAddWithoutValidation("Referer",             "https://www.target.com/");
                r.Headers.TryAddWithoutValidation("Accept-Language",     "en-US,en;q=0.9");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Site",      "same-site");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Mode",      "cors");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Dest",      "empty");
                r.Headers.TryAddWithoutValidation("Sec-CH-UA",           "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
                r.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile",    "?0");
                r.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform",  "\"Windows\"");
                if (payload != null)
                    r.Content = new StringContent(
                        payload.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8, "application/json");
                var rs = await client.SendAsync(r, ct);
                var b  = await rs.Content.ReadAsStringAsync(ct);
                return (rs.IsSuccessStatusCode, b);
            }

            // ── Step 1: GET — initialise checkout session ─────────────────
            // This call registers the cart on Target's checkout server so
            // subsequent steps know which session to modify.
            var initUrl = $"{baseUrl}&cart_id={cartId}&field_groups=CHECKOUT%2CCART%2CPAYMENT%2CADDRESS";
            var (initOk, initBody) = await Send(HttpMethod.Get, initUrl, null);
            Log(initOk ? "INFO" : "WARN",
                $"[Target] Init checkout HTTP {(initOk ? "OK" : "FAIL")}: {(initBody.Length > 300 ? initBody[..300] : initBody)}");
            if (!initOk)
            {
                // Non-fatal — some accounts work without it; continue anyway
                Log("WARN", "[Target] Init checkout failed (continuing)");
            }

            // Build shipping address object
            var shippingAddr = new JObject
            {
                ["address_type"]    = "SHIPPING",
                ["first_name"]      = profile.FirstName,
                ["last_name"]       = profile.LastName,
                ["email_address"]   = profile.Email,
                ["phone"]           = profile.Phone,
                ["mobile_phone"]    = profile.Phone,
                ["line1"]           = addr.Line1,
                ["city"]            = addr.City,
                ["state"]           = addr.State,
                ["zip_code"]        = addr.ZipCode,
                ["country_code"]    = "US",
                ["save_as_default"] = false
            };
            if (!string.IsNullOrWhiteSpace(addr.Line2))
                shippingAddr["line2"] = addr.Line2;

            // ── Step 2: PUT — submit shipping address (cookie-only, no Bearer) ──
            // The real endpoint for setting the shipping address is
            // PUT /web_checkouts/v1/cart_shipping_addresses — it is cookie-only
            // and rejects requests that carry an Authorization: Bearer header.
            var shippingUrl = $"https://carts.target.com/web_checkouts/v1/cart_shipping_addresses?key={apiKey}&cart_id={cartId}&field_groups=CHECKOUT%2CCART%2CADDRESS";
            var shippingPayload = new JObject
            {
                ["cart_id"]  = cartId,
                ["address"]  = shippingAddr
            };

            // Build a cookie-only request — strip the Authorization header
            async Task<(bool ok, string body)> SendCookieOnly(HttpMethod method, string url, JObject? payload)
            {
                using var r = new HttpRequestMessage(method, url);
                r.Headers.TryAddWithoutValidation("Accept",             "application/json");
                r.Headers.TryAddWithoutValidation("X-Api-Key",          apiKey);
                r.Headers.TryAddWithoutValidation("X-Application-Name", "web");
                r.Headers.TryAddWithoutValidation("Origin",             "https://www.target.com");
                r.Headers.TryAddWithoutValidation("Referer",            "https://www.target.com/");
                r.Headers.TryAddWithoutValidation("Accept-Language",    "en-US,en;q=0.9");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Site",     "same-site");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Mode",     "cors");
                r.Headers.TryAddWithoutValidation("Sec-Fetch-Dest",     "empty");
                r.Headers.TryAddWithoutValidation("Sec-CH-UA",          "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
                r.Headers.TryAddWithoutValidation("Sec-CH-UA-Mobile",   "?0");
                r.Headers.TryAddWithoutValidation("Sec-CH-UA-Platform", "\"Windows\"");
                // Deliberately omit Authorization — this endpoint is cookie-only
                if (payload != null)
                    r.Content = new StringContent(
                        payload.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8, "application/json");
                var rs = await client.SendAsync(r, ct);
                var b  = await rs.Content.ReadAsStringAsync(ct);
                return (rs.IsSuccessStatusCode, b);
            }

            var (addrOk, addrBody) = await SendCookieOnly(HttpMethod.Put, shippingUrl, shippingPayload);
            Log(addrOk ? "INFO" : "WARN",
                $"[Target] PUT cart_shipping_addresses HTTP {(addrOk ? "OK" : "FAIL")}: {(addrBody.Length > 300 ? addrBody[..300] : addrBody)}");
            if (!addrOk)
                Log("WARN", "[Target] Shipping address PUT failed (continuing to order submission)");

            var billingLine1 = profile.BillingAddress?.Line1?.Length  > 0 ? profile.BillingAddress.Line1   : addr.Line1;
            var billingCity  = profile.BillingAddress?.City?.Length   > 0 ? profile.BillingAddress.City    : addr.City;
            var billingState = profile.BillingAddress?.State?.Length  > 0 ? profile.BillingAddress.State   : addr.State;
            var billingZip   = profile.BillingAddress?.ZipCode?.Length > 0 ? profile.BillingAddress.ZipCode : addr.ZipCode;

            // ── Step 3: POST — place order ────────────────────────────────
            // Address is already stored server-side via the PUT above, so it is
            // not included here. Payment and guest identity are still required.
            var orderPayload = new JObject
            {
                ["cart_id"]          = cartId,
                ["cart_type"]        = "REGULAR",
                ["channel_id"]       = "10",
                ["shopping_context"] = "DIGITAL",
                ["guest"] = new JObject
                {
                    ["email_address"] = profile.Email,
                    ["first_name"]    = profile.FirstName,
                    ["last_name"]     = profile.LastName
                },
                ["payment_instructions"] = new JArray
                {
                    new JObject
                    {
                        ["payment_type"]    = "CREDITCARD",
                        ["card_number"]     = pay.CardNumber,
                        ["name_on_card"]    = !string.IsNullOrWhiteSpace(pay.CardHolder)
                                             ? pay.CardHolder
                                             : $"{profile.FirstName} {profile.LastName}".Trim(),
                        ["expiration_date"] = $"{pay.ExpiryMonth.PadLeft(2, '0')}/{pay.ExpiryYear}",
                        ["cvv"]             = pay.Cvv,
                        ["billing_address"] = new JObject
                        {
                            ["first_name"]   = profile.FirstName,
                            ["last_name"]    = profile.LastName,
                            ["line1"]        = billingLine1,
                            ["city"]         = billingCity,
                            ["state"]        = billingState,
                            ["zip_code"]     = billingZip,
                            ["country_code"] = "US"
                        }
                    }
                }
            };

            Log("INFO", $"[Target] POST checkout for cart {cartId}");
            var (orderOk, orderBody) = await Send(HttpMethod.Post, baseUrl, orderPayload);
            if (!orderOk)
            {
                Log("WARN", $"[Target] PlaceOrder FAIL raw: {(orderBody.Length > 600 ? orderBody[..600] : orderBody)}");
                return (null, $"HTTP — {CheckoutError(orderBody)}");
            }

            var json = JObject.Parse(orderBody);
            var id = json["order_id"]?.ToString()
                  ?? json["id"]?.ToString()
                  ?? Guid.NewGuid().ToString("N")[..12];
            Log("INFO", $"[Target] Order placed! orderId={id}");
            return (id, null);
        }
    }
}
