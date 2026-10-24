using System;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Models;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// 游戏存档自动备份能力。定义此接口让视图模型面向契约而非具体实现，
    /// 便于替换实现与单元测试。
    /// </summary>
    public interface IGameSaveAutoBackupService : IDisposable
    {
        event EventHandler<GameSaveAutoBackupResult>? BackupCompleted;

        bool Enabled { get; set; }

        Task BackupBeforeAccountSwitchAsync(SteamAccount? currentAccount);

        Task<GameSaveAutoBackupResult> BackupGameAsync(
            string steamId,
            int appId,
            string gameName,
            CancellationToken cancellationToken = default);
    }
}
