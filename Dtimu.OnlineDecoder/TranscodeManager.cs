using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Dtimu.OnlineDecoder;

internal enum JobState { Initial, Downloading, Running, Done, Failed }

internal sealed record StatusDto(
    string State,
    string Phase,        // download | transcode
    string Mode,
    string Res,          // 分辨率（降级时如 "4096x2048 → 1920x960"）
    double Progress,     // 转码进度 0-1
    double DoneSec,
    double TotalSec,
    double Speed,
    double DownloadProgress, // 下载进度 0-1（无 Content-Length 时为 0）
    double DownloadDone,
    double DownloadTotal,
    bool PlaylistReady,
    string? Error);

internal sealed class Job(string key, string outDir, string inputFile, bool isRemote, string? remoteUrl, string? downloadFile)
{
    public readonly object Sync = new();
    public string Key = key;                 // 字典键：本地=绝对路径；远程=完整 URL
    public string OutDir = outDir;            // 输出目录 = 输入文件 + ".hls"
    public string InputFile = inputFile;      // ffmpeg 输入（远程=下载缓存文件）
    public bool IsRemote = isRemote;
    public string? RemoteUrl = remoteUrl;
    public string? DownloadFile = downloadFile;
    public CancellationTokenSource? Cancel;

    public JobState State;
    public string Phase = "transcode";        // cache 模式下载期间切为 download
    public DateTime LastAccessUtc = DateTime.UtcNow; // 最近一次 HTTP 命中（空闲清理依据）
    public string Mode = "";
    public string Res = "";                    // 分辨率描述（降级时含箭头）
    public string Fingerprint = "";           // 转码参数指纹（mode|qp|目标尺寸），存入 .done 供复用校验
    public double DoneSec;
    public double TotalSec;
    public double Speed;
    public double DownloadDone;
    public double DownloadTotal;
    public string? Error;
    public int Attempts;
    public DateTime FailedAt;
    public Process? Proc;

    public StatusDto Snapshot()
    {
        lock (Sync)
        {
            return new StatusDto(
                State switch
                {
                    JobState.Running => "running",
                    JobState.Downloading => "downloading",
                    JobState.Done => "done",
                    JobState.Failed => "failed",
                    _ => "initial",
                },
                Phase,
                Mode,
                Res,
                TotalSec > 0 ? Math.Clamp(DoneSec / TotalSec, 0, 1) : 0,
                DoneSec,
                TotalSec,
                Speed,
                DownloadTotal > 0 ? Math.Clamp(DownloadDone / DownloadTotal, 0, 1) : 0,
                DownloadDone,
                DownloadTotal,
                File.Exists(Path.Combine(OutDir, "index.m3u8")),
                Error);
        }
    }
}

/// <summary>
/// 转码任务管理：每个源一个 Job（产物目录 = scratch/hls/&lt;源哈希&gt;，与媒体目录解耦），
/// 远程源 stream 模式直接把 URL 交给 ffmpeg（零落盘），cache 模式先下载到 scratch/dl，
/// 转码阶段 ffmpeg 渐进式写 HLS（边转边播），退出码 0 落 .done，服务重启后直接复用产物；
/// 空闲超时自动删除产物（idleMinutes&gt;0），配合 tmpfs scratch 实现零磁盘占用。
/// </summary>
internal sealed class TranscodeManager(
    string ffmpegPath, string vaapiDevice, int qp, ProbeService probe, bool insecureTls, int idleMinutes)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();

    public Job GetOrStart(SourceResolver.Resolved src)
    {
        var job = _jobs.GetOrAdd(src.SourceKey, key =>
            new Job(key, src.OutDir, src.FfmpegInput, src.IsRemote,
                src.IsRemote ? src.SourceKey : null, src.DownloadFile));

        lock (job.Sync)
        {
            job.LastAccessUtc = DateTime.UtcNow;
            if (job.State is JobState.Running or JobState.Downloading or JobState.Done) return job;
            if (job.State == JobState.Failed && job.Attempts >= MaxAttempts) return job;
            if (job.State == JobState.Failed && DateTime.UtcNow - job.FailedAt < RetryCooldown) return job;
            KickOffLocked(job);
            return job;
        }
    }

    /// <summary>启动空闲清理循环（Program 启动时调用一次；idleMinutes&gt;0 才生效）。</summary>
    public void StartSweeper()
    {
        if (idleMinutes > 0)
            _ = Task.Run(SweepLoopAsync);
    }

    /// <summary>服务停止时停清理循环、取消下载并杀掉全部 ffmpeg。</summary>
    public void KillAll()
    {
        _life.Cancel();
        foreach (var job in _jobs.Values)
        {
            lock (job.Sync)
            {
                try { job.Cancel?.Cancel(); } catch { }
                if (job.Proc is { HasExited: false } p)
                    try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    // ---------------- 空闲清理：零磁盘/内存模式的配套，防止 scratch 无限增长 ----------------

    private async Task SweepLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token))
                Sweep();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Sweep()
    {
        foreach (var kv in _jobs)
        {
            var job = kv.Value;
            bool remove = false;
            lock (job.Sync)
            {
                if (job.State is JobState.Running or JobState.Downloading) continue;
                bool finished = job.State == JobState.Done
                    || (job.State == JobState.Failed && job.Attempts >= MaxAttempts);
                if (!finished) continue;
                if (DateTime.UtcNow - job.LastAccessUtc < TimeSpan.FromMinutes(idleMinutes)) continue;

                // 超时无人访问：删产物目录（含 file.mp4 拼接）与下载缓存，释放 scratch 空间
                TryDeleteTree(job.OutDir);
                if (job.DownloadFile is not null)
                {
                    TryDeleteFile(job.DownloadFile);
                    TryDeleteFile(job.DownloadFile + ".part");
                }
                job.State = JobState.Initial;
                remove = true;
            }
            if (remove) _jobs.TryRemove(kv.Key, out _);
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir))
                TryDeleteFile(f);
            try { Directory.Delete(dir); } catch { }
        }
        catch { }
    }

    private static void TryDeleteFile(string f)
    {
        try { File.Delete(f); } catch { }
    }

    private void KickOffLocked(Job job)
    {
        job.Error = null;
        job.DoneSec = 0;
        job.Speed = 0;

        // cache 模式远程源且缓存缺失 → 先下载（.part 写完后 rename，缓存文件要么完整要么不存在）
        // stream 模式 DownloadFile 为 null → 直接进转码（ffmpeg 自己拉 URL，源零落盘）
        if (job.DownloadFile is not null && !File.Exists(job.DownloadFile))
        {
            job.State = JobState.Downloading;
            job.Phase = "download";
            job.Cancel = new CancellationTokenSource();
            _ = Task.Run(() => DownloadAndContinueAsync(job));
            return;
        }

        StartTranscodeLocked(job);
    }

    private void StartTranscodeLocked(Job job)
    {
        job.Phase = "transcode";

        var info = probe.Get(job.InputFile);
        if (info is null)
        {
            job.State = JobState.Failed;
            job.Attempts = MaxAttempts; // 探测失败不自动重试
            job.FailedAt = DateTime.UtcNow;
            job.Error = job.IsRemote
                ? "无法解析远程源：URL 不可达 / 需要鉴权 / moov 在文件尾且服务器不支持 Range（可改 DTIMU_REMOTE_MODE=cache）"
                : "ffprobe 无法解析该文件（文件不存在 / 损坏 / 不是媒体文件）";
            return;
        }
        job.Mode = info.Mode;
        job.TotalSec = info.DurationSec;
        job.Res = info.Width > 0
            ? $"{info.Width}x{info.Height}" + (info.Downscale ? $" → {info.TargetWidth}x{info.TargetHeight}" : "")
            : "";
        job.Fingerprint = Fingerprint(info);

        try { Directory.CreateDirectory(job.OutDir); }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Attempts = MaxAttempts;
            job.FailedAt = DateTime.UtcNow;
            job.Error = "无法创建输出目录 " + job.OutDir + "：" + ex.Message;
            return;
        }

        // 已完成且参数指纹一致：直接复用（服务重启后的断点）；
        // 指纹不匹配（旧版产物 / QP 调整 / 兼容判定规则变更）：作废重转
        string donePath = Path.Combine(job.OutDir, ".done");
        if (File.Exists(donePath))
        {
            try
            {
                if (File.ReadAllText(donePath).Trim() == job.Fingerprint)
                {
                    job.DoneSec = info.DurationSec; // 复用产物：进度直接置满（否则显示 0%）
                    job.State = JobState.Done;
                    return;
                }
            }
            catch { }
        }

        CleanStale(job.OutDir);

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true, // -progress pipe:1
            RedirectStandardError = true,
        };
        foreach (var a in BuildArgs(info, job.InputFile, job.OutDir)) psi.ArgumentList.Add(a);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.FailedAt = DateTime.UtcNow;
            job.Attempts++;
            job.Error = "无法启动 ffmpeg：" + ex.Message + "（请确认已安装，或用 DTIMU_FFMPEG 指定路径）";
            return;
        }

        job.Proc = proc;
        job.State = JobState.Running;
        _ = Task.Run(() => MonitorAsync(job, proc!));
    }

    private async Task DownloadAndContinueAsync(Job job)
    {
        var cts = job.Cancel!;
        try
        {
            await DownloadAsync(job, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            lock (job.Sync)
            {
                job.State = JobState.Failed;
                job.FailedAt = DateTime.UtcNow;
                job.Attempts++;
                job.Error = "下载失败：" + Unwrap(ex);
            }
            return;
        }
        if (cts.IsCancellationRequested) return;

        lock (job.Sync)
        {
            if (!File.Exists(job.DownloadFile) || new FileInfo(job.DownloadFile!).Length == 0)
            {
                job.State = JobState.Failed;
                job.FailedAt = DateTime.UtcNow;
                job.Attempts++;
                job.Error = "下载结果为空";
                return;
            }
            job.State = JobState.Initial;
            StartTranscodeLocked(job);
        }
    }

    /// <summary>剥开外层泛化异常（HttpClient 常把真实原因包在 InnerException 里，如 SSL 链校验失败）。</summary>
    private static string Unwrap(Exception ex) =>
        ex.InnerException is { } inner ? $"{ex.Message}（{inner.Message}）" : ex.Message;

    private async Task DownloadAsync(Job job, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        if (insecureTls)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true; // 内网自签 https
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        using var resp = await http.GetAsync(job.RemoteUrl!, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        long? total = resp.Content.Headers.ContentLength;
        string part = job.DownloadFile! + ".part";
        string? dir = Path.GetDirectoryName(job.DownloadFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true))
        await using (var input = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buf = new byte[256 * 1024];
            long done = 0;
            while (true)
            {
                int n = await input.ReadAsync(buf, ct);
                if (n <= 0) break;
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                lock (job.Sync)
                {
                    job.DownloadDone = done;
                    if (total.HasValue) job.DownloadTotal = total.Value;
                }
            }
        }

        File.Move(part, job.DownloadFile!, overwrite: true);
    }

    private static async Task MonitorAsync(Job job, Process proc)
    {
        // stderr 单独收集，避免与 stdout progress 互锁
        var errSb = new StringBuilder();
        var errTask = Task.Run(() =>
        {
            try { errSb.Append(proc.StandardError.ReadToEnd()); } catch { }
        });

        try
        {
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                {
                    lock (job.Sync) job.DoneSec = us / 1e6;
                }
                else if (line.StartsWith("speed=", StringComparison.Ordinal))
                {
                    var s = line[6..].TrimEnd('x');
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                        lock (job.Sync) job.Speed = sp;
                }
            }
        }
        catch { }

        await errTask;
        try { await proc.WaitForExitAsync(); } catch { }

        int exit = proc.ExitCode;
        string err = errSb.ToString();

        if (exit == 0)
        {
            try { File.WriteAllText(Path.Combine(job.OutDir, ".done"), job.Fingerprint + "\n"); }
            catch { }
        }
        else
        {
            try { File.WriteAllText(Path.Combine(job.OutDir, ".log"), $"ffmpeg 退出码 {exit}\n{Tail(err, 4000)}"); }
            catch { }
        }

        lock (job.Sync)
        {
            job.Proc = null;
            if (exit == 0)
            {
                job.State = JobState.Done;
            }
            else
            {
                job.State = JobState.Failed;
                job.FailedAt = DateTime.UtcNow;
                job.Attempts++;
                job.Error = "ffmpeg 退出码 " + exit + "：" + Tail(err, 400);
            }
        }
    }

    /// <summary>转码参数指纹：remux 类仅看模式；转码类含 QP 与目标尺寸（改参数自动作废旧产物）。</summary>
    private string Fingerprint(MediaInfo info) =>
        info.Mode + "|" + (info.Mode is Modes.Transcode or Modes.TranscodeSw
            ? qp + "|" + (info.Downscale ? $"{info.TargetWidth}x{info.TargetHeight}" : "-")
            : "-");

    private string[] BuildArgs(MediaInfo info, string input, string outDir)
    {
        var a = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "warning",
            "-progress", "pipe:1",
            "-y",
        };

        if (info.Mode == Modes.Transcode)
        {
            // VAAPI 硬解：解码结果直接留在 GPU 显存
            a.AddRange(["-hwaccel", "vaapi", "-hwaccel_device", vaapiDevice, "-hwaccel_output_format", "vaapi"]);
        }
        else if (info.Mode == Modes.TranscodeSw)
        {
            // 软解链：全局指定 VAAPI 设备供 hwupload 使用
            a.AddRange(["-vaapi_device", vaapiDevice]);
        }

        // 远程 stream 模式：input 是 URL；显式关证书校验（ffmpeg 默认也不校验，双保险）
        if (insecureTls && input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            a.AddRange(["-tls_verify", "0"]);

        a.AddRange(["-i", input]);

        if (info.Mode == Modes.Transcode)
            a.AddRange(["-vf", info.Downscale
                ? $"scale_vaapi=w={info.TargetWidth}:h={info.TargetHeight}:format=nv12"  // 4K/高帧率 → ≤1080p60（L4.2）
                : "scale_vaapi=format=nv12"]);                                          // 10bit P010 → 8bit NV12
        else if (info.Mode == Modes.TranscodeSw)
            a.AddRange(["-vf", info.Downscale
                ? $"scale=w={info.TargetWidth}:h={info.TargetHeight},format=nv12,hwupload"
                : "scale=format=nv12,hwupload"]);                                       // CPU 转 NV12 后上载 GPU

        if (info.Mode is Modes.Transcode or Modes.TranscodeSw)
        {
            // 保画质核心：固定 QP（视觉无损区间），帧率不动，分辨率不动
            a.AddRange(["-c:v", "h264_vaapi",
                        "-rc_mode", "CQP",
                        "-qp", qp.ToString(),
                        "-profile:v", "high",
                        "-g", "120"]);
        }
        else
        {
            a.AddRange(["-c:v", "copy"]);
        }

        a.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"]);

        if (info.Mode == Modes.Remux)
            a.AddRange(["-c:a", "copy"]);
        else
            a.AddRange(["-c:a", "aac", "-b:a", "256k"]);

        // HLS fMP4：6 秒一片，list_size 0 保留全部分片（渐进可播，结束后自动 ENDLIST）
        a.AddRange([
            "-f", "hls",
            "-hls_time", "6",
            "-hls_list_size", "0",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_segment_filename", Path.Combine(outDir, "seg-%05d.m4s"),
            "-hls_flags", "independent_segments+temp_file",
            Path.Combine(outDir, "index.m3u8"),
        ]);
        return a.ToArray();
    }

    /// <summary>
    /// CMAF 特性：init.mp4 + 顺序媒体分片拼接 = 一个完整合法的 MP4。
    /// 转码完成后物理拼接出 file.mp4（幂等，供不支持 HLS/MSE 的老设备渐进式播放，支持 Range seek）。
    /// </summary>
    public string? EnsureFlatFile(Job job)
    {
        lock (job.Sync)
        {
            string flat = Path.Combine(job.OutDir, "file.mp4");
            if (File.Exists(flat)) return flat;
            if (job.State is not JobState.Done || !File.Exists(Path.Combine(job.OutDir, ".done"))) return null;

            var files = FlatFiles(job.OutDir);
            if (files.Count == 0) return null;

            string tmp = flat + ".tmp";
            using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                foreach (var f in files)
                using (var inFs = File.OpenRead(f))
                    inFs.CopyTo(outFs);
            }
            File.Move(tmp, flat, overwrite: true);
            return flat;
        }
    }

    /// <summary>转码中：当前已有分片的快照（temp_file 标志保证枚举到的 .m4s 均已完整），流式拼接输出。</summary>
    public (string[] Files, long TotalBytes)? BuildFlatSnapshot(Job job)
    {
        var files = FlatFiles(job.OutDir);
        if (files.Count == 0) return null;
        long total = 0;
        foreach (var f in files)
        {
            try { total += new FileInfo(f).Length; }
            catch { return null; }
        }
        return (files.ToArray(), total);
    }

    /// <summary>init.mp4 + 按序号排序的媒体分片（%05d 补零，文件名序即数字序）。</summary>
    private static List<string> FlatFiles(string outDir)
    {
        var result = new List<string>();
        try
        {
            string? init = null;
            var segs = new List<string>();
            foreach (var f in Directory.EnumerateFiles(outDir))
            {
                string n = Path.GetFileName(f);
                if (n == "init.mp4") init = f;
                else if (n.EndsWith(".m4s", StringComparison.Ordinal)) segs.Add(f);
            }
            segs.Sort(StringComparer.Ordinal);
            if (init is not null) result.Add(init);
            result.AddRange(segs);
        }
        catch { }
        return result;
    }

    /// <summary>只清理本服务自己产生的分片/playlist/拼接产物，不碰 .done / .log 和其他文件。</summary>
    private static void CleanStale(string outDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(outDir))
            {
                string n = Path.GetFileName(f);
                if (n is ".done" or ".log") continue; // 完成标记与日志保留，其余全部重生成
                if (n.EndsWith(".m4s", StringComparison.Ordinal)
                    || n.EndsWith(".tmp", StringComparison.Ordinal)
                    || n is "init.mp4" or "index.m3u8" or "master.m3u8" or "file.mp4")
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}