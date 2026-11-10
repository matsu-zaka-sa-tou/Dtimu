# 档位系统（Tiering）

> 本文档描述 Dtimu.OnlineDecoder 的转码档位体系：变量如何拆分、三档为何只是预设、UA 如何路由、如何自定义。

## 这次改动解决了什么

此前"低/中/高三档"是硬编码在代码里的三个静态对象，改一个数值就要改逻辑文件，且"哪几档、默认哪档、什么 UA 到哪档"散落在不同方法里。

现在的结构是**通用档位系统 + 库内预设**：

```
TierSpec      纯数据：一档 = 一组可独立调节的约束（9 个字段，无逻辑耦合）
TierPresets   库内置预设：Low / Mid / High 只是 TierSpec 的三份普通实例
TierRegistry  注册表：生效档位集合 + 默认档 + UA 路由规则（全部数据化）
TranscodePlan 规划结果：TierSpec.Plan(源) 的输出，ffmpeg 参数据此生成
```

系统本身不绑定任何固定档位：可以删掉高档、可以加第 4 档、可以改预设数值，路由和转码逻辑一行都不用动。

## TierSpec 变量表

| 字段 | 含义 | 依据 / 调参说明 |
|---|---|---|
| `Id` | 档位标识 | URL `?tier=` 参数、任务字典键、产物指纹都用它；小写短词 |
| `Short` | 徽章短名 | 播放页切换链接显示（480p / 1080p / 4K） |
| `Label` | 完整描述 | 播放页徽章与页脚显示 |
| `MaxWidth` / `MaxHeight` | 分辨率盒 | 保比例缩入，**只缩不放大**；宽高自动偶数对齐（H.264 4:2:0 要求） |
| `MaxFps` | 帧率上限 | 源帧率超线时 ffmpeg 加 `-r` 显式降帧；不超线不动帧率 |
| `MaxLevel` | H.264 level 上限 | remux 直通判定条件之一。42=L4.2（1080p60 通吃线）、51=L5.1（4K30） |
| `MaxMbsPerSec` | 宏块吞吐兜底 | 防文件 level 标注不实：L4.2=522,240 MB/s、L5.1=983,040 MB/s，按实测 分辨率×帧率 复核 |
| `Qp` | 每档固定 QP 覆盖 | `null` = 继承全局 `DTIMU_QP`（默认 16）。VAAPI 硬编无 CRF，只有 CQP |
| `AudioKbps` | 音频码率 (AAC-LC) | `null` = 256k。低档可降 128k（小屏听感无差） |

> 分辨率盒 + 帧率 + 宏块吞吐三个条件**全部满足**且编码兼容时才走 remux 直通——因为直通不能改任何参数，必须全绿。

## 内置三预设（TierPresets）

| 预设 | 规格 | 覆盖设备 | 关键依据 |
|---|---|---|---|
| **low** | ≤640×360@30，音频 128k | Nokia 8110 (KaiOS) | 240×320 QVGA 屏；硬解上限 720p@30；KaiOS 播放器生态是最底线 |
| **mid**（默认） | ≤1920×1080@60 (L4.2) | P9 Plus / Y70t / Note 13 Pro / Lumia 950 | H.264 High@L4.2 = 骁龙 400 级以上硬解通吃线 |
| **high** | ≤3840×2160@30 (L5.1) | X100s / 桌面浏览器 | 4K 限 30fps：UHD630 的 H.264 VDENC **编码**吞吐上限约 4K@30；X100s 想保 60fps 请用中档 |

编码统一 **H.264 High + AAC-LC**（所有档），依据：这批设备对 H.264 硬解支持最成熟；HEVC over HLS 在老播放器上兼容性不稳；非常规音频（AC-3/DTS）在移动端经常表现为黑屏/卡住。

## UA 路由规则（TierRegistry.BuiltInRules）

按序匹配（UA 转小写），**首条命中生效**；都不命中 → 默认档（安全兜底）：

| 规则 | 命中条件（含其一/且不含） | → 档位 | 说明 |
|---|---|---|---|
| 1 | 含 `kaios` / `kai/` / `8110` | low | Nokia 8110 |
| 2 | 含 `windows phone` / `iemobile` | mid | Lumia 950 |
| 3 | 含 `v23` | high | vivo X100 系列（V23xx） |
| 4 | 含 `windows nt` / `macintosh` / `x11`，且不含 `mobile` / `android` | high | 桌面浏览器 |
| — | 其余（P9+ / Y70t / Note 13 Pro / 未知 UA） | **mid** | 安全默认 |

UA 只是**分档引导**，不是解码开关：能否播放最终取决于设备播放器对编码/分辨率/音频的实际支持。

## 配置入口

| 入口 | 优先级 | 说明 |
|---|---|---|
| URL `?tier=low\|mid\|high` | 最高 | 显式强制（播放页切换链接即此实现）；也用于分片/playlist 回路由 |
| UA 规则 | 其次 | 上表 |
| `DTIMU_DEFAULT_TIER` 环境变量 | 兜底默认 | 未命中/为空回落 mid；非法值静默回落 mid（见启动日志） |
| `DTIMU_QP` 环境变量 | 全局 QP | 档位 `Qp=null` 时生效；默认 16 |

## 产物指纹与自动失效

`.done` 标记内容 = `mode|tier|qp|audio|目标尺寸|帧率上限`。任一参数变化（切档、调 QP、改预设数值、升级判定逻辑）→ 指纹不匹配 → 旧产物自动作废重转，**无需手动删缓存**。

产物目录 = `scratch/hls/<sha256(源+tier)[0..16]>`：各档互不干扰，同源同档共享一份转码（5 用户看同一源同一档 = 1 个 ffmpeg）。

## 自定义档示例

加一档"720p 平板档"只需两步（无需触碰任何逻辑文件）：

```csharp
// 1. 定义档位（TierPresets.cs 或 Program.cs 内联均可）
var tablet = new TierSpec
{
    Id = "tab", Short = "720p", Label = "平板 · ≤1280×720@60",
    MaxWidth = 1280, MaxHeight = 720, MaxFps = 60,
    MaxLevel = 42, MaxMbsPerSec = 522_240,
};

// 2. 注册进生效集合（Program.cs）
var registry = new TierRegistry([..TierPresets.All, tablet], Cfg("DTIMU_DEFAULT_TIER", "mid"));
// 可选：给某 UA 加路由（插队首，优先于内置规则）
registry.AddUaRule(new UaRule(["sm-t5"], [], "tab", "三星平板 UA"));
```

播放页切换链接、`?tier=tab` URL、指纹、产物目录全部自动适配。

## 设计取舍（为什么不做 ABR）

不做 master playlist 多码率自适应（ABR）：老设备（Nokia 8110 / Lumia）在主 playlist 里选 variant 的逻辑不可靠，经常硬吃最高档然后打不开。本系统改为**服务端 UA 直接路由到唯一档位**，每档独立 playlist；播放页头部保留手动切换链接作为逃生门。

## 相关文件

| 文件 | 内容 |
|---|---|
| `TierSpec.cs` | TierSpec（纯数据）+ TranscodePlan（规划结果）+ Plan 判定逻辑 |
| `TierPresets.cs` | 库内置三预设 |
| `TierRegistry.cs` | 注册表 + UaRule 路由规则 |
| `TranscodeManager.cs` | 按 plan 生成 ffmpeg 参数、每档 QP/音频覆盖、指纹 |
| `Program.cs` | registry 组装（`DTIMU_DEFAULT_TIER`）、`?tier=` 解析、m3u8 query 重写 |

> AI生成
