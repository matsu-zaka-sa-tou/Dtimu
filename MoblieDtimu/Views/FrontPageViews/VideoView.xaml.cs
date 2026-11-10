using Microsoft.Maui.ApplicationModel;
using MoblieDtimu.PageViews.FrontPageViews;


namespace MoblieDtimu.Views.FrontPageViews
{
    /// <summary>
    /// 视频 Tab：顶部 VLC 播放器 + 视频信息 + 操作按钮（点赞/点踩/收藏/分享/下载）
    /// + UP主关注 + 两列相关推荐（点卡片换台，播完自动连播）。
    /// 另有 dtimu:// 深链接入口（MainActivity → DtimuLinkDispatcher → MainPage → PlayExternal）。
    /// 数据与状态逻辑见 VideoPageViewModel。
    /// </summary>
    public partial class VideoView : ContentView
    {
        private readonly VideoPageViewModel _viewModel;

        public VideoView()
        {
            InitializeComponent();

            _viewModel = new VideoPageViewModel();
            BindingContext = _viewModel;

            // 换片后滚回顶部（首次设置时页面还没布局，跳过）
            _viewModel.CurrentVideoChanged += OnCurrentVideoChanged;

            // 分享先弹占位提示（后续接系统分享 / 好友分享面板）
            _viewModel.ShareRequested += async (s, e) =>
            {
                var page = Application.Current?.MainPage;
                if (page != null)
                    await page.DisplayAlert("分享", "分享面板还没实装，敬请期待", "好");
            };
        }

        /// <summary>
        /// 播放外部传入的媒体地址（dtimu:// 深链接入口）。
        /// https 源（可能是自家转码服务的自签名证书）才启用"不校验证书"模式；
        /// http 明文源用普通实例即可，避免不必要的实例重建。
        /// </summary>
        public void PlayExternal(string url)
        {
            Player.AllowUntrustedTls = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            _viewModel.PlayExternal(url);
        }

        // 播放自然结束：自动连播下一个推荐（可由 VM.AutoPlayNext 关闭）
        private void OnPlaybackEnded(object? sender, EventArgs e)
        {
            if (_viewModel.AutoPlayNext)
                _viewModel.PlayNext();
        }

        // 播放失败：提示但不打断浏览（演示用的是公网测试源，网络不通很正常）
        private void OnPlaybackFailed(object? sender, string message)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                var page = Application.Current?.MainPage;
                if (page != null)
                    await page.DisplayAlert("播放失败", message, "知道了");
            });
        }

        private void OnCurrentVideoChanged(object? sender, EventArgs e)
        {
            if (!IsLoaded) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { _ = ContentScroll.ScrollToAsync(0, 0, animated: true); }
                catch { /* 页面尚未完成布局时忽略 */ }
            });
        }
    }
}
