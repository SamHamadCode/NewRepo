using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MonitorBot.App.Commands;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.App.ViewModels
{
    public class LogsViewModel : BaseViewModel
    {
        private readonly ILogStore _logStore;

        public ObservableCollection<LogEntry> Entries { get; } = new();

        private string _filter = string.Empty;
        public string Filter
        {
            get => _filter;
            set { SetField(ref _filter, value); ApplyFilter(); }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand CopyCommand { get; }

        public LogsViewModel(ILogStore logStore)
        {
            _logStore = logStore;
            RefreshCommand = new RelayCommand(Refresh);
            ClearCommand   = new RelayCommand(Clear);
            CopyCommand    = new RelayCommand(CopyLogs);
        }

        public void Refresh()
        {
            var all = _logStore.GetRecent(500);
            ApplyFilter(all);
        }

        private void ApplyFilter(System.Collections.Generic.IReadOnlyList<LogEntry>? source = null)
        {
            source ??= _logStore.GetRecent(500);
            var filtered = string.IsNullOrWhiteSpace(_filter)
                ? source
                : source.Where(e => e.Message.Contains(_filter, System.StringComparison.OrdinalIgnoreCase)
                    || e.Category.Contains(_filter, System.StringComparison.OrdinalIgnoreCase)).ToList();

            Entries.Clear();
            foreach (var e in filtered.TakeLast(200))
                Entries.Add(e);
        }

        private void CopyLogs()
        {
            if (!Entries.Any()) return;
            var sb = new StringBuilder();
            foreach (var e in Entries)
                sb.AppendLine($"[{e.Timestamp:HH:mm:ss.fff}] [{e.Level,-5}] [{e.Category}] {e.Message}");
            Clipboard.SetText(sb.ToString());
        }

        private void Clear() => Entries.Clear();
    }
}
