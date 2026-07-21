using System;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using MonitorBot.Infrastructure.Checkout;

namespace MonitorBot.Infrastructure.Monitoring
{
    public class MonitorService : IMonitorService
    {
        private readonly IProductChecker _checker;
        private readonly INotificationService _notifications;
        private readonly ICheckoutService _checkout;
        private readonly IProfileRepository _profiles;
        private readonly IAccountRepository _accounts;
        private readonly ITaskRepository _tasks;
        private readonly ILogger<MonitorService> _logger;
        private readonly ILogStore _logStore;
        private readonly TargetLoginService _targetLogin;
        private readonly WalmartLoginService _walmartLogin;

        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTasks = new();

        public event EventHandler<MonitorResult>? ResultReceived;
        public event EventHandler<MonitorTask>? TaskStatusChanged;
        public event EventHandler<CheckoutResult>? CheckoutCompleted;
        public event EventHandler<MonitorTask>? ReHarvestRequested;

        public MonitorService(
            IProductChecker checker,
            INotificationService notifications,
            ICheckoutService checkout,
            IProfileRepository profiles,
            IAccountRepository accounts,
            ITaskRepository tasks,
            ILogger<MonitorService> logger,
            ILogStore logStore,
            TargetLoginService targetLogin,
            WalmartLoginService walmartLogin)
        {
            _checker = checker;
            _notifications = notifications;
            _checkout = checkout;
            _profiles = profiles;
            _accounts = accounts;
            _tasks = tasks;
            _logger = logger;
            _logStore = logStore;
            _targetLogin = targetLogin;
            _walmartLogin = walmartLogin;
        }

        public bool IsRunning(Guid taskId) => _activeTasks.ContainsKey(taskId);

        public Task StartTaskAsync(MonitorTask task, CancellationToken ct = default)
        {
            if (_activeTasks.ContainsKey(task.Id)) return Task.CompletedTask;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeTasks[task.Id] = cts;

            _ = Task.Run(() => RunLoopAsync(task, cts.Token), cts.Token);
            return Task.CompletedTask;
        }

        public Task StopTaskAsync(Guid taskId)
        {
            if (_activeTasks.TryRemove(taskId, out var cts))
                cts.Cancel();
            return Task.CompletedTask;
        }

        public Task StopAllAsync()
        {
            foreach (var pair in _activeTasks)
                pair.Value.Cancel();
            _activeTasks.Clear();
            return Task.CompletedTask;
        }

        private async Task RunLoopAsync(MonitorTask task, CancellationToken ct)
        {
            UpdateStatus(task, MonitorTaskStatus.Running);
            _logger.LogInformation("Task {Name} started", task.Name);

            while (!ct.IsCancellationRequested)
            {
                task.LastChecked = DateTime.UtcNow;
                task.NextCheck = task.LastChecked.Value.AddSeconds(task.IntervalSeconds);

                MonitorResult? result = null;
                int attempt = 0;

                while (attempt <= task.MaxRetries && !ct.IsCancellationRequested)
                {
                    if (attempt > 0)
                    {
                        UpdateStatus(task, MonitorTaskStatus.Retrying);
                        await Task.Delay(task.RetryDelaySeconds * 1000, ct);
                    }

                    try { result = await _checker.CheckAsync(task, ct); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Attempt {Attempt} for task {Id} failed", attempt + 1, task.Id);
                    }

                    if (result?.IsSuccess == true) break;
                    attempt++;
                }

                if (result != null)
                {
                    ResultReceived?.Invoke(this, result);
                    task.LastResult = result.IsAvailable ? "In Stock" : "Out of Stock";
                    LogResult(task, result);

                    if (result.IsSuccess && result.IsAvailable)
                    {
                        UpdateStatus(task, MonitorTaskStatus.Success);
                        await _notifications.SendSuccessAsync(task, result);

                        // ?? Auto Checkout ????????????????????????????????
                        if (task.AutoCheckout)
                        {
                            await RunCheckoutAsync(task, result, ct);

                            // Only stop if checkout succeeded — otherwise keep monitoring
                            if (task.CheckoutStatus == CheckoutStatus.Success)
                            {
                                await StopTaskAsync(task.Id);
                                return;
                            }

                            // Checkout failed — reset and keep looping
                            task.RetryCount = 0;
                            UpdateStatus(task, MonitorTaskStatus.Running);
                        }
                        else
                        {
                            // No auto checkout — stop once in stock, user handles manually
                            await StopTaskAsync(task.Id);
                            return;
                        }
                    }
                    else if (!result.IsSuccess)
                    {
                        task.RetryCount++;
                        UpdateStatus(task, MonitorTaskStatus.Failed);
                        if (task.RetryCount >= task.MaxRetries)
                            await _notifications.SendFailureAsync(task, result.ErrorMessage ?? "Unknown error");
                    }
                    else
                    {
                        UpdateStatus(task, MonitorTaskStatus.Running);
                    }
                }

                try { await Task.Delay(task.IntervalSeconds * 1000, ct); }
                catch (OperationCanceledException) { break; }
            }

            UpdateStatus(task, MonitorTaskStatus.Stopped);
            _activeTasks.TryRemove(task.Id, out _);
            _logger.LogInformation("Task {Name} stopped", task.Name);
        }

        private async Task RunCheckoutAsync(MonitorTask task, MonitorResult monitorResult, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(task.ProfileId))
            {
                task.CheckoutError = "No profile assigned to task.";
                task.CheckoutStatus = CheckoutStatus.Failed;
                LogCheckout(task, null);
                return;
            }

            var profile = await _profiles.GetByIdAsync(task.ProfileId);
            if (profile == null)
            {
                task.CheckoutError = $"Profile '{task.ProfileId}' not found.";
                task.CheckoutStatus = CheckoutStatus.Failed;
                LogCheckout(task, null);
                return;
            }

            _logger.LogInformation("Checkout starting for task {Name} with profile {Profile}",
                task.Name, profile.Name);

            UpdateStatus(task, MonitorTaskStatus.CheckingOut);

            // Load account if assigned
            SiteAccount? account = null;
            if (!string.IsNullOrEmpty(task.AccountId))
            {
                account = await _accounts.GetByIdAsync(task.AccountId);
                if (account != null)
                    _logger.LogInformation("Using account {Name} ({Email}) for checkout",
                        account.Name, account.Email);
            }

            var checkoutResult = await _checkout.CheckoutAsync(
                task, profile, account, monitorResult,
                phase => UpdateStatus(task, phase),
                ct);

            task.CheckoutStatus = checkoutResult.Status;
            task.LastOrderId    = checkoutResult.OrderId;
            task.CheckoutError  = checkoutResult.ErrorMessage;

            CheckoutCompleted?.Invoke(this, checkoutResult);
            LogCheckout(task, checkoutResult);

            if (checkoutResult.NeedsReHarvest)
            {
                // Try silent automatic re-login if an account is assigned
                if (account != null)
                {
                    LogInfo(task, "Cookies expired — attempting silent re-login");
                    IAccountLoginService loginSvc = task.TargetUrl.ToLowerInvariant().Contains("walmart")
                        ? _walmartLogin
                        : _targetLogin;

                    var freshCookies = await loginSvc.LoginAsync(account, ct);
                    if (!string.IsNullOrEmpty(freshCookies))
                    {
                        task.CookieOverride = freshCookies;
                        await _tasks.SaveAsync(task);
                        LogInfo(task, "Silent re-login succeeded — retrying checkout");

                        // Retry checkout once with fresh cookies
                        var retryResult = await _checkout.CheckoutAsync(
                            task, profile!, account, monitorResult,
                            phase => UpdateStatus(task, phase), ct);

                        task.CheckoutStatus = retryResult.Status;
                        task.LastOrderId    = retryResult.OrderId;
                        task.CheckoutError  = retryResult.ErrorMessage;
                        CheckoutCompleted?.Invoke(this, retryResult);
                        LogCheckout(task, retryResult);

                        if (retryResult.IsSuccess)
                        {
                            task.LastResult = $"Ordered! #{retryResult.OrderId}";
                            await _notifications.SendDesktopAsync("? Order Placed!",
                                $"{task.Name}\nOrder ID: {retryResult.OrderId}");
                        }
                        return;
                    }
                    LogWarn(task, "Silent re-login failed — falling back to manual harvest");
                }
                else
                {
                    LogWarn(task, "Cookies expired and no account assigned — requesting manual re-harvest");
                }

                ReHarvestRequested?.Invoke(this, task);
                return;
            }

            if (checkoutResult.IsSuccess)
            {
                task.LastResult = $"Ordered! #{checkoutResult.OrderId}";
                await _notifications.SendDesktopAsync(
                    "? Order Placed!",
                    $"{task.Name}\nOrder ID: {checkoutResult.OrderId}");
            }
            else
            {
                task.LastResult = $"Checkout failed: {checkoutResult.Status}";
                await _notifications.SendDesktopAsync(
                    "? Checkout Failed",
                    $"{task.Name}\n{checkoutResult.ErrorMessage}");
            }

            TaskStatusChanged?.Invoke(this, task);
        }

        private void UpdateStatus(MonitorTask task, MonitorTaskStatus status)
        {
            task.Status = status;
            TaskStatusChanged?.Invoke(this, task);
        }

        private void LogResult(MonitorTask task, MonitorResult result)
        {
            _logStore.Add(new LogEntry
            {
                Level    = result.IsSuccess ? "INFO" : "WARN",
                Category = "Monitor",
                Message  = $"[{task.Name}] {(result.IsAvailable ? "AVAILABLE" : "Unavailable")} — {result.Title ?? result.Url}",
                TaskId   = task.Id.ToString()
            });
        }

        private void LogCheckout(MonitorTask task, CheckoutResult? cr)
        {
            _logStore.Add(new LogEntry
            {
                Level    = cr?.IsSuccess == true ? "INFO" : "WARN",
                Category = "Checkout",
                Message  = cr?.IsSuccess == true
                    ? $"[{task.Name}] Order placed — #{cr.OrderId}"
                    : $"[{task.Name}] Checkout failed — {cr?.Status}: {cr?.ErrorMessage ?? task.CheckoutError}",
                TaskId   = task.Id.ToString()
            });
        }

        private void LogInfo(MonitorTask task, string message) =>
            _logStore.Add(new LogEntry { Level = "INFO", Category = "Checkout", Message = $"[{task.Name}] {message}", TaskId = task.Id.ToString() });

        private void LogWarn(MonitorTask task, string message) =>
            _logStore.Add(new LogEntry { Level = "WARN", Category = "Checkout", Message = $"[{task.Name}] {message}", TaskId = task.Id.ToString() });
    }
}
