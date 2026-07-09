using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using MonitorBot.Infrastructure.Email;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Checkout
{
    /// <summary>
    /// Logs into a Walmart account.
    /// Flow:
    ///   1. GET  login page          ? scrape CSRF / state token
    ///   2. POST /account/electrode/api/login ? exchange email+password for session
    ///   3. If OTP challenge detected, poll inbox via IMAP and submit code
    ///   4. Return cookie header string for use in checkout requests
    /// </summary>
    public class WalmartLoginService : IAccountLoginService
    {
        private readonly ILogger<WalmartLoginService> _logger;
        private readonly EmailVerificationService _emailVerification;

        private const string LoginPageUrl = "https://www.walmart.com/account/login";
        private const string LoginApiUrl  = "https://www.walmart.com/account/electrode/api/login";
        private const string OtpApiUrl    = "https://www.walmart.com/account/electrode/api/account/2sv/verify";

        private static readonly Regex CsrfRegex = new(
            @"""csrfToken""\s*:\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public WalmartLoginService(
            ILogger<WalmartLoginService> logger,
            EmailVerificationService emailVerification)
        {
            _logger = logger;
            _emailVerification = emailVerification;
        }

        public async Task<string?> LoginAsync(SiteAccount account, CancellationToken ct = default)
        {
            _logger.LogInformation("Logging into Walmart account: {Email}", account.Email);

            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            AddBaseHeaders(client);

            try
            {
                // ?? Step 1: Load login page to obtain CSRF token ??????????
                var loginPage = await client.GetStringAsync(LoginPageUrl, ct);
                var csrfMatch = CsrfRegex.Match(loginPage);
                var csrfToken = csrfMatch.Success ? csrfMatch.Groups[1].Value : string.Empty;

                if (string.IsNullOrEmpty(csrfToken))
                    _logger.LogDebug("Walmart CSRF token not found — proceeding without it");

                // ?? Step 2: POST credentials ??????????????????????????????
                var loginPayload = new JObject
                {
                    ["username"] = account.Email,
                    ["password"] = account.Password,
                    ["rememberme"] = true
                };

                using var loginReq = new HttpRequestMessage(HttpMethod.Post, LoginApiUrl)
                {
                    Content = new StringContent(loginPayload.ToString(), Encoding.UTF8, "application/json")
                };
                loginReq.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (!string.IsNullOrEmpty(csrfToken))
                    loginReq.Headers.TryAddWithoutValidation("WM_QOS.CORRELATION_ID", csrfToken);

                var loginResp = await client.SendAsync(loginReq, ct);
                var loginBody = await loginResp.Content.ReadAsStringAsync(ct);

                JObject? loginJson = null;
                try { loginJson = JObject.Parse(loginBody); }
                catch { /* non-JSON response handled below */ }

                // ?? Step 3: Detect OTP challenge ??????????????????????????
                var needsOtp = false;
                if (!loginResp.IsSuccessStatusCode)
                {
                    needsOtp = loginBody.Contains("2sv", StringComparison.OrdinalIgnoreCase)
                            || loginBody.Contains("verification", StringComparison.OrdinalIgnoreCase)
                            || (int)loginResp.StatusCode == 401;
                }
                else if (loginJson != null)
                {
                    var flowType = loginJson["payload"]?["authFlowType"]?.ToString()
                                ?? loginJson["authFlowType"]?.ToString()
                                ?? string.Empty;
                    needsOtp = flowType.Contains("2SV", StringComparison.OrdinalIgnoreCase)
                            || flowType.Contains("OTP", StringComparison.OrdinalIgnoreCase)
                            || loginJson["payload"]?["twoStepVerification"] != null;
                }

                if (needsOtp)
                {
                    _logger.LogInformation("Walmart OTP challenge detected for {Email}", account.Email);

                    if (!account.UseEmailVerification)
                    {
                        _logger.LogWarning("OTP required but email verification is not configured for this account");
                        return null;
                    }

                    var sentAfter = DateTime.UtcNow.AddSeconds(-30);
                    var code = await _emailVerification.WaitForCodeAsync(
                        account,
                        senderDomain: "walmart.com",
                        sentAfter: sentAfter,
                        timeoutSeconds: 120,
                        ct: ct);

                    if (string.IsNullOrEmpty(code))
                    {
                        _logger.LogWarning("Timed out waiting for Walmart OTP code");
                        return null;
                    }

                    _logger.LogInformation("Submitting Walmart OTP code: {Code}", code);

                    var otpPayload = new JObject
                    {
                        ["code"]       = code,
                        ["rememberMe"] = true
                    };

                    using var otpReq = new HttpRequestMessage(HttpMethod.Post, OtpApiUrl)
                    {
                        Content = new StringContent(otpPayload.ToString(), Encoding.UTF8, "application/json")
                    };
                    otpReq.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (!string.IsNullOrEmpty(csrfToken))
                        otpReq.Headers.TryAddWithoutValidation("WM_QOS.CORRELATION_ID", csrfToken);

                    var otpResp = await client.SendAsync(otpReq, ct);
                    if (!otpResp.IsSuccessStatusCode)
                    {
                        var otpBody = await otpResp.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning("Walmart OTP verify failed: {Status} — {Body}",
                            (int)otpResp.StatusCode, otpBody.Length > 200 ? otpBody[..200] : otpBody);
                        return null;
                    }

                    _logger.LogInformation("Walmart OTP verified for {Email}", account.Email);
                }
                else if (!loginResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Walmart login failed: {Status} — {Body}",
                        (int)loginResp.StatusCode,
                        loginBody.Length > 200 ? loginBody[..200] : loginBody);
                    return null;
                }

                // ?? Step 4: Collect session cookies ???????????????????????
                var cookies = cookieContainer.GetCookieHeader(new Uri("https://www.walmart.com"));
                if (string.IsNullOrEmpty(cookies))
                {
                    _logger.LogWarning("Walmart login: no cookies returned — session may be invalid");
                    return null;
                }

                _logger.LogInformation("Walmart login successful for {Email}", account.Email);
                return cookies;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Walmart login exception for {Email}", account.Email);
                return null;
            }
        }

        private static void AddBaseHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.walmart.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.walmart.com/");
        }
    }
}
