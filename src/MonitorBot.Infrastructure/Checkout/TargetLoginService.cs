using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using MonitorBot.Infrastructure.Email;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Checkout
{
    public class TargetLoginService : IAccountLoginService
    {
        private readonly ILogger<TargetLoginService> _logger;
        private readonly EmailVerificationService _emailVerification;

        private const string GuestTokenUrl =
            "https://gsp.target.com/v1/guest_tokens?client_id=ecom-web-1.0.0&channel=WEB&page=%2F";
        private const string LoginUrl =
            "https://account.target.com/accounts/v4/login";
        private const string VerifyUrl =
            "https://account.target.com/accounts/v4/verify_code";

        public TargetLoginService(
            ILogger<TargetLoginService> logger,
            EmailVerificationService emailVerification)
        {
            _logger = logger;
            _emailVerification = emailVerification;
        }

        public async Task<string?> LoginAsync(SiteAccount account, CancellationToken ct = default)
        {
            _logger.LogInformation("Logging into Target account: {Email}", account.Email);

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
                // ?? Step 1: Get guest token ????????????????????????????
                var guestResp = await client.GetAsync(GuestTokenUrl, ct);
                if (!guestResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Target guest token failed: {Status}", (int)guestResp.StatusCode);
                    return null;
                }

                var guestJson = JObject.Parse(await guestResp.Content.ReadAsStringAsync(ct));
                var accessToken = guestJson["access_token"]?.ToString();

                // ?? Step 2: Submit credentials ?????????????????????????
                var loginPayload = new JObject
                {
                    ["username"]          = account.Email,
                    ["password"]          = account.Password,
                    ["keep_me_signed_in"] = true
                };

                using var loginReq = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
                {
                    Content = new StringContent(loginPayload.ToString(), Encoding.UTF8, "application/json")
                };
                loginReq.Headers.TryAddWithoutValidation("X-Api-Key", "ff457966e64d5e877fdbad070f276d18ecec4a01");
                if (!string.IsNullOrEmpty(accessToken))
                    loginReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

                // Record time just before login so email search doesn't pick up old emails
                var loginTime = DateTime.UtcNow.AddSeconds(-5);

                var loginResp = await client.SendAsync(loginReq, ct);
                var loginBody = await loginResp.Content.ReadAsStringAsync(ct);
                var loginJson = JObject.Parse(loginBody);

                // ?? Step 3: Handle verification code challenge ?????????
                // Target returns 401 with "OTP" or "VERIFY" type when it needs a code
                if (loginResp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || loginJson["type"]?.ToString()?.ToUpperInvariant() == "OTP"
                    || loginJson["code"]?.ToString()?.ToUpperInvariant().Contains("VERIFY") == true)
                {
                    _logger.LogInformation("Target requires email verification code");

                    if (!account.UseEmailVerification)
                    {
                        _logger.LogWarning("Email verification required but not configured on account");
                        return null;
                    }

                    // Wait for the verification email and extract the code
                    var otpCode = await _emailVerification.WaitForCodeAsync(
                        account,
                        senderDomain: "target.com",
                        sentAfter: loginTime,
                        timeoutSeconds: 120,
                        ct: ct);

                    if (string.IsNullOrEmpty(otpCode))
                    {
                        _logger.LogWarning("Could not retrieve verification code from email");
                        return null;
                    }

                    // Submit the verification code
                    var verifyPayload = new JObject
                    {
                        ["code"]     = otpCode,
                        ["username"] = account.Email
                    };

                    // The challenge response may include a token needed for verification
                    var challengeToken = loginJson["access_token"]?.ToString()
                                      ?? loginJson["challenge_token"]?.ToString()
                                      ?? accessToken;

                    using var verifyReq = new HttpRequestMessage(HttpMethod.Post, VerifyUrl)
                    {
                        Content = new StringContent(verifyPayload.ToString(), Encoding.UTF8, "application/json")
                    };
                    verifyReq.Headers.TryAddWithoutValidation("X-Api-Key", "ff457966e64d5e877fdbad070f276d18ecec4a01");
                    if (!string.IsNullOrEmpty(challengeToken))
                        verifyReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {challengeToken}");

                    var verifyResp = await client.SendAsync(verifyReq, ct);
                    var verifyBody = await verifyResp.Content.ReadAsStringAsync(ct);

                    if (!verifyResp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Verification code submission failed: {Status}", (int)verifyResp.StatusCode);
                        return null;
                    }

                    loginJson = JObject.Parse(verifyBody);
                    _logger.LogInformation("Verification code accepted");
                }
                else if (!loginResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Target login failed: {Status}", (int)loginResp.StatusCode);
                    return null;
                }

                // ?? Step 4: Extract session token ??????????????????????
                var sessionToken = loginJson["access_token"]?.ToString()
                                ?? loginJson["token"]?.ToString();

                if (string.IsNullOrEmpty(sessionToken))
                {
                    _logger.LogWarning("Target login: no token in response");
                    return null;
                }

                _logger.LogInformation("Target login successful for {Email}", account.Email);

                var cookies = cookieContainer.GetCookieHeader(new Uri("https://www.target.com"));
                return $"{cookies}; target_access_token={sessionToken}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Target login exception for {Email}", account.Email);
                return null;
            }
        }

        private static void AddBaseHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.target.com");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.target.com/");
        }
    }
}
