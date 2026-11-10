using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Dtimu.OnlineDecoder;

internal static class Modes
{
    /// <summary>H.264 8bit + AAC/MP3 且全部参数在档位内：纯重封装，零画质损失。</summary>
    public const string Remux = "remux";

    /// <summary>视频兼容但音频不是 AAC/MP3：视频直通，仅音频转 AAC。</summary>
    public const string RemuxAudio = "remux-audio";

    /// <summary>HEVC/VP9/超档 H.264 等 UHD630 可硬解的源：VAAPI 硬解 → scale_vaapi 落 8bit（超档同步降分辨率）→ 硬编。</summary>
    public const string Transcode = "transcode";

    /// <summary>Hi10P / AV1 等硬解不了的源：CPU 软解 → scale 落 nv12（超档同步降分辨率）→ hwupload → 硬编。</summary>
    public const string TranscodeSw = "transcode-sw";
}

/// <summary>ffprobe 探测的源裸参数（档位无关；档位规划见 <see cref="TierSpec.Plan"/>）。</summary>
internal sealed record MediaInfo(
    string VideoCodec,
    string? PixFmt,
    string? Profile,
    int? Level,
    string? AudioCodec,
    double DurationSec,
    int Width,
    int Height,
    double FrameRate);

/// <summary>ffprobe 封装（本地按 mtime 缓存，远程 URL 按 TTL 缓存）。</summary>
internal sealed class ProbeService(string ffprobePath)
{
    public static readonly string[] Extensions =
        [".mp4", ".m4v", ".mkv", ".mov", ".avi", ".ts", ".flv", ".webm", ".wmv", ".mpg", ".mpeg"];

    private sealed record CacheEntry(bool IsUrl, long Stamp, MediaInfo? Info);

    private static readonly TimeSpan UrlTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public static bool IsMediaFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>本地目标按 mtime 缓存；远程 URL（stream 模式）按 TTL 缓存。</summary>
    public MediaInfo? Get(string target)
    {
        bool isUrl = SourceResolver.IsHttpUrl(target);

        if (_cache.TryGetValue(target, out var hit))
        {
            if (isUrl && hit.IsUrl && DateTime.UtcNow.Ticks - hit.Stamp < UrlTtl.Ticks) return hit.Info;
            if (!isUrl && !hit.IsUrl && hit.Stamp == FileStamp(target)) return hit.Info;
        }

        long stamp = isUrl ? DateTime.UtcNow.Ticks : FileStamp(target);
        if (!isUrl && stamp < 0) return null;

        var info = ProbeCore(target);
        _cache[target] = new CacheEntry(isUrl, stamp, info);
        return info;
    }

    private static long FileStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return -1; }
    }

    private MediaInfo? ProbeCore(string source)
    {
        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in (string[])["-v", "error", "-print_format", "json", "-show_format", "-show_streams", source])
            psi.ArgumentList.Add(a);

        string json;
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            json = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            if (p.ExitCode != 0) return null;
        }
        catch { return null; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string vCodec = "";
            string? vPix = null, vProfile = null, aCodec = null;
            int? vLevel = null;
            int vWidth = 0, vHeight = 0;
            double vFps = 0;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    string? type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "video" && vCodec.Length == 0)
                    {
                        // 跳过 mkv 封面图之类的附加视频流
                        bool attached = s.TryGetProperty("disposition", out var d)
                            && d.TryGetProperty("attached_pic", out var ap)
                            && ap.ValueKind == JsonValueKind.True;
                        string cn = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                        if (attached || cn is "mjpeg" or "jpeg" or "png" or "bmp" or "gif") continue;

                        vCodec = cn;
                        vPix = s.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                        vProfile = s.TryGetProperty("profile", out var pr) ? pr.GetString() : null;
                        if (s.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number)
                            vLevel = lv.GetInt32();
                        if (s.TryGetProperty("width", out var wj) && wj.ValueKind == JsonValueKind.Number)
                            vWidth = wj.GetInt32();
                        if (s.TryGetProperty("height", out var hj) && hj.ValueKind == JsonValueKind.Number)
                            vHeight = hj.GetInt32();
                        vFps = ParseFrac(s.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() : null);
                        if (vFps <= 0)
                            vFps = ParseFrac(s.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString() : null);
                    }
                    else if (type == "audio" && aCodec is null)
                    {
                        aCodec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    }
                }
            }

            double duration = 0;
            if (root.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var dur)
                && dur.ValueKind == JsonValueKind.String
                && double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dd))
            {
                duration = dd;
            }

            if (vCodec.Length == 0) return null;

            return new MediaInfo(vCodec, vPix, vProfile, vLevel, aCodec, duration, vWidth, vHeight, vFps);
        }
        catch { return null; }
    }

    /// <summary>解析 ffprobe 的分数帧率字段（如 "60/1"、"30000/1001"）。</summary>
    private static double ParseFrac(string? s)
    {
        if (string.IsNullOrEmpty(s) || s == "0/0") return 0;
        var parts = s.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d > 0)
            return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
