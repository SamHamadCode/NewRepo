using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Captcha
{
    /// <summary>
    /// Solves reCAPTCHA v2/v3 and hCaptcha challenges via the 2Captcha API.
    /// Docs: https://2captcha.com/api-docs
    /// </summary>
    public class CaptchaSolverService
    {
        private readonly ISettingsService _settings;
        private readonly ILogger<CaptchaSolverService> _logger;
        private readonly HttpClient _http;

        private const string SubmitUrl  = "https://2captcha.com/in.php";
        private const string ResultUrl  = "https://2captcha.com/res.php";
        private const int    PollMs     = 5000;
        private const int    MaxPolls   = 48; // 4 minutes max

        public CaptchaSolverService(
            ISettingsService settings,
            ILogger<CaptchaSolverService> logger)
        {
            _settings = settings;
            _logger   = logger;
            _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_settings.Current.TwoCaptchaApiKey);

        /// <summary>
        /// Solves a reCAPTCHA v2 challenge.
        /// </summary>
        public Task<string?> SolveRecaptchaV2Async(
            string siteKey, string pageUrl, CancellationToken ct = default)
            => SolveAsync("userrecaptcha", $"googlekey={Uri.EscapeDataString(siteKey)}&pageurl={Uri.EscapeDataString(pageUrl)}", ct);

        /// <summary>
        /// Solves a reCAPTCHA v3 challenge.
        /// </summary>
        public Task<string?> SolveRecaptchaV3Async(
            string siteKey, string pageUrl, string action = "verify", double minScore = 0.7, CancellationToken ct = default)
            => SolveAsync("userrecaptcha",
                $"googlekey={Uri.EscapeDataString(siteKey)}&pageurl={Uri.EscapeDataString(pageUrl)}&version=v3&action={Uri.EscapeDataString(action)}&min_score={minScore}", ct);

        /// <summary>
        /// Solves an hCaptcha challenge.
        /// </summary>
        public Task<string?> SolveHCaptchaAsync(
            string siteKey, string pageUrl, CancellationToken ct = default)
            => SolveAsync("hcaptcha", $"sitekey={Uri.EscapeDataString(siteKey)}&pageurl={Uri.EscapeDataString(pageUrl)}", ct);

        // ?? Core submit ? poll loop ??????????????????????????????????????

        private async Task<string?> SolveAsync(string method, string extraParams, CancellationToken ct)
        {
            var key = _settings.Current.TwoCaptchaApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogWarning("2Captcha API key not configured — skipping captcha solve");
                return null;
            }

            try
            {
                // Submit task
                var submitUri = $"{SubmitUrl}?key={key}&method={method}&{extraParams}&json=1";
                var submitResp = await _http.GetStringAsync(submitUri, ct);
                var submitJson = Newtonsoft.Json.Linq.JObject.Parse(submitResp);

                if (submitJson["status"]?.ToObject<int>() != 1)
                {
                    _logger.LogWarning("2Captcha submit failed: {Resp}", submitResp);
                    return null;
                }

                var taskId = submitJson["request"]?.ToString();
                _logger.LogInformation("2Captcha task submitted: {Id}", taskId);

                // Poll for result
                for (int i = 0; i < MaxPolls; i++)
                {
                    await Task.Delay(PollMs, ct);

                    var resultUri = $"{ResultUrl}?key={key}&action=get&id={taskId}&json=1";
                    var resultResp = await _http.GetStringAsync(resultUri, ct);
                    var resultJson = Newtonsoft.Json.Linq.JObject.Parse(resultResp);

                    var request = resultJson["request"]?.ToString();
                    if (request == "CAPCHA_NOT_READY") continue;

                    if (resultJson["status"]?.ToObject<int>() != 1)
                    {
                        _logger.LogWarning("2Captcha solve failed: {Resp}", resultResp);
                        return null;
                    }

                    _logger.LogInformation("2Captcha solved on poll {Poll}", i + 1);
                    return request;
                }

                _logger.LogWarning("2Captcha timed out after {Max} polls", MaxPolls);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "2Captcha exception");
                return null;
            }
        }
    }
}
