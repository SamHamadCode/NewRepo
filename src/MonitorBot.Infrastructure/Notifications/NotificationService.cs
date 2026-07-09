using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MonitorBot.Infrastructure.Notifications
{
    public class NotificationService : INotificationService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISettingsService _settings;
        private readonly ILogger<NotificationService> _logger;

        public event EventHandler<(string Title, string Message)>? DesktopNotificationRequested;

        public NotificationService(
            IHttpClientFactory httpClientFactory,
            ISettingsService settings,
            ILogger<NotificationService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _settings = settings;
            _logger = logger;
        }

        public async Task SendSuccessAsync(MonitorTask task, MonitorResult result)
        {
            var title = $"? {task.Name} — In Stock!";
            var body = BuildBody(task, result);

            await DispatchAsync(title, body, onSuccess: true);
        }

        public async Task SendFailureAsync(MonitorTask task, string reason)
        {
            var title = $"? {task.Name} — Failed";
            var body = $"Task failed after retries.\nReason: {reason}";

            await DispatchAsync(title, body, onSuccess: false);
        }

        public Task SendDesktopAsync(string title, string message)
        {
            RaiseDesktop(title, message);
            return Task.CompletedTask;
        }

        private async Task DispatchAsync(string title, string body, bool onSuccess)
        {
            var ns = _settings.Current.Notifications;

            if (ns.DesktopEnabled && (onSuccess ? ns.DesktopOnSuccess : ns.DesktopOnFailure))
                RaiseDesktop(title, body);

            foreach (var webhook in ns.Webhooks)
            {
                if (!webhook.IsEnabled) continue;
                if (onSuccess && !webhook.OnSuccess) continue;
                if (!onSuccess && !webhook.OnFailure) continue;

                try
                {
                    switch (webhook.Channel)
                    {
                        case NotificationChannel.Discord:
                            await SendDiscordAsync(webhook.Url, title, body);
                            break;
                        case NotificationChannel.Slack:
                            await SendSlackAsync(webhook.Url, title, body);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Webhook delivery failed: {Name}", webhook.Name);
                }
            }
        }

        private async Task SendDiscordAsync(string url, string title, string body)
        {
            using var client = _httpClientFactory.CreateClient();
            var payload = new JObject
            {
                ["embeds"] = new JArray
                {
                    new JObject
                    {
                        ["title"] = title,
                        ["description"] = body,
                        ["color"] = 5763719,
                        ["footer"] = new JObject { ["text"] = "MonitorBot" },
                        ["timestamp"] = DateTime.UtcNow.ToString("o")
                    }
                }
            };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            await client.PostAsync(url, content);
        }

        private async Task SendSlackAsync(string url, string title, string body)
        {
            using var client = _httpClientFactory.CreateClient();
            var payload = new JObject
            {
                ["text"] = $"*{title}*\n{body}"
            };
            var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            await client.PostAsync(url, content);
        }

        private static string BuildBody(MonitorTask task, MonitorResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Product: {result.Title ?? task.TargetUrl}");
            if (result.Price.HasValue) sb.AppendLine($"Price:   ${result.Price:F2}");
            sb.AppendLine($"URL:     {result.Url ?? task.TargetUrl}");
            return sb.ToString();
        }

        private void RaiseDesktop(string title, string message) =>
            DesktopNotificationRequested?.Invoke(this, (title, message));
    }
}
