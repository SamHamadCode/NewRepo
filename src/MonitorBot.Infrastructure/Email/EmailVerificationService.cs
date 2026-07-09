using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Email
{
    public class EmailVerificationService
    {
        private readonly ILogger<EmailVerificationService> _logger;

        // Matches 4-8 digit codes — covers most site verification emails
        private static readonly Regex CodeRegex = new(
            @"\b([0-9]{4,8})\b", RegexOptions.Compiled);

        public EmailVerificationService(ILogger<EmailVerificationService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Polls the inbox via IMAP for up to <paramref name="timeoutSeconds"/> seconds,
        /// looking for an email from <paramref name="senderDomain"/> received after
        /// <paramref name="sentAfter"/>. Extracts and returns the numeric verification code.
        /// </summary>
        public async Task<string?> WaitForCodeAsync(
            SiteAccount account,
            string senderDomain,
            DateTime sentAfter,
            int timeoutSeconds = 120,
            CancellationToken ct = default)
        {
            _logger.LogInformation(
                "Waiting for verification email from @{Domain} (timeout {Timeout}s)",
                senderDomain, timeoutSeconds);

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    var code = await FetchCodeAsync(account, senderDomain, sentAfter, ct);
                    if (!string.IsNullOrEmpty(code))
                    {
                        _logger.LogInformation("Verification code found: {Code}", code);
                        return code;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IMAP poll error — retrying");
                }

                // Poll every 5 seconds
                await Task.Delay(5000, ct);
            }

            _logger.LogWarning("Timed out waiting for verification email from @{Domain}", senderDomain);
            return null;
        }

        private async Task<string?> FetchCodeAsync(
            SiteAccount account,
            string senderDomain,
            DateTime sentAfter,
            CancellationToken ct)
        {
            using var client = new ImapClient();

            await client.ConnectAsync(
                account.ImapHost,
                account.ImapPort,
                account.ImapUseSsl
                    ? MailKit.Security.SecureSocketOptions.SslOnConnect
                    : MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable,
                ct);

            var imapEmail = string.IsNullOrWhiteSpace(account.ImapEmail)
                ? account.Email : account.ImapEmail;
            var imapPass = string.IsNullOrWhiteSpace(account.ImapPassword)
                ? account.Password : account.ImapPassword;

            await client.AuthenticateAsync(imapEmail, imapPass, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            // Search for unread messages from the sender domain since sentAfter
            var query = SearchQuery.DeliveredAfter(sentAfter)
                .And(SearchQuery.FromContains(senderDomain));

            var uids = await inbox.SearchAsync(query, ct);

            foreach (var uid in uids.Reverse())
            {
                var msg = await inbox.GetMessageAsync(uid, ct);
                var code = ExtractCode(msg);
                if (!string.IsNullOrEmpty(code))
                {
                    await client.DisconnectAsync(true, ct);
                    return code;
                }
            }

            await client.DisconnectAsync(true, ct);
            return null;
        }

        private string? ExtractCode(MimeMessage message)
        {
            // Try HTML body first, then plain text
            var body = message.HtmlBody ?? message.TextBody ?? string.Empty;

            // Strip HTML tags for cleaner matching
            var plain = Regex.Replace(body, @"<[^>]+>", " ");

            // Look for common patterns first
            // "Your verification code is: 123456"
            // "Use code 123456 to verify"
            // "Enter code: 123456"
            var contextPattern = new Regex(
                @"(?:code|verify|verification|otp|one.time|confirm)[^0-9]{0,30}([0-9]{4,8})",
                RegexOptions.IgnoreCase);

            var m = contextPattern.Match(plain);
            if (m.Success) return m.Groups[1].Value;

            // Subject line fallback
            m = contextPattern.Match(message.Subject ?? "");
            if (m.Success) return m.Groups[1].Value;

            // Generic digit sequence fallback
            m = CodeRegex.Match(plain);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
