using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json;

namespace MonitorBot.Infrastructure.Persistence
{
    public class SettingsService : ISettingsService
    {
        private readonly string _path;
        public AppSettings Current { get; private set; } = new();

        public SettingsService(string dataDir)
        {
            _path = Path.Combine(dataDir, "settings.json");
            Directory.CreateDirectory(dataDir);
        }

        public Task LoadAsync()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    Current = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { Current = new AppSettings(); }
            return Task.CompletedTask;
        }

        public async Task SaveAsync()
        {
            var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
            await File.WriteAllTextAsync(_path, json);
        }

        public Task ResetAsync()
        {
            Current = new AppSettings();
            return SaveAsync();
        }
    }
}
