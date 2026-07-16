using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitorBot.App.ViewModels;
using MonitorBot.App.Views;
using MonitorBot.Core.Interfaces;
using MonitorBot.Infrastructure.Checkout;
using MonitorBot.Infrastructure.Email;
using MonitorBot.Infrastructure.IO;
using MonitorBot.Infrastructure.Logging;using MonitorBot.Infrastructure.Monitoring;
using MonitorBot.Infrastructure.Notifications;
using MonitorBot.Infrastructure.Persistence;
using MonitorBot.Infrastructure.Updates;

namespace MonitorBot.App
{
    public partial class App : Application
    {
        private IServiceProvider? _services;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Catch any unhandled exception and show a message box before crashing
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var msg = ex.ExceptionObject?.ToString() ?? "Unknown error";
                MessageBox.Show(msg, "MonitorBot — Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(ex.Exception?.ToString() ?? "Unknown error",
                    "MonitorBot — UI Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            base.OnStartup(e);

            try
            {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MonitorBot");

            var services = new ServiceCollection();

            // Logging
            services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

            // HTTP — realistic browser client with cookies, gzip, redirects
            services.AddHttpClient("monitor")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip
                                           | DecompressionMethods.Deflate
                                           | DecompressionMethods.Brotli,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    UseCookies = true,
                    CookieContainer = new System.Net.CookieContainer(),
                    UseProxy = false,
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                })
                .ConfigureHttpClient(c =>
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/124.0.0.0 Safari/537.36");
                    c.Timeout = TimeSpan.FromSeconds(20);
                });
            services.AddHttpClient();

            // Infrastructure
            services.AddSingleton<ILogStore>(_ => new FileLogStore(dataDir));
            services.AddSingleton<ISettingsService>(_ => new SettingsService(dataDir));
            services.AddSingleton<ITaskRepository>(_ => new TaskRepository(dataDir));
            services.AddSingleton<ITaskGroupRepository>(_ => new TaskGroupRepository(dataDir));
            services.AddSingleton<IProfileRepository>(_ => new ProfileRepository(dataDir));
            services.AddSingleton<IAccountRepository>(_ => new AccountRepository(dataDir));
            services.AddSingleton<IProxyRepository>(_ => new ProxyRepository(dataDir));
            services.AddSingleton<IProductChecker, HttpProductChecker>();
            services.AddSingleton<HttpStockChecker>();
            services.AddSingleton<EmailVerificationService>();
            services.AddSingleton<TargetLoginService>();
            services.AddSingleton<WalmartLoginService>();
            services.AddSingleton<WalmartCheckoutService>();
            // TargetBrowserCheckout must be created on the UI thread — use a factory
            services.AddSingleton<ITargetBrowserCheckout>(p =>
            {
                var logStore = p.GetRequiredService<ILogStore>();
                TargetBrowserCheckout? instance = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    instance = new MonitorBot.App.Views.TargetBrowserCheckout(logStore);
                });
                return instance!;
            });
            services.AddSingleton<TargetCheckoutService>();
            services.AddSingleton<ICheckoutService, CheckoutRouter>();
            services.AddSingleton<NotificationService>();
            services.AddSingleton<INotificationService>(p => p.GetRequiredService<NotificationService>());
            services.AddSingleton<IMonitorService, MonitorService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IImportExportService, ImportExportService>();

            // ViewModels
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<TasksViewModel>(p => new TasksViewModel(
                p.GetRequiredService<IMonitorService>(),
                p.GetRequiredService<ITaskRepository>(),
                p.GetRequiredService<ITaskGroupRepository>(),
                p.GetRequiredService<IProfileRepository>(),
                p.GetRequiredService<IAccountRepository>()));
            services.AddSingleton<ProfilesViewModel>();
            services.AddSingleton<AccountsViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<LogsViewModel>();
            services.AddSingleton<MainViewModel>();

            // Window
            services.AddSingleton<MainWindow>();

            _services = services.BuildServiceProvider();

            // Load settings first
            await _services.GetRequiredService<ISettingsService>().LoadAsync();

            // Wire desktop notification → toast
            var notifSvc = _services.GetRequiredService<NotificationService>();
            notifSvc.DesktopNotificationRequested += (_, args) =>
                ShowToast(args.Title, args.Message);

            var window = _services.GetRequiredService<MainWindow>();
            window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "MonitorBot — Failed to Start",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_services != null)
            {
                await _services.GetRequiredService<IMonitorService>().StopAllAsync();
                await _services.GetRequiredService<ILogStore>().FlushAsync();
            }
            base.OnExit(e);
        }

        private static void ShowToast(string title, string message)
        {
            // Non-blocking — runs on UI thread but does not pause the monitoring loop
            Current.Dispatcher.InvokeAsync(() =>
            {
                var toast = new ToastWindow(title, message);
                toast.Show();
            });
        }
    }
}
