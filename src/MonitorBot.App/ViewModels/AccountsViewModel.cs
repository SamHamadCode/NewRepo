using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using MonitorBot.App.Commands;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class AccountsViewModel : BaseViewModel
    {
        private readonly IAccountRepository _repo;

        public ObservableCollection<SiteAccount> Accounts { get; } = new();

        private SiteAccount? _selected;
        public SiteAccount? Selected
        {
            get => _selected;
            set { SetField(ref _selected, value); IsEditing = value != null; }
        }

        private bool _isEditing;
        public bool IsEditing { get => _isEditing; set => SetField(ref _isEditing, value); }

        public ICommand AddCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand RefreshCommand { get; }

        public AccountsViewModel(IAccountRepository repo)
        {
            _repo = repo;
            AddCommand = new AsyncRelayCommand(AddAsync);
            SaveCommand = new AsyncRelayCommand(SaveAsync, () => Selected != null);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected != null);
            RefreshCommand = new AsyncRelayCommand(LoadAsync);
        }

        public async Task LoadAsync()
        {
            var items = await _repo.GetAllAsync();
            Accounts.Clear();
            foreach (var a in items) Accounts.Add(a);
        }

        private async Task AddAsync()
        {
            var account = new SiteAccount { Name = "New Account" };
            await _repo.SaveAsync(account);
            Accounts.Add(account);
            Selected = account;
        }

        private async Task SaveAsync()
        {
            if (Selected == null) return;
            await _repo.SaveAsync(Selected);
        }

        private async Task DeleteAsync()
        {
            if (Selected == null) return;
            await _repo.DeleteAsync(Selected.Id);
            Accounts.Remove(Selected);
            Selected = null;
        }
    }
}
