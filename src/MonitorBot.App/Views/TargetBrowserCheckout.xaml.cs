using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MonitorBot.App.Views
{
    /// <summary>
    /// Executes Target checkout entirely inside an embedded Chromium browser.
    /// This is the only reliable approach because Target's checkout endpoints
    /// require an ID2 token (target_access_token) which is set by gsp.target.com
    /// only inside a real browser session, and CORS/auth middleware blocks all
    /// non-browser HTTP clients from PATCH/POST checkout APIs.
    ///
    /// Flow (mirrors Stellar AIO / RafflBot approach):
    ///   1. Spin up hidden WebView2
    ///   2. Inject harvested session cookies into the browser's cookie store
    ///   3. Navigate to target.com (establishes session)
    ///   4. Run fetch() calls from inside JS — same-origin, real TLS, real ID2 token
    ///      a. GET  /checkout    (init checkout session — browser gets ID2 token here)
    ///      b. PATCH address
    ///      c. PATCH payment
    ///      d. POST /checkout    (place order)
    ///   5. Return order ID or error
    /// </summary>
    public partial class TargetBrowserCheckout : Window, ITargetBrowserCheckout
    {
        private TaskCompletionSource<(string? orderId, string? error)>? _tcs;
        private readonly ILogStore _logStore;

        public TargetBrowserCheckout(ILogStore logStore)
        {
            _logStore = logStore;
            InitializeComponent();
        }

        private void Log(string level, string msg) => _logStore.Add(new LogEntry
        {
            Level    = level,
            Category = "Checkout",
            Message  = $"[Target:Browser] {msg}"
        });

        /// <summary>
        /// Runs the full Target checkout from inside the browser.
        /// Safe to call from any thread — dispatches to the UI thread internally.
        /// </summary>
        public Task<(string? orderId, string? error)> RunAsync(
            string rawCookies,
            string tcin,
            int quantity,
            string apiKey,
            string cartId,
            UserProfile profile,
            CancellationToken ct)
        {
            // WebView2 requires all calls on the UI (STA) thread.
            // InvokeAsync marshals the entire async checkout onto the Dispatcher.
            return Dispatcher.InvokeAsync(() =>
                RunOnUiThreadAsync(rawCookies, tcin, quantity, apiKey, cartId, profile, ct)
            ).Task.Unwrap();
        }

        private async Task<(string? orderId, string? error)> RunOnUiThreadAsync(
            string rawCookies,
            string tcin,
            int quantity,
            string apiKey,
            string cartId,
            UserProfile profile,
            CancellationToken ct)
        {
            _tcs = new TaskCompletionSource<(string?, string?)>();

            using var reg = ct.Register(() =>
                _tcs.TrySetResult((null, "Checkout cancelled")));

            try
            {
                // Init WebView2 with isolated profile
                var profileDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MonitorBot", "CheckoutProfiles", "target");
                System.IO.Directory.CreateDirectory(profileDir);

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDir);
                await WebBrowser.EnsureCoreWebView2Async(env);

                // Set up message channel — JS will post results back via window.chrome.webview.postMessage
                WebBrowser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Inject cookies BEFORE navigating
                await InjectCookiesAsync(rawCookies);

                // Navigate to target.com — this triggers gsp.target.com to issue ID2 token
                var navTcs = new TaskCompletionSource<bool>();
                void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    WebBrowser.CoreWebView2.NavigationCompleted -= OnNav;
                    navTcs.TrySetResult(true);
                }
                WebBrowser.CoreWebView2.NavigationCompleted += OnNav;
                WebBrowser.CoreWebView2.Navigate("https://www.target.com");
                var navTimeout = Task.Delay(TimeSpan.FromSeconds(20));
                await Task.WhenAny(navTcs.Task, navTimeout);

                // Wait for gsp.target.com to set target_access_token
                await Task.Delay(3000, ct);

                // Build and execute the full checkout JS
                var js = BuildCheckoutScript(tcin, quantity, apiKey, cartId, profile);
                var jsResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(js);
                // If ExecuteScriptAsync returns a value directly (sync path), handle it
                if (!string.IsNullOrEmpty(jsResult) && jsResult != "null" && jsResult != "undefined")
                {
                    try
                    {
                        var direct = JObject.Parse(jsResult.Trim('"').Replace("\\\"", "\""));
                        var t = direct["type"]?.ToString();
                        if (t == "success") _tcs.TrySetResult((direct["orderId"]?.ToString() ?? "OK", null));
                        else if (t == "error") _tcs.TrySetResult((null, direct["message"]?.ToString()));
                    }
                    catch { /* async path — wait for postMessage */ }
                }

                // Wait for JS to post back the result (up to 40s)
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(40));
                var completed   = await Task.WhenAny(_tcs.Task, timeoutTask);
                if (completed == timeoutTask)
                    return (null, "Browser checkout timed out — JS did not post a result");
                return await _tcs.Task;
            }
            catch (TimeoutException)
            {
                return (null, "Browser checkout timed out");
            }
            catch (Exception ex)
            {
                return (null, $"Browser checkout error: {ex.Message}");
            }
            finally
            {
                try { WebBrowser.CoreWebView2.WebMessageReceived -= OnWebMessageReceived; } catch { }
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg  = JObject.Parse(e.WebMessageAsJson);
                var type = msg["type"]?.ToString();
                var rawSnippet = e.WebMessageAsJson.Length > 200 ? e.WebMessageAsJson[..200] : e.WebMessageAsJson;
                Log("INFO", $"JS message: type={type} raw={rawSnippet}");
                if (type == "log")
                    return; // progress log, not a final result
                if (type == "success")
                    _tcs?.TrySetResult((msg["orderId"]?.ToString() ?? "UNKNOWN", null));
                else if (type == "error")
                    _tcs?.TrySetResult((null, msg["message"]?.ToString() ?? "Unknown error"));
            }
            catch (Exception ex)
            {
                _tcs?.TrySetResult((null, $"JS message parse error: {ex.Message}"));
            }
        }

        private async Task InjectCookiesAsync(string rawCookies)
        {
            var mgr = WebBrowser.CoreWebView2.CookieManager;
            // Clear stale cookies first
            mgr.DeleteAllCookies();

            foreach (var part in rawCookies.Split(';'))
            {
                var kv = part.Trim();
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                var name  = kv[..eq].Trim();
                var value = kv[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(name)) continue;

                try
                {
                    // Set cookie for both .target.com and target.com
                    var c = mgr.CreateCookie(name, value, ".target.com", "/");
                    c.IsSecure = true;
                    mgr.AddOrUpdateCookie(c);
                }
                catch { /* skip malformed cookies */ }
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// Builds the JavaScript that executes the full checkout flow inside the browser.
        /// Uses fetch() with credentials:'include' — the browser automatically attaches
        /// the correct cookies and Authorization header from the active session.
        /// </summary>
        private static string BuildCheckoutScript(
            string tcin, int quantity, string apiKey, string cartId, UserProfile profile)
        {
            var addr    = profile.ShippingAddress;
            var pay     = profile.Payment;
            var billing = profile.BillingAddress;

            var billingLine1 = billing?.Line1?.Length  > 0 ? billing.Line1   : addr.Line1;
            var billingCity  = billing?.City?.Length   > 0 ? billing.City    : addr.City;
            var billingState = billing?.State?.Length  > 0 ? billing.State   : addr.State;
            var billingZip   = billing?.ZipCode?.Length > 0 ? billing.ZipCode : addr.ZipCode;

            var nameOnCard = !string.IsNullOrWhiteSpace(pay.CardHolder)
                ? pay.CardHolder
                : $"{profile.FirstName} {profile.LastName}".Trim();

            var expiry = $"{pay.ExpiryMonth.PadLeft(2, '0')}/{pay.ExpiryYear}";

            // Inline the values as JSON strings — properly escaped
            string J(string? s) => JsonConvert.ToString(s ?? "");

            var shippingAddrJson = $@"{{
                ""address_type"":    ""SHIPPING"",
                ""first_name"":      {J(profile.FirstName)},
                ""last_name"":       {J(profile.LastName)},
                ""email_address"":   {J(profile.Email)},
                ""phone"":           {J(profile.Phone)},
                ""mobile_phone"":    {J(profile.Phone)},
                ""line1"":           {J(addr.Line1)},
                ""city"":            {J(addr.City)},
                ""state"":           {J(addr.State)},
                ""zip_code"":        {J(addr.ZipCode)},
                ""country_code"":    ""US"",
                ""save_as_default"": false
            }}";

            if (!string.IsNullOrWhiteSpace(addr.Line2))
                shippingAddrJson = shippingAddrJson.Replace(
                    @"""save_as_default"": false",
                    $@"""line2"": {J(addr.Line2)}, ""save_as_default"": false");

            return $@"
(async () => {{
    const apiKey  = {J(apiKey)};
    const cartId  = {J(cartId)};
    const baseUrl = `https://carts.target.com/web_checkouts/v1/checkout?key=${{apiKey}}`;

    const log = (msg) => window.chrome.webview.postMessage(JSON.stringify({{ type: 'log', message: msg }}));
    const err = (msg) => window.chrome.webview.postMessage(JSON.stringify({{ type: 'error', message: msg }}));
    const ok  = (id)  => window.chrome.webview.postMessage(JSON.stringify({{ type: 'success', orderId: id }}));

    const headers = {{
        'Content-Type':       'application/json',
        'Accept':             'application/json',
        'X-Api-Key':          apiKey,
        'X-Application-Name': 'web'
    }};

    try {{
        // Step 1: Init checkout session (GET) — browser has ID2 token from gsp.target.com
        log('Step 1: init checkout GET...');
        const initResp = await fetch(
            `${{baseUrl}}&cart_id=${{cartId}}&field_groups=CHECKOUT,CART,PAYMENT,ADDRESS`,
            {{ method: 'GET', credentials: 'include', headers }}
        );
        const initBody = await initResp.text();
        log(`Step 1 done: ${{initResp.status}} ${{initBody.slice(0,200)}}`);

        // Step 2: PATCH shipping address
        log('Step 2: PATCH address...');
        const addrResp = await fetch(baseUrl, {{
            method: 'PATCH', credentials: 'include', headers,
            body: JSON.stringify({{
                cart_id:   cartId,
                cart_type: 'REGULAR',
                addresses: [ {shippingAddrJson} ]
            }})
        }});
        const addrBody = await addrResp.text();
        log(`Step 2 done: ${{addrResp.status}} ${{addrBody.slice(0,200)}}`);
        if (!addrResp.ok) {{ err(`Address PATCH ${{addrResp.status}}: ${{addrBody.slice(0,300)}}`); return; }}

        // Step 3: PATCH payment
        log('Step 3: PATCH payment...');
        const payResp = await fetch(baseUrl, {{
            method: 'PATCH', credentials: 'include', headers,
            body: JSON.stringify({{
                cart_id:   cartId,
                cart_type: 'REGULAR',
                payment_instructions: [{{
                    payment_type:    'CREDITCARD',
                    card_number:     {J(pay.CardNumber)},
                    name_on_card:    {J(nameOnCard)},
                    expiration_date: {J(expiry)},
                    cvv:             {J(pay.Cvv)},
                    billing_address: {{
                        first_name:   {J(profile.FirstName)},
                        last_name:    {J(profile.LastName)},
                        line1:        {J(billingLine1)},
                        city:         {J(billingCity)},
                        state:        {J(billingState)},
                        zip_code:     {J(billingZip)},
                        country_code: 'US'
                    }}
                }}]
            }})
        }});
        const payBody = await payResp.text();
        log(`Step 3 done: ${{payResp.status}} ${{payBody.slice(0,200)}}`);
        if (!payResp.ok) {{ err(`Payment PATCH ${{payResp.status}}: ${{payBody.slice(0,300)}}`); return; }}

        // Step 4: POST — place order
        log('Step 4: POST place order...');
        const orderResp = await fetch(baseUrl, {{
            method: 'POST', credentials: 'include', headers,
            body: JSON.stringify({{
                cart_id:          cartId,
                cart_type:        'REGULAR',
                channel_id:       '10',
                shopping_context: 'DIGITAL',
                guest: {{
                    email_address: {J(profile.Email)},
                    first_name:    {J(profile.FirstName)},
                    last_name:     {J(profile.LastName)}
                }}
            }})
        }});
        const orderBody = await orderResp.text();
        log(`Step 4 done: ${{orderResp.status}} ${{orderBody.slice(0,300)}}`);

        if (!orderResp.ok) {{
            let msg;
            try {{ msg = JSON.parse(orderBody).message || orderBody; }} catch {{ msg = orderBody; }}
            err(`${{orderResp.status}} — ${{msg}}`);
            return;
        }}

        let orderId;
        try {{ orderId = JSON.parse(orderBody).order_id || JSON.parse(orderBody).id || 'OK'; }}
        catch {{ orderId = 'OK'; }}
        ok(orderId);

    }} catch(e) {{
        err(e.message || String(e));
    }}
}})();
";
        }
    }
}
