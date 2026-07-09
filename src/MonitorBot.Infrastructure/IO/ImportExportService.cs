using System;
using System.Threading.Tasks;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;
using Newtonsoft.Json;

namespace MonitorBot.Infrastructure.IO
{
    public class ImportExportService : IImportExportService
    {
        public Task<string> ExportAsync(ExportBundle bundle)
        {
            var json = JsonConvert.SerializeObject(bundle, Formatting.Indented);
            return Task.FromResult(json);
        }

        public Task<ExportBundle?> ImportAsync(string json)
        {
            try
            {
                var bundle = JsonConvert.DeserializeObject<ExportBundle>(json);
                return Task.FromResult(bundle);
            }
            catch
            {
                return Task.FromResult<ExportBundle?>(null);
            }
        }
    }
}
