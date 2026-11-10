using System.Net;
using System.Net.Sockets;
using System.Text;
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
bool remoteStream = !string.Equals(Cfg("DTIMU_REMOTE_MODE", "stream"), "cache", StringComparison.OrdinalIgnoreCase);
// 空闲清理：转码完成且无人访问超过 N 分钟 → 删除产物释放空间；0=永久保留
int idleMin = int.TryParse(Cfg("DTIMU_IDLE_DELETE_MIN", "0"), out var im) ? Math.Max(0, im) : 0;
// CORS：放行内网来源页面跨域引用（hls.js 拉流 / fetch 状态接口）；默认 192.168.0.0/16，可改 CIDR 列表 / 加精确 Origin 白名单；0=关闭
bool corsEnabled = Cfg("DTIMU_CORS", "1") == "1";
string[] corsCidrs = Cfg("DTIMU_CORS_CIDRS", "192.168.0.0/16")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
string[] corsExtraOrigins = Cfg("DTIMU_CORS_ORIGINS", "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var probe = new ProbeService(Cfg("DTIMU_FFPROBE", "ffprobe"));
// 档位系统：TierSpec 纯数据 + TierPresets 库预设 + TierRegistry 生效集合/默认档/UA 路由（见 TIERS.md）
var registry = new TierRegistry(TierPresets.All, Cfg("DTIMU_DEFAULT_TIER", TierPresets.Mid.Id));
var manager = new TranscodeManager(ffmpegPath, vaapiDevice, qp, probe, insecureTls, idleMin, registry);
manager.StartSweeper();

app.Lifetime.ApplicationStopping.Register(manager.KillAll);

app.Logger.LogInformation("Dtimu.OnlineDecoder 媒体根目录 : {Root}", mediaRoot);
app.Logger.LogInformation("FFmpeg: {Ffmpeg} | QP: {Qp} | VAAPI 设备: {Dev}", ffmpegPath, qp, vaapiDevice);
app.Logger.LogInformation("产物目录 : {Scratch} | 远程模式 : {Mode} | 空闲清理 : {Idle} | 自签 TLS : {Tls}",
    scratchRoot, remoteStream ? "stream 直连" : "cache 缓存", idleMin > 0 ? idleMin + " 分钟" : "关闭", insecureTls);
app.Logger.LogInformation("档位 : {Count} 档 [{Ids}]，默认 {Def}（按 UA 识别，?tier= 可强制；详见 TIERS.md）",
    registry.Tiers.Count, string.Join("/", registry.Tiers.Select(t => t.Id)), registry.Default.Id);
app.Logger.LogInformation("CORS : {State} | 放行网段 : {Cidrs} | 额外白名单 : {Origins}",
    corsEnabled ? "开启" : "关闭", string.Join(", ", corsCidrs),
    corsExtraOrigins.Length > 0 ? string.Join(", ", corsExtraOrigins) : "无");

// ---- CORS 中间件：仅放行内网 CIDR 来源页面的跨域引用；预检 OPTIONS 短路 204 ----
// （catch-all 只映射 GET，预检不短路会 404；带 Origin 头才处理，同源请求不受影响）
app.Use(async (ctx, next) =>
{
    if (!corsEnabled)
    {
        await next();
        return;
    }

    string origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length == 0)
    {
        await next();
        return;
    }

    if (!CorsAllowed(origin, corsExtraOrigins, corsCidrs))
    {
        await next(); // 白名单外：不加 CORS 头，浏览器侧自行拒绝
        return;
    }

    ctx.Response.Headers.AccessControlAllowOrigin = origin;          // 回显来源（比 * 精确，且兼容带凭据场景）
    ctx.Response.Headers.AccessControlAllowMethods = "GET, HEAD, OPTIONS";
    ctx.Response.Headers.AccessControlExposeHeaders = "Content-Length, Content-Range, Accept-Ranges";
    ctx.Response.Headers.AccessControlMaxAge = "86400";
    ctx.Response.Headers.Vary = "Origin";
    // 预检允许头：回显请求方要求的头（* 在部分老内核上不受信任，回显最稳）
    string reqHeaders = ctx.Request.Headers.AccessControlRequestHeaders.ToString();
    ctx.Response.Headers.AccessControlAllowHeaders = reqHeaders.Length > 0 ? reqHeaders : "*";

    if (HttpMethods.IsOptions(ctx.Request.Method))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return; // 预检短路
    }

    await next();
});

app.UseStaticFiles(); // wwwroot/hls.min.js

// 唯一入口：/{*route}
//   source=movies/demo.mp4              本地相对路径
//   source=http://host/videos/demo.mp4  远程 http(s) URL（stream 直连 / cache 缓存后转码）
// 档位：?tier=low|mid|high 显式指定；缺省按浏览器 UA 识别（KaiOS→低 / X100s、桌面→高 / 其余→中）
// 虚拟文件：.../index.m3u8、.../init.mp4、.../seg-*.m4s、.../status.json、.../file.mp4（老设备兜底）
app.MapGet("/{*route}", (string? route, HttpContext http) =>
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

    // ---- 档位解析：?tier= 显式优先，否则按 UA（媒体 URL 的 tier 由播放页/playlist 显式携带）----
    string? tierParam = http.Request.Query.TryGetValue("tier", out var tv) && tv.Count > 0 ? tv.ToString() : null;
    var tier = registry.Parse(tierParam) ?? registry.FromUserAgent(http.Request.Headers.UserAgent.ToString());

    // ---- 源 query 重建：把 tier 参数从 query 剥离，其余（token 等）完整保留给远程源 URL ----
    string rawQuery = http.Request.QueryString.Value ?? "";
    string remoteQuery = rawQuery;
    if (tierParam is not null && rawQuery.Length > 0)
    {
        var m = RoutePatterns.TierQuery().Match(rawQuery);
        if (m.Success)
        {
            remoteQuery = rawQuery.Remove(m.Index, m.Length);
            if (remoteQuery.StartsWith('&')) remoteQuery = "?" + remoteQuery[1..];
        }
    }

    var src = SourceResolver.Resolve(rest, remoteQuery, mediaRoot, scratchRoot, remoteStream, tier);

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
        case null:
        {
            var job = manager.GetOrStart(src, tier);
            // 徽章 QP 用本档生效值（档位可覆盖全局）
            int pageQp = job.EffQp > 0 ? job.EffQp : qp;
            return Results.Text(
                PlayerHtml.Player(src.DisplayName, job.Mode, pageQp, job.IsRemote, job.Res,
                    tier.Id, tier.Label,
                    PlayerHtml.TierLinks(registry.Tiers, http.Request.Path.Value ?? "/", remoteQuery, tier.Id)),
                "text/html; charset=utf-8");
        }

        // 转码状态：/source=xxx.mp4/status.json
        case "status.json":
        {
            var st = manager.GetOrStart(src, tier).Snapshot();
            http.Response.Headers.CacheControl = "no-store";
            // AOT：必须显式传 JsonTypeInfo，不能用默认重载
            return Results.Json(st, AppJson.Default.StatusDto);
        }

        // 老设备兜底（Lumia Edge 等无 HLS/MSE）：fMP4 拼接直出，原生 video 标签渐进式播放
        case "file.mp4":
        {
            var job = manager.GetOrStart(src, tier);

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

            var job = manager.GetOrStart(src, tier);
            string path = Path.Combine(job.OutDir, vfile);
            if (!File.Exists(path)) return Results.NotFound();

            if (vfile.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                // playlist 在转码完成前会持续增长，必须禁缓存
                http.Response.Headers.CacheControl = "no-store";

                // 关键：playlist 里的分片/MAP URI 是相对名，HLS 客户端相对解析会丢 query
                // → 把当前 query（tier + token）拼到每个 URI 上，
                //   保证分片请求路由回同一源、同一档位（含 token 的鉴权源）
                string text = File.ReadAllText(path);
                if (rawQuery.Length > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var rawLine in text.Split('\n'))
                    {
                        string l = rawLine.TrimEnd('\r');
                        if (l.Length > 0)
                        {
                            if (l.StartsWith('#'))
                            {
                                if (l.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
                                    l = l.Replace("URI=\"init.mp4\"", "URI=\"init.mp4" + rawQuery + "\"");
                            }
                            else
                            {
                                l += rawQuery; // seg-XXXXX.m4s?tier=..&token=..
                            }
                        }
                        sb.Append(l).Append('\n');
                    }
                    text = sb.ToString();
                }
                return Results.Text(text, "application/vnd.apple.mpegurl");
            }

            // 同名分片在重转码（指纹变化）后内容会变，immutable 会把旧产物钉死在手机缓存里
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

/// <summary>Origin 跨域放行判定：host 为配置网段内的 IPv4，或命中精确 Origin 白名单。</summary>
static bool CorsAllowed(string origin, string[] extraOrigins, string[] cidrs)
{
    foreach (var e in extraOrigins)
        if (string.Equals(e, origin, StringComparison.OrdinalIgnoreCase)) return true;

    if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
    // IPv6 字面量 Host 带方括号，统一剥掉；域名来源不做 IP 网段判定，走显式白名单
    if (!IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip)) return false;
    if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

    byte[] b = ip.GetAddressBytes();
    foreach (var c in cidrs)
    {
        var parts = c.Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var net)
            || net.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out int prefix) || prefix is < 0 or > 32) continue;

        byte[] nb = net.GetAddressBytes();
        int full = prefix / 8, rem = prefix % 8;
        bool ok = true;
        for (int i = 0; i < full && ok; i++) ok = b[i] == nb[i];
        if (ok && rem > 0)
        {
            int mask = 0xFF << (8 - rem);
            ok = (b[full] & mask) == (nb[full] & mask);
        }
        if (ok) return true;
    }
    return false;
}

internal static partial class RoutePatterns
{
    [GeneratedRegex(@"^seg-\d+\.m4s$")]
    public static partial Regex SegFile();

    [GeneratedRegex(@"[?&]tier=[^&]*")]
    public static partial Regex TierQuery();
}
