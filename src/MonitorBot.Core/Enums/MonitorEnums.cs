namespace MonitorBot.Core.Enums
{
    public enum MonitorType
    {
        ProductUrl,
        Sku,
        Keyword
    }

    public enum DetectionMode
    {
        Availability,
        PriceThreshold,
        Both
    }

    public enum NotificationChannel
    {
        Discord,
        Slack,
        Desktop
    }

    public enum ProxyType
    {
        None,
        Http,
        Socks5
    }
}
