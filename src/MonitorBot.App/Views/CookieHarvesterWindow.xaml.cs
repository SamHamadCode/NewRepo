using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace MonitorBot.App.Views
{
    /// <summary>
    /// Embedded Chromium harvester window.
    /// Opens the target retail site in a real browser, waits for the user to log in,
    /// then extracts all cookies and returns them as a single Cookie header string.
    /// </summary>
    public partial class CookieHarvesterWindow : Window
    {
        private readonly string _startUrl;
        private readonly string _checkoutUrl;
        private bool _autoHarvestDone = false;
        private bool _redirectedToCheckout = false;

        /// <summary>The harvested cookie string — set after HarvestButton_Click succeeds.</summary>
        public string? HarvestedCookies { get; private set; }

        public CookieHarvesterWindow(string siteUrl)
        {
            InitializeComponent();
            _startUrl = siteUrl;
            // For Target always land on checkout so target_access_token gets set
            _checkoutUrl = new Uri(siteUrl).Host.Contains("target.com")
                ? "https://www.target.com/checkout"
                : siteUrl;
            SiteLabel.Text = $"— {new Uri(siteUrl).Host}";
        }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                // Each site gets its own isolated browser profile so cookies don't mix
                var host = new Uri(_startUrl).Host.Replace("www.", "");
                var profileDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MonitorBot", "HarvestProfiles", host);
                System.IO.Directory.CreateDirectory(profileDir);

                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: profileDir);

                await WebBrowser.EnsureCoreWebView2Async(env);

                // Suppress the annoying "controlled by automation" banner
                WebBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                WebBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;

                WebBrowser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                WebBrowser.CoreWebView2.Navigate(_startUrl);

                SetStatus("Navigating…", "#F59E0B");
            }
            catch (Exception ex)
            {
                SetStatus($"WebView2 error: {ex.Message}", "#EF4444");
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            Dispatcher.Invoke(async () =>
            {
                if (!e.IsSuccess)
                {
                    SetStatus("Navigation failed — check your internet connection", "#EF4444");
                    return;
                }

                if (_autoHarvestDone) return;

                var url = WebBrowser.Source?.ToString() ?? string.Empty;
                var isTarget = url.Contains("target.com");

                // Check if target_access_token is present — harvest immediately if so
                if (isTarget)
                {
                    // Wait longer so gsp.target.com has time to set target_access_token
                    await Task.Delay(3000);
                    var hasCookie = await CheckForTargetAccessTokenAsync();
                    if (hasCookie)
                    {
                        SetStatus("target_access_token detected — auto-harvesting…", "#22C55E");
                        _autoHarvestDone = true;
                        await DoHarvestAsync();
                        return;
                    }
                }

                // Redirect to checkout once to trigger target_access_token being set,
                // but only if we haven't already tried (avoids infinite loop on empty cart)
                if (isTarget && !_redirectedToCheckout && !url.Contains("/checkout"))
                {
                    _redirectedToCheckout = true;
                    SetStatus("Logged in — navigating to checkout to get access token…", "#F59E0B");
                    await Task.Delay(400);
                    WebBrowser.CoreWebView2.Navigate(_checkoutUrl);
                }
                else
                {
                    SetStatus("Logged in — click Harvest & Apply", "#22C55E");
                }
            });
        }

        private async Task<bool> CheckForTargetAccessTokenAsync()
        {
            try
            {
                var cookies = await WebBrowser.CoreWebView2.CookieManager
                    .GetCookiesAsync("https://www.target.com");
                return cookies.Any(c => c.Name == "target_access_token");
            }
            catch { return false; }
        }

        private async void HarvestButton_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Harvesting cookies…", "#F59E0B");
            await DoHarvestAsync();
        }

        private async Task DoHarvestAsync()
        {
            try
            {
                var cookieManager = WebBrowser.CoreWebView2.CookieManager;
                var uri = new Uri(_startUrl);
                var host = uri.Host;

                // Collect cookies for the main domain + common subdomains
                var domains = new[]
                {
                    $"https://{host}",
                    $"https://www.{host}",
                    $"https://api.{host}",
                    $"https://account.{host}",
                    $"https://carts.{host}",
                    // gsp.target.com sets target_access_token (ID2) — required for checkout
                    $"https://gsp.{host}"
                };

                var allCookies = new List<CoreWebView2Cookie>();
                foreach (var domain in domains)
                {
                    try
                    {
                        var list = await cookieManager.GetCookiesAsync(domain);
                        allCookies.AddRange(list);
                    }
                    catch { /* subdomain may not exist */ }
                }

                // De-duplicate by name+domain, build Cookie header string
                var seen = new HashSet<string>();
                var sb = new StringBuilder();
                foreach (var c in allCookies)
                {
                    var key = $"{c.Name}|{c.Domain}";
                    if (!seen.Add(key)) continue;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(c.Name).Append('=').Append(c.Value);
                }

                if (sb.Length == 0)
                {
                    SetStatus("No cookies found — make sure you are logged in first", "#EF4444");
                    return;
                }

                var cookieStr = sb.ToString();
                var hasId2 = cookieStr.Contains("target_access_token=");
                var count = seen.Count;

                HarvestedCookies = cookieStr;
                SetStatus(hasId2
                    ? $"? {count} cookies harvested (target_access_token ?) — applying…"
                    : $"? {count} cookies harvested — applying…", "#22C55E");

                await Task.Delay(800);
                Dispatcher.Invoke(() => { DialogResult = true; Close(); });
            }
            catch (Exception ex)
            {
                SetStatus($"Harvest failed: {ex.Message}", "#EF4444");
            }
        }

        private void SetStatus(string message, string hexColor)
        {
            StatusText.Text = message;
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor));
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
            else
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
