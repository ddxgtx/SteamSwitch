using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamSwitcher.Models;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// 快速启动项的读写与启动能力。定义此接口让视图模型面向契约而非具体实现，
    /// 便于替换实现与单元测试。
    /// </summary>
    public interface IQuickLaunchService : IDisposable
    {
        event EventHandler? ItemsChanged;

        Task LoadAsync();

        Task<QuickLaunchItem> AddAsync(string name, string launchPath, string? args = null, string? iconPath = null);

        Task RemoveAsync(string id);

        Task TogglePinAsync(string id);

        Task ReorderAsync(List<string> orderedIds);

        List<QuickLaunchItem> GetAll();

        List<QuickLaunchItem> GetPinned();

        QuickLaunchItem? GetById(string id);

        bool Launch(QuickLaunchItem item);
    }
}
