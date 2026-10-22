using System;
using System.Text.Json;

namespace SteamSwitcher.Services;

/// <summary>Owns the live settings snapshot and keeps failed writes pending.</summary>
internal sealed class SettingsSession
{
    private readonly Func<AppSettings, bool> _save;
    public AppSettings Current { get; private set; }
    public bool IsDirty { get; private set; }

    public SettingsSession(AppSettings initial, Func<AppSettings, bool> save)
    {
        _save = save;
        Current = Copy(initial);
    }

    public static AppSettings Copy(AppSettings settings) => SettingsService.Normalize(
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
        ?? throw new ArgumentException("设置不能为空。", nameof(settings)));

    public void MarkDirty() => IsDirty = true;

    public bool TrySave()
    {
        if (!IsDirty) return true;
        if (!_save(Current)) return false;
        IsDirty = false;
        return true;
    }

    public bool TryReplace(AppSettings settings, bool persist = true)
    {
        var next = Copy(settings);
        // Do not discard the current snapshot or pending edits on a failed import.
        if (persist && !_save(next)) return false;
        Current = next;
        IsDirty = false;
        return true;
    }
}
