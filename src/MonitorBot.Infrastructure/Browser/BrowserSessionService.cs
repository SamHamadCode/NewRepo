using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Interfaces;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Browser
{
    public class BrowserSessionService : IBrowserSessionService
    {
        private readonly ILogger<BrowserSessionService> _logger;
        private HttpClient? _client;
        private string? _debuggerUrl;

        public bool IsConnected => _debuggerUrl != null;

        public BrowserSessionService(ILogger<BrowserSessionService> logger)
        {
            _logger = logger;
        }

        public async Task<bool> ConnectAsync(string port, CancellationToken ct = default)
        {
            try
            {
                _client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await _client.GetStringAsync($"http://localhost:{port}/json/version", ct);
                var info = JObject.Parse(response);
                _debuggerUrl = info["webSocketDebuggerUrl"]?.ToString();
                _logger.LogInformation("Browser connected on port {Port}", port);
                return IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to browser on port {Port}", port);
                return false;
            }
        }

        public async Task<string?> GetCookiesAsync(string domain, CancellationToken ct = default)
        {
            if (!IsConnected) return null;
            try
            {
                var response = await _client!.GetStringAsync(
                    $"http://localhost:9222/json", ct);
                return response;
            }
            catch { return null; }
        }

        public void Disconnect()
        {
            _debuggerUrl = null;
            _client?.Dispose();
            _client = null;
        }
    }
}
