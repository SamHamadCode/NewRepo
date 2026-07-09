using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using MonitorBot.App.Commands;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class DashboardViewModel : BaseViewModel
    {
        private readonly IMonitorService _monitor;
        private readonly ITaskRepository _taskRepo;
        private readonly IUpdateService _updateService;

        private int _totalTasks;
        public int TotalTasks { get => _totalTasks; set => SetField(ref _totalTasks, value); }

        private int _runningTasks;
        public int RunningTasks { get => _runningTasks; set => SetField(ref _runningTasks, value); }

        private int _successCount;
        public int SuccessCount { get => _successCount; set => SetField(ref _successCount, value); }

        private int _failureCount;
        public int FailureCount { get => _failureCount; set => SetField(ref _failureCount, value); }

        private string _updateStatus = "Up to date";
        public string UpdateStatus { get => _updateStatus; set => SetField(ref _updateStatus, value); }

        private bool _hasUpdate;
        public bool HasUpdate { get => _hasUpdate; set => SetField(ref _hasUpdate, value); }

        public ICommand CheckUpdateCommand { get; }
        public ICommand InstallUpdateCommand { get; }

        public DashboardViewModel(
            IMonitorService monitor,
            ITaskRepository taskRepo,
            IUpdateService updateService)
        {
            _monitor = monitor;
            _taskRepo = taskRepo;
            _updateService = updateService;

            CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
            InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => HasUpdate);

            _monitor.TaskStatusChanged += (_, t) => RefreshStats(t);
            _monitor.ResultReceived += (_, r) => { if (r.IsAvailable) SuccessCount++; };
        }

        public async Task InitAsync()
        {
            var tasks = await _taskRepo.GetAllAsync();
            foreach (var t in tasks) TotalTasks++;
            await CheckUpdateAsync();
        }

        private void RefreshStats(MonitorTask task)
        {
            switch (task.Status)
            {
                case Core.Enums.MonitorTaskStatus.Running: RunningTasks++; break;
                case Core.Enums.MonitorTaskStatus.Stopped: if (RunningTasks > 0) RunningTasks--; break;
                case Core.Enums.MonitorTaskStatus.Failed: FailureCount++; break;
            }
        }

        private async Task CheckUpdateAsync()
        {
            HasUpdate = await _updateService.CheckForUpdateAsync();
            UpdateStatus = HasUpdate ? "Update available!" : "Up to date";
        }

        private async Task InstallUpdateAsync() => await _updateService.DownloadAndInstallAsync();
    }
}
