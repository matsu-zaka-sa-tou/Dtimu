using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MoblieDtimu
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    // 声明 dtimu:// 协议：浏览器/其他 App 点了 dtimu://xxx 会唤起本应用
    // 注：Intent 必须全限定 Android.Content.Intent，否则会解析成 Activity.Intent 实例属性（CS0120/CS0176）
    [IntentFilter(new[] { Android.Content.Intent.ActionView },
        Categories = new[] { Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable },
        DataScheme = "dtimu")]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // 冷启动路径：base.OnCreate 内 MAUI 完成初始化；MainPage 由 AppShell 的 DataTemplate 懒加载，
            // 此刻多半还没构造——Report 内部自动缓存 pending，MainPage 构造后 TakePendingUrl 消费
            ReportDeepLink(Intent?.DataString);
        }

        protected override void OnNewIntent(Android.Content.Intent? intent)
        {
            base.OnNewIntent(intent);
            // 热启动路径：App 已在运行（LaunchMode=SingleTop 复用本实例，不新建 Activity），页面已订阅事件
            ReportDeepLink(intent?.DataString);
        }

        // ============ Activity 生命周期 → VLC 播放器转发 ============
        // Android 上 Surface 随 Activity 暂停/销毁，VLC 播放器必须同步收到通知，
        // 否则切后台回来黑屏/崩溃。VedioPlayer 是唯一 VLC 使用方，构造时订阅本事件
        // （VedioPlayer 随 MainPage 常驻整个应用生命周期，无需注销）。

        /// <summary>Activity 暂停时通知播放器（VedioPlayer 构造时订阅）。</summary>
        internal static event Action? PausePlayback;
        /// <summary>Activity 恢复时通知播放器（VedioPlayer 构造时订阅）。</summary>
        internal static event Action? ResumePlayback;

        protected override void OnPause()
        {
            base.OnPause();
            try { PausePlayback?.Invoke(); } catch { }
        }

        protected override void OnResume()
        {
            base.OnResume();
            try { ResumePlayback?.Invoke(); } catch { }
        }

        private static void ReportDeepLink(string? dataString)
        {
            // 例：dataString = "dtimu://play_raw_vedio=http://example.com/x.m3u8"
            if (string.IsNullOrWhiteSpace(dataString)) return;
            DtimuLinkDispatcher.Report(dataString);
        }
    }
}
