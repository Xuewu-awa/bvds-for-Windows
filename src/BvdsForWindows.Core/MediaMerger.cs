using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BvdsForWindows.Core;

/// <summary>
/// 音视频合轨（对应 Android MediaMerger，但用捆绑的 ffmpeg -c copy 实现）。
/// Windows 没有类似 MediaMuxer 的简单系统 API（Media Foundation 的 MP4 Sink 对
/// B站 DASH 分离流支持极差且 COM 管线复杂），故捆绑 ffmpeg 便携版。
/// </summary>
public sealed class MediaMerger
{
    private readonly Action<string> _log;
    private readonly string? _ffmpegPath;

    public MediaMerger(Action<string> log, string? ffmpegPath = null)
    {
        _log = log;
        _ffmpegPath = ffmpegPath ?? FindFfmpeg();
        if (_ffmpegPath == null)
            _log("警告: 未找到 ffmpeg，合轨模式将不可用");
    }

    /// <summary>查找 ffmpeg：优先项目捆绑目录，其次 PATH</summary>
    public static string? FindFfmpeg()
    {
        // 1. 程序目录（发布后 tools/ffmpeg/ffmpeg.exe 随输出复制）
        var direct = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(direct)) return direct;

        // 2. 源码仓库根（调试时 tools/ 在仓库根，bin/Debug/net10.0 向上 5 级）
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        direct = Path.Combine(repoRoot, "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(direct)) return direct;

        // 3. 解压后的子目录（ffmpeg-xxx-essentials_build/bin/ffmpeg.exe）
        foreach (var root in new[] { AppContext.BaseDirectory, repoRoot })
        {
            var toolsDir = Path.Combine(root, "tools", "ffmpeg");
            if (Directory.Exists(toolsDir))
            {
                var sub = Directory.GetDirectories(toolsDir)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "ffmpeg.exe")));
                if (sub != null) return Path.Combine(sub, "bin", "ffmpeg.exe");
            }
        }

        // 4. PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var line = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(line) && File.Exists(line.Trim()))
                    return line.Trim();
            }
        }
        catch { }

        return null;
    }

    /// <summary>合并音视频（-c copy 不转码，秒级完成）。返回合并后的文件路径。</summary>
    public async Task<string?> MergeAsync(
        string videoPath, string audioPath, string outputPath, CancellationToken ct = default)
    {
        if (_ffmpegPath == null) return null;
        if (!File.Exists(videoPath) || !File.Exists(audioPath)) return null;

        _log($"ffmpeg 合轨: {Path.GetFileName(videoPath)} + {Path.GetFileName(audioPath)}");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // -hide_banner -y -i video -i audio -c copy -map 0:v:0 -map 1:a:0 output
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1:a:0");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(outputPath);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            _log($"ffmpeg 失败 exit={proc.ExitCode}: {stderr}");
            return null;
        }
        return File.Exists(outputPath) ? outputPath : null;
    }
}
