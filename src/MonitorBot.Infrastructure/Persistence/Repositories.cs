using System.Collections.Generic;
using System.Threading.Tasks;
using MonitorBot.Core.Interfaces;
using MonitorBot.Core.Models;

namespace MonitorBot.Infrastructure.Persistence
{
    public class TaskRepository : JsonRepository<MonitorTask>, ITaskRepository
    {
        public TaskRepository(string dataDir)
            : base(System.IO.Path.Combine(dataDir, "tasks.json")) { }

        protected override string GetId(MonitorTask item) => item.Id.ToString();
    }

    public class ProfileRepository : JsonRepository<UserProfile>, IProfileRepository
    {
        public ProfileRepository(string dataDir)
            : base(System.IO.Path.Combine(dataDir, "profiles.json")) { }

        protected override string GetId(UserProfile item) => item.Id;
    }

    public class AccountRepository : JsonRepository<SiteAccount>, IAccountRepository
    {
        public AccountRepository(string dataDir)
            : base(System.IO.Path.Combine(dataDir, "accounts.json")) { }

        protected override string GetId(SiteAccount item) => item.Id;
    }

    public class ProxyRepository : JsonRepository<ProxyEntry>, IProxyRepository
    {
        public ProxyRepository(string dataDir)
            : base(System.IO.Path.Combine(dataDir, "proxies.json")) { }

        protected override string GetId(ProxyEntry item) => item.Id;
    }

    public class TaskGroupRepository : JsonRepository<TaskGroup>, ITaskGroupRepository
    {
        public TaskGroupRepository(string dataDir)
            : base(System.IO.Path.Combine(dataDir, "taskgroups.json")) { }

        protected override string GetId(TaskGroup item) => item.Id.ToString();
    }
}
