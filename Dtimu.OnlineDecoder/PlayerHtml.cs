using System.Net;

namespace Dtimu.OnlineDecoder;

internal static class PlayerHtml
{
    public static string Player(string name, string mode, int qp, bool remote, string res) =>
        Base.Replace("__NAME__", WebUtility.HtmlEncode(name))
            .Replace("__MODE__", ModeLabel(mode, qp, remote))
            .Replace("__RESHIDDEN__", string.IsNullOrEmpty(res) ? " hidden" : "")
            .Replace("__RES__", WebUtility.HtmlEncode(res))
            .Replace("__REMOTE__", remote ? "true" : "false")
            .Replace("__QP__", qp.ToString());

    public static string Error(string code, string msg) =>
        Err.Replace("__CODE__", WebUtility.HtmlEncode(code))
           .Replace("__MSG__", WebUtility.HtmlEncode(msg));

    private static string ModeLabel(string mode, int qp, bool remote)
    {
        string m = mode switch
        {
            Modes.Remux      => "兼容直通 · 无损重封装",
            Modes.RemuxAudio => "视频直通 · 音频转 AAC",
            Modes.Transcode  => $"UHD 硬件转码 · QP{qp}",
            Modes.TranscodeSw => $"软解 + 硬编 · QP{qp}",
            _ => "分析中",
        };
        return remote ? "远程源 · " + m : m;
    }

    private const string Base = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>__NAME__ · 在线解码</title>
<style>
:root{color-scheme:dark}
*{box-sizing:border-box;margin:0}
html,body{height:100%}
body{background:#0e1116;color:#dfe5ec;font:15px/1.5 system-ui,"Segoe UI",Roboto,"PingFang SC","Microsoft YaHei",sans-serif;display:flex;flex-direction:column}
header{padding:12px 16px;display:flex;gap:10px;align-items:center;border-bottom:1px solid #1d2430}
header .name{font-weight:600;font-size:15px;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.badge{flex-shrink:0;font-size:12px;padding:3px 10px;border-radius:999px;background:#12314f;color:#7ec3ff;border:1px solid #1c4a77}
#status{padding:10px 16px;font-size:13px;color:#9fb0c3;border-bottom:1px solid #1d2430;display:flex;gap:12px;align-items:center}
#pbar{flex:1;height:6px;border-radius:3px;background:#1d2430;overflow:hidden}
#pfill{height:100%;width:0;background:linear-gradient(90deg,#2d8cff,#6adb8f);transition:width .6s}
#v{width:100%;flex:1;min-height:40vh;background:#000;outline:none}
#err{margin:16px;padding:14px;background:#3a1520;border:1px solid #6e2740;color:#ff9ab3;border-radius:10px;font-size:13px;white-space:pre-wrap}
footer{padding:8px 16px;font-size:12px;color:#5c6b7d;border-top:1px solid #1d2430}
</style>
</head>
<body>
<header><div class="name" id="name">__NAME__</div><div class="badge" id="mode">__MODE__</div><div class="badge" id="res"__RESHIDDEN__>__RES__</div></header>
<div id="status"><span id="st">正在分析…</span><div id="pbar"><div id="pfill"></div></div><span id="pct">0%</span></div>
<video id="v" controls playsinline preload="metadata"></video>
<div id="err" hidden></div>
<footer>Dtimu.OnlineDecoder · VAAPI CQP __QP__ · 6s fMP4 分片</footer>
<script src="/hls.min.js"></script>
<script>
(function(){
  var base = location.pathname;
  var qp = __QP__, remote = __REMOTE__;
  var v = document.getElementById('v'), st = document.getElementById('st'),
      pct = document.getElementById('pct'), fill = document.getElementById('pfill'),
      err = document.getElementById('err'), modeB = document.getElementById('mode'),
      resB = document.getElementById('res');
  var started = false, timer = null, flatMode = false;

  function modeLabel(m, remote){
    var t = m === 'remux' ? '兼容直通 · 无损重封装'
        : m === 'remux-audio' ? '视频直通 · 音频转 AAC'
        : m === 'transcode' ? 'UHD 硬件转码 · QP' + qp
        : m === 'transcode-sw' ? '软解 + 硬编 · QP' + qp : '分析中';
    return remote ? '远程源 · ' + t : t;
  }

  function fmt(s){
    s = Math.max(0, s | 0);
    var h = (s / 3600) | 0, m = ((s % 3600) / 60) | 0, x = s % 60;
    return (h ? h + ':' : '') + String(m).padStart(2,'0') + ':' + String(x).padStart(2,'0');
  }

  function start(){
    started = true;
    // ① iPhone Safari：原生 HLS
    if (v.canPlayType('application/vnd.apple.mpegurl')) { v.src = base + '/index.m3u8'; return; }
    // ② Chrome / Edge（有 MSE）：hls.js
    if (window.Hls && Hls.isSupported()) {
      var hls = new Hls({ liveSyncDurationCount: 2, maxBufferLength: 30 });
      hls.loadSource(base + '/index.m3u8');
      hls.attachMedia(v);
      hls.on(Hls.Events.ERROR, function(_, d){
        if (d.fatal) { err.hidden = false; err.textContent = '播放错误：' + d.details; }
      });
      return;
    }
    // ③ 老设备兑底（Lumia Edge 等无 MSE）：fMP4 拼接直出，原生 video 渐进式播放
    flatMode = true;
    v.src = base + '/file.mp4';
    safePlay();
  }

  // 老浏览器 play() 可能不返回 Promise（EdgeHTML 14），防御性封装
  function safePlay(){
    try { var pr = v.play(); if (pr && pr.catch) pr.catch(function(){}); } catch(e){}
  }

  // 直连模式：播到当前转码进度尽头时，等转码完成后精确续播
  // （running 版无 Range，不反复重拉；done 后拼接版支持 Range，秒级 seek 回原位）
  v.addEventListener('ended', function(){
    if (!flatMode || !timer) return; // timer 为 null 说明转码已 done，正常播完
    waitForDone(v.currentTime);
  });

  function waitForDone(pos){
    st.textContent = '已播到当前转码进度，转码完成后自动续播…';
    setTimeout(function(){
      fetch(base + '/status.json', { cache: 'no-store' })
        .then(function(r){ return r.ok ? r.json() : null; })
        .then(function(s){
          if (s && s.state === 'done') {
            v.addEventListener('loadedmetadata', function once(){
              v.removeEventListener('loadedmetadata', once);
              v.currentTime = pos;
              safePlay();
            });
            v.src = base + '/file.mp4';
            v.load();
          } else if (s && s.state === 'failed') {
            st.textContent = '转码失败';
          } else {
            waitForDone(pos);
          }
        })
        .catch(function(){ waitForDone(pos); });
    }, 8000);
  }

  function tick(){
    fetch(base + '/status.json', { cache: 'no-store' })
      .then(function(r){ return r.ok ? r.json() : null; })
      .then(function(s){
        if (!s) return;
        if (s.error) { err.hidden = false; err.textContent = s.error; }
        // 下载/探测完成后同步徽章（初始页面可能还是“分析中”）
        if (modeB) modeB.textContent = modeLabel(s.mode, remote);
        if (resB) {
          if (s.res) { resB.hidden = false; resB.textContent = s.res; }
          else resB.hidden = true;
        }
        var p = Math.round((s.progress || 0) * 100);
        fill.style.width = p + '%';
        pct.textContent = p + '%';
        if (s.state === 'done') {
          st.textContent = '转码完成 · 可任意拖动进度条';
          if (timer) { clearInterval(timer); timer = null; }
        } else if (s.phase === 'download') {
          // 远程源：先下载后转码
          var dp = Math.round((s.downloadProgress || 0) * 100);
          fill.style.width = dp + '%';
          pct.textContent = dp + '%';
          st.textContent = '正在下载源文件 · ' + dp + '%'
            + (s.downloadTotal > 0 ? '（' + (s.downloadDone/1048576).toFixed(1)
               + ' / ' + (s.downloadTotal/1048576).toFixed(1) + ' MB）' : '');
        } else if (s.state === 'running') {
          st.textContent = (flatMode ? '直连 MP4 · 边转边播 · ' : '边转边播 · ') + fmt(s.doneSec) + ' / ' + fmt(s.totalSec)
            + (s.speed > 0 ? ' · ' + s.speed.toFixed(1) + 'x' : '');
        } else if (s.state === 'failed') {
          st.textContent = '转码失败';
        }
        // playlist 已写出（首个分片落盘）即开播，转码结束时 ENDLIST 出现自动变 VOD
        if (!started && s.playlistReady && (s.state === 'running' || s.state === 'done')) start();
      })
      .catch(function(){});
  }

  timer = setInterval(tick, 2000);
  tick();
})();
</script>
</body>
</html>
""";

    private const string Err = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>__CODE__</title>
<style>body{background:#0e1116;color:#dfe5ec;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;font:15px/1.6 system-ui,sans-serif}
div{max-width:560px;padding:32px;border:1px solid #2a1e26;border-radius:14px;background:#1a1216}
h1{font-size:40px;color:#ff9ab3;margin:0 0 12px}
p{color:#9fb0c3;word-break:break-all;margin:0}</style></head>
<body><div><h1>__CODE__</h1><p>__MSG__</p></div></body></html>
""";
}
