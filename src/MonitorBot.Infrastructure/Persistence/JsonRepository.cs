using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json;

namespace MonitorBot.Infrastructure.Persistence
{
    public abstract class JsonRepository<T>
    {
        private readonly string _filePath;
        private readonly ConcurrentDictionary<string, T> _cache = new();
        private readonly object _lock = new();

        protected JsonRepository(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            Load();
        }

        protected abstract string GetId(T item);

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var json = File.ReadAllText(_filePath);
                var items = JsonConvert.DeserializeObject<List<T>>(json) ?? new List<T>();
                foreach (var item in items)
                    _cache[GetId(item)] = item;
            }
            catch { /* ignore corrupt file */ }
        }

        private Task PersistAsync()
        {
            return Task.Run(() =>
            {
                lock (_lock)
                {
                    var json = JsonConvert.SerializeObject(_cache.Values.ToList(), Formatting.Indented);
                    File.WriteAllText(_filePath, json);
                }
            });
        }

        public Task<IEnumerable<T>> GetAllAsync() =>
            Task.FromResult<IEnumerable<T>>(_cache.Values.ToList());

        public Task<T?> GetByIdAsync(string id) =>
            Task.FromResult(_cache.TryGetValue(id, out var item) ? item : default);

        public async Task SaveAsync(T item)
        {
            _cache[GetId(item)] = item;
            await PersistAsync();
        }

        public async Task DeleteAsync(string id)
        {
            _cache.TryRemove(id, out _);
            await PersistAsync();
        }

        public async Task SaveAllAsync(IEnumerable<T> items)
        {
            _cache.Clear();
            foreach (var item in items)
                _cache[GetId(item)] = item;
            await PersistAsync();
        }
    }
}
