using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using MoblieDtimu.Views.FrontPageViews;

namespace MoblieDtimu
{
    public partial class MainPage : ContentPage
    {
        // 缓存各页面实例：来回切换时保留滚动位置、输入框内容等状态
        // _videoView 用强类型：dtimu:// 深链接需要调 PlayExternal
        private readonly VideoView _videoView;
        private readonly ContentView _musicView;
        private readonly ContentView _photoView;
        private readonly ContentView _settingsView;

        public MainPage()
        {
            InitializeComponent();

            _videoView = new VideoView();
            _musicView = new MusicView();
            _photoView = new PhotoView();
            _settingsView = new SettingsView();

            // 默认选中第一个 Tab
            SwitchTab(0);

            // dtimu:// 深链接：运行中收到 → 切到视频 Tab 并交给播放器（BeginInvoke 排队一拍，等首帧布局完成）
            DtimuLinkDispatcher.LinkReceived += OnDtimuLinkReceived;

            // 冷启动缓存的深链接（MainActivity OnCreate 时 MainPage 还没由 DataTemplate 构造，调度器先存了）
            var pending = DtimuLinkDispatcher.TakePendingUrl();
            if (pending != null) HandleDtimuLink(pending);
        }

        private void OnDtimuLinkReceived(object? sender, string url) => HandleDtimuLink(url);

        /// <summary>处理 dtimu:// 深链接：切到视频 Tab，播放内嵌媒体地址（目标可能是自签名 https）。</summary>
        private void HandleDtimuLink(string url)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SwitchTab(0);
                _videoView.PlayExternal(url);
            });
        }

        private void OnTabTapped(object? sender, TappedEventArgs e)
        {
            if (e.Parameter != null && int.TryParse(e.Parameter.ToString(), out int tabIndex))
            {
                SwitchTab(tabIndex);
            }
        }

        private void SwitchTab(int index)
        {
            // 1. 切换主内容区
            MainContentArea.Content = index switch
            {
                0 => _videoView,
                1 => _musicView,
                2 => _photoView,
                3 => _settingsView,
                _ => _videoView
            };

            // 2. 更新底部导航栏的选中状态 (颜色)
            UpdateTabVisualState(index);
        }

        private void UpdateTabVisualState(int selectedIndex)
        {
            Color selectedColor = Color.FromArgb("#00FFFF");   // 选中的文字颜色（青）
            Color unselectedColor = Color.FromArgb("#999999"); // 未选中的颜色
            Color selectedBg = Color.FromArgb("#1E1E2F");      // 选中的圆角背景（深藏青）

            // 重置所有 Tab：文字灰色、背景透明
            Tab1Text.TextColor = unselectedColor; Tab1Background.BackgroundColor = null;
            Tab2Text.TextColor = unselectedColor; Tab2Background.BackgroundColor = null;
            Tab3Text.TextColor = unselectedColor; Tab3Background.BackgroundColor = null;
            Tab4Text.TextColor = unselectedColor; Tab4Background.BackgroundColor = null;

            // 设置当前选中 Tab：青色文字 + 深色圆角背景
            switch (selectedIndex)
            {
                case 0: Tab1Text.TextColor = selectedColor; Tab1Background.BackgroundColor = selectedBg; break;
                case 1: Tab2Text.TextColor = selectedColor; Tab2Background.BackgroundColor = selectedBg; break;
                case 2: Tab3Text.TextColor = selectedColor; Tab3Background.BackgroundColor = selectedBg; break;
                case 3: Tab4Text.TextColor = selectedColor; Tab4Background.BackgroundColor = selectedBg; break;
            }

            // 选中指示条：透明度切换（指示条常驻占位，无布局跳动）+ 150ms 淡入淡出
            _ = Tab1Indicator.FadeTo(selectedIndex == 0 ? 1 : 0, 150);
            _ = Tab2Indicator.FadeTo(selectedIndex == 1 ? 1 : 0, 150);
            _ = Tab3Indicator.FadeTo(selectedIndex == 2 ? 1 : 0, 150);
            _ = Tab4Indicator.FadeTo(selectedIndex == 3 ? 1 : 0, 150);
        }
    }
}
