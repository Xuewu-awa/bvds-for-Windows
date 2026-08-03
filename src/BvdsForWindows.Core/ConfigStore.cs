using System.IO;
using System.Text.Json;

namespace BvdsForWindows.Core;

/// <summary>
/// 配置读写（对应 Android SharedPreferences 封装 ConfigStore）。
/// 持久化到 %AppData%/bvds/config.json。
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values = new();

    public ConfigStore(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bvds");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (json == null) return;
            foreach (var (k, v) in json) _values[k] = v;
        }
        catch { /* 配置损坏时忽略，用默认值 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public string this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : "";
        set { _values[key] = value; Save(); }
    }

    public void Remove(string key) { _values.Remove(key); Save(); }

    // ── 类型化便捷属性 ────────────────────────────

    public string Theme { get => this["theme"].IfEmpty("teal"); set => this["theme"] = value; }
    public string Quality { get => this["quality"].IfEmpty("2"); set => this["quality"] = value; }

    public string DownloadDir
    {
        get => this["download_dir"].IfEmpty(DefaultDir);
        set { this["download_dir"] = value; try { Directory.CreateDirectory(value); } catch { } }
    }

    public string Cookies { get => this["cookies"]; set => this["cookies"] = value; }
    public void ClearCookies() => Remove("cookies");

    /// <summary>最大并发下载数（1-64，默认 5）</summary>
    public int MaxConcurrent
    {
        get => int.TryParse(this["max_concurrent"], out var n) ? Math.Clamp(n, 1, 64) : 5;
        set => this["max_concurrent"] = Math.Clamp(value, 1, 64).ToString();
    }

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "bvds");

    // ── 清晰度映射（与前端 QUALITY_MAP 一致） ────

    public static readonly IReadOnlyDictionary<string, int> QualityMap = new Dictionary<string, int>
    {
        ["1"] = 120,  // 4K
        ["2"] = 80,   // 1080P
        ["3"] = 64,   // 720P
        ["4"] = 32,   // 480P
        ["5"] = 16,   // 360P
    };

    public static readonly IReadOnlyDictionary<int, string> QualityNames = new Dictionary<int, string>
    {
        [120] = "4K 超高清",
        [80] = "1080P 高清",
        [64] = "720P 准高清",
        [32] = "480P 清晰",
        [16] = "360P 流畅",
    };

    public static int QualityCode(string key)
    {
        var idx = int.TryParse(key, out var i) ? i : 2;
        var k = Math.Clamp(idx - 1, 0, 4);
        return new[] { 120, 80, 64, 32, 16 }[k];
    }

    public static string QualityName(int code) => QualityNames.TryGetValue(code, out var n) ? n : code.ToString();
}

internal static class StringExt
{
    public static string IfEmpty(this string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
