namespace MonitorBot.Core.Models
{
    public class AppSettings
    {
        public string LicenseKey { get; set; } = string.Empty;
        public string Theme { get; set; } = "Dark";
        public int DefaultIntervalSeconds { get; set; } = 30;
        public int DefaultMaxRetries { get; set; } = 3;
        public int MaxConcurrentTasks { get; set; } = 10;
        public bool AutoStartTasks { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool LaunchOnStartup { get; set; } = false;
        public bool AutoUpdate { get; set; } = true;
        public string UpdateChannel { get; set; } = "stable";
        public NotificationSettings Notifications { get; set; } = new();
        public string DefaultProxyId { get; set; } = string.Empty;
        public bool UseBrowserSession { get; set; } = false;
        public string BrowserExtensionPort { get; set; } = "9222";

        // Captcha solving
        public string TwoCaptchaApiKey { get; set; } = string.Empty;

        public string LogLevel { get; set; } = "Information";
        public int LogRetentionDays { get; set; } = 7;
        public string LastVersion { get; set; } = string.Empty;
    }
}
