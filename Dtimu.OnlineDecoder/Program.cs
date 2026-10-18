using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dtimu.OnlineDecoder;

var builder = WebApplication.CreateSlimBuilder(args);

static string Cfg(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

// 监听地址：默认 0.0.0.0（手机才能访问），DTIMU_URLS 可覆盖
string urls = Cfg("DTIMU_URLS",
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is { Length: > 0 } u ? u : "http://0.0.0.0:8090");
builder.WebHost.UseUrls(urls);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

var app = builder.Build();

string mediaRoot = Path.GetFullPath(Cfg("DTIMU_MEDIA_ROOT", Environment.CurrentDirectory));
// 产物与下载缓存根目录：指向 tmpfs（如 /dev/shm/dtimu）即零磁盘模式
string scratchRoot = Path.GetFullPath(Cfg("DTIMU_SCRATCH_ROOT", Path.Combine(mediaRoot, ".scratch")));
int qp = int.TryParse(Cfg("DTIMU_QP", "16"), out var q) ? q : 16;
string ffmpegPath = Cfg("DTIMU_FFMPEG", "ffmpeg");
string vaapiDevice = Cfg("DTIMU_VAAPI_DEVICE", "/dev/dri/renderD128");
bool insecureTls = Cfg("DTIMU_INSECURE_TLS", "0") == "1";
// 远程源：stream=ffmpeg 直连 URL 零落盘（默认）；cache=先下载到 scratch 再转码
// （cache 用于兼容 moov 在文件尾且服务器不支持 Range 请求的源站）
bool remoteStream = !string.Equals(Cfg("DTIMU_REMOTE_MODE", "stream"), "cache", StringComparison.OrdinalIgnoreCase);
// 空闲清理：转码完成且无人访问超过 N 分钟 → 删除产物释放空间；0=永久保留
int idleMin = int.TryParse(Cfg("DTIMU_IDLE_DELETE_MIN", "0"), out var im) ? Math.Max(0, im) : 0;

var probe = new ProbeService(Cfg("DTIMU_FFPROBE", "ffprobe"));
var manager = new TranscodeManager(ffmpegPath, vaapiDevice, qp, probe, insecureTls, idleMin);
manager.StartSweeper();

app.Lifetime.ApplicationStopping.Register(manager.KillAll);

app.Logger.LogInformation("Dtimu.OnlineDecoder 媒体根目录 : {Root}", mediaRoot);
app.Logger.LogInformation("FFmpeg: {Ffmpeg} | QP: {Qp} | VAAPI 设备: {Dev}", ffmpegPath, qp, vaapiDevice);
app.Logger.LogInformation("产物目录 : {Scratch} | 远程模式 : {Mode} | 空闲清理 : {Idle} | 自签 TLS : {Tls}",
    scratchRoot, remoteStream ? "stream 直连" : "cache 缓存", idleMin > 0 ? idleMin + " 分钟" : "关闭", insecureTls);

app.UseStaticFiles(); // wwwroot/hls.min.js

// 唯一入口：/{*route}，形如：
//   source=movies/demo.mp4                      （本地相对路径）
//   source=http://host/videos/demo.mp4          （远程 http(s) URL，自动下载到缓存再转码）
// 虚拟文件挂在同一 URL 树下：.../index.m3u8、.../init.mp4、.../seg-*.m4s、
//   .../status.json、.../file.mp4（fMP4 拼接直出，老设备兜底）
app.MapGet("/{*route}", async (string? route, HttpContext http) =>
{
    if (string.IsNullOrEmpty(route) || !route.StartsWith("source=", StringComparison.Ordinal))
    {
http.Response.StatusCode = 404;
        return Results.Text(
            PlayerHtml.Error("404", "用法：/source=<相对媒体根目录的视频路径 或 http(s)://远程视频URL>，例如 /source=movies/demo.mp4"),
            "text/html; charset=utf-8");
    }

    string rest = route["source=".Length..].Trim();

    // 拆虚拟文件名：仅当最后一段是已知虚拟文件时拆分，否则整段都是源描述
    // （本地源必须是视频扩展名，不会与 index.m3u8/seg-*.m4s 冲突）
    string? vfile = null;
    int slash = rest.LastIndexOf('/');
    if (slash >= 0)
    {
        string tail = rest[(slash + 1)..];
        if (tail is "index.m3u8" or "init.mp4" or "status.json" or "file.mp4"
            || RoutePatterns.SegFile().IsMatch(tail))
        {
            vfile = tail;
            rest = rest[..slash];
        }
    }
    if (rest.Length == 0)
    {
        http.Response.StatusCode = 404;
        return Results.Text(PlayerHtml.Error("404", "source 为空"), "text/html; charset=utf-8");
    }

    // query（如 ?token=xxx）在 ASP.NET 路由里不进 route 值，需拼回远程 URL
    var src = SourceResolver.Resolve(rest, http.Request.QueryString.Value ?? "", mediaRoot, scratchRoot, remoteStream);

    if (!src.IsRemote)
    {
        if (!ProbeService.IsMediaFile(rest))
        {
            http.Response.StatusCode = 404;
            return Results.Text(
                PlayerHtml.Error("404", "不支持该文件类型，仅支持：" + string.Join(' ', ProbeService.Extensions)),
                "text/html; charset=utf-8");
        }
        if (!IsUnderRoot(src.SourceKey, mediaRoot))
        {
            http.Response.StatusCode = 403;
            return Results.Text(PlayerHtml.Error("403", "路径越界：" + rest), "text/html; charset=utf-8");
        }
        if (!File.Exists(src.SourceKey))
        {
            http.Response.StatusCode = 404;
            return Results.Text(PlayerHtml.Error("404", "文件不存在：" + rest), "text/html; charset=utf-8");
        }
    }

    switch (vfile)
    {
        // 播放页：/source=xxx.mp4 —— vfile 为 null 表示尾段不是虚拟文件
        // （重构后 vfile 只会是 null 或虚拟文件名，写 case "" 永远匹配不到，曾导致播放页 404）
        case null:
        {
            var job = manager.GetOrStart(src);
            return Results.Text(PlayerHtml.Player(src.DisplayName, job.Mode, qp, job.IsRemote, job.Res),
                "text/html; charset=utf-8");
        }

        // 转码状态：/source=xxx.mp4/status.json
        case "status.json":
        {
            var st = manager.GetOrStart(src).Snapshot();
            http.Response.Headers.CacheControl = "no-store";
            // AOT：必须显式传 JsonTypeInfo，不能用默认重载
            return Results.Json(st, AppJson.Default.StatusDto);
        }

        // 老设备兜底（Lumia Edge 等无 HLS/MSE）：fMP4 拼接直出，原生 video 标签渐进式播放
        case "file.mp4":
        {
            var job = manager.GetOrStart(src);

            // 完成态：物理拼接产物（Content-Length + Range 已知，任意 seek）
            if (File.Exists(Path.Combine(job.OutDir, ".done")))
            {
                string? flat = manager.EnsureFlatFile(job);
                if (flat is null) return Results.NotFound();
                http.Response.Headers.CacheControl = "no-store"; // 重转后同名内容变化，禁缓存
                return Results.File(flat, "video/mp4", enableRangeProcessing: true);
            }

            // 转码中：快照式流式拼接当前已有分片（不可 seek，播到当前转码进度为止）
            var snap = manager.BuildFlatSnapshot(job);
            if (snap is null) return Results.NotFound();

            http.Response.Headers.CacheControl = "no-store";
            http.Response.ContentLength = snap.Value.TotalBytes;
            return Results.Stream(async body =>
            {
                foreach (var f in snap.Value.Files)
                {
                    await using var fs = File.OpenRead(f);
                    await fs.CopyToAsync(body, 256 * 1024, http.RequestAborted);
                }
            }, "video/mp4");
        }

        // HLS 虚拟目录：index.m3u8 / init.mp4 / seg-*.m4s
        default:
        {
            // 走到这里 vfile 必非 null（上面 case null 已接走）
            if (vfile is not ("index.m3u8" or "init.mp4") && !RoutePatterns.SegFile().IsMatch(vfile))
                return Results.NotFound();

            var job = manager.GetOrStart(src);
            string path = Path.Combine(job.OutDir, vfile);
            if (!File.Exists(path)) return Results.NotFound();

            if (vfile.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                // playlist 在转码完成前会持续增长，必须禁缓存
                http.Response.Headers.CacheControl = "no-store";
                return Results.File(path, "application/vnd.apple.mpegurl");
            }

            // 同名分片在重转码（指纹变化）后内容会变，immutable 会把旧 4K60 产物钉死在手机缓存里
            // → 一律 no-store；内网带宽充足，牺牲跨会话缓存换正确性
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(path,
                vfile == "init.mp4" ? "video/mp4" : "video/iso.segment",
                enableRangeProcessing: true);
        }
    }
});

app.Run();

static bool IsUnderRoot(string path, string root)
{
    string r = Path.TrimEndingDirectorySeparator(root);
    return string.Equals(path, r, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

internal static partial class RoutePatterns
{
    [GeneratedRegex(@"^seg-\d+\.m4s$")]
    public static partial Regex SegFile();
}