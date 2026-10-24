namespace SteamSwitcher.Services;

/// <summary>
/// 设置会话契约：持有实时设置快照，并管理脏标记与持久化。
/// 从具体类 <see cref="SettingsSession"/> 抽离，使 ViewModel 面向契约而非实现。
/// </summary>
internal interface ISettingsSession
{
    AppSettings Current { get; }
    bool IsDirty { get; }
    string? SaveProtectionReason { get; }
    void MarkDirty();
    bool TrySave();
    bool TryReplace(AppSettings settings, bool persist = true);
}
