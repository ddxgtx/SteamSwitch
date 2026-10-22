using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamSwitcher.Services;

internal static class SettingsImportService
{
    public static async Task<AppSettings> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        // FileInfo.Length 对不存在的文件抛 FileNotFoundException（不是
        // InvalidDataException），会导致调用方按未预期的异常类型处理。先显式校验。
        if (!File.Exists(path))
            throw new InvalidDataException("设置文件不存在。");
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("设置文件超过 1 MB 限制。");
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("设置文件必须是 JSON 对象。");
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Any(property => string.Equals(property.Name, "Format", StringComparison.OrdinalIgnoreCase) &&
            property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "SteamSwitch.Configuration"))
            throw new InvalidDataException("这是应用配置包，请使用“导入应用配置包”。");
        var known = typeof(AppSettings).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!properties.Any(property => known.Contains(property.Name)))
            throw new InvalidDataException("文件不包含可识别的 Steam Switch 设置，未修改当前配置。");
        var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("设置文件为空。");
        return SettingsSession.Copy(settings);
    }
}
