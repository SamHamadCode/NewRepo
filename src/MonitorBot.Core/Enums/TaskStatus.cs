namespace MonitorBot.Core.Enums
{
    public enum MonitorTaskStatus
    {
        Idle,
        Running,
        Stopped,
        Success,
        Failed,
        Retrying,
        Scheduled,
        CheckingOut,
        LoggingIn,
        AddingToCart,
        PlacingOrder
    }
}
