using System.Security.Cryptography;
using System.Text;

namespace Dtimu.OnlineDecoder;

/// <summary>
/// 把 source= 后的描述解析为统一任务描述：
///   本地相对路径   → mediaRoot 下的文件
///   http(s)://URL  → stream 模式（默认）：URL 直接作为 ffmpeg/ffprobe 输入，源零落盘
///                    cache  模式：先下载到 scratch/dl 再转码（兼容 moov 在文件尾且服务器不支持 Range 的源）
/// 转码产物统一放 scratch/hls/&lt;源+档位哈希&gt;（scratch 指向 tmpfs 即零磁盘模式）。
/// </summary>
internal static class SourceResolver
{
    public readonly record struct Resolved(
        bool IsRemote,
        string SourceKey,      // 任务字典键：本地=绝对路径；远程=完整 URL（含 query）
        string FfmpegInput,    // ffmpeg -i 目标：本地路径 / 远程 URL / 下载缓存文件
        string DisplayName,    // 页面标题用（远程不含 query，避免泄露 token）
        string OutDir,         // 转码产物目录：scratch/hls/<hash(源+档位)>
        string? DownloadFile); // cache 模式下载缓存文件；stream 模式 null

    public static Resolved Resolve(string rest, string remoteQuery, string mediaRoot, string scratchRoot,
        bool remoteStream, TierSpec tier)
    {
        if (!IsHttpUrl(rest))
        {
            // 本地源：SourceKey 即绝对路径（外层用它做存在性/越界检查）
            string full = Path.GetFullPath(Path.Combine(mediaRoot, rest));
            return new Resolved(false, full, full, rest, OutDirOf(scratchRoot, full + "|" + tier.Id), null);
        }

        // query 在 ASP.NET 路由里不会进入 route 值，需要手动拼回完整 URL（tier 参数已在外层剥离）
        string url = remoteQuery.Length > 0 ? rest + remoteQuery : rest;
        var uri = new Uri(url);

        if (remoteStream)
        {
            // 纯网络模式：URL 直接作为 ffmpeg/ffprobe 输入（ffmpeg 的 https 默认不校验证书）
            return new Resolved(true, url, url, RemoteName(uri, url),
                OutDirOf(scratchRoot, url + "|" + tier.Id), null);
        }

        string ext = Path.GetExtension(uri.AbsolutePath);
        if (!ProbeService.IsMediaFile(ext)) ext = ".mp4";
        string cache = Path.Combine(scratchRoot, "dl", Hash16(url) + ext);
        return new Resolved(true, url, cache, RemoteName(uri, url),
            OutDirOf(scratchRoot, url + "|" + tier.Id), cache);
    }

    public static bool IsHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string RemoteName(Uri uri, string url)
    {
        string name = Path.GetFileName(uri.AbsolutePath);
        return name.Length > 0 ? name : Hash16(url) + ".mp4";
    }

    private static string OutDirOf(string scratchRoot, string sourceKey) =>
        Path.Combine(scratchRoot, "hls", Hash16(sourceKey));

    private static string Hash16(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}