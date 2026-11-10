namespace Dtimu.OnlineDecoder;

/// <summary>
/// 库内置档位预设。数值依据（设备硬解上限 / H.264 level / UHD630 编码吞吐）见项目根 TIERS.md。
/// 预设只是 TierSpec 的三份普通实例——系统本身不绑定任何固定档位，可增删可自定义。
/// </summary>
internal static class TierPresets
{
    /// <summary>
    /// 低档：Nokia 8110 (KaiOS) 底线——240×320 QVGA 屏、硬解上限 720p@30，
    /// 统一压 ≤640×360@30；音频降到 128k（QVGA 屏听感无差）。
    /// </summary>
    public static readonly TierSpec Low = new()
    {
        Id = "low",
        Short = "480p",
        Label = "低 · ≤640×360@30",
        MaxWidth = 640,
        MaxHeight = 360,
        MaxFps = 30,
        MaxLevel = 42,       // L4.2
        MaxMbsPerSec = 522_240,
        AudioKbps = 128,
    };

    /// <summary>
    /// 中档：P9 Plus / Y70t / Note 13 Pro / Lumia 950 主力——
    /// H.264 High@L4.2 = 1080p60，骁龙 400 级以上硬解通吃线。
    /// </summary>
    public static readonly TierSpec Mid = new()
    {
        Id = "mid",
        Short = "1080p",
        Label = "中 · ≤1920×1080@60",
        MaxWidth = 1920,
        MaxHeight = 1080,
        MaxFps = 60,
        MaxLevel = 42,       // L4.2
        MaxMbsPerSec = 522_240,
    };

    /// <summary>
    /// 高档：X100s / 桌面浏览器——4K。
    /// 帧率限 30：UHD630 的 H.264 VDENC 编码吞吐上限约 4K@30
    /// （X100s 解码端 4K60 无压力，想保 60fps 请用中档 1080p60）。
    /// </summary>
    public static readonly TierSpec High = new()
    {
        Id = "high",
        Short = "4K",
        Label = "高 · ≤3840×2160@30",
        MaxWidth = 3840,
        MaxHeight = 2160,
        MaxFps = 30,
        MaxLevel = 51,       // L5.1
        MaxMbsPerSec = 983_040,
    };

    /// <summary>默认生效集合（顺序即播放页切换链接顺序）。</summary>
    public static readonly TierSpec[] All = [Low, Mid, High];
}
