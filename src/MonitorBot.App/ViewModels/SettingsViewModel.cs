using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MonitorBot.App.Commands;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class SettingsViewModel : BaseViewModel
    {
        private readonly ISettingsService _settings;
        private readonly INotificationService _notificationService;

        public AppSettings Current => _settings.Current;

        public ObservableCollection<NotificationWebhook> Webhooks { get; } = new();

        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand AddWebhookCommand { get; }
        public ICommand DeleteWebhookCommand { get; }
        public ICommand TestWebhookCommand { get; }

        private NotificationWebhook? _selectedWebhook;
        public NotificationWebhook? SelectedWebhook
        {
            get => _selectedWebhook;
            set => SetField(ref _selectedWebhook, value);
        }

        public SettingsViewModel(ISettingsService settings, INotificationService notificationService)
        {
            _settings = settings;
            _notificationService = notificationService;

            SaveCommand = new AsyncRelayCommand(SaveAsync);
            ResetCommand = new AsyncRelayCommand(ResetAsync);
            AddWebhookCommand = new RelayCommand(AddWebhook);
            DeleteWebhookCommand = new RelayCommand(DeleteWebhook, () => SelectedWebhook != null);
            TestWebhookCommand = new AsyncRelayCommand(TestWebhookAsync, () => SelectedWebhook != null);

            Load();
        }

        private void Load()
        {
            Webhooks.Clear();
            foreach (var w in Current.Notifications.Webhooks)
                Webhooks.Add(w);
        }

        private async Task SaveAsync()
        {
            Current.Notifications.Webhooks = Webhooks.ToList();
            await _settings.SaveAsync();
        }

        private async Task ResetAsync()
        {
            await _settings.ResetAsync();
            Load();
            OnPropertyChanged(nameof(Current));
        }

        private void AddWebhook()
        {
            var webhook = new NotificationWebhook { Name = "New Webhook" };
            Webhooks.Add(webhook);
            SelectedWebhook = webhook;
        }

        private void DeleteWebhook()
        {
            if (SelectedWebhook == null) return;
            Webhooks.Remove(SelectedWebhook);
            SelectedWebhook = null;
        }

        private async Task TestWebhookAsync()
        {
            if (SelectedWebhook == null) return;
            await _notificationService.SendDesktopAsync("Test", $"Webhook '{SelectedWebhook.Name}' is configured.");
        }
    }
}
