using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MonitorBot.App.Commands;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private readonly IImportExportService _importExport;
        private readonly ITaskRepository _taskRepo;
        private readonly IProfileRepository _profileRepo;
        private readonly IAccountRepository _accountRepo;
        private readonly IProxyRepository _proxyRepo;
        private readonly INotificationService _notificationService;

        public DashboardViewModel Dashboard { get; }
        public TasksViewModel Tasks { get; }
        public ProfilesViewModel Profiles { get; }
        public AccountsViewModel Accounts { get; }
        public SettingsViewModel Settings { get; }
        public LogsViewModel Logs { get; }

        private object _currentView;
        public object CurrentView
        {
            get => _currentView;
            set => SetField(ref _currentView, value);
        }

        private string _currentPage = "Dashboard";
        public string CurrentPage
        {
            get => _currentPage;
            set => SetField(ref _currentPage, value);
        }

        public ICommand NavigateCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ImportCommand { get; }

        public MainViewModel(
            DashboardViewModel dashboard,
            TasksViewModel tasks,
            ProfilesViewModel profiles,
            AccountsViewModel accounts,
            SettingsViewModel settingsVm,
            LogsViewModel logs,
            IImportExportService importExport,
            ITaskRepository taskRepo,
            IProfileRepository profileRepo,
            IAccountRepository accountRepo,
            IProxyRepository proxyRepo,
            INotificationService notificationService)
        {
            Dashboard = dashboard;
            Tasks = tasks;
            Profiles = profiles;
            Accounts = accounts;
            Settings = settingsVm;
            Logs = logs;
            _importExport = importExport;
            _taskRepo = taskRepo;
            _profileRepo = profileRepo;
            _accountRepo = accountRepo;
            _proxyRepo = proxyRepo;
            _notificationService = notificationService;

            _currentView = dashboard;

            NavigateCommand = new RelayCommand(param =>
            {
                CurrentPage = param?.ToString() ?? "Dashboard";
                CurrentView = CurrentPage switch
                {
                    "Tasks" => Tasks,
                    "Profiles" => Profiles,
                    "Accounts" => Accounts,
                    "Settings" => Settings,
                    "Logs" => Logs,
                    _ => Dashboard
                };
            });

            ExportCommand = new AsyncRelayCommand(ExportAsync);
            ImportCommand = new AsyncRelayCommand(ImportAsync);
        }

        public async Task InitAsync()
        {
            await Dashboard.InitAsync();
            await Tasks.LoadAsync();
            await Profiles.LoadAsync();
            await Accounts.LoadAsync();
        }

        private async Task ExportAsync()
        {
            var tasks = await _taskRepo.GetAllAsync();
            var profiles = await _profileRepo.GetAllAsync();
            var accounts = await _accountRepo.GetAllAsync();
            var proxies = await _proxyRepo.GetAllAsync();

            var bundle = new ExportBundle
            {
                Tasks = System.Linq.Enumerable.ToArray(tasks),
                Profiles = System.Linq.Enumerable.ToArray(profiles),
                Accounts = System.Linq.Enumerable.ToArray(accounts),
                Proxies = System.Linq.Enumerable.ToArray(proxies)
            };

            var json = await _importExport.ExportAsync(bundle);

            var dlg = new SaveFileDialog
            {
                Title = "Export Configuration",
                Filter = "JSON files (*.json)|*.json",
                FileName = $"monitorbot-export-{System.DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dlg.ShowDialog() == true)
            {
                await System.IO.File.WriteAllTextAsync(dlg.FileName, json);
                await _notificationService.SendDesktopAsync("Export", "Configuration exported successfully.");
            }
        }

        private async Task ImportAsync()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import Configuration",
                Filter = "JSON files (*.json)|*.json"
            };
            if (dlg.ShowDialog() != true) return;

            var json = await System.IO.File.ReadAllTextAsync(dlg.FileName);
            var bundle = await _importExport.ImportAsync(json);
            if (bundle == null)
            {
                MessageBox.Show("Invalid import file.", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (bundle.Tasks != null) await _taskRepo.SaveAllAsync(bundle.Tasks);
            if (bundle.Profiles != null) foreach (var p in bundle.Profiles) await _profileRepo.SaveAsync(p);
            if (bundle.Accounts != null) foreach (var a in bundle.Accounts) await _accountRepo.SaveAsync(a);
            if (bundle.Proxies != null) foreach (var pr in bundle.Proxies) await _proxyRepo.SaveAsync(pr);

            await InitAsync();
            await _notificationService.SendDesktopAsync("Import", "Configuration imported successfully.");
        }
    }
}
