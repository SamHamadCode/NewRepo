using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Logging
{
    public class FileLogStore : ILogStore
    {
        private readonly string _logDir;
        private readonly ConcurrentQueue<LogEntry> _buffer = new();
        private readonly int _maxBuffer = 2000;

        public FileLogStore(string dataDir)
        {
            _logDir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(_logDir);
        }

        public void Add(LogEntry entry)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > _maxBuffer && _buffer.TryDequeue(out _)) { }
        }

        public IReadOnlyList<LogEntry> GetRecent(int count = 500)
        {
            return _buffer.TakeLast(count).ToList();
        }

        public async Task FlushAsync()
        {
            var entries = _buffer.ToList();
            if (!entries.Any()) return;
            var path = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
            var sb = new StringBuilder();
            foreach (var e in entries)
                sb.AppendLine($"[{e.Timestamp:HH:mm:ss}] [{e.Level}] [{e.Category}] {e.Message}{(e.Exception != null ? $"\n{e.Exception}" : "")}");
            await File.AppendAllTextAsync(path, sb.ToString());
        }

        public Task PurgeOldAsync(int retentionDays)
        {
            return Task.Run(() =>
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                foreach (var file in Directory.GetFiles(_logDir, "*.log"))
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
            });
        }
    }
}
