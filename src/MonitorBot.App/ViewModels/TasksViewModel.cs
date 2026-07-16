using System;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MonitorBot.App.Commands;
using MonitorBot.App.Views;
using MonitorBot.Core.Enums;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class TasksViewModel : BaseViewModel
    {
        private readonly IMonitorService    _monitor;
        private readonly ITaskRepository    _repo;
        private readonly ITaskGroupRepository _groupRepo;
        private readonly IProfileRepository _profileRepo;
        private readonly IAccountRepository _accountRepo;

        // ?? Collections ??????????????????????????????????????????????????????
        public ObservableCollection<TaskGroupViewModel>  Groups   { get; } = new();
        public ObservableCollection<MonitorTaskViewModel> Tasks   { get; } = new();
        public ObservableCollection<UserProfile>          Profiles { get; } = new();
        public ObservableCollection<SiteAccount>          Accounts { get; } = new();

        // ?? Selected group ????????????????????????????????????????????????????
        private TaskGroupViewModel? _selectedGroup;
        public TaskGroupViewModel? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (_selectedGroup != null) _selectedGroup.IsSelected = false;
                SetField(ref _selectedGroup, value);
                if (_selectedGroup != null) _selectedGroup.IsSelected = true;
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(HeaderSubtitle));
                _ = LoadTasksForGroupAsync(value);
            }
        }

        // ?? Selected task (shows editor drawer) ???????????????????????????????
        private MonitorTaskViewModel? _selected;
        public MonitorTaskViewModel? Selected
        {
            get => _selected;
            set
            {
                SetField(ref _selected, value);
                RaiseCommands();
                OnPropertyChanged(nameof(SelectedProfile));
                OnPropertyChanged(nameof(SelectedAccount));
            }
        }

        // ?? Search ????????????????????????????????????????????????????????????
        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set { SetField(ref _searchText, value); ApplyFilter(); }
        }

        // ?? Header info ???????????????????????????????????????????????????????
        public string HeaderTitle    => SelectedGroup?.Name ?? "No Product";
        public string HeaderSubtitle => SelectedGroup == null
            ? "0 Tasks · 0 Running Tasks"
            : $"{Tasks.Count} Tasks · {Tasks.Count(t => t.IsRunning)} Running Tasks";

        // ?? Profile / Account binding helpers ????????????????????????????????
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

        // ?? Commands ??????????????????????????????????????????????????????????
        public ICommand NewGroupCommand   { get; }
        public ICommand SelectGroupCommand { get; }
        public ICommand AddCommand        { get; }
        public ICommand StartCommand      { get; }
        public ICommand StopCommand       { get; }
        public ICommand StartAllCommand   { get; }
        public ICommand StopAllCommand    { get; }
        public ICommand DeleteCommand     { get; }
        public ICommand EditGroupCommand  { get; }
        public ICommand SaveCommand       { get; }
        public ICommand SelectCommand     { get; }
        public ICommand DeselectCommand   { get; }
        public ICommand IncrementQtyCommand { get; }
        public ICommand DecrementQtyCommand { get; }
        public ICommand ClearCookiesCommand { get; }
        public ICommand HarvestCookiesCommand { get; }
        public ICommand DeleteTaskCommand { get; }

        public TasksViewModel(
            IMonitorService    monitor,
            ITaskRepository    repo,
            ITaskGroupRepository groupRepo,
            IProfileRepository profileRepo,
            IAccountRepository accountRepo)
        {
            _monitor     = monitor;
            _repo        = repo;
            _groupRepo   = groupRepo;
            _profileRepo = profileRepo;
            _accountRepo = accountRepo;

            NewGroupCommand    = new AsyncRelayCommand(CreateGroupAsync);
            SelectGroupCommand = new RelayCommand(p => { if (p is TaskGroupViewModel gvm) SelectedGroup = gvm; });
            AddCommand         = new AsyncRelayCommand(AddTaskAsync, () => SelectedGroup != null);
            StartCommand       = new AsyncRelayCommand(StartSelectedAsync, () => Selected != null && !Selected.IsRunning);
            StopCommand        = new AsyncRelayCommand(StopSelectedAsync,  () => Selected != null && Selected.IsRunning);
            StartAllCommand    = new AsyncRelayCommand(StartAllAsync);
            StopAllCommand     = new AsyncRelayCommand(StopAllAsync);
            DeleteCommand      = new AsyncRelayCommand(ClearGroupTasksAsync, () => SelectedGroup != null);
            EditGroupCommand   = new AsyncRelayCommand(EditGroupAsync, () => SelectedGroup != null);
            SaveCommand        = new AsyncRelayCommand(SaveSelectedAsync, () => Selected != null);
            SelectCommand      = new RelayCommand(p => { if (p is MonitorTaskViewModel vm) Selected = vm; });
            DeselectCommand    = new RelayCommand(_ => Selected = null);
            IncrementQtyCommand = new RelayCommand(_ =>
            {
                if (Selected != null) { Selected.Quantity = Math.Min(Selected.Quantity + 1, 10); _ = SaveSelectedAsync(); }
            }, _ => Selected != null);
            DecrementQtyCommand = new RelayCommand(_ =>
            {
                if (Selected != null) { Selected.Quantity = Math.Max(Selected.Quantity - 1, 1); _ = SaveSelectedAsync(); }
            }, _ => Selected != null);
            ClearCookiesCommand = new RelayCommand(_ =>
            {
                if (Selected != null) { Selected.CookieOverride = null; _ = SaveSelectedAsync(); }
            }, _ => Selected != null);
            HarvestCookiesCommand = new RelayCommand(_ => OpenHarvester(), _ => Selected != null);
            DeleteTaskCommand     = new AsyncRelayCommand(DeleteSelectedTaskAsync, () => Selected != null);

            _monitor.TaskStatusChanged += (_, t) => Application.Current.Dispatcher.Invoke(() => RefreshTask(t));
            _monitor.ResultReceived    += (_, r) => Application.Current.Dispatcher.Invoke(() => UpdateResult(r));
        }

        // ?? Load ??????????????????????????????????????????????????????????????
        public async Task LoadAsync()
        {
            var profiles = await _profileRepo.GetAllAsync();
            Profiles.Clear();
            foreach (var p in profiles) Profiles.Add(p);

            var accounts = await _accountRepo.GetAllAsync();
            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add(a);

            var groups = await _groupRepo.GetAllAsync();
            Groups.Clear();
            foreach (var g in groups.OrderBy(x => x.CreatedAt))
                Groups.Add(new TaskGroupViewModel(g));

            // Auto-select first group if any
            if (SelectedGroup == null && Groups.Count > 0)
                SelectedGroup = Groups[0];
            else if (SelectedGroup != null)
                await LoadTasksForGroupAsync(SelectedGroup);
        }

        private async Task LoadTasksForGroupAsync(TaskGroupViewModel? groupVm)
        {
            Tasks.Clear();
            Selected = null;

            if (groupVm == null) return;

            var allTasks = await _repo.GetAllAsync();
            var groupTaskIds = new System.Collections.Generic.HashSet<string>(groupVm.Group.TaskIds);

            foreach (var t in allTasks.Where(x => groupTaskIds.Contains(x.Id.ToString())).OrderBy(x => x.CreatedAt))
                Tasks.Add(new MonitorTaskViewModel(t));

            OnPropertyChanged(nameof(HeaderSubtitle));
        }

        // ?? Group CRUD ????????????????????????????????????????????????????????
        private async Task CreateGroupAsync()
        {
            var dlg = new CreateTaskGroupDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;

            var group = new TaskGroup
            {
                Name = dlg.GroupName,
                Site = dlg.GroupSite
            };

            await _groupRepo.SaveAsync(group);
            var vm = new TaskGroupViewModel(group);
            Groups.Add(vm);
            SelectedGroup = vm;
        }

        private async Task EditGroupAsync()
        {
            if (SelectedGroup == null) return;

            var dlg = new CreateTaskGroupDialog { Owner = Application.Current.MainWindow };
            dlg.GroupNameBox.Text = SelectedGroup.Name;
            if (dlg.ShowDialog() != true) return;

            SelectedGroup.Name = dlg.GroupName;
            SelectedGroup.Site = dlg.GroupSite;
            await _groupRepo.SaveAsync(SelectedGroup.Group);
            OnPropertyChanged(nameof(HeaderTitle));
        }

        // ?? Task CRUD ?????????????????????????????????????????????????????????
        private async Task AddTaskAsync()
        {
            if (SelectedGroup == null) return;

            var task = new MonitorTask
            {
                Name            = "New Task",
                IntervalSeconds = SelectedGroup.Group.MonitorDelay > 0
                                  ? SelectedGroup.Group.MonitorDelay / 1000
                                  : 30
            };

            await _repo.SaveAsync(task);

            SelectedGroup.Group.TaskIds.Add(task.Id.ToString());
            await _groupRepo.SaveAsync(SelectedGroup.Group);

            var vm = new MonitorTaskViewModel(task);
            Tasks.Add(vm);
            Selected = vm;
            OnPropertyChanged(nameof(HeaderSubtitle));
        }

        private async Task SaveSelectedAsync()
        {
            if (Selected == null) return;
            await _repo.SaveAsync(Selected.Task);
        }

        private async Task DeleteSelectedTaskAsync()
        {
            if (Selected == null || SelectedGroup == null) return;

            await _monitor.StopTaskAsync(Selected.Task.Id);
            await _repo.DeleteAsync(Selected.Task.Id.ToString());

            SelectedGroup.Group.TaskIds.Remove(Selected.Task.Id.ToString());
            await _groupRepo.SaveAsync(SelectedGroup.Group);

            Tasks.Remove(Selected);
            Selected = null;
            OnPropertyChanged(nameof(HeaderSubtitle));
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

        private async Task ClearGroupTasksAsync()
        {
            if (SelectedGroup == null) return;
            foreach (var vm in Tasks.ToList())
            {
                await _monitor.StopTaskAsync(vm.Task.Id);
                await _repo.DeleteAsync(vm.Task.Id.ToString());
            }
            SelectedGroup.Group.TaskIds.Clear();
            await _groupRepo.SaveAsync(SelectedGroup.Group);
            Tasks.Clear();
            Selected = null;
            OnPropertyChanged(nameof(HeaderSubtitle));
        }

        private void OpenHarvester()
        {
            if (Selected == null) return;

            // Resolve start URL from group site ? task URL ? fallback
            string startUrl = ResolveHarvestUrl();

            var win = new Views.CookieHarvesterWindow(startUrl)
            {
                Owner = Application.Current.MainWindow
            };

            if (win.ShowDialog() == true && !string.IsNullOrEmpty(win.HarvestedCookies))
            {
                Selected.CookieOverride = win.HarvestedCookies;
                _ = SaveSelectedAsync();
            }
        }

        private string ResolveHarvestUrl()
        {
            // 1. Use the selected group's site setting
            var site = SelectedGroup?.Site ?? string.Empty;
            var siteUrl = site.ToLowerInvariant() switch
            {
                var s when s.Contains("target")        => "https://www.target.com",
                var s when s.Contains("walmart")       => "https://www.walmart.com",
                var s when s.Contains("best buy")      => "https://www.bestbuy.com",
                var s when s.Contains("costco")        => "https://www.costco.com",
                var s when s.Contains("sam")           => "https://www.samsclub.com",
                var s when s.Contains("pokemon")       => "https://www.pokemoncenter.com",
                var s when s.Contains("bandai")        => "https://p-bandai.com",
                var s when s.Contains("footlocker")    => "https://www.footlocker.com",
                var s when s.Contains("nike")          => "https://www.nike.com",
                var s when s.Contains("amazon")        => "https://www.amazon.com",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(siteUrl))
                return siteUrl;

            // 2. Fall back to parsing the task's TargetUrl
            if (!string.IsNullOrWhiteSpace(Selected?.TargetUrl))
            {
                try
                {
                    var uri = new Uri(Selected.TargetUrl);
                    return $"{uri.Scheme}://{uri.Host}";
                }
                catch { }
            }

            // 3. Last resort
            return "https://www.target.com";
        }

        // ?? Refresh helpers ???????????????????????????????????????????????????
        private void RefreshTask(MonitorTask task)
        {
            var vm = Tasks.FirstOrDefault(x => x.Task.Id == task.Id);
            if (vm != null)
            {
                vm.Refresh();
                OnPropertyChanged(nameof(HeaderSubtitle));
            }
        }

        private void UpdateResult(MonitorResult result)
        {
            var vm = Tasks.FirstOrDefault(x => x.Task.Id == result.TaskId);
            if (vm != null) vm.Refresh();
        }

        private void ApplyFilter()
        {
            // Re-trigger binding refresh — filtering done in XAML CollectionView if needed
            OnPropertyChanged(nameof(Tasks));
        }

        private void RaiseCommands() => CommandManager.InvalidateRequerySuggested();
    }

    // ?? TaskGroupViewModel ????????????????????????????????????????????????????
    public class TaskGroupViewModel : BaseViewModel
    {
        public TaskGroup Group { get; }

        public TaskGroupViewModel(TaskGroup group) => Group = group;

        public string Name
        {
            get => Group.Name;
            set { Group.Name = value; OnPropertyChanged(); }
        }

        public string Site
        {
            get => Group.Site;
            set { Group.Site = value; OnPropertyChanged(); }
        }

        public int TaskCount => Group.TaskIds.Count;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }
    }

    // ?? MonitorTaskViewModel ??????????????????????????????????????????????????
    public class MonitorTaskViewModel : BaseViewModel
    {
        public MonitorTask Task { get; }

        public MonitorTaskViewModel(MonitorTask task) => Task = task;

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

        public MonitorTaskStatus Status      => Task.Status;
        public string? LastResult            => Task.LastResult;
        public DateTime? LastChecked         => Task.LastChecked;
        public CheckoutStatus CheckoutStatus => Task.CheckoutStatus;
        public string? LastOrderId           => Task.LastOrderId;
        public string? CheckoutError         => Task.CheckoutError;

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

        public string? CookieOverride
        {
            get => Task.CookieOverride;
            set { Task.CookieOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCookies)); }
        }

        public bool HasCookies => !string.IsNullOrWhiteSpace(Task.CookieOverride);

        public int Quantity
        {
            get => Task.Quantity;
            set
            {
                // Clamp to 1–10 in the VM so the saved value is always valid
                var clamped = Math.Max(1, Math.Min(value, 10));
                if (Task.Quantity == clamped) return;
                Task.Quantity = clamped;
                OnPropertyChanged();
            }
        }

        public bool IsRunning =>
            Task.Status == MonitorTaskStatus.Running ||
            Task.Status == MonitorTaskStatus.Retrying;

        public System.Windows.Media.SolidColorBrush StatusColor =>
            Task.Status switch
            {
                MonitorTaskStatus.Running     => new(System.Windows.Media.Color.FromRgb(0, 200, 150)),
                MonitorTaskStatus.Success     => new(System.Windows.Media.Color.FromRgb(0, 200, 150)),
                MonitorTaskStatus.Failed      => new(System.Windows.Media.Color.FromRgb(255, 76, 106)),
                MonitorTaskStatus.Retrying    => new(System.Windows.Media.Color.FromRgb(255, 184, 0)),
                MonitorTaskStatus.CheckingOut => new(System.Windows.Media.Color.FromRgb(139, 92, 246)),
                MonitorTaskStatus.LoggingIn   => new(System.Windows.Media.Color.FromRgb(139, 92, 246)),
                MonitorTaskStatus.AddingToCart => new(System.Windows.Media.Color.FromRgb(139, 92, 246)),
                MonitorTaskStatus.PlacingOrder => new(System.Windows.Media.Color.FromRgb(251, 146, 60)),
                MonitorTaskStatus.Stopped     => new(System.Windows.Media.Color.FromRgb(64, 64, 90)),
                _                             => new(System.Windows.Media.Color.FromRgb(64, 64, 90))
            };

        public string StatusLabel => Task.Status switch
        {
            MonitorTaskStatus.CheckingOut  => "Checking Out",
            MonitorTaskStatus.LoggingIn    => "Logging In",
            MonitorTaskStatus.AddingToCart => "Adding to Cart",
            MonitorTaskStatus.PlacingOrder => "Placing Order",
            _                              => Task.Status.ToString()
        };

        public void Refresh()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusLabel));
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
