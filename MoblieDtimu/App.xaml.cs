using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace MoblieDtimu
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }

        /// <summary>
        /// dtimu:// 链接统一兜底入口（Android 主路径在 MainActivity 直接调 DtimuLinkDispatcher.Report，
        /// 不会自动走到这里；本入口供其他平台/未来 http 自动验证链接复用，重复派发由调度器去重）。
        /// 形态：dtimu://play_raw_vedio=&lt;完整媒体URL&gt;（旧形态 dtimu://&lt;URL&gt; 仍兼容），
        /// 例：dtimu://play_raw_vedio=https://example.com/x.m3u8
        /// </summary>
        protected override void OnAppLinkRequestReceived(Uri uri)
        {
            base.OnAppLinkRequestReceived(uri);
            // 必须用 OriginalString（原始未规范化形式）：ToString() 会把自定义 scheme 内嵌的
            // URL 破坏（"http:" → "http"、%XX 解码成裸字符），链接解析即失败
            DtimuLinkDispatcher.Report(uri.OriginalString);
        }
    }
}