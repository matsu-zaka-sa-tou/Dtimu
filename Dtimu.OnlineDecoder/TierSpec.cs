namespace Dtimu.OnlineDecoder;

/// <summary>
/// 档位规格：一档 = 一组可独立调节的转码约束（纯数据，不含注册/路由逻辑）。
/// 库内置预设见 <see cref="TierPresets"/>；字段含义与调参依据见项目根 TIERS.md。
/// </summary>
internal sealed record TierSpec
{
    // ---------------- 标识 ----------------

    /// <summary>档位 id（URL ?tier= 参数与产物指纹使用）：low / mid / high / 自定义。</summary>
    public required string Id { get; init; }

    /// <summary>徽章短名：480p / 1080p / 4K。</summary>
    public required string Short { get; init; }

    /// <summary>完整描述（播放页徽章）：低 · ≤640×360@30。</summary>
    public required string Label { get; init; }

    // ---------------- 分辨率盒：保比例缩入（只缩不放大），宽高偶数对齐 ----------------

    /// <summary>最大宽度（像素）。</summary>
    public required int MaxWidth { get; init; }

    /// <summary>最大高度（像素）。</summary>
    public required int MaxHeight { get; init; }

    // ---------------- 帧率上限：源帧率超线时 -r 显式降帧 ----------------

    /// <summary>最大帧率（fps）。</summary>
    public required double MaxFps { get; init; }

    // ---------------- H.264 兼容判定线（remux 直通条件之一）----------------

    /// <summary>编码器标注 level 上限：42=L4.2（1080p60 通吃线）、51=L5.1（4K30）。</summary>
    public required int MaxLevel { get; init; }

    /// <summary>宏块吞吐兜底（防文件 level 标注不实）：L4.2=522,240 MB/s、L5.1=983,040 MB/s。</summary>
    public required double MaxMbsPerSec { get; init; }

    // ---------------- 每档编码参数覆盖（null = 继承全局 DTIMU_QP / 默认值）----------------

    /// <summary>本档固定 QP 覆盖；null = 用全局 QP（DTIMU_QP，默认 16）。</summary>
    public int? Qp { get; init; }

    /// <summary>音频码率 kbps（AAC-LC）；null = 256。</summary>
    public int? AudioKbps { get; init; }

    /// <summary>按本档规格为源规划转码参数（模式 + 目标尺寸 + 输出帧率上限）。</summary>
    public TranscodePlan Plan(MediaInfo info)
    {
        bool tenBit = info.PixFmt?.Contains("p10", StringComparison.Ordinal) == true;

        // remux 直通判定：编码/像素格式/profile 兼容 + level ≤ 档位 + 尺寸/帧率/宏块吞吐全在档内
        // （直通不能改任何参数，所以必须全部满足；level 标注可能不实，吞吐按实测兜底）
        bool fpsOk = info.FrameRate <= 0 || info.FrameRate <= MaxFps + 0.01;
        bool sizeOk = info.Width > 0 && info.Width <= MaxWidth && info.Height > 0 && info.Height <= MaxHeight;
        bool mbsOk = info.Width <= 0
            || Mbs(info.Width, info.Height) * (info.FrameRate > 0 ? info.FrameRate : 60) <= MaxMbsPerSec;
        bool vOk = info.VideoCodec == "h264"
            && info.PixFmt is "yuv420p" or "yuvj420p"
            && info.Profile is "High" or "Main" or "Baseline" or "Constrained Baseline"
            && (info.Level is null || info.Level <= MaxLevel)
            && fpsOk && sizeOk && mbsOk;
        bool aOk = info.AudioCodec is "aac" or "mp3";

        string mode;
        if (vOk && aOk) mode = Modes.Remux;
        else if (vOk) mode = Modes.RemuxAudio;
        // UHD630 (Gen9.5) 没有 AV1 解码，也没有 H.264 10bit 解码 → 只能 CPU 软解再上载
        else if (info.VideoCodec == "av1" || (info.VideoCodec == "h264" && tenBit)) mode = Modes.TranscodeSw;
        else mode = Modes.Transcode;

        var (down, tw, th) = Target(info);
        // 输出帧率 = min(源帧率, 档位上限)；源帧率未知时取档位上限（不主动加 -r 的分支在 BuildArgs）
        double capFps = info.FrameRate > 0 ? Math.Min(info.FrameRate, MaxFps) : MaxFps;
        return new TranscodePlan(mode, tw, th, capFps, down);
    }

    /// <summary>目标尺寸：保比例缩入档位分辨率盒，宏块吞吐超线再按 √ 比例缩，宽高偶数对齐。</summary>
    private (bool down, int tw, int th) Target(MediaInfo info)
    {
        int w = info.Width, h = info.Height;
        if (w <= 0 || h <= 0) return (false, w, h);

        double scale = Math.Min((double)MaxWidth / w, (double)MaxHeight / h);
        if (scale > 1.0) scale = 1.0;

        double effFps = info.FrameRate > 0 ? Math.Min(info.FrameRate, MaxFps) : MaxFps;
        double mbs = Mbs(w * scale, h * scale);
        if (mbs * effFps > MaxMbsPerSec)
            scale *= Math.Sqrt(MaxMbsPerSec / (mbs * effFps)); // 宏块数 ~ scale²

        if (scale >= 1.0 - 1e-9) return (false, w, h);
        int tw = Math.Max(2, (int)Math.Floor(w * scale / 2) * 2);
        int th = Math.Max(2, (int)Math.Floor(h * scale / 2) * 2);
        return (true, tw, th);
    }

    private static double Mbs(double w, double h) => Math.Ceiling(w / 16.0) * Math.Ceiling(h / 16.0);
}

/// <summary>档位规划结果：模式 + 目标尺寸 + 输出帧率上限（ffmpeg 参数据此生成）。</summary>
internal sealed record TranscodePlan(
    string Mode,
    int TargetWidth,
    int TargetHeight,
    double CapFps,
    bool Downscale);
