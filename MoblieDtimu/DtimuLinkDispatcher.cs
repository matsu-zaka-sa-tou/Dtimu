namespace MoblieDtimu
{
    /// <summary>
    /// dtimu:// 深链接统一调度器。
    /// 当前形态：dtimu://play_raw_vedio=&lt;完整媒体URL&gt;
    /// 例：dtimu://play_raw_vedio=http://192.168.50.113:26131/files/xxx/abc%20def.mp4
    /// 兼容旧形态（直嵌）：dtimu://&lt;完整媒体URL&gt;，例：dtimu://https://example.com/x.m3u8
    /// 链路：Android MainActivity（OnCreate/OnNewIntent）/ Windows App（命令行参数）
    ///       → Report → MainPage 订阅 → 切到视频 Tab → VideoView.PlayExternal → VLC 自动播放。
    /// 冷启动（MainPage 尚未由 AppShell 的 DataTemplate 懒加载构造）时先缓存 pending，
    /// MainPage 构造完成后 TakePendingUrl() 取走，避免时序丢链接。
    /// </summary>
    public static class DtimuLinkDispatcher
    {
        private const string Prefix = "dtimu://";     // 标准形态（IntentFilter 声明 scheme=dtimu）
        private const string PrefixBare = "dtimu:";   // 宽容形态：dtimu:play_raw_vedio=...
        private const string RawVideoKey = "play_raw_vedio";   // 当前协议 key（沿用项目 Vedio 拼写）
        private const string RawVideoKeyAlt = "play_raw_video"; // 常规拼写别名，防手滑
        private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(3);

        private static string? _pendingUrl;           // 冷启动缓存
        private static string? _lastUrl;              // 双入口去重
        private static DateTime _lastAt;

        /// <summary>解析成功的媒体 URL 派发（订阅者就绪时）或缓存（冷启动时）。</summary>
        public static event EventHandler<string>? LinkReceived;

        /// <summary>入口：传入原始链接字符串（Android Intent.DataString / Windows 命令行参数）。</summary>
        public static void Report(string? rawLink)
        {
            var url = ParseMediaUrl(rawLink);
            if (url is null) return;

            // MainActivity 直报与 App.OnAppLinkRequestReceived 双入口兜底：短窗口内同一 URL 只派发一次
            if (string.Equals(_lastUrl, url, StringComparison.Ordinal)
                && DateTime.UtcNow - _lastAt < DedupWindow)
                return;
            _lastUrl = url;
            _lastAt = DateTime.UtcNow;

            if (LinkReceived is { } handler)
                handler(null, url);
            else
                _pendingUrl = url; // 页面未就绪（冷启动），等 MainPage 来取
        }

        /// <summary>MainPage 构造完成后调用：取走冷启动缓存的链接（没有则返回 null）。</summary>
        public static string? TakePendingUrl()
        {
            var url = _pendingUrl;
            _pendingUrl = null;
            return url;
        }

        /// <summary>
        /// 解析 dtimu:// 链接，取出内嵌的 http/https 媒体地址：
        /// "dtimu://play_raw_vedio=http://x/y.mp4" → "http://x/y.mp4"（当前形态：key 大小写不敏感，
        /// 兼容 play_raw_video 常规拼写；只按第一个 '=' 分割，URL 查询串自带的 '=' 不受影响）
        /// "dtimu://https://example.com/x.m3u8"   → "https://example.com/x.m3u8"（旧形态兼容）
        /// 规则：前缀大小写不敏感；兼容 dtimu: 省略斜杠写法；内嵌地址必须是合法 http/https URL；
        /// 其余（未知 key / ftp / 裸文字等）一律返回 null。
        /// </summary>
        public static string? ParseMediaUrl(string? rawLink)
        {
            if (string.IsNullOrWhiteSpace(rawLink)) return null;

            var link = rawLink.Trim();

            string rest;
            if (link.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                rest = link[Prefix.Length..];
            else if (link.StartsWith(PrefixBare, StringComparison.OrdinalIgnoreCase))
                rest = link[PrefixBare.Length..].TrimStart('/');
            else
                return null; // 不是 dtimu 链接（如普通 http 直链），不在本协议处理范围

            if (rest.Length == 0) return null;

            // 新形态判定：play_raw_vedio=<URL>。只认第一个 '='（其后 URL 查询串可自带 '='）；
            // key 不匹配的（含自带 '=' 的旧形态直嵌 URL）一律按旧形态整体当作内嵌地址处理
            string embedded;
            var eq = rest.IndexOf('=');
            if (eq > 0)
            {
                var key = rest[..eq];
                if (key.Equals(RawVideoKey, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(RawVideoKeyAlt, StringComparison.OrdinalIgnoreCase))
                    embedded = rest[(eq + 1)..];
                else
                    embedded = rest;
            }
            else
            {
                embedded = rest;
            }

            if (embedded.Length == 0) return null;

            // 只接受可自动播放的 web 媒体地址
            if (!embedded.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !embedded.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Uri.TryCreate(embedded, UriKind.Absolute, out var uri)) return null;
            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return null;

            return embedded;
        }
    }
}
