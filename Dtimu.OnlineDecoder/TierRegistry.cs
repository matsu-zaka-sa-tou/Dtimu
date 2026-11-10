namespace Dtimu.OnlineDecoder;

/// <summary>
/// UA 路由规则（数据化，按序匹配，首条命中生效）：
/// UA 转小写后，须包含 <paramref name="AnyOf"/> 之一、且不包含 <paramref name="NoneOf"/> 任何词。
/// </summary>
internal sealed record UaRule(string[] AnyOf, string[] NoneOf, string TierId, string Note);

/// <summary>
/// 档位注册表：生效档位集合 + 默认档 + UA 路由规则。
/// “有哪几档、默认哪档、什么 UA 到哪档”全部集中为这里的普通数据，不再散落硬编码。
/// </summary>
internal sealed class TierRegistry
{
    /// <summary>内置 UA 路由规则（依据见 TIERS.md 设备表；未知 UA → Default 安全默认）。</summary>
    private static readonly UaRule[] BuiltInRules =
    [
        new(["kaios", "kai/", "8110"], [], "low",
            "Nokia 8110 / KaiOS：硬解上限 720p30、QVGA 屏 → 低档"),
        new(["windows phone", "iemobile"], [], "mid",
            "Lumia 950：骁龙 808，1080p60 H.264 最稳 → 中档"),
        new(["v23"], [], "high",
            "vivo X100 系列（V23xx）：天玑 9300+ → 高档"),
        new(["windows nt", "macintosh", "x11"], ["mobile", "android"], "high",
            "桌面浏览器（非移动 UA）→ 高档"),
    ];

    private readonly List<UaRule> _uaRules;

    /// <param name="tiers">生效档位集合（顺序即播放页切换链接顺序）。</param>
    /// <param name="defaultTierId">默认档 id（DTIMU_DEFAULT_TIER）；未命中/为空回落中档。</param>
    public TierRegistry(IReadOnlyList<TierSpec> tiers, string? defaultTierId = null)
    {
        Tiers = tiers;
        Default = tiers.FirstOrDefault(t => string.Equals(t.Id, defaultTierId, StringComparison.OrdinalIgnoreCase))
                  ?? tiers.FirstOrDefault(t => t.Id == TierPresets.Mid.Id)
                  ?? TierPresets.Mid;
        _uaRules = [.. BuiltInRules];
    }

    /// <summary>生效档位集合。</summary>
    public IReadOnlyList<TierSpec> Tiers { get; }

    /// <summary>默认档（未知 UA / 无 tier 参数时的档位）。</summary>
    public TierSpec Default { get; }

    /// <summary>按 id 查档（大小写不敏感）；未命中回落默认档。</summary>
    public TierSpec Get(string id) =>
        Tiers.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>?tier= 参数解析：null / 空串 / 非法值 → null（由调用方决定是否回落 UA 判档）。</summary>
    public TierSpec? Parse(string? id) =>
        string.IsNullOrEmpty(id) ? null
        : Tiers.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>默认档位按 UA 识别；未知 UA → Default（安全默认）。</summary>
    public TierSpec FromUserAgent(string? ua)
    {
        if (!string.IsNullOrEmpty(ua))
        {
            string u = ua.ToLowerInvariant();
            foreach (var r in _uaRules)
            {
                if (r.NoneOf.Any(u.Contains)) continue;
                if (r.AnyOf.Any(u.Contains)) return Get(r.TierId);
            }
        }
        return Default;
    }

    /// <summary>注册自定义 UA 规则（插入队首，优先于内置规则；供后续配置化扩展使用）。</summary>
    public void AddUaRule(UaRule rule) => _uaRules.Insert(0, rule);
}
