using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Services;

public sealed class ConfigurationBundle
{
    public string Format { get; set; } = "SteamSwitch.Configuration";
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<SavedAccountEntry> Accounts { get; set; } = new();
    public Dictionary<int, GameBinding> GameBindings { get; set; } = new();
    public List<QuickLaunchItem> QuickLaunch { get; set; } = new();
}

/// <summary>Portable metadata configuration, with rollback for a failed/interrupted multi-file import.</summary>
public sealed class ConfigurationBundleService
{
    private const long MaxBundleBytes = 16 * 1024 * 1024;
    private const string JournalName = "configuration-import-pending.json";
    private static readonly string[] FileNames = { "settings.json", "accounts.json", "gamebindings.json", "quicklaunch.json" };
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _directory;
    private readonly Action<int>? _afterWrite;

    public ConfigurationBundleService(string directory) : this(directory, null) { }
    internal ConfigurationBundleService(string directory, Action<int>? afterWrite)
    {
        _directory = Path.GetFullPath(directory);
        _afterWrite = afterWrite;
    }

    public static ConfigurationBundle Prepare(ConfigurationBundle input)
    {
        if (input.Format != "SteamSwitch.Configuration" || input.Version != 1 || input.Settings == null ||
            input.Accounts == null || input.GameBindings == null || input.QuickLaunch == null)
            throw new InvalidDataException("不是受支持的 Steam Switch 应用配置包。");
        if (input.Accounts.Count > 10000 || input.GameBindings.Count > 10000 || input.QuickLaunch.Count > 10000)
            throw new InvalidDataException("配置包包含的项目数量超过限制。");

        // Explicit account whitelist: never carry credentials or machine-local avatar paths.
        var accounts = input.Accounts.Select(entry =>
        {
            if (entry == null || !AccountLibraryService.IsValidSteamId(entry.SteamId))
                throw new InvalidDataException("账号档案的 Steam ID 无效。");
            if (!entry.IsIgnored && (string.IsNullOrWhiteSpace(entry.AccountName) || entry.AccountName.Length > 128 ||
                entry.AccountName.Any(char.IsControl) || entry.PersonaName == null || entry.PersonaName.Length > 256 ||
                entry.PersonaName.Any(char.IsControl) || entry.Timestamp is < 0 or > 253402300799 ||
                entry.LastSeenUnix is < 0 or > 253402300799))
                throw new InvalidDataException("账号档案包含无效名称或时间，未导入任何内容。");
            return new SavedAccountEntry
            {
                SteamId = entry.SteamId,
                IsIgnored = entry.IsIgnored,
                AccountName = entry.IsIgnored ? string.Empty : entry.AccountName ?? string.Empty,
                PersonaName = entry.IsIgnored ? string.Empty : entry.PersonaName ?? string.Empty,
                Timestamp = entry.IsIgnored ? 0 : Math.Max(0, entry.Timestamp),
                LastSeenUnix = entry.IsIgnored ? 0 : Math.Max(0, entry.LastSeenUnix)
            };
        }).ToList();
        if (accounts.Select(a => a.SteamId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Count)
            throw new InvalidDataException("账号档案包含重复 Steam ID。");
        if (input.GameBindings.Any(pair => pair.Key <= 0 || pair.Value == null || pair.Value.AppId != pair.Key))
            throw new InvalidDataException("游戏绑定包含无效或不一致的 AppID。");
        if (input.QuickLaunch.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ExecutablePath)) ||
            input.QuickLaunch.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.QuickLaunch.Count)
            throw new InvalidDataException("快速启动项目缺少目标或含重复 ID。");

        var settings = SettingsSession.Copy(input.Settings);
        return new ConfigurationBundle
        {
            Settings = settings,
            Accounts = accounts,
            GameBindings = JsonSerializer.Deserialize<Dictionary<int, GameBinding>>(JsonSerializer.Serialize(input.GameBindings))!,
            QuickLaunch = JsonSerializer.Deserialize<List<QuickLaunchItem>>(JsonSerializer.Serialize(input.QuickLaunch))!
        };
    }

    public static async Task<ConfigurationBundle> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxBundleBytes)
            throw new InvalidDataException("应用配置包为空或超过 16 MB 限制。");
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBundleBytes)
            throw new InvalidDataException("应用配置包超过 16 MB 限制。");
        using var document = JsonDocument.Parse(json);
        var required = new[] { "Format", "Version", "Settings", "Accounts", "GameBindings", "QuickLaunch" };
        if (document.RootElement.ValueKind != JsonValueKind.Object || required.Any(name =>
            !document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("应用配置包缺少必要字段，未修改当前配置。");
        var bundle = JsonSerializer.Deserialize<ConfigurationBundle>(json, JsonOptions)
            ?? throw new InvalidDataException("应用配置包内容为空。");
        return Prepare(bundle);
    }

    public static Task ExportAsync(ConfigurationBundle bundle, string path, CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(bundle);
        prepared.Settings.DesktopIconsHiddenByApp = false;
        if (JsonSerializer.SerializeToUtf8Bytes(prepared, JsonOptions).LongLength > MaxBundleBytes)
            throw new InvalidDataException("应用配置包超过 16 MB 限制。");
        return AtomicJsonFile.WriteAsync(path, prepared, JsonOptions, cancellationToken);
    }

    public async Task ImportAsync(ConfigurationBundle bundle, CancellationToken cancellationToken = default, Func<Task>? applyRuntime = null)
    {
        var validated = Prepare(bundle);
        await ImportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            RecoverInterruptedImport(_directory);
            var transactionName = ".configuration-import-" + Guid.NewGuid().ToString("N");
            var transaction = Path.Combine(_directory, transactionName);
            Directory.CreateDirectory(transaction);
            var originals = FileNames.SelectMany(name => new[] { name, name + ".bak" }).ToArray();
            var existing = originals.Where(name => File.Exists(Path.Combine(_directory, name))).ToArray();
            foreach (var name in existing)
                File.Copy(Path.Combine(_directory, name), Path.Combine(transaction, name));

            var journal = new ImportJournal { TransactionName = transactionName, ExistingFiles = existing };
            var journalPath = Path.Combine(_directory, JournalName);
            var temporaryJournal = Path.Combine(transaction, "journal.json");
            await using (var stream = new FileStream(temporaryJournal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryJournal, journalPath);
            try
            {
                await AtomicJsonFile.WriteAsync(Path.Combine(_directory, "accounts.json"), validated.Accounts, JsonOptions, cancellationToken).ConfigureAwait(false);
                _afterWrite?.Invoke(1);
                await AtomicJsonFile.WriteAsync(Path.Combine(_directory, "gamebindings.json"), validated.GameBindings, JsonOptions, cancellationToken).ConfigureAwait(false);
                _afterWrite?.Invoke(2);
                await AtomicJsonFile.WriteAsync(Path.Combine(_directory, "quicklaunch.json"), validated.QuickLaunch, JsonOptions, cancellationToken).ConfigureAwait(false);
                _afterWrite?.Invoke(3);
                // settings.json 必须和 SettingsService.Save 共用同一把锁：两套锁互不
                // 通气时，配置包导入与去抖自动保存并发会让 File.Replace 失败或互相覆盖。
                var settingsPath = Path.Combine(_directory, "settings.json");
                await Task.Run(() => SettingsService.RunUnderSettingsFileLock(
                    () => AtomicJsonFile.WriteAsync(settingsPath, validated.Settings, JsonOptions, cancellationToken)
                        .GetAwaiter().GetResult()), cancellationToken).ConfigureAwait(false);
                _afterWrite?.Invoke(4);
                // 四个数据文件已全部落盘，先把"提交标记"（journal）删掉，再应用到运行时。
                // 这样即使应用运行时阶段崩溃/抛异常，下次启动也不会把一次已写盘成功的导入
                // 误判为"未完成"而回滚掉。若 applyRuntime 失败，下面的 catch 仍会把数据文件
                // 从事务副本还原回去。
                File.Delete(journalPath);
                if (applyRuntime != null) await applyRuntime().ConfigureAwait(false);
            }
            catch
            {
                RestoreOriginalFiles(_directory, transaction, existing);
                File.Delete(journalPath);
                throw;
            }
            finally
            {
                // Keep rollback data when recovery itself failed.
                if (!File.Exists(journalPath)) TryRemoveTransaction(transaction);
            }
        }
        finally { ImportGate.Release(); }
    }

    public static void RecoverInterruptedImport(string directory)
    {
        var root = Path.GetFullPath(directory);
        var journalPath = Path.Combine(root, JournalName);
        if (!File.Exists(journalPath)) return;
        var journalInfo = new FileInfo(journalPath);
        if (journalInfo.Length > 16384) throw new InvalidDataException("配置恢复记录无效。");
        var journal = JsonSerializer.Deserialize<ImportJournal>(File.ReadAllText(journalPath))
            ?? throw new InvalidDataException("配置恢复记录为空。");
        var prefix = ".configuration-import-";
        var allowed = FileNames.SelectMany(name => new[] { name, name + ".bak" }).ToHashSet(StringComparer.Ordinal);
        if (!journal.TransactionName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(journal.TransactionName[prefix.Length..], "N", out _) ||
            journal.ExistingFiles == null || journal.ExistingFiles.Any(name => !allowed.Contains(name)))
            throw new InvalidDataException("配置恢复记录包含无效路径。");
        var transaction = Path.Combine(root, journal.TransactionName);
        if (!Directory.Exists(transaction)) throw new IOException("配置恢复副本缺失，原配置未被覆盖。");
        RestoreOriginalFiles(root, transaction, journal.ExistingFiles);
        File.Delete(journalPath);
        TryRemoveTransaction(transaction);
        AppLogger.Warn("Recovered configuration files from an interrupted import.");
    }

    /// <summary>
    /// 恢复记录本身损坏（记录非法、恢复副本缺失）时 RecoverInterruptedImport 会抛异常。
    /// 若让异常冒泡到启动流程，应用会直接退出，且此后每次启动都进不去、也无法在界面里
    /// 执行"一键修复"。这里把无法采信的记录连同暂存目录一并清掉，
    /// 让应用能用磁盘上的当前配置正常启动——原配置从未被覆盖，丢弃是安全的。
    /// </summary>
    public static void DiscardInterruptedImport(string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory);
            var journalPath = Path.Combine(root, JournalName);
            if (File.Exists(journalPath))
                File.Delete(journalPath);

            if (!Directory.Exists(root)) return;
            foreach (var staging in Directory.GetDirectories(root, ".configuration-import-*"))
                TryRemoveTransaction(staging);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Unable to discard the interrupted configuration import record: {ex.Message}");
        }
    }

    private static void RestoreOriginalFiles(string directory, string transaction, IEnumerable<string> existing)
    {
        var originals = existing.ToHashSet(StringComparer.Ordinal);
        foreach (var name in originals)
            if (!File.Exists(Path.Combine(transaction, name))) throw new IOException($"配置恢复副本缺失：{name}");
        foreach (var name in FileNames.SelectMany(file => new[] { file, file + ".bak" }))
        {
            var target = Path.Combine(directory, name);
            if (originals.Contains(name))
            {
                var temporary = target + ".rollback-" + Guid.NewGuid().ToString("N");
                File.Copy(Path.Combine(transaction, name), temporary);
                File.Move(temporary, target, overwrite: true);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
    }

    private static void TryRemoveTransaction(string transaction)
    {
        try { Directory.Delete(transaction, recursive: true); }
        catch (Exception ex) { AppLogger.Warn($"Unable to clean configuration import staging: {ex.Message}"); }
    }

    private sealed class ImportJournal
    {
        public string TransactionName { get; set; } = string.Empty;
        public string[] ExistingFiles { get; set; } = Array.Empty<string>();
    }
}
