using System.Collections.Generic;
using System.Threading.Tasks;
using MonitorBot.Core.Models;

namespace MonitorBot.Core.Interfaces
{
    public interface ITaskRepository
    {
        Task<IEnumerable<MonitorTask>> GetAllAsync();
        Task<MonitorTask?> GetByIdAsync(string id);
        Task SaveAsync(MonitorTask task);
        Task DeleteAsync(string id);
        Task SaveAllAsync(IEnumerable<MonitorTask> tasks);
    }

    public interface ITaskGroupRepository
    {
        Task<IEnumerable<TaskGroup>> GetAllAsync();
        Task<TaskGroup?> GetByIdAsync(string id);
        Task SaveAsync(TaskGroup group);
        Task DeleteAsync(string id);
    }

    public interface IProfileRepository
    {
        Task<IEnumerable<UserProfile>> GetAllAsync();
        Task<UserProfile?> GetByIdAsync(string id);
        Task SaveAsync(UserProfile profile);
        Task DeleteAsync(string id);
    }

    public interface IAccountRepository
    {
        Task<IEnumerable<SiteAccount>> GetAllAsync();
        Task<SiteAccount?> GetByIdAsync(string id);
        Task SaveAsync(SiteAccount account);
        Task DeleteAsync(string id);
    }

    public interface IProxyRepository
    {
        Task<IEnumerable<ProxyEntry>> GetAllAsync();
        Task SaveAsync(ProxyEntry proxy);
        Task DeleteAsync(string id);
    }
}
