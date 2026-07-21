using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Harvest
{
    /// <summary>
    /// Lightweight HTTP server that listens on localhost:52384 for harvest POSTs
    /// from the MonitorBot browser extension. When a harvest arrives it broadcasts
    /// the cookies + bearer token to all registered task handlers.
    /// </summary>
    public class HarvestListenerService : IDisposable
    {
        public const int Port = 52384;

        private readonly ILogger<HarvestListenerService> _logger;
        private readonly ILogStore _logStore;
        private readonly ITaskRepository _taskRepo;

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;

        public event EventHandler<HarvestPayload>? HarvestReceived;

        public HarvestListenerService(
            ILogger<HarvestListenerService> logger,
            ILogStore logStore,
            ITaskRepository taskRepo)
        {
            _logger  = logger;
            _logStore = logStore;
            _taskRepo = taskRepo;
        }

        public void Start()
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = ListenLoopAsync(_cts.Token);
            _logger.LogInformation("HarvestListenerService started on port {Port}", Port);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener!.GetContextAsync();
                    _ = HandleAsync(ctx);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "HarvestListener accept error");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (ctx.Request.HttpMethod == "POST" &&
                ctx.Request.Url?.AbsolutePath == "/harvest")
            {
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var j    = JObject.Parse(body);

                    var payload = new HarvestPayload
                    {
                        Site        = j["site"]?.ToString() ?? "",
                        Cookies     = j["cookies"]?.ToString() ?? "",
                        Bearer      = j["bearer"]?.ToString(),
                        CookieCount = j["cookieCount"]?.Value<int>() ?? 0,
                        Timestamp   = DateTimeOffset.UtcNow
                    };

                    _logStore.Add(new LogEntry
                    {
                        Level    = "INFO",
                        Category = "Harvester",
                        Message  = $"[Extension] Harvest received — {payload.CookieCount} cookies" +
                                   (payload.Bearer != null ? $" + Bearer (len={payload.Bearer.Length})" : "") +
                                   $" from {payload.Site}"
                    });

                    HarvestReceived?.Invoke(this, payload);

                    // Auto-apply to all tasks matching the site that have AutoCheckout enabled
                    await ApplyToTasksAsync(payload);

                    ctx.Response.StatusCode = 200;
                    var resp = Encoding.UTF8.GetBytes("{\"ok\":true}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = resp.Length;
                    await ctx.Response.OutputStream.WriteAsync(resp, 0, resp.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error handling harvest POST");
                    ctx.Response.StatusCode = 500;
                }
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }

            ctx.Response.Close();
        }

        private async Task ApplyToTasksAsync(HarvestPayload payload)
        {
            try
            {
                var tasks = await _taskRepo.GetAllAsync();
                int updated = 0;
                foreach (var t in tasks)
                {
                    // Match by site domain
                    if (string.IsNullOrEmpty(t.TargetUrl)) continue;
                    if (!t.TargetUrl.Contains(payload.Site, StringComparison.OrdinalIgnoreCase)) continue;

                    t.CookieOverride = payload.Cookies;
                    if (!string.IsNullOrEmpty(payload.Bearer))
                        t.BearerOverride = payload.Bearer;

                    await _taskRepo.SaveAsync(t);
                    updated++;
                }

                if (updated > 0)
                    _logStore.Add(new LogEntry
                    {
                        Level    = "INFO",
                        Category = "Harvester",
                        Message  = $"[Extension] Auto-applied harvest to {updated} task(s)"
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ApplyToTasksAsync failed");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        public void Dispose() => Stop();
    }

    public class HarvestPayload
    {
        public string Site        { get; set; } = "";
        public string Cookies     { get; set; } = "";
        public string? Bearer     { get; set; }
        public int CookieCount    { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}
