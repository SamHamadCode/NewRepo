using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MonitorBot.App.Commands;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
namespace MonitorBot.App.ViewModels
{
    public class TasksViewModel : BaseViewModel
    {
        private readonly IMonitorService _monitor;
        private readonly ITaskRepository _repo;
        private readonly IProfileRepository _profileRepo;
        private readonly IAccountRepository _accountRepo;

        public ObservableCollection<MonitorTaskViewModel> Tasks { get; } = new();
        public ObservableCollection<UserProfile> Profiles { get; } = new();
        public ObservableCollection<SiteAccount> Accounts { get; } = new();

        private MonitorTaskViewModel? _selected;
        public MonitorTaskViewModel? Selected
        {
            get => _selected;
            set
            {
                SetField(ref _selected, value);
                RaiseCommands();
                // Sync profile/account dropdowns to the newly selected task
                OnPropertyChanged(nameof(SelectedProfile));
                OnPropertyChanged(nameof(SelectedAccount));
            }
        }

        // Bound to Profile ComboBox SelectedItem — reads/writes ProfileId on the task
        public UserProfile? SelectedProfile
        {
            get => Profiles.FirstOrDefault(p => p.Id == Selected?.ProfileId);
            set
            {
                if (Selected == null) return;
                Selected.ProfileId = value?.Id;
                OnPropertyChanged();
                _ = SaveSelectedAsync();
            }
        }

        // Bound to Account ComboBox SelectedItem — reads/writes AccountId on the task
        public SiteAccount? SelectedAccount
        {
            get => Accounts.FirstOrDefault(a => a.Id == Selected?.AccountId);
            set
            {
                if (Selected == null) return;
                Selected.AccountId = value?.Id;
                OnPropertyChanged();
                _ = SaveSelectedAsync();
            }
        }

        public ICommand AddCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand StartAllCommand { get; }
        public ICommand StopAllCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand RefreshCommand { get; }

        public TasksViewModel(IMonitorService monitor, ITaskRepository repo,
            IProfileRepository profileRepo, IAccountRepository accountRepo)
        {
            _monitor = monitor;
            _repo = repo;
            _profileRepo = profileRepo;
            _accountRepo = accountRepo;

            AddCommand     = new AsyncRelayCommand(AddTaskAsync);
            StartCommand   = new AsyncRelayCommand(StartSelectedAsync, () => Selected != null && !Selected.IsRunning);
            StopCommand    = new AsyncRelayCommand(StopSelectedAsync,  () => Selected != null && Selected.IsRunning);
            StartAllCommand = new AsyncRelayCommand(StartAllAsync);
            StopAllCommand  = new AsyncRelayCommand(StopAllAsync);
            DeleteCommand   = new AsyncRelayCommand(DeleteSelectedAsync, () => Selected != null);
            SaveCommand     = new AsyncRelayCommand(SaveSelectedAsync,   () => Selected != null);
            RefreshCommand  = new AsyncRelayCommand(LoadAsync);

            _monitor.TaskStatusChanged += (_, t) => Application.Current.Dispatcher.Invoke(() => RefreshTask(t));
            _monitor.ResultReceived    += (_, r) => Application.Current.Dispatcher.Invoke(() => UpdateResult(r));
        }

        public async Task LoadAsync()
        {
            // Remember which task was selected so we can restore it after reload
            var previousId = Selected?.Task.Id;

            var profiles = await _profileRepo.GetAllAsync();
            Profiles.Clear();
            foreach (var p in profiles) Profiles.Add(p);

            var accounts = await _accountRepo.GetAllAsync();
            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add(a);

            var tasks = await _repo.GetAllAsync();
            Tasks.Clear();
            foreach (var t in tasks.OrderBy(x => x.CreatedAt))
                Tasks.Add(new MonitorTaskViewModel(t));

            // Restore selection after profiles are loaded so bindings resolve correctly
            if (previousId.HasValue)
            {
                Selected = Tasks.FirstOrDefault(x => x.Task.Id == previousId.Value);
                OnPropertyChanged(nameof(SelectedProfile));
                OnPropertyChanged(nameof(SelectedAccount));
            }
        }

        private async Task SaveSelectedAsync()
        {
            if (Selected == null) return;
            await _repo.SaveAsync(Selected.Task);
        }

        private async Task AddTaskAsync()
        {
            var task = new MonitorTask { Name = "New Task", IntervalSeconds = 30 };
            await _repo.SaveAsync(task);
            var vm = new MonitorTaskViewModel(task);
            Tasks.Add(vm);
            Selected = vm;
        }

        private async Task StartSelectedAsync()
        {
            if (Selected == null) return;
            await _monitor.StartTaskAsync(Selected.Task);
        }

        private async Task StopSelectedAsync()
        {
            if (Selected == null) return;
            await _monitor.StopTaskAsync(Selected.Task.Id);
        }

        private async Task StartAllAsync()
        {
            foreach (var vm in Tasks)
                if (vm.Task.IsEnabled && !vm.IsRunning)
                    await _monitor.StartTaskAsync(vm.Task);
        }

        private async Task StopAllAsync() => await _monitor.StopAllAsync();

        private async Task DeleteSelectedAsync()
        {
            if (Selected == null) return;
            await _monitor.StopTaskAsync(Selected.Task.Id);
            await _repo.DeleteAsync(Selected.Task.Id.ToString());
            Tasks.Remove(Selected);
            Selected = null;
        }

        private void RefreshTask(MonitorTask task)
        {
            var vm = Tasks.FirstOrDefault(x => x.Task.Id == task.Id);
            if (vm != null) vm.Refresh();
        }

        private void UpdateResult(MonitorResult result)
        {
            var vm = Tasks.FirstOrDefault(x => x.Task.Id == result.TaskId);
            if (vm != null) vm.Refresh();
        }

        private void RaiseCommands() => CommandManager.InvalidateRequerySuggested();
    }

    public class MonitorTaskViewModel : BaseViewModel
    {
        public MonitorTask Task { get; }

        public MonitorTaskViewModel(MonitorTask task) { Task = task; }

        public string Name
        {
            get => Task.Name;
            set { Task.Name = value; OnPropertyChanged(); }
        }

        public string TargetUrl
        {
            get => Task.TargetUrl;
            set { Task.TargetUrl = value; OnPropertyChanged(); }
        }

        public string? Sku
        {
            get => Task.Sku;
            set { Task.Sku = value; OnPropertyChanged(); }
        }

        public string? Keyword
        {
            get => Task.Keyword;
            set { Task.Keyword = value; OnPropertyChanged(); }
        }

        public MonitorType Type
        {
            get => Task.Type;
            set { Task.Type = value; OnPropertyChanged(); }
        }

        public DetectionMode DetectionMode
        {
            get => Task.DetectionMode;
            set { Task.DetectionMode = value; OnPropertyChanged(); }
        }

        public decimal? PriceThreshold
        {
            get => Task.PriceThreshold;
            set { Task.PriceThreshold = value; OnPropertyChanged(); }
        }

        public int IntervalSeconds
        {
            get => Task.IntervalSeconds;
            set { Task.IntervalSeconds = value; OnPropertyChanged(); }
        }

        public int MaxRetries
        {
            get => Task.MaxRetries;
            set { Task.MaxRetries = value; OnPropertyChanged(); }
        }

        public string? ProfileId
        {
            get => Task.ProfileId;
            set { Task.ProfileId = value; OnPropertyChanged(); }
        }

        public string? AccountId
        {
            get => Task.AccountId;
            set { Task.AccountId = value; OnPropertyChanged(); }
        }

        public MonitorTaskStatus Status => Task.Status;
        public string? LastResult => Task.LastResult;
        public DateTime? LastChecked => Task.LastChecked;
        public bool IsEnabled
        {
            get => Task.IsEnabled;
            set { Task.IsEnabled = value; OnPropertyChanged(); }
        }

        public bool AutoCheckout
        {
            get => Task.AutoCheckout;
            set { Task.AutoCheckout = value; OnPropertyChanged(); }
        }

        public int Quantity
        {
            get => Task.Quantity;
            set { Task.Quantity = value; OnPropertyChanged(); }
        }

        public CheckoutStatus CheckoutStatus => Task.CheckoutStatus;
        public string? LastOrderId           => Task.LastOrderId;
        public string? CheckoutError         => Task.CheckoutError;

        public bool IsRunning => Task.Status == MonitorTaskStatus.Running || Task.Status == MonitorTaskStatus.Retrying;

        public string StatusColor => Task.Status switch
        {
            MonitorTaskStatus.Running      => "#27AE60",
            MonitorTaskStatus.Success      => "#2ECC71",
            MonitorTaskStatus.Failed       => "#E74C3C",
            MonitorTaskStatus.Retrying     => "#F39C12",
            MonitorTaskStatus.CheckingOut  => "#9B59B6",
            MonitorTaskStatus.Stopped      => "#95A5A6",
            _                              => "#7F8C8D"
        };

        public void Refresh()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(LastResult));
            OnPropertyChanged(nameof(LastChecked));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(CheckoutStatus));
            OnPropertyChanged(nameof(LastOrderId));
            OnPropertyChanged(nameof(CheckoutError));
        }
    }
}
