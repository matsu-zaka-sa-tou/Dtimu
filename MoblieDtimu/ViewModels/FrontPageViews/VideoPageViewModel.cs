using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MoblieDtimu.PageViews.FrontPageViews
{
    /// <summary>
    /// 视频 Tab 页面数据：当前播放信息 + 点赞/点踩/收藏/分享/下载 + UP主关注 + 相关推荐列表。
    /// 目前为演示假数据；接真实后端（DiscViewer / Dtimu）时替换 Recommends 的来源即可，
    /// 页面与控件不用动。
    /// </summary>
    public class VideoPageViewModel : INotifyPropertyChanged
    {
        /// <summary>当前视频自然播完后是否自动连播下一个推荐。</summary>
        public bool AutoPlayNext { get; set; } = true;

        public event EventHandler? CurrentVideoChanged;
        public event EventHandler? ShareRequested;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<RecommendItem> Recommends { get; }
        public IList<ActionButtonItem> ActionButtons { get; }

        public ICommand PlayCommand { get; }
        public ICommand LikeCommand { get; }
        public ICommand DislikeCommand { get; }
        public ICommand FavoriteCommand { get; }
        public ICommand ShareCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand FollowCommand { get; }

        // 当前播放
        private RecommendItem? _current;
        private int _currentIndex = -1;
        private string _currentTitle = "";
        private string _currentMeta = "";
        private string? _currentUrl;
        private bool _isExternal;

        // UP 主
        private string _upName = "";
        private string _upEmoji = "📺";
        private string _upFans = "";
        private Color _upColor = Colors.DimGrey;
        private bool _isFollowed;

        // 互动状态
        private bool _isLiked;
        private bool _isDisliked;
        private bool _isFavorited;
        private DownloadState _downloadState = DownloadState.None;
        private int _downloadProgress;

        private readonly ActionButtonItem _likeButton;
        private readonly ActionButtonItem _dislikeButton;
        private readonly ActionButtonItem _favoriteButton;
        private readonly ActionButtonItem _shareButton;
        private readonly ActionButtonItem _downloadButton;

        public VideoPageViewModel()
        {
            PlayCommand = new Command<RecommendItem>(item => { if (item != null) PlayRecommend(item); });
            LikeCommand = new Command(ToggleLike);
            DislikeCommand = new Command(ToggleDislike);
            FavoriteCommand = new Command(ToggleFavorite);
            ShareCommand = new Command(() => ShareRequested?.Invoke(this, EventArgs.Empty));
            DownloadCommand = new Command(ToggleDownload);
            FollowCommand = new Command(ToggleFollow);

            Recommends = new ObservableCollection<RecommendItem>(CreateDemoItems(PlayCommand));

            _likeButton = new ActionButtonItem { Command = LikeCommand };
            _dislikeButton = new ActionButtonItem { Command = DislikeCommand };
            _favoriteButton = new ActionButtonItem { Command = FavoriteCommand };
            _shareButton = new ActionButtonItem { Icon = "↗", Text = "分享", TextColor = Colors.Gray };
            _downloadButton = new ActionButtonItem { Command = DownloadCommand };
            ActionButtons = new List<ActionButtonItem>
            {
                _likeButton, _dislikeButton, _favoriteButton, _shareButton, _downloadButton,
            };

            // 初始播放第一个推荐
            PlayRecommend(Recommends[0]);
        }

        // ---------------- 当前播放 ----------------

        public string CurrentTitle { get => _currentTitle; private set => Set(ref _currentTitle, value); }
        public string CurrentMeta { get => _currentMeta; private set => Set(ref _currentMeta, value); }
        public string? CurrentUrl { get => _currentUrl; private set => Set(ref _currentUrl, value); }

        /// <summary>当前是否在播 dtimu:// 深链接等外部内容（而非推荐列表项）。</summary>
        public bool IsExternal { get => _isExternal; private set => Set(ref _isExternal, value); }

        /// <summary>切到某个推荐视频：更新标题/UP信息、重置互动状态、通知页面滚动回顶部。</summary>
        public void PlayRecommend(RecommendItem item)
        {
            var index = Recommends.IndexOf(item);
            if (index < 0) return;

            _current = item;
            _currentIndex = index;
            IsExternal = false;

            foreach (var r in Recommends)
                r.IsPlaying = ReferenceEquals(r, item);

            CurrentTitle = item.Title;
            CurrentMeta = $"{item.PlayCountText}播放 · {item.DanmakuText}弹幕 · {item.DateText}";
            CurrentUrl = item.Url;

            UpName = item.UpName;
            UpEmoji = item.UpEmoji;
            UpColor = item.UpColor;
            UpFans = item.FansText;

            // 新视频：互动状态全部回到默认
            _isLiked = false;
            _isDisliked = false;
            _isFavorited = false;
            _isFollowed = false;
            _downloadState = DownloadState.None; // 下载中的模拟计时器检测到状态变化会自行停止
            _downloadProgress = 0;
            UpdateActionButtonVisuals();
            UpdateFollowVisual();

            CurrentVideoChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>自动连播：播完当前后按列表顺序播下一个（循环）。</summary>
        public void PlayNext()
        {
            if (_currentIndex < 0 || Recommends.Count == 0) return;
            var next = Recommends[(_currentIndex + 1) % Recommends.Count];
            PlayRecommend(next);
        }

        /// <summary>
        /// 播放外部地址（dtimu:// 深链接）：标题取 URL 文件名，互动状态重置，列表全部取消选中，
        /// CurrentUrl 更新触发绑定 → VedioPlayer.Source 变化 → AutoPlay 自动播放。
        /// 注意：外部内容不参与自动连播（播完停在封面，等用户选下一个）。
        /// </summary>
        public void PlayExternal(string url)
        {
            foreach (var r in Recommends)
                r.IsPlaying = false;
            _current = null;
            _currentIndex = -1;
            IsExternal = true;

            CurrentTitle = ExtractFileName(url);
            CurrentMeta = $"外部来源 · {new Uri(url).Host}";
            CurrentUrl = url;

            _isLiked = false;
            _isDisliked = false;
            _isFavorited = false;
            _isFollowed = false;
            _downloadState = DownloadState.None; // 下载中的模拟计时器检测到状态变化会自行停止
            _downloadProgress = 0;
            UpdateActionButtonVisuals();
            UpdateFollowVisual();

            CurrentVideoChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string ExtractFileName(string url)
        {
            try
            {
                var name = System.IO.Path.GetFileName(new Uri(url).AbsolutePath);
                return string.IsNullOrWhiteSpace(name) ? url : name;
            }
            catch
            {
                return url; // 非常规 URL 兜底直接展示原串
            }
        }

        // ---------------- UP 主 ----------------

        public string UpName { get => _upName; private set => Set(ref _upName, value); }
        public string UpEmoji { get => _upEmoji; private set => Set(ref _upEmoji, value); }
        public string UpFans { get => _upFans; private set => Set(ref _upFans, value); }
        public Color UpColor { get => _upColor; private set => Set(ref _upColor, value); }
        public string FollowText => _isFollowed ? "已关注" : "关注";
        public Color FollowBackground => _isFollowed ? Color.FromArgb("#BBBBBB") : Color.FromArgb("#00B8B8");

        private void ToggleFollow()
        {
            _isFollowed = !_isFollowed;
            UpdateFollowVisual();
        }

        private void UpdateFollowVisual()
        {
            OnPropertyChanged(nameof(FollowText));
            OnPropertyChanged(nameof(FollowBackground));
        }

        // ---------------- 互动按钮 ----------------

        private void ToggleLike()
        {
            _isLiked = !_isLiked;
            if (_isLiked) _isDisliked = false; // 赞和踩互斥
            UpdateActionButtonVisuals();
        }

        private void ToggleDislike()
        {
            _isDisliked = !_isDisliked;
            if (_isDisliked) _isLiked = false;
            UpdateActionButtonVisuals();
        }

        private void ToggleFavorite()
        {
            _isFavorited = !_isFavorited;
            UpdateActionButtonVisuals();
        }

        // 下载状态机：待下载 → 下载中（模拟进度）→ 已下载；下载中再点一次=取消
        private void ToggleDownload()
        {
            switch (_downloadState)
            {
                case DownloadState.None:
                    _downloadState = DownloadState.Downloading;
                    _downloadProgress = 0;
                    UpdateActionButtonVisuals();
                    // 模拟下载进度（真实下载后续接 Dtimu 转码源拉流落盘）
                    Device.StartTimer(TimeSpan.FromMilliseconds(600), () =>
                    {
                        if (_downloadState != DownloadState.Downloading) return false; // 换片/取消则停止
                        _downloadProgress = Math.Min(100, _downloadProgress + 15);
                        if (_downloadProgress >= 100)
                        {
                            _downloadState = DownloadState.Done;
                            UpdateActionButtonVisuals();
                            return false;
                        }
                        UpdateActionButtonVisuals();
                        return true;
                    });
                    break;

                case DownloadState.Downloading:
                    _downloadState = DownloadState.None;
                    _downloadProgress = 0;
                    UpdateActionButtonVisuals();
                    break;

                case DownloadState.Done:
                    // 已下载：真实场景跳下载管理页，这里维持状态即可
                    break;
            }
        }

        private void UpdateActionButtonVisuals()
        {
            var likes = (_current?.LikeCount ?? 0) + (_isLiked ? 1 : 0);
            _likeButton.Icon = _isLiked ? "👍" : "👍";
            _likeButton.Text = FormatCount(likes);
            _likeButton.TextColor = _isLiked ? Color.FromArgb("#00B8B8") : Colors.Gray;

            _dislikeButton.Icon = "👎";
            _dislikeButton.Text = _isDisliked ? "已踩" : "点踩";
            _dislikeButton.TextColor = _isDisliked ? Color.FromArgb("#00B8B8") : Colors.Gray;

            _favoriteButton.Icon = _isFavorited ? "★" : "☆";
            _favoriteButton.Text = _isFavorited ? "已收藏" : "收藏";
            _favoriteButton.TextColor = _isFavorited ? Color.FromArgb("#FFB800") : Colors.Gray;

            _downloadButton.Icon = _downloadState switch
            {
                DownloadState.Downloading => "⏳",
                DownloadState.Done => "✓",
                _ => "⬇",
            };
            _downloadButton.Text = _downloadState switch
            {
                DownloadState.Downloading => $"{_downloadProgress}%",
                DownloadState.Done => "已下载",
                _ => "下载",
            };
            _downloadButton.TextColor = _downloadState switch
            {
                DownloadState.Downloading => Color.FromArgb("#00B8B8"),
                DownloadState.Done => Color.FromArgb("#4CAF50"),
                _ => Colors.Gray,
            };
        }

        // ---------------- 演示数据 ----------------

        // 公网测试源（VLC 全兼容）；实际使用换成自己 Dtimu.OnlineDecoder 转码服务的输出地址
        private static readonly string[] DemoUrls =
        {
            "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4",
            "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4",
            "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ForBiggerBlazes.mp4",
            "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ForBiggerEscapes.mp4",
            "http://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8", // HLS 演示
            "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/Sintel.mp4",
        };

        private static List<RecommendItem> CreateDemoItems(ICommand playCommand)
        {
            RecommendItem R(
                string title, int urlIndex, string up, string upEmoji, string upHex,
                string duration, int plays, int danmakus, int likes, string fans, string date,
                string emoji, string thumbHex) => new()
            {
                Title = title,
                Url = DemoUrls[urlIndex % DemoUrls.Length],
                UpName = up,
                UpEmoji = upEmoji,
                UpColor = Color.FromArgb(upHex),
                FansText = fans,
                Duration = duration,
                PlayCountText = FormatCount(plays),
                DanmakuText = FormatCount(danmakus),
                LikeCount = likes,
                DateText = date,
                Emoji = emoji,
                ThumbColor = Color.FromArgb(thumbHex),
                Meta = $"{FormatCount(plays)} · {up}",
                PlayCommand = playCommand,
            };

            return new List<RecommendItem>
            {
                R("【4K 60帧】重庆赛博朋克夜景航拍：每一帧都是壁纸", 0, "山城飞行日记", "🛩", "#5B4B8A", "12:36", 582000, 12400, 32890, "2.1万粉丝", "10-01", "🏙", "#34275A"),
                R("手搓56K软猫：把1998年的拨号上网接进2026年的家宽", 1, "复古通信研究所", "☎", "#1F3A5F", "21:08", 34000, 2100, 5812, "8600粉丝", "09-18", "📞", "#0F3D3E"),
                R("PPP over RTP 实战：用RTP隧道承载PPP链路全流程", 2, "复古通信研究所", "☎", "#1F3A5F", "15:42", 8912, 466, 1203, "8600粉丝", "09-02", "🌐", "#173F5F"),
                R("Lumia 950 复活计划：骁龙808 的最后一战", 3, "塞班遗民", "📱", "#4A4A5A", "18:24", 127000, 5300, 8934, "3.4万粉丝", "08-27", "📱", "#2F3A4A"),
                R("Ryzen 5600H 五年老兵实测：2026年还能跑什么游戏？", 4, "装机猿", "🎮", "#5C4033", "22:15", 89000, 4100, 7260, "12万粉丝", "10-12", "🎮", "#4A3326"),
                R("奉节脐橙从开花到采摘全过程：三峡边的四季", 5, "三峡果农小刘", "🍊", "#7A5C1E", "09:47", 51000, 890, 6534, "1.8万粉丝", "10-08", "🍊", "#5C4718"),
                R("用 .NET 手写线程池是种什么体验", 0, "硬核搬砖人", "🧵", "#3A2E4A", "16:33", 22000, 980, 3120, "6700粉丝", "09-25", "🧵", "#2E2A44"),
                R("天翼光猫 HSAN 平台 telnet 解锁全记录", 1, "电信玩家社区", "🛠", "#0F4C4C", "13:59", 18000, 760, 2480, "9200粉丝", "07-30", "🛠", "#12403E"),
                R("Avalonia 音乐播放器开发实录（一）：MVVM 起手式", 2, "桌面端笔记", "🎵", "#4A2E5A", "14:21", 9613, 380, 1506, "4300粉丝", "10-15", "🎵", "#38254C"),
                R("RTX 3050 Laptop 2026 帧数大考：40款游戏横评", 3, "装机猿", "🎯", "#5C2E2E", "25:40", 153000, 6800, 11000, "12万粉丝", "10-19", "🎯", "#4A2A2A"),
            };
        }

        // ---------------- 工具 ----------------

        private static string FormatCount(int n) => n >= 10000 ? $"{n / 10000.0:0.#}万" : n.ToString();

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private enum DownloadState { None, Downloading, Done }
    }

    /// <summary>相关推荐条目（缩略图、标题、UP、时长、播放数；IsPlaying 标记当前正在播放）。</summary>
    public class RecommendItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; init; } = "";
        public string Url { get; init; } = "";
        public string UpName { get; init; } = "";
        public string UpEmoji { get; init; } = "📺";
        public Color UpColor { get; init; } = Colors.DimGrey;
        public string FansText { get; init; } = "";
        public string Duration { get; init; } = "";
        public string PlayCountText { get; init; } = "";
        public string DanmakuText { get; init; } = "";
        public int LikeCount { get; init; }
        public string DateText { get; init; } = "";
        public string Emoji { get; init; } = "🎬";
        public Color ThumbColor { get; init; } = Colors.DimGrey;
        public string Meta { get; init; } = "";

        /// <summary>点击卡片切换播放的命令（VM 注入，参数是本条目）。</summary>
        public ICommand PlayCommand { get; init; } = CommandFactory.Disabled;

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
            }
        }
    }

    /// <summary>操作按钮行的一个按钮（图标/文字/颜色运行时可变，点击走 Command）。</summary>
    public class ActionButtonItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private string _icon = "";
        private string _text = "";
        private Color _textColor = Colors.Gray;

        public ICommand Command { get; init; } = CommandFactory.Disabled;

        public string Icon { get => _icon; set => Set(ref _icon, value); }
        public string Text { get => _text; set => Set(ref _text, value); }
        public Color TextColor { get => _textColor; set => Set(ref _textColor, value); }

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>占位空命令（防止 XAML 绑定 null Command 报错）。</summary>
    internal static class CommandFactory
    {
        public static readonly ICommand Disabled = new Command(() => { });
    }
}
