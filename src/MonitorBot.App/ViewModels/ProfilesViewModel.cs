using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using MonitorBot.App.Commands;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class ProfilesViewModel : BaseViewModel
    {
        private readonly IProfileRepository _repo;

        public ObservableCollection<UserProfile> Profiles { get; } = new();

        private UserProfile? _selected;
        public UserProfile? Selected
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

        public ProfilesViewModel(IProfileRepository repo)
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
            Profiles.Clear();
            foreach (var p in items) Profiles.Add(p);
        }

        private async Task AddAsync()
        {
            var profile = new UserProfile { Name = "New Profile" };
            await _repo.SaveAsync(profile);
            Profiles.Add(profile);
            Selected = profile;
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
            Profiles.Remove(Selected);
            Selected = null;
        }
    }
}
