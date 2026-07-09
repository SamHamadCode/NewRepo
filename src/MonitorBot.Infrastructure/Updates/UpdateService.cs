using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitorBot.Core.Interfaces;

namespace MonitorBot.Infrastructure.Updates
{
    public class UpdateService : IUpdateService
    {
        private readonly ILogger<UpdateService> _logger;
        private const string CurrentVersion = "1.0.0";

        public UpdateService(ILogger<UpdateService> logger)
        {
            _logger = logger;
        }

        public async Task<bool> CheckForUpdateAsync()
        {
            var latest = await GetLatestVersionAsync();
            return string.Compare(latest, CurrentVersion, StringComparison.Ordinal) > 0;
        }

        public Task<string> GetLatestVersionAsync()
        {
            // Stub — replace with real GitHub releases API call
            _logger.LogInformation("Checking for updates...");
            return Task.FromResult(CurrentVersion);
        }

        public Task DownloadAndInstallAsync()
        {
            _logger.LogInformation("Downloading update...");
            return Task.CompletedTask;
        }
    }
}
