using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json.Linq;

namespace MonitorBot.App.Views
{
    /// <summary>
    /// Checks Walmart product availability inside a hidden embedded Chromium browser.
    /// Uses a real browser TLS fingerprint + injected session cookies so Walmart's
    /// bot detection is bypassed and __NEXT_DATA__ is always served.
    /// </summary>
    public partial class WalmartBrowserStockChecker : Window, IWalmartBrowserStockChecker
    {
        private readonly ILogStore _logStore;
        private bool _initialized = false;

        public WalmartBrowserStockChecker(ILogStore logStore)
        {
            _logStore = logStore;
            InitializeComponent();
        }

        private void Log(string level, string msg) => _logStore.Add(new LogEntry
        {
            Level    = level,
            Category = "Monitor",
            Message  = $"[Walmart:Browser] {msg}"
        });

        public Task<(string? cartId, string? error)> AddToCartAsync(
            string itemId,
            string offerId,
            int quantity,
            CancellationToken ct = default)
        {
            return Dispatcher.InvokeAsync(() =>
                AddToCartOnUiThreadAsync(itemId, offerId, quantity, ct)
            ).Task.Unwrap();
        }

        private async Task<(string? cartId, string? error)> AddToCartOnUiThreadAsync(
            string itemId, string offerId, int quantity, CancellationToken ct)
        {
            try
            {
                await EnsureInitializedAsync();

                // ?? Step 1: Clear the cart so we start from 0 ?????????????
                Log("INFO", "Browser ATC — clearing cart before ATC");
                await ClearCartAsync(ct);

                // ?? Step 2: Navigate to the product page ??????????????????
                Log("INFO", $"Browser ATC — navigating to walmart.com/ip/{itemId}");
                var navTcs = new TaskCompletionSource<bool>();
                void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    WebBrowser.CoreWebView2.NavigationCompleted -= OnNav;
                    navTcs.TrySetResult(e.IsSuccess);
                }
                WebBrowser.CoreWebView2.NavigationCompleted += OnNav;
                WebBrowser.CoreWebView2.Navigate("https://www.walmart.com/ip/" + itemId);
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));

                // Wait for React hydration
                await Task.Delay(2000, ct);

                // Verify we're on the right page
                var pageUrl = await WebBrowser.CoreWebView2.ExecuteScriptAsync("location.href");
                Log("INFO", $"Browser ATC — on page: {pageUrl}");

                // ?? Step 3: Read cart count before click ??????????????????
                var cartCountJs =
                    "(function(){" +
                    "  var el = document.querySelector('[data-automation-id=\"cart-button-header\"]') ||" +
                    "            document.querySelector('button[aria-label*=\"cart\" i]');" +
                    "  if (!el) return '0';" +
                    "  var m = (el.innerText||'').match(/\\d+/);" +
                    "  return m ? m[0] : '0';" +
                    "})()";
                var beforeRaw   = await WebBrowser.CoreWebView2.ExecuteScriptAsync(cartCountJs);
                var beforeCount = int.TryParse(beforeRaw.Trim('"'), out var bc) ? bc : 0;
                Log("INFO", $"Browser ATC — cart count before: {beforeCount}");

                // ?? Step 4: Click the ATC button scoped to this item ??????
                // Target ONLY the main product's ATC button by checking that the button's
                // closest ancestor product container matches our itemId, or by preferring
                // buttons in the main content area over related/recommended sections.
                var clickJs = new System.Text.StringBuilder();
                clickJs.Append("(function() {");
                clickJs.Append("  var itemId = '" + itemId + "';");
                // First try: button whose closest section has a data-item-id matching ours
                clickJs.Append("  var allAtc = Array.from(document.querySelectorAll('[data-automation-id=\"atc\"],[data-automation-id=\"add-to-cart-btn\"],[data-automation-id=\"atc-button\"],[data-testid=\"add-to-cart-button\"]'));");
                // Filter to only buttons NOT inside a carousel/recommended section
                clickJs.Append("  var mainBtn = allAtc.find(function(b) {");
                clickJs.Append("    var p = b;");
                clickJs.Append("    for (var i=0;i<10;i++) {");
                clickJs.Append("      if (!p) break;");
                clickJs.Append("      var cls = (p.className||'') + (p.getAttribute('data-testid')||'');");
                clickJs.Append("      if (/carousel|recommend|similar|related|sponsored/i.test(cls)) return false;");
                clickJs.Append("      p = p.parentElement;");
                clickJs.Append("    }");
                clickJs.Append("    return true;");
                clickJs.Append("  });");
                // Fallback: first ATC button on page
                clickJs.Append("  var btn = mainBtn || allAtc[0];");
                // Last resort: text match, but only outside carousel sections
                clickJs.Append("  if (!btn) {");
                clickJs.Append("    btn = Array.from(document.querySelectorAll('button')).find(function(b){");
                clickJs.Append("      var t=(b.innerText||'').trim().toLowerCase();");
                clickJs.Append("      if (t!=='add to cart') return false;");
                clickJs.Append("      var p=b; for(var i=0;i<10;i++){if(!p)break;if(/carousel|recommend/i.test((p.className||'')))return false;p=p.parentElement;}");
                clickJs.Append("      return true;");
                clickJs.Append("    }) || null;");
                clickJs.Append("  }");
                clickJs.Append("  if (!btn) return 'notfound';");
                clickJs.Append("  var id=btn.getAttribute('data-automation-id')||btn.getAttribute('data-testid')||'?';");
                clickJs.Append("  btn.click();");
                clickJs.Append("  return 'clicked:'+id+' text='+(btn.innerText||'').trim().substring(0,20);");
                clickJs.Append("})();");

                var clickResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(clickJs.ToString());
                Log("INFO", $"Browser ATC click result: {clickResult}");

                if (clickResult?.Contains("notfound") == true)
                    return (null, "ATC button not found on page");

                // ?? Step 5: Poll for cart count to increase ????????????????
                Log("INFO", "Browser ATC — waiting for cart count to increase...");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                    var afterRaw   = await WebBrowser.CoreWebView2.ExecuteScriptAsync(cartCountJs);
                    var afterCount = int.TryParse(afterRaw.Trim('"'), out var ac) ? ac : 0;

                    if (afterCount > beforeCount)
                    {
                        var cartId = Guid.NewGuid().ToString("N");
                        Log("INFO", $"Browser ATC OK — cart {beforeCount}?{afterCount} cartId={cartId}");

                        // ?? Step 6: Switch item to Shipping (away from Pickup) ??
                        await SwitchToShippingAsync(ct);

                        return (cartId, null);
                    }
                }

                return (null, "ATC timed out — cart count did not increase after button click");
            }
            catch (OperationCanceledException)
            {
                return (null, "ATC cancelled");
            }
            catch (Exception ex)
            {
                Log("WARN", $"Browser ATC error: {ex.Message}");
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// After ATC, navigates to the cart page and switches the fulfillment method
        /// to Shipping if it was defaulted to Pickup. This ensures the checkout flow
        /// goes through the full shipping address + payment path.
        /// </summary>
        private async Task SwitchToShippingAsync(CancellationToken ct)
        {
            try
            {
                // We may already be on the product page — navigate to cart
                var navTcs = new TaskCompletionSource<bool>();
                void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    WebBrowser.CoreWebView2.NavigationCompleted -= OnNav;
                    navTcs.TrySetResult(true);
                }
                WebBrowser.CoreWebView2.NavigationCompleted += OnNav;
                WebBrowser.CoreWebView2.Navigate("https://www.walmart.com/cart");
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
                await Task.Delay(2000, ct);

                // Check current fulfillment and switch to Shipping if needed.
                // Walmart shows a "Shipping" option button / tab next to "Pickup" on each cart item.
                var switchJs =
                    "(function(){" +
                    // Try clicking a Shipping radio/button/tab on the item
                    "  var shippingBtn =" +
                    "    document.querySelector('[data-automation-id=\"shipping-option\"]') ||" +
                    "    document.querySelector('[data-testid=\"shipping-option\"]') ||" +
                    "    document.querySelector('input[value=\"SHIPPING\"]') ||" +
                    "    document.querySelector('input[value=\"shipping\"]') ||" +
                    "    Array.from(document.querySelectorAll('button,label,input')).find(function(el){" +
                    "      var t=(el.innerText||el.value||el.getAttribute('aria-label')||'').trim().toLowerCase();" +
                    "      return t==='shipping'||t==='ship'||t==='ship it';" +
                    "    });" +
                    "  if(shippingBtn){shippingBtn.click();return 'switched:'+shippingBtn.tagName;}" +
                    // Already on shipping or not found — check if Pickup text is present
                    "  var isPickup = document.body.innerText.toLowerCase().indexOf('pickup')!==-1;" +
                    "  return isPickup ? 'pickup-no-switch' : 'already-shipping';" +
                    "})()";

                var switchResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(switchJs);
                Log("INFO", $"Browser ATC — fulfillment switch: {switchResult}");

                if (switchResult?.Contains("switched") == true)
                    await Task.Delay(1500, ct); // wait for cart to update after switch
            }
            catch (Exception ex)
            {
                Log("WARN", $"SwitchToShipping error (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Navigates to the Walmart cart page and clicks every Remove button,
        /// leaving the cart empty before the ATC attempt.
        /// </summary>
        private async Task ClearCartAsync(CancellationToken ct)
        {
            try
            {
                var navTcs = new TaskCompletionSource<bool>();
                void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    WebBrowser.CoreWebView2.NavigationCompleted -= OnNav;
                    navTcs.TrySetResult(true);
                }
                WebBrowser.CoreWebView2.NavigationCompleted += OnNav;
                WebBrowser.CoreWebView2.Navigate("https://www.walmart.com/cart");
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
                await Task.Delay(1500, ct); // wait for React to render cart items

                // Click every Remove button until none remain
                for (int pass = 0; pass < 10; pass++)
                {
                    var removeJs =
                        "(function(){" +
                        "  var btn = Array.from(document.querySelectorAll('button')).find(function(b){" +
                        "    var t=(b.innerText||'').trim().toLowerCase();" +
                        "    return t==='remove'||t.startsWith('remove');"+
                        "  });" +
                        "  if (!btn) return 'none';" +
                        "  btn.click();" +
                        "  return 'removed';" +
                        "})()";
                    var r = await WebBrowser.CoreWebView2.ExecuteScriptAsync(removeJs);
                    if (r?.Contains("none") == true) break;
                    await Task.Delay(800, ct); // wait for cart to update
                }

                Log("INFO", "Cart cleared");
            }
            catch (Exception ex)
            {
                Log("WARN", $"ClearCart error (non-fatal): {ex.Message}");
            }
        }

        public Task<(string? orderId, string? error)> BrowserCheckoutAsync(
            string cartId,
            MonitorBot.Core.Models.UserProfile profile,
            CancellationToken ct = default)
        {
            return Dispatcher.InvokeAsync(() =>
                BrowserCheckoutOnUiThreadAsync(cartId, profile, ct)
            ).Task.Unwrap();
        }

        private async Task<(string? orderId, string? error)> BrowserCheckoutOnUiThreadAsync(
            string cartId,
            MonitorBot.Core.Models.UserProfile profile,
            CancellationToken ct)
        {
            var orderTcs = new TaskCompletionSource<(int status, string body)>();

            // Intercept the order network response at the C# level.
            // Checkout contract/order are direct AJAX (not service worker), so this fires.
            void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
            {
                try
                {
                    var uri = e.Request.Uri ?? string.Empty;
                    if (uri.Contains("/checkout/guest/order", StringComparison.OrdinalIgnoreCase) ||
                        uri.Contains("/checkout/guest/contract", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("INFO", $"Checkout network response: status={e.Response.StatusCode} url={uri[..Math.Min(80,uri.Length)]}");
                        // Only resolve on the order endpoint (contract comes first)
                        if (uri.Contains("/checkout/guest/order", StringComparison.OrdinalIgnoreCase))
                            orderTcs.TrySetResult((e.Response.StatusCode, string.Empty));
                    }
                }
                catch { }
            }

            WebBrowser.CoreWebView2.WebResourceResponseReceived += OnResponse;
            try
            {
                await EnsureInitializedAsync();

                var addr    = profile.ShippingAddress;
                var billing = profile.BillingAddress;
                var pay     = profile.Payment;
                var billLine1 = !string.IsNullOrWhiteSpace(billing.Line1)   ? billing.Line1   : addr.Line1;
                var billCity  = !string.IsNullOrWhiteSpace(billing.City)    ? billing.City    : addr.City;
                var billState = !string.IsNullOrWhiteSpace(billing.State)   ? billing.State   : addr.State;
                var billZip   = !string.IsNullOrWhiteSpace(billing.ZipCode) ? billing.ZipCode : addr.ZipCode;

                // Step 1: go to cart and click "Checkout all items" CTA (not the skip-nav link)
                Log("INFO", "Browser checkout — navigating to cart");
                var navTcs = new TaskCompletionSource<bool>();
                void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) { WebBrowser.CoreWebView2.NavigationCompleted -= OnNav; navTcs.TrySetResult(true); }
                WebBrowser.CoreWebView2.NavigationCompleted += OnNav;
                WebBrowser.CoreWebView2.Navigate("https://www.walmart.com/cart");
                await Task.WhenAny(navTcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
                await Task.Delay(2500, ct);

                // Click the real Checkout button — exclude skip-nav links
                var checkoutClickJs =
                    "(function(){" +
                    "  var btn = document.querySelector('[data-automation-id=\"cart-checkout-btn\"]') ||" +
                    "    document.querySelector('[data-testid=\"cart-checkout-button\"]') ||" +
                    "    document.querySelector('a[href*=\"/checkout\"]') ||" +
                    "    Array.from(document.querySelectorAll('button,a')).find(function(b){" +
                    "      var t=(b.innerText||b.textContent||'').trim().toLowerCase();" +
                    "      return (t==='checkout'||t==='checkout all items'||t.startsWith('checkout all')) && t.indexOf('skip')===-1;" +
                    "    });" +
                    "  if(!btn) return 'notfound';" +
                    "  btn.click(); return 'clicked:'+(btn.getAttribute('data-automation-id')||btn.tagName)+' text='+(btn.innerText||'').trim().substring(0,40);" +
                    "})()";
                var coClick = await WebBrowser.CoreWebView2.ExecuteScriptAsync(checkoutClickJs);
                Log("INFO", $"Browser checkout — cart checkout click: {coClick}");

                // Wait for navigation away from /cart to the checkout page
                var coNavTcs = new TaskCompletionSource<bool>();
                void OnCoNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    var url = WebBrowser.CoreWebView2.Source ?? string.Empty;
                    if (!url.Contains("/cart"))
                    {
                        WebBrowser.CoreWebView2.NavigationCompleted -= OnCoNav;
                        coNavTcs.TrySetResult(true);
                    }
                }
                WebBrowser.CoreWebView2.NavigationCompleted += OnCoNav;
                await Task.WhenAny(coNavTcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
                WebBrowser.CoreWebView2.NavigationCompleted -= OnCoNav;
                await Task.Delay(3000, ct); // let React fully hydrate checkout page

                var pageUrl = await WebBrowser.CoreWebView2.ExecuteScriptAsync("location.href");
                Log("INFO", $"Browser checkout — on page: {pageUrl}");

                // Step 2: fill shipping address fields (present for shipping orders, skipped for pickup)
                var addrJs = new System.Text.StringBuilder();
                addrJs.Append("(function(){");
                addrJs.Append("  function fill(sel,val){");
                addrJs.Append("    var el=document.querySelector(sel);if(!el)return false;");
                addrJs.Append("    var nv=Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype,'value');");
                addrJs.Append("    nv.set.call(el,val);");
                addrJs.Append("    el.dispatchEvent(new Event('input',{bubbles:true}));");
                addrJs.Append("    el.dispatchEvent(new Event('change',{bubbles:true}));");
                addrJs.Append("    return true;");
                addrJs.Append("  }");
                addrJs.Append("  fill('[data-automation-id=\"firstName\"]','" + Esc(profile.FirstName) + "');");
                addrJs.Append("  fill('[data-automation-id=\"lastName\"]','" + Esc(profile.LastName) + "');");
                addrJs.Append("  fill('[data-automation-id=\"addressLineOne\"]','" + Esc(addr.Line1) + "');");
                addrJs.Append("  fill('[data-automation-id=\"city\"]','" + Esc(addr.City) + "');");
                addrJs.Append("  fill('[data-automation-id=\"postalCode\"]','" + Esc(addr.ZipCode) + "');");
                addrJs.Append("  fill('input[autocomplete=\"given-name\"]','" + Esc(profile.FirstName) + "');");
                addrJs.Append("  fill('input[autocomplete=\"family-name\"]','" + Esc(profile.LastName) + "');");
                addrJs.Append("  fill('input[autocomplete=\"address-line1\"]','" + Esc(addr.Line1) + "');");
                addrJs.Append("  fill('input[autocomplete=\"address-level2\"]','" + Esc(addr.City) + "');");
                addrJs.Append("  fill('input[autocomplete=\"postal-code\"]','" + Esc(addr.ZipCode) + "');");
                addrJs.Append("  fill('input[name=\"phone\"]','" + Esc(profile.Phone) + "');");
                addrJs.Append("  fill('input[autocomplete=\"tel\"]','" + Esc(profile.Phone) + "');");
                // State — try select dropdown first, then input
                addrJs.Append("  var stateEl=document.querySelector('[data-automation-id=\"state\"]')||document.querySelector('select[name=\"state\"]')||document.querySelector('select[autocomplete=\"address-level1\"]');");
                addrJs.Append("  if(stateEl&&stateEl.tagName==='SELECT'){stateEl.value='" + Esc(addr.State) + "';stateEl.dispatchEvent(new Event('change',{bubbles:true}));}");
                addrJs.Append("  else fill('[data-automation-id=\"state\"]','" + Esc(addr.State) + "');");
                // Click "Continue to payment" / "Use this address" if present
                addrJs.Append("  var contBtn=document.querySelector('[data-automation-id=\"continue-to-payment\"]')||");
                addrJs.Append("    document.querySelector('[data-automation-id=\"use-this-address\"]')||");
                addrJs.Append("    Array.from(document.querySelectorAll('button')).find(function(b){");
                addrJs.Append("      var t=(b.innerText||'').trim().toLowerCase();");
                addrJs.Append("      return t.indexOf('continue')!==-1||t==='use this address'||t==='save & continue';");
                addrJs.Append("    });");
                addrJs.Append("  if(contBtn){contBtn.click();return 'addr-filled-and-continued';}");
                addrJs.Append("  return 'addr-filled';");
                addrJs.Append("})()");
                var addrResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(addrJs.ToString());
                Log("INFO", $"Browser checkout — address fill: {addrResult}");
                if (addrResult?.Contains("continued") == true)
                    await Task.Delay(2500, ct); // wait for payment section to appear

                // Step 3: fill payment card fields
                // Walmart's checkout card fields are typically iframes or input[autocomplete] fields
                var fillJs = new System.Text.StringBuilder();
                fillJs.Append("(function(){");
                fillJs.Append("  function fill(sel,val){");
                fillJs.Append("    var el=document.querySelector(sel);");
                fillJs.Append("    if(!el)return false;");
                fillJs.Append("    var nv=Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype,'value');");
                fillJs.Append("    nv.set.call(el,val);");
                fillJs.Append("    el.dispatchEvent(new Event('input',{bubbles:true}));");
                fillJs.Append("    el.dispatchEvent(new Event('change',{bubbles:true}));");
                fillJs.Append("    return true;");
                fillJs.Append("  }");
                // Card number — try multiple selectors
                fillJs.Append("  fill('[data-automation-id=\"creditCardNumber\"]','" + Esc(pay.CardNumber.Replace(" ", "")) + "');");
                fillJs.Append("  fill('input[autocomplete=\"cc-number\"]','" + Esc(pay.CardNumber.Replace(" ", "")) + "');");
                fillJs.Append("  fill('input[name=\"cardNumber\"]','" + Esc(pay.CardNumber.Replace(" ", "")) + "');");
                // Expiry
                fillJs.Append("  fill('[data-automation-id=\"expiryDate\"]','" + Esc(pay.ExpiryMonth + "/" + pay.ExpiryYear) + "');");
                fillJs.Append("  fill('input[autocomplete=\"cc-exp\"]','" + Esc(pay.ExpiryMonth + "/" + pay.ExpiryYear) + "');");
                fillJs.Append("  fill('[data-automation-id=\"expiryMonth\"]','" + Esc(pay.ExpiryMonth) + "');");
                fillJs.Append("  fill('[data-automation-id=\"expiryYear\"]','" + Esc(pay.ExpiryYear) + "');");
                // CVV
                fillJs.Append("  fill('[data-automation-id=\"cvv\"]','" + Esc(pay.Cvv) + "');");
                fillJs.Append("  fill('input[autocomplete=\"cc-csc\"]','" + Esc(pay.Cvv) + "');");
                fillJs.Append("  fill('input[name=\"cvv\"]','" + Esc(pay.Cvv) + "');");
                // Name on card
                fillJs.Append("  fill('[data-automation-id=\"nameOnCard\"]','" + Esc(profile.FirstName + " " + profile.LastName) + "');");
                fillJs.Append("  fill('input[autocomplete=\"cc-name\"]','" + Esc(profile.FirstName + " " + profile.LastName) + "');");
                fillJs.Append("  return 'filled';");
                fillJs.Append("})()");
                var fillResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(fillJs.ToString());
                Log("INFO", $"Browser checkout — field fill: {fillResult}");
                await Task.Delay(1000, ct);

                // Dump buttons AFTER fill so we see the live checkout page state
                var diagJs2 =
                    "(function(){var btns=Array.from(document.querySelectorAll('button,input[type=submit],a[role=button]')).map(function(b){return (b.getAttribute('data-automation-id')||b.getAttribute('data-testid')||b.type||b.tagName||'')+':'+( b.innerText||b.value||'').trim().substring(0,40);});return JSON.stringify(btns.slice(0,40));})()";
                var diagRaw2 = await WebBrowser.CoreWebView2.ExecuteScriptAsync(diagJs2);
                Log("INFO", $"Browser checkout — post-fill buttons: " + System.Text.RegularExpressions.Regex.Unescape(diagRaw2.Trim('"')));

                // Step 3: click Place Order — broad selector covering all known variants
                var placeJs =
                    "(function(){" +
                    "  var candidates = [" +
                    "    '[data-automation-id=\"place-order-btn\"]'," +
                    "    '[data-automation-id=\"place-order\"]'," +
                    "    '[data-automation-id=\"placeOrder\"]'," +
                    "    '[data-testid=\"place-order-button\"]'," +
                    "    '[data-testid=\"placeOrder\"]'," +
                    "    'button[class*=\"place-order\" i]'" +
                    "  ];" +
                    "  var btn = null;" +
                    "  for(var i=0;i<candidates.length;i++){btn=document.querySelector(candidates[i]);if(btn)break;}" +
                    "  if(!btn) btn = Array.from(document.querySelectorAll('button,input[type=submit]')).find(function(b){" +
                    "    var t=(b.innerText||b.value||'').trim().toLowerCase();" +
                    "    return t==='place order'||t==='place your order'||t==='submit order'||t.startsWith('place order');" +
                    "  });" +
                    "  if(!btn) return 'notfound';" +
                    "  var id=btn.getAttribute('data-automation-id')||btn.getAttribute('data-testid')||'?';" +
                    "  btn.click();" +
                    "  return 'clicked:'+id+' text='+(btn.innerText||'').trim().substring(0,40);" +
                    "})()";
                var placeResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(placeJs);
                Log("INFO", $"Browser checkout — place order click: {placeResult}");

                if (placeResult?.Contains("notfound") == true)
                    return (null, "Place Order button not found — checkout page may need manual intervention");

                // Step 4: wait for the order network response (up to 30s)
                Log("INFO", "Browser checkout — waiting for order response...");
                var done2 = await Task.WhenAny(orderTcs.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
                if (done2 != orderTcs.Task)
                    return (null, "Order placement timed out — no network response seen");

                var (oStatus, _) = await orderTcs.Task;
                Log("INFO", $"Browser checkout order response status={oStatus}");

                if (oStatus >= 200 && oStatus < 300)
                {
                    // Read the orderId from the page DOM (confirmation page)
                    await Task.Delay(2000, ct);
                    var confirmJs =
                        "(function(){" +
                        "  var el = document.querySelector('[data-automation-id=\"order-id\"]') ||" +
                        "    document.querySelector('[data-testid=\"order-id\"]');" +
                        "  if(el) return el.innerText||el.textContent||'';" +
                        "  var m = (document.body.innerText||'').match(/order[\\s#:]+([A-Z0-9\\-]{6,})/i);" +
                        "  return m ? m[1] : 'OK';" +
                        "})()";
                    var confirmRaw = await WebBrowser.CoreWebView2.ExecuteScriptAsync(confirmJs);
                    var orderId    = confirmRaw.Trim('"').Trim();
                    if (string.IsNullOrWhiteSpace(orderId)) orderId = "OK";
                    Log("INFO", $"Browser order placed! orderId={orderId}");
                    return (orderId, null);
                }

                return (null, $"Order HTTP {oStatus}");
            }
            catch (OperationCanceledException) { return (null, "Checkout cancelled"); }
            catch (Exception ex)
            {
                Log("WARN", $"Browser checkout error: {ex.Message}");
                return (null, ex.Message);
            }
            finally
            {
                try { WebBrowser.CoreWebView2.WebResourceResponseReceived -= OnResponse; } catch { }
            }
        }

        private static string Esc(string? s) => (s ?? string.Empty).Replace("'", "\\'").Replace("\\", "\\\\");

        private static string GuessCardType(string? num)
        {
            if (string.IsNullOrWhiteSpace(num)) return "VISA";
            num = num.Replace(" ", "");
            if (num.StartsWith("4")) return "VISA";
            if (num.StartsWith("51") || num.StartsWith("52") || num.StartsWith("53") || num.StartsWith("54") || num.StartsWith("55")) return "MASTERCARD";
            if (num.StartsWith("34") || num.StartsWith("37")) return "AMEX";
            if (num.StartsWith("6011") || num.StartsWith("65")) return "DISCOVER";
            return "VISA";
        }

        public Task<(bool isAvailable, string? status, string? title, decimal? price)> CheckAsync(
            string itemId,
            string? offerIdOverride,
            string? cookies,
            CancellationToken ct = default)
        {
            return Dispatcher.InvokeAsync(() =>
                CheckOnUiThreadAsync(itemId, offerIdOverride, cookies, ct)
            ).Task.Unwrap();
        }

        private async Task<(bool isAvailable, string? status, string? title, decimal? price)> CheckOnUiThreadAsync(
            string itemId,
            string? offerIdOverride,
            string? cookies,
            CancellationToken ct)
        {
            try
            {
                await EnsureInitializedAsync();

                var productUrl = "https://www.walmart.com/ip/" + itemId;

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    // Navigate and wait for load
                    var navTcs = new TaskCompletionSource<bool>();
                    void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
                    {
                        WebBrowser.CoreWebView2.NavigationCompleted -= OnNav;
                        navTcs.TrySetResult(e.IsSuccess);
                    }
                    WebBrowser.CoreWebView2.NavigationCompleted += OnNav;
                    WebBrowser.CoreWebView2.Navigate(productUrl);

                    var navTimeout = Task.Delay(TimeSpan.FromSeconds(20), ct);
                    var navDone   = await Task.WhenAny(navTcs.Task, navTimeout);
                    if (navDone == navTimeout)
                    {
                        Log("WARN", $"Navigation timed out for itemId={itemId} (attempt {attempt})");
                        if (attempt >= 3) return (true, "UNKNOWN", null, null);
                        await ResetPxSessionAsync(ct);
                        continue;
                    }

                    // Check immediately if we landed on the block page — don't waste 12s polling
                    var currentUrl = WebBrowser.CoreWebView2.Source ?? string.Empty;
                    if (currentUrl.Contains("/blocked", StringComparison.OrdinalIgnoreCase) ||
                        currentUrl.Contains("robot-or-human", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("WARN", $"PerimeterX block detected (attempt {attempt}) — resetting session");
                        if (attempt >= 3) return (true, "UNKNOWN", null, null);
                        await ResetPxSessionAsync(ct);
                        continue;
                    }

                    // Poll for __NEXT_DATA__ (up to 12s)
                    var pollJs   = "document.getElementById('__NEXT_DATA__') !== null";
                    var deadline = DateTime.UtcNow.AddSeconds(12);
                    var found    = false;
                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        var present = await WebBrowser.CoreWebView2.ExecuteScriptAsync(pollJs);
                        if (present == "true") { found = true; break; }

                        // Check mid-poll if we got redirected to blocked page
                        var midUrl = WebBrowser.CoreWebView2.Source ?? string.Empty;
                        if (midUrl.Contains("/blocked", StringComparison.OrdinalIgnoreCase))
                        {
                            Log("WARN", $"Redirected to block page mid-poll (attempt {attempt})");
                            break;
                        }

                        await Task.Delay(400, ct);
                    }

                    if (!found)
                    {
                        var diagRaw = await WebBrowser.CoreWebView2.ExecuteScriptAsync(
                            "JSON.stringify({title:document.title,url:location.href,body:document.body&&document.body.innerText?document.body.innerText.substring(0,200):''})");
                        Log("WARN", $"__NEXT_DATA__ missing (attempt {attempt}) — page: " +
                            System.Text.RegularExpressions.Regex.Unescape(diagRaw.Trim('"')));

                        if (attempt >= 3) return (true, "UNKNOWN", null, null);
                        await ResetPxSessionAsync(ct);
                        continue;
                    }

                    // Extract stock data from __NEXT_DATA__
                    var js       = BuildExtractScript(offerIdOverride);
                    var jsResult = await WebBrowser.CoreWebView2.ExecuteScriptAsync(js);
                    var result   = ParseResult(jsResult, itemId);

                    // Genuine parse failure on first attempt — retry
                    if (!result.isAvailable && result.status == null && attempt < 3)
                    {
                        Log("WARN", $"Extract script error on attempt {attempt} — retrying");
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        continue;
                    }

                    return result;
                }

                return (true, "UNKNOWN", null, null);
            }
            catch (OperationCanceledException)
            {
                return (false, null, null, null);
            }
            catch (Exception ex)
            {
                Log("WARN", $"Browser stock check error: {ex.Message}");
                return (true, "UNKNOWN", null, null);
            }
        }

        /// <summary>
        /// Clears PerimeterX tracking cookies and navigates to the Walmart homepage
        /// to obtain a fresh, unblocked session before retrying a product page.
        /// </summary>
        private async Task ResetPxSessionAsync(CancellationToken ct)
        {
            try
            {
                Log("INFO", "Resetting PX session — clearing PX cookies and warming up via homepage");

                // Delete all PX-related cookies so the next request gets a fresh visitor ID
                var mgr = WebBrowser.CoreWebView2.CookieManager;
                var allCookies = await mgr.GetCookiesAsync("https://www.walmart.com");
                foreach (var cookie in allCookies)
                {
                    var n = cookie.Name;
                    if (n.StartsWith("_px", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("_pxvid", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("_pxde", StringComparison.OrdinalIgnoreCase)  ||
                        n.Equals("_pxcts", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("pxcts", StringComparison.OrdinalIgnoreCase))
                    {
                        mgr.DeleteCookie(cookie);
                    }
                }

                // Warm-up: navigate to the homepage so Walmart/PX issues a fresh visitor ID
                var homeTcs = new TaskCompletionSource<bool>();
                void OnHome(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    WebBrowser.CoreWebView2.NavigationCompleted -= OnHome;
                    homeTcs.TrySetResult(true);
                }
                WebBrowser.CoreWebView2.NavigationCompleted += OnHome;
                WebBrowser.CoreWebView2.Navigate("https://www.walmart.com/");
                await Task.WhenAny(homeTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));

                // Give PX JS time to run and write fresh cookies
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                Log("INFO", "PX session reset complete");
            }
            catch (Exception ex)
            {
                Log("WARN", $"ResetPxSessionAsync error: {ex.Message}");
            }
        }

        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            // Reuse the same profile the harvester uses for walmart.com
            // — it already has a real logged-in session with valid cookies
            var profileDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MonitorBot", "HarvestProfiles", "walmart.com");
            System.IO.Directory.CreateDirectory(profileDir);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDir);

            var initTask = WebBrowser.EnsureCoreWebView2Async(env);
            var timeout  = Task.Delay(TimeSpan.FromSeconds(15));
            if (await Task.WhenAny(initTask, timeout) == timeout)
                throw new TimeoutException("WebView2 initialization timed out");
            await initTask;

            WebBrowser.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
            WebBrowser.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            WebBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            // Install cart interceptor to run before Walmart's JS on every page load.
            // Intercepts both fetch() and XMLHttpRequest so we capture the ATC response
            // regardless of which transport Walmart's React bundle uses.
            var intercept = new System.Text.StringBuilder();
            intercept.Append("(function() {");
            // --- fetch intercept ---
            intercept.Append("  var _f = window.fetch;");
            intercept.Append("  window.fetch = async function(input, init) {");
            intercept.Append("    var url = typeof input === 'string' ? input : (input && input.url) || '';");
            intercept.Append("    var r = await _f.apply(this, arguments);");
            intercept.Append("    if (url.indexOf('/api/v3/cart') !== -1) {");
            intercept.Append("      r.clone().text().then(function(t) {");
            intercept.Append("        window.chrome.webview.postMessage({type:'atcResult',status:r.status,body:t.substring(0,800),via:'fetch'});");
            intercept.Append("      }).catch(function(e) {");
            intercept.Append("        window.chrome.webview.postMessage({type:'atcResult',status:r.status,body:'',via:'fetch'});");
            intercept.Append("      });");
            intercept.Append("    }");
            intercept.Append("    return r;");
            intercept.Append("  };");
            // --- XHR intercept ---
            intercept.Append("  var _open = XMLHttpRequest.prototype.open;");
            intercept.Append("  var _send = XMLHttpRequest.prototype.send;");
            intercept.Append("  XMLHttpRequest.prototype.open = function(m, u) {");
            intercept.Append("    this._wm_url = u || '';");
            intercept.Append("    return _open.apply(this, arguments);");
            intercept.Append("  };");
            intercept.Append("  XMLHttpRequest.prototype.send = function() {");
            intercept.Append("    var self = this;");
            intercept.Append("    if ((self._wm_url||'').indexOf('/api/v3/cart') !== -1) {");
            intercept.Append("      self.addEventListener('load', function() {");
            intercept.Append("        window.chrome.webview.postMessage({type:'atcResult',status:self.status,body:(self.responseText||'').substring(0,800),via:'xhr'});");
            intercept.Append("      });");
            intercept.Append("    }");
            intercept.Append("    return _send.apply(this, arguments);");
            intercept.Append("  };");
            intercept.Append("})();");

            await WebBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(intercept.ToString());

            _initialized = true;
        }

        private void InjectCookies(string rawCookies, string domain)
        {
            var mgr = WebBrowser.CoreWebView2.CookieManager;
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
                    var c = mgr.CreateCookie(name, value, $".{domain}", "/");
                    c.IsSecure = true;
                    mgr.AddOrUpdateCookie(c);
                }
                catch { }
            }
        }

        private static string BuildExtractScript(string? offerIdOverride)
        {
            var offerIdJs = string.IsNullOrWhiteSpace(offerIdOverride)
                ? "null"
                : "\"" + offerIdOverride.Trim() + "\"";

            var sb = new System.Text.StringBuilder();
            sb.Append("(function() { try {");
            sb.Append(" var el = document.getElementById('__NEXT_DATA__');");
            sb.Append(" if (!el) return JSON.stringify({error:'__NEXT_DATA__ not found'});");
            sb.Append(" var data = JSON.parse(el.textContent);");
            sb.Append(" var targetOfferId = ").Append(offerIdJs).Append(";");
            sb.Append(" var foundStatus = null, foundName = null, foundPrice = null;");
            // Try known product name paths first before walking
            sb.Append(" try {");
            sb.Append("  var pd = data.props && data.props.pageProps && data.props.pageProps.initialData && data.props.pageProps.initialData.data && data.props.pageProps.initialData.data.product;");
            sb.Append("  if (pd && pd.name) foundName = pd.name;");
            sb.Append(" } catch(e2) {}");
            sb.Append(" function walk(o) {");
            sb.Append("  if (!o || typeof o !== 'object') return;");
            sb.Append("  var ks = Object.keys(o);");
            sb.Append("  for (var i=0;i<ks.length;i++) {");
            sb.Append("   var k=ks[i], v=o[k];");
            sb.Append("   if (k==='name'&&!foundName&&typeof v==='string'&&v.length>5&&v.length<300&&!v.startsWith('http')&&!v.startsWith('NEW ')) foundName=v;");
            sb.Append("   if (k==='availabilityStatus'&&typeof v==='string'&&v.length>0) {");
            sb.Append("    if (targetOfferId) {");
            sb.Append("     if (o['offerId']===targetOfferId) { foundStatus=v; return; }");
            sb.Append("     if (!foundStatus) foundStatus=v;");
            sb.Append("    } else { foundStatus=v; return; }");
            sb.Append("   }");
            sb.Append("   if (k==='price'&&!foundPrice&&typeof v==='number') foundPrice=v;");
            sb.Append("   if (k==='currentPrice'&&typeof v==='object'&&v&&!foundPrice) foundPrice=v['price'];");
            sb.Append("   if (typeof v==='object') walk(v);");
            sb.Append("  }");
            sb.Append(" }");
            sb.Append(" walk(data);");
            sb.Append(" return JSON.stringify({status:foundStatus,name:foundName,price:foundPrice});");
            sb.Append("} catch(e) { return JSON.stringify({error:e.message}); }");
            sb.Append("})();");
            return sb.ToString();
        }

        private (bool isAvailable, string? status, string? title, decimal? price) ParseResult(
            string jsResult, string itemId)
        {
            try
            {
                // jsResult is a JSON-encoded string (double-escaped by ExecuteScriptAsync)
                var unescaped = System.Text.RegularExpressions.Regex.Unescape(
                    jsResult.Trim('"'));
                var j = JObject.Parse(unescaped);

                if (j["error"] != null)
                {
                    Log("WARN", $"JS error for itemId={itemId}: {j["error"]}");
                    // Return null status so caller can decide to retry rather than mark unavailable
                    return (false, null, null, null);
                }

                var status = j["status"]?.ToString() ?? string.Empty;
                var name   = j["name"]?.ToString();
                var price  = j["price"]?.Value<decimal?>();

                if (string.IsNullOrEmpty(status))
                {
                    Log("WARN", $"itemId={itemId} — status missing from __NEXT_DATA__ (name={name})");
                    // Treat as soft failure so monitor keeps retrying
                    return (false, null, name, price);
                }

                var isAvailable = status.Equals("IN_STOCK", StringComparison.OrdinalIgnoreCase)
                               || status.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase);

                Log("INFO", $"itemId={itemId} status={status} name={name}");
                return (isAvailable, status, name, price);
            }
            catch (Exception ex)
            {
                Log("WARN", $"ParseResult error: {ex.Message} raw={jsResult}");
                return (false, null, null, null);
            }
        }
    }
}
