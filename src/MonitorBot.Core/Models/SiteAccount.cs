using System;

namespace MonitorBot.Core.Models
{
    public class SiteAccount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;

        // Site login credentials
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? TwoFactorSecret { get; set; }
        public bool IsActive { get; set; } = true;

        // IMAP config for auto-fetching verification codes
        public bool UseEmailVerification { get; set; } = false;
        public string ImapHost { get; set; } = string.Empty;
        public int ImapPort { get; set; } = 993;
        public bool ImapUseSsl { get; set; } = true;
        public string ImapEmail { get; set; } = string.Empty;
        public string ImapPassword { get; set; } = string.Empty;

        public System.DateTime CreatedAt { get; set; } = System.DateTime.UtcNow;
    }
}
