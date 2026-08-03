using System.IO;

namespace BvdsForWindows.Core;

/// <summary>
/// 日志收集（对应 LogCollector）：内存环形缓冲 + 可导出到文件。
/// </summary>
public sealed class LogCollector
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();
    private readonly int _maxEntries;
    private readonly string? _logDir;

    public event Action<string>? EntryAdded;

    public LogCollector(int maxEntries = 500, string? logDir = null)
    {
        _maxEntries = maxEntries;
        _logDir = logDir;
    }

    public void Log(string tag, string msg, bool isError = false)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}][{(isError ? "ERR" : "INF")}][{tag}] {msg}";
        _entries.Enqueue(line);
        while (_entries.Count > _maxEntries) _entries.TryDequeue(out _);
        EntryAdded?.Invoke(line);
    }

    public string AllLogs() => string.Join(Environment.NewLine, _entries);

    public void Clear() { while (_entries.TryDequeue(out _)) { } }

    public string? ExportTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"bvds_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, AllLogs(), System.Text.Encoding.UTF8);
            return path;
        }
        catch { return null; }
    }
}
