using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitorBot.App.ViewModels;
using MonitorBot.Core.Interfaces;
using MonitorBot.Infrastructure.Browser;
using MonitorBot.Infrastructure.Captcha;
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
            base.OnStartup(e);

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
            services.AddSingleton<IProfileRepository>(_ => new ProfileRepository(dataDir));
            services.AddSingleton<IAccountRepository>(_ => new AccountRepository(dataDir));
            services.AddSingleton<IProxyRepository>(_ => new ProxyRepository(dataDir));
            services.AddSingleton<IProductChecker, HttpProductChecker>();
            services.AddSingleton<EmailVerificationService>();
            services.AddSingleton<CaptchaSolverService>();
            services.AddSingleton<TargetLoginService>();
            services.AddSingleton<WalmartLoginService>();
            services.AddSingleton<WalmartCheckoutService>();
            services.AddSingleton<TargetCheckoutService>();
            services.AddSingleton<PlaywrightCheckoutService>();
            services.AddSingleton<ICheckoutService, CheckoutRouter>();
            services.AddSingleton<NotificationService>();
            services.AddSingleton<INotificationService>(p => p.GetRequiredService<NotificationService>());
            services.AddSingleton<IMonitorService, MonitorService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IBrowserSessionService, BrowserSessionService>();
            services.AddSingleton<IImportExportService, ImportExportService>();

            // ViewModels
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<TasksViewModel>(p => new TasksViewModel(
                p.GetRequiredService<IMonitorService>(),
                p.GetRequiredService<ITaskRepository>(),
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
            // Strip emoji/non-ASCII so MessageBox title renders correctly
            var cleanTitle = System.Text.RegularExpressions.Regex.Replace(title, @"[^\u0000-\u007F\s]", "").Trim();
            Current.Dispatcher.Invoke(() =>
                MessageBox.Show(message, cleanTitle, MessageBoxButton.OK, MessageBoxImage.Information));
        }
    }
}
