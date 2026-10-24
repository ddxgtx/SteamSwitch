using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamSwitcher.Core;

/// <summary>
/// 游戏-账号绑定契约，从具体类 <see cref="GameAccountBinding"/> 抽离，
/// 使 ViewModel 面向契约而非实现，便于替换与单元测试。
/// </summary>
public interface IGameAccountBinding
{
    event EventHandler? BindingsChanged;
    string ConfigPath { get; }
    GameBinding? GetBinding(int appId);
    Dictionary<int, GameBinding> GetAllBindings();
    Task SetBindingAsync(int appId, string gameName, string accountSteamId, string accountName);
    Task RemoveBindingAsync(int appId);
    Task RecordPlayAsync(int appId, string accountSteamId, string accountName);
    Task SetLaunchWorkflowAsync(int appId, string launchArguments, bool backupBeforeLaunch, bool backupAfterExit, bool closeSteamAfterExit, bool startInBigPicture);
    Task LoadAsync();
}
