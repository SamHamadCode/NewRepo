using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface IImportExportService
    {
        Task<string> ExportAsync(ExportBundle bundle);
        Task<ExportBundle?> ImportAsync(string json);
    }
}
