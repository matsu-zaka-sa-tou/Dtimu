using LibVLCSharp.Shared;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;

namespace MoblieDtimu.Views.PlayerViews
{
    /// <summary>
    /// VLC 视频播放器控件（封装 LibVLCSharp.MAUI.VideoView）。
    /// 功能：播放/暂停、进度拖动、静音、显示模式（适应/16:9/4:3）切换、
    /// 点击视频区显隐控制条（播放中 3 秒无操作自动隐藏，暂停时常显）、
    /// 自签名证书 https 播放（AllowUntrustedTls，dtimu:// 深链接场景）。
    /// Windows：视频渲染进控件内的 SwapChainPanel——--winrt-swapchain 选项必须在
    /// 平台视频视图 Initialized 之后取得并传给 LibVLC 构造（vout 启动时读取渲染目标，
    /// 事后无法挂接），否则 VLC 会自建独立输出窗口（即"弹新窗口"问题）。
    /// 用法：设置 Source 属性（http/https 地址）即自动播放；AutoPlay=false 则等 Play() 调用。
    /// 高度：默认按宽度保持 16:9（Aspect 属性可改比例，设 0 表示填满父容器给的高度）。
    /// </summary>
    public partial class VedioPlayer : ContentView
    {
        // 显示模式循环顺序：适应=不裁剪（可能有黑边）；16:9 / 4:3 = 按比例裁剪铺满
        private static readonly (string Label, string? Ratio)[] DisplayModes =
        {
            ("适应", null),
            ("16:9", "16:9"),
            ("4:3", "4:3"),
        };

        // ---- LibVLC 实例策略：进程内单实例（libVLC 3.x 一个进程只正式支持一个，第二次 libvlc_new 直接失败） ----
        // 实证结论（二进制字符串检查）：libVLC 3.x（Windows 3.0.21 / Android 3.0.21-621）没有任何
        // 禁用 TLS 证书验证的选项；"--no-tls-verify" 是 libVLC 4.0 新增选项。
        // 当前 3.x 原生包上该选项仅产生 warning 被忽略（自签名 https 仍会握手失败）；
        // 将来换 libVLC 4 原生包后同一份代码即生效，无需再改。
        // Windows 上不使用此静态实例（SwapChain 选项跟随平台视图生命周期，见文件中部 Windows 区）。
        private static readonly string[] UntrustedTlsOptions = { "--no-tls-verify" };

        private static LibVLC? _libVlc;                 // Android/iOS：进程级单实例
        private static bool _libVlcHasUntrustedFlag;    // 当前实例创建时是否带了 --no-tls-verify
        private static readonly object LibVlcGate = new();

        private LibVLC? _libVlcInUse;              // 当前 _mediaPlayer 绑定的 LibVLC 实例
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private string? _lastUrl;
        private long _lengthMs;
        private bool _seekingByUser;
        private bool _resumeAfterReload;
        private long _resumePositionMs;            // Windows：切走记位，切回重建播放器后续播定位
        private int _displayModeIndex;
        private int _hideToken;
        private string? _lastInitError;           // 最近一次 VLC 初始化异常（供 Play 失败时展示真实原因，不被兜底文案覆盖）

        /// <summary>播放自然结束（宿主可用来自动连播下一个）。</summary>
        public event EventHandler? PlaybackEnded;

        /// <summary>播放失败（参数：错误描述）。</summary>
        public event EventHandler<string>? PlaybackFailed;

        /// <summary>显示模式切换后触发。</summary>
        public event EventHandler? DisplayModeChanged;

        // ---------------- 可绑定属性 ----------------

        public static readonly BindableProperty SourceProperty =
            BindableProperty.Create(nameof(Source), typeof(string), typeof(VedioPlayer), null,
                propertyChanged: (bindable, _, newValue) =>
                    ((VedioPlayer)bindable).OnSourceChanged((string?)newValue));

        /// <summary>视频源地址（http/https，或本机文件绝对路径）。</summary>
        public string? Source
        {
            get => (string?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public static readonly BindableProperty AutoPlayProperty =
            BindableProperty.Create(nameof(AutoPlay), typeof(bool), typeof(VedioPlayer), true);

        /// <summary>设置 Source 后是否立即播放（默认 true）。</summary>
        public bool AutoPlay
        {
            get => (bool)GetValue(AutoPlayProperty);
            set => SetValue(AutoPlayProperty, value);
        }

        public static readonly BindableProperty AllowUntrustedTlsProperty =
            BindableProperty.Create(nameof(AllowUntrustedTls), typeof(bool), typeof(VedioPlayer), false);

        /// <summary>
        /// 是否允许播放使用不信任证书（自签名）的 https 源。dtimu:// 深链接场景开启。
        /// 仅在下次 Play 时生效（切换不打断当前播放）；true 会使用独立的"不校验证书"LibVLC 实例。
        /// 注：libVLC 3.x 原生库无此开关（见字段区实证注释），4.x 起生效。
        /// </summary>
        public bool AllowUntrustedTls
        {
            get => (bool)GetValue(AllowUntrustedTlsProperty);
            set => SetValue(AllowUntrustedTlsProperty, value);
        }

        public static readonly BindableProperty AspectProperty =
            BindableProperty.Create(nameof(Aspect), typeof(double), typeof(VedioPlayer), 16.0 / 9.0);

        /// <summary>控件宽高比（默认 16:9：高度=宽度/比例自适应；设 0 表示不限制高度）。</summary>
        public double Aspect
        {
            get => (double)GetValue(AspectProperty);
            set => SetValue(AspectProperty, value);
        }

        public VedioPlayer()
        {
            InitializeComponent();

            SizeChanged += OnSizeChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

#if WINDOWS
            // Windows：等平台视频视图 Initialized（SwapChain 就绪）再建 LibVLC/MediaPlayer，
            // 提前建会因缺 --winrt-swapchain 选项导致 VLC 自建独立输出窗口
            VlcView.HandlerChanged += OnVlcViewHandlerChanged;
#elif ANDROID
            // Android：SurfaceView 的 Surface 生命周期由 AWindow 管理（设 VlcView.MediaPlayer
            // 时自动 AttachViews），构造期建播放器即可；同时把 Activity 暂停/恢复接到播放器，
            // 避免 Surface 销毁后播放器还在跑（切后台黑屏/崩溃的常见根因）
            EnsurePlayerFor(AllowUntrustedTls);
            MainActivity.PausePlayback += OnAndroidActivityPause;
            MainActivity.ResumePlayback += OnAndroidActivityResume;
#else
            // iOS：构造期建播放器，渲染目标由平台层接
            EnsurePlayerFor(AllowUntrustedTls);
#endif
        }

        // ---------------- 公开播放控制 ----------------

        /// <summary>播放指定地址（http/https）。地址会被记住，供封面重播用。</summary>
        public void Play(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                ShowError($"无效的视频地址：{url}");
                return;
            }

            // 换片：Windows 续播定位作废（旧进度不带到新视频）
            if (!string.Equals(_lastUrl, url, StringComparison.Ordinal))
                _resumePositionMs = 0;

            // 幂等：同一地址已处于活动播放状态（打开中/缓冲/播放/暂停）时跳过，
            // 防止 dtimu:// 深链接双入口或绑定链重复触发导致从头重播
            if (string.Equals(_lastUrl, url, StringComparison.Ordinal)
                && _mediaPlayer?.State is VLCState.Opening or VLCState.Buffering
                                or VLCState.Playing or VLCState.Paused)
                return;

#if WINDOWS
            if (_winSwapChainOptions is null)
            {
                // 平台视频视图还没 Initialized（冷启动/深链接到达太早）：排队，SwapChain 就绪后自动播
                _pendingPlayUrl = url;
                ErrorLabel.IsVisible = false;
                CoverOverlay.IsVisible = false;
                return;
            }
#endif

            // 按当前信任模式选用/重建播放器（AllowUntrustedTls 切换时换用对应 LibVLC 实例）
            EnsurePlayerFor(AllowUntrustedTls);

            if (_mediaPlayer is null || _libVlcInUse is null)
            {
                // 带出底层真实原因（初始化异常捕获时记录），不再用笼统文案覆盖掉关键信息
                ShowError(_lastInitError is null
                    ? "播放器初始化失败，无法播放。"
                    : $"播放器初始化失败：{_lastInitError}");
                return;
            }

            _lastUrl = url;
            ErrorLabel.IsVisible = false;
            CoverOverlay.IsVisible = false;

            // net10 版 LibVLCSharp 的 Media 构造第三参（options）非可选参数，必须显式传空数组
            using var media = new Media(_libVlcInUse, uri, Array.Empty<string>());
            _mediaPlayer.Play(media);
        }

        /// <summary>暂停/恢复播放（视频区点击或按钮触发）。</summary>
        public void TogglePlayPause()
        {
            if (_mediaPlayer is null) return;

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }
            else if (_mediaPlayer.State == VLCState.Paused)
            {
                _mediaPlayer.Play(); // 暂停恢复：从暂停位继续（不重建 Media 从头播）
            }
            else if (!string.IsNullOrWhiteSpace(_lastUrl))
            {
                // Ended/Stopped 状态：走完整重播逻辑（重建 Media）
                Play(_lastUrl);
            }
        }

        /// <summary>静音切换。</summary>
        public void ToggleMute()
        {
            if (_mediaPlayer is null) return;
            _mediaPlayer.ToggleMute();
            MuteButton.Text = _mediaPlayer.Mute ? "🔇" : "🔊";
        }

        /// <summary>循环切换显示模式：适应 → 16:9 → 4:3。</summary>
        public void CycleDisplayMode()
        {
            _displayModeIndex = (_displayModeIndex + 1) % DisplayModes.Length;
            ApplyDisplayMode();
            DisplayModeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>当前显示模式文案（"适应" / "16:9" / "4:3"）。</summary>
        public string CurrentDisplayMode => DisplayModes[_displayModeIndex].Label;

        /// <summary>停止播放并回到封面状态。</summary>
        public void Stop()
        {
            _resumePositionMs = 0;   // 主动停止 = 进度作废（防同 URL 重播误跳旧进度）
            _resumeAfterReload = false;
            try { _mediaPlayer?.Stop(); } catch { /* 忽略释放竞态 */ }
            PlayPauseButton.Text = "▶";
            ProgressBar.Value = 0;
            TimeLabel.Text = "00:00 / " + FormatTime(_lengthMs);
            CoverOverlay.IsVisible = true;
            SetControlsVisible(true);
        }

        // ---------------- 播放器实例管理 ----------------

        /// <summary>确保 _mediaPlayer 与可用的 LibVLC 实例就绪；配置不匹配则拆旧建新。</summary>
        private void EnsurePlayerFor(bool allowUntrustedTls)
        {
#if WINDOWS
            // SwapChain 选项未就绪时不建播放器（否则 VLC 弹独立窗口），等 Initialized 事件
            if (_winSwapChainOptions is null) return;
#endif
            // 现有实例可用条件：实例已存在，且（不需要信任标志 或 实例创建时已带标志）。
            // 标志只单向升级：带 --no-tls-verify 的实例也能播普通源，避免来回重建。
            bool instanceUsable =
#if WINDOWS
                _winLibVlc is not null && (!allowUntrustedTls || _winLibVlcHasUntrustedFlag);
#else
                _libVlc is not null && (!allowUntrustedTls || _libVlcHasUntrustedFlag);
#endif
            if (instanceUsable && _mediaPlayer is not null && _libVlcInUse is not null)
                return; // 实例与播放器都在且配置匹配，复用

            // 需要新建/重建：先拆旧播放器，再取实例（内部会释放被替换的旧 LibVLC），最后建播放器
            DestroyPlayer();
            try
            {
                var target = GetOrCreateLibVlc(allowUntrustedTls);
                CreatePlayerFor(target);
            }
            catch (Exception ex)
            {
                // 典型场景：原生库缺失 / LibVLC 实例创建失败。记录真实原因，Play 里会带出显示
                _lastInitError = $"{ex.GetType().Name}: {ex.Message}";
                ShowError($"VLC 初始化失败：{_lastInitError}");
            }
        }

        /// <summary>
        /// 取（或建）当前应使用的 LibVLC 实例。单实例策略：libVLC 3.x 一个进程只正式支持一个
        /// 实例（第二次 libvlc_new 直接失败），且 --no-tls-verify 在 3.x 上是 no-op——
        /// 双实例零收益纯风险（双实例正是"播放器初始化失败"的根因）。需要带信任标志的实例
        /// 而当前没有时：先 Dispose 旧实例再建新，任一时刻进程内只有一个活实例。
        /// </summary>
        private LibVLC GetOrCreateLibVlc(bool allowUntrustedTls)
        {
#if WINDOWS
            // Windows：LibVLC 必须带当前平台视图的 SwapChain 选项创建（vout 启动时读取渲染目标）。
            // 选项随平台视图（Tab 切走再切回会销毁重建）而变 → 实例跟随视图生命周期，不进静态池。
            var swapChain = _winSwapChainOptions ?? Array.Empty<string>();
            if (_winLibVlc is not null && (!allowUntrustedTls || _winLibVlcHasUntrustedFlag))
                return _winLibVlc;

            DisposeWinLibVlcInstances();
            _winLibVlc = new LibVLC(allowUntrustedTls
                ? swapChain.Concat(UntrustedTlsOptions).ToArray()
                : swapChain);
            _winLibVlcHasUntrustedFlag = allowUntrustedTls;
            AttachVlcLogging(_winLibVlc);
            return _winLibVlc;
#else
            lock (LibVlcGate)
            {
                if (_libVlc is not null && (!allowUntrustedTls || _libVlcHasUntrustedFlag))
                    return _libVlc;

                try { _libVlc?.Dispose(); } catch { }
                _libVlc = allowUntrustedTls
                    ? new LibVLC(UntrustedTlsOptions)
                    : new LibVLC(Array.Empty<string>());
                _libVlcHasUntrustedFlag = allowUntrustedTls;
                AttachVlcLogging(_libVlc);
                return _libVlc;
            }
#endif
        }

        private void DestroyPlayer()
        {
            if (_mediaPlayer is null) return;
            try
            {
                _mediaPlayer.Playing -= OnVlcPlaying;
                _mediaPlayer.Paused -= OnVlcPaused;
                _mediaPlayer.EndReached -= OnVlcEndReached;
                _mediaPlayer.EncounteredError -= OnVlcError;
                _mediaPlayer.TimeChanged -= OnVlcTimeChanged;
                _mediaPlayer.LengthChanged -= OnVlcLengthChanged;
                _mediaPlayer.Buffering -= OnVlcBuffering;
#if ANDROID
                _mediaPlayer.Vout -= OnVlcVoutChanged;
#endif
                try { _mediaPlayer.Stop(); } catch { /* 忽略释放竞态 */ }
                if (ReferenceEquals(VlcView.MediaPlayer, _mediaPlayer))
                    VlcView.MediaPlayer = null;
                _mediaPlayer.Dispose();
            }
            catch { /* 释放竞态忽略 */ }
            finally
            {
                _mediaPlayer = null;
                _libVlcInUse = null;
                _lengthMs = 0;
            }
        }

        private void CreatePlayerFor(LibVLC libVlc)
        {
            try
            {
                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(libVlc)
                {
                    // 优先硬解（低配设备省电保帧率），不支持时 VLC 自动降级软解
                    EnableHardwareDecoding = true,
                };
                _libVlcInUse = libVlc;

                _mediaPlayer.Playing += OnVlcPlaying;
                _mediaPlayer.Paused += OnVlcPaused;
                _mediaPlayer.EndReached += OnVlcEndReached;
                _mediaPlayer.EncounteredError += OnVlcError;
                _mediaPlayer.TimeChanged += OnVlcTimeChanged;
                _mediaPlayer.LengthChanged += OnVlcLengthChanged;
                _mediaPlayer.Buffering += OnVlcBuffering;
#if ANDROID
                _mediaPlayer.Vout += OnVlcVoutChanged;   // Surface 重建时 vout 计数变化，用于续播
#endif

                // VideoView 挂上 MediaPlayer 才会开始渲染视频（Android：内部自动建 AWindow
                // 挂 SurfaceView；Windows：Handler 把 MediaPlayer 传给平台 SwapChainPanel 视图）
                VlcView.MediaPlayer = _mediaPlayer;
                ApplyDisplayMode();
            }
            catch (Exception ex)
            {
                _lastInitError = $"{ex.GetType().Name}: {ex.Message}";
                ShowError($"VLC 初始化失败：{_lastInitError}");
            }
        }

        // ---------------- 布局与生命周期 ----------------

        private void OnSizeChanged(object? sender, EventArgs e)
        {
            if (Aspect <= 0 || Width <= 0) return;
            var height = Width / Aspect;
            if (Math.Abs(HeightRequest - height) > 0.5)
                HeightRequest = height;
        }

        private void OnLoaded(object? sender, EventArgs e)
        {
#if !WINDOWS
            // 切回本 Tab：恢复切走前的播放（VLC 的 Pause/Play 会保留进度）
            // Windows 不走这里：平台视图会随 Tab 切换销毁重建，恢复逻辑在 OnWinSwapChainReady 整体重播
            if (_resumeAfterReload && _mediaPlayer is not null && !string.IsNullOrWhiteSpace(_lastUrl))
            {
                _resumeAfterReload = false;
                if (!_mediaPlayer.IsPlaying)
                    _mediaPlayer.Play();
            }
#endif
        }

        private void OnUnloaded(object? sender, EventArgs e)
        {
#if WINDOWS
            // Windows：切走时平台视图 Unloaded 会销毁 SwapChain（VideoViewBase.DestroySwapChain），
            // vout 若还挂在旧交换链上继续渲染就是悬垂指针 → 先记进度再整体 Stop（拆除 vout），
            // 切回后由 OnWinSwapChainReady 重建播放器并定位续播。暂停态同样要记（切回才能恢复）。
            if (_mediaPlayer is not null && !string.IsNullOrWhiteSpace(_lastUrl))
            {
                _resumeAfterReload = true;
                try { _resumePositionMs = Math.Max(0, _mediaPlayer.Time); } catch { _resumePositionMs = 0; }
                try { _mediaPlayer.Stop(); } catch { }
            }
#else
            // Android/iOS：切走暂停即可（Surface 生命周期由库管理），切回 Play 续播
            if (_mediaPlayer?.IsPlaying == true)
            {
                _resumeAfterReload = true;
                _mediaPlayer.Pause();
            }
#endif
        }

#if WINDOWS
        // ============ Windows 专用：SwapChainPanel 内嵌渲染 ============
        // VLC 的 Direct3D11 输出在 vout 启动时从 LibVLC 实例选项（--winrt-swapchain /
        // --winrt-d3dcontext）读取渲染目标，事后无法挂接 → 选项必须在建 LibVLC 前拿到，
        // 而选项只有平台视频视图 Initialized 之后才存在。不给 → VLC 自建独立窗口（弹窗根因）。
        // 接法与官方 MAUI 示例一致：HandlerChanged → PlatformView.Initialized →
        // new LibVLC(swapchainOptions) → new MediaPlayer(libVLC)。
        private LibVLC? _winLibVlc;                 // 单实例（含 SwapChain 选项；见 GetOrCreateLibVlc 注释）
        private bool _winLibVlcHasUntrustedFlag;    // _winLibVlc 创建时是否带了 --no-tls-verify
        private string[]? _winSwapChainOptions;
        private LibVLCSharp.Platforms.Windows.VideoView? _winPlatformView;
        private string? _pendingPlayUrl;   // 平台视图就绪前的排队播放请求

        private void OnVlcViewHandlerChanged(object? sender, EventArgs e)
        {
            if (VlcView.Handler?.PlatformView is not LibVLCSharp.Platforms.Windows.VideoView platformView)
                return;

            // Tab 切回时 MAUI 会重挂 Handler 并重建平台视图：换订阅目标
            if (ReferenceEquals(_winPlatformView, platformView)) return;
            if (_winPlatformView != null)
                _winPlatformView.Initialized -= OnWinPlatformInitialized;

            _winPlatformView = platformView;
            platformView.Initialized += OnWinPlatformInitialized;

            // 兜底：HandlerChanged 到达时平台视图若已初始化完成（错过 Initialized 事件），直接读
            // SwapChainOptions 属性取选项；未就绪时 getter 会抛 InvalidOperationException，
            // 交给后续 Initialized 事件处理。
            // 注：API 是 public 属性 VideoViewBase.SwapChainOptions（VideoView 继承），
            // 没有叫 GetSwapChainOptions 的方法——那只是 getter 异常文案里的旧说法
            if (_winSwapChainOptions is null)
            {
                try { OnWinSwapChainReady(platformView.SwapChainOptions); }
                catch { /* 尚未真正就绪则等 Initialized */ }
            }
        }

        private void OnWinPlatformInitialized(object? sender, LibVLCSharp.Platforms.Windows.InitializedEventArgs e)
            => OnWinSwapChainReady(e.SwapChainOptions);

        /// <summary>平台视频视图 SwapChain 就绪（或重建）：按新选项建/重建 LibVLC 与播放器，消费排队请求。</summary>
        private void OnWinSwapChainReady(string[]? swapChainOptions)
        {
            if (swapChainOptions is null || swapChainOptions.Length == 0) return;

            // SwapChain 选项变化（Tab 切走再切回会销毁重建平台视图，指针全新）：
            // 按值比较（每次 Initialized 都会 new 一个数组，ReferenceEquals 永远不等会误重建）；
            // 内容相同 → 保留播放器；不同 → 旧实例选项指向已销毁的面板，播放器与实例全部丢弃重建
            if (_winSwapChainOptions is { } oldOptions && !oldOptions.SequenceEqual(swapChainOptions))
            {
                DestroyPlayer();
                DisposeWinLibVlcInstances();
            }

            _winSwapChainOptions = swapChainOptions;
            EnsurePlayerFor(AllowUntrustedTls);

            // 消费排队的播放请求（深链接可能在平台视图就绪前到达）
            if (_pendingPlayUrl is not null)
            {
                var url = _pendingPlayUrl;
                _pendingPlayUrl = null;
                Play(url);
            }
            else if (_resumeAfterReload && !string.IsNullOrWhiteSpace(_lastUrl))
            {
                // 切回 Tab：播放器已按新 SwapChain 重建，重播并在开播后定位到切走时的进度
                _resumeAfterReload = false;
                Play(_lastUrl);
            }
        }

        private void DisposeWinLibVlcInstances()
        {
            try { _winLibVlc?.Dispose(); } catch { }
            _winLibVlc = null;
            _winLibVlcHasUntrustedFlag = false;
        }
#endif

        // ---------------- VLC 事件（回调在 VLC 后台线程，必须切回主线程碰 UI） ----------------

        private void OnVlcPlaying(object? sender, EventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                CoverOverlay.IsVisible = false;
                BufferingOverlay.IsVisible = false;
                ErrorLabel.IsVisible = false;
                PlayPauseButton.Text = "⏸";
                ScheduleControlsAutoHide();

                // 重建/中断后重播的续播定位（Windows 切 Tab 重建播放器；Android 切后台 Surface
                // 销毁重建）。值仅在对应场景记录；Pause→Play 原生保留进度的场合设置同值为无害 seek
                if (_resumePositionMs > 0 && _mediaPlayer is not null)
                {
                    try { _mediaPlayer.Time = _resumePositionMs; } catch { }
                    _resumePositionMs = 0;
                }
            });

        private void OnVlcPaused(object? sender, EventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                PlayPauseButton.Text = "▶";
                SetControlsVisible(true);
            });

        private void OnVlcEndReached(object? sender, EventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                // VLC 回调线程里不能直接调播放器 API（会死锁），先切回主线程；
                // Stop 让播放器从 Ended 状态复位，便于下次重播；
                // 自然播完 = 进度无意义，续播定位一并作废（否则同 URL 重播会误跳到旧进度）
                _resumePositionMs = 0;
                _resumeAfterReload = false;
                try { _mediaPlayer?.Stop(); } catch { }
                PlayPauseButton.Text = "▶";
                ProgressBar.Value = 0;
                TimeLabel.Text = "00:00 / " + FormatTime(_lengthMs);
                CoverOverlay.IsVisible = true;
                SetControlsVisible(true);
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            });

        private void OnVlcError(object? sender, EventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                BufferingOverlay.IsVisible = false;
                CoverOverlay.IsVisible = true;
                const string msg = "播放出错：无法打开该视频源（地址失效 / 网络不通 / 格式不支持 / 证书不受信任）";
                ShowError(msg);
                PlaybackFailed?.Invoke(this, msg);
            });

        private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_seekingByUser) return;
                if (_lengthMs > 0)
                    ProgressBar.Value = Math.Clamp(e.Time / (double)_lengthMs, 0, 1);
                TimeLabel.Text = $"{FormatTime(e.Time)} / {FormatTime(_lengthMs)}";
            });

        private void OnVlcLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                _lengthMs = Math.Max(0, e.Length);
                TimeLabel.Text = $"{FormatTime(_mediaPlayer?.Time ?? 0)} / {FormatTime(_lengthMs)}";
            });

        private void OnVlcBuffering(object? sender, MediaPlayerBufferingEventArgs e)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                // 缓冲未完成且尚未真正开播时显示指示；开播后由 Playing 事件隐藏
                BufferingOverlay.IsVisible = e.Cache < 99f
                                             && _mediaPlayer?.IsPlaying != true
                                             && !string.IsNullOrWhiteSpace(_lastUrl);
            });

        // ---------------- 控制条显隐 ----------------

        private void SetControlsVisible(bool visible) => ControlsPanel.IsVisible = visible;

        // 播放中点一下显示控制条，3 秒无操作自动隐藏（暂停时保持常显）
        private void ScheduleControlsAutoHide()
        {
            var token = ++_hideToken;
            var timer = Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (token != _hideToken) return;
                if (_mediaPlayer?.IsPlaying == true && ControlsPanel.IsVisible)
                    SetControlsVisible(false);
            };
            timer.Start();
        }

        // ---------------- 控件事件 ----------------

        private void OnSurfaceTapped(object? sender, TappedEventArgs e)
        {
            if (ControlsPanel.IsVisible)
            {
                SetControlsVisible(false);
            }
            else
            {
                SetControlsVisible(true);
                if (_mediaPlayer?.IsPlaying == true)
                    ScheduleControlsAutoHide();
            }
        }

        private void OnTogglePlayPause(object? sender, EventArgs e) => TogglePlayPause();

        private void OnToggleMute(object? sender, EventArgs e) => ToggleMute();

        private void OnCycleDisplayMode(object? sender, EventArgs e) => CycleDisplayMode();

        private void OnProgressDragStarted(object? sender, EventArgs e) => _seekingByUser = true;

        private void OnProgressDragCompleted(object? sender, EventArgs e)
        {
            _seekingByUser = false;
            if (_mediaPlayer is null || _lengthMs <= 0) return;
            var pos = Math.Clamp((float)ProgressBar.Value, 0f, 1f);
            _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(_lengthMs * pos));
        }

        private void OnProgressValueChanged(object? sender, ValueChangedEventArgs e)
        {
            // 只有用户拖动中才实时预览时间；正常播放时进度条由 TimeChanged 驱动
            if (!_seekingByUser || _lengthMs <= 0) return;
            TimeLabel.Text = $"{FormatTime((long)(_lengthMs * e.NewValue))} / {FormatTime(_lengthMs)}";
        }

        private void OnSourceChanged(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && AutoPlay)
                Play(url); // Play 内部按 AllowUntrustedTls 自选 LibVLC 实例（Windows 上可能排队）
        }

        // ---------------- 内部工具 ----------------

#if ANDROID
        /// <summary>
        /// Android：Activity.onPause（切后台/被遮挡/锁屏）。Surface 即将销毁，播放器必须暂停，
        /// 否则视频输出线程对着已销毁的 Surface 渲染（黑屏/崩溃）。记进度供回前台续播。
        /// </summary>
        private void OnAndroidActivityPause()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_mediaPlayer?.IsPlaying != true) return;
                _resumeAfterReload = true;
                try { _resumePositionMs = Math.Max(0, _mediaPlayer.Time); } catch { _resumePositionMs = 0; }
                try { _mediaPlayer.Pause(); } catch { }
            });
        }

        /// <summary>
        /// Android：Activity.onResume（回前台）。Surface 会由 AWindow 自动重建接回播放器；
        /// 若播放器被系统停掉（vout 事件里没来得及恢复的场合），这里兜底续播。
        /// </summary>
        private void OnAndroidActivityResume()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_mediaPlayer is null || !_resumeAfterReload || string.IsNullOrWhiteSpace(_lastUrl)) return;
                _resumeAfterReload = false;
                if (!_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Play(); // Pause 恢复：从暂停位继续；Time 定位在 OnVlcPlaying 里做
                }
            });
        }

        /// <summary>
        /// Android：vout 计数变化（Surface 创建/销毁都会触发）。切后台/锁屏/旋转后回来
        /// Surface 会销毁重建（vout N→0→N）：AWindow 自动把新 Surface 接回播放器，
        /// 视频输出恢复无需干预；播放器被系统停掉的场合由 OnLoaded 的续播逻辑兜底。
        /// 这里只处理一件 Android 特有的事：Surface 销毁瞬间（vout 归零）视频输出已断，
        /// 若正在播放则记一份进度，避免重建后从头播。
        /// </summary>
        private void OnVlcVoutChanged(object? sender, EventArgs e)
        {
            try
            {
                if (_mediaPlayer is null) return;
                if (_mediaPlayer.VoutCount == 0 && _mediaPlayer.IsPlaying)
                {
                    // Surface 已销毁但播放器还在跑（切后台/锁屏）：记进度，
                    // 回前台 Surface 重建（vout>0）后由续播定位恢复
                    _resumePositionMs = Math.Max(0, _mediaPlayer.Time);
                    _resumeAfterReload = true;
                }
                else if (_mediaPlayer.VoutCount > 0 && _resumeAfterReload && _resumePositionMs > 0)
                {
                    // Surface 重建完成：清掉切走标记；播放器还在播则进度本就连续，
                    // 残留的定位值一并作废（否则下次 Playing 事件会误跳到旧进度）
                    _resumeAfterReload = false;
                    if (_mediaPlayer.IsPlaying)
                    {
                        _resumePositionMs = 0;
                    }
                    else if (!string.IsNullOrWhiteSpace(_lastUrl))
                    {
                        _mediaPlayer.Play(); // 被系统停了：重播，Playing 事件里按记录值定位
                    }
                }
            }
            catch { /* vout 回调竞态忽略 */ }
        }
#endif

        private void ApplyDisplayMode()
        {
            var (label, ratio) = DisplayModes[_displayModeIndex];
            DisplayModeButton.Text = label;
            if (_mediaPlayer is not null)
                _mediaPlayer.AspectRatio = ratio;
        }

        private void ShowError(string message)
        {
            ErrorLabel.Text = message;
            ErrorLabel.IsVisible = true;
            BufferingOverlay.IsVisible = false;
        }

        /// <summary>订阅 LibVLC 原生日志输出到 Debug（VS 输出窗口可见，诊断播放失败用）。</summary>
        private static void AttachVlcLogging(LibVLC libVlc)
        {
            try
            {
                libVlc.Log += (_, e) =>
                    System.Diagnostics.Debug.WriteLine($"[VLC/{e.Level}] {e.Module}: {e.Message}");
            }
            catch { /* 日志订阅失败不影响播放 */ }
        }

        private static string FormatTime(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
    }
}
