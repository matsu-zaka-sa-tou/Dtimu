using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MoblieDtimu.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            RegisterDtimuProtocol();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            base.OnLaunched(args);
            ForwardDtimuLinkIfNeeded();
        }

        // ============ dtimu:// 协议支持（非打包 WinUI 应用） ============
        // 注：若以后改成 MSIX 打包（去掉 WindowsPackageType=None），
        //     应删除下面的注册表代码，改在 Package.appxmanifest 里声明 <uap:Protocol Name="dtimu" />

        /// <summary>
        /// 每次启动把当前 exe 路径写进 HKCU 注册表（无需管理员，
        /// Debug/Release 输出路径变化时会自动更新指向）。
        /// 想彻底取消关联：Remove-Item 'HKCU:\Software\Classes\dtimu' -Recurse
        /// </summary>
        private static void RegisterDtimuProtocol()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\dtimu");
                key.SetValue("URL Protocol", string.Empty);        // 必需：标记这是 URL 协议
                key.SetValue(string.Empty, "URL:Dtimu Protocol");  // 默认值：显示名

                using var cmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\dtimu\shell\open\command");
                cmd.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            }
            catch
            {
                // 注册失败不影响正常启动
            }
        }

        /// <summary>
        /// 通过 dtimu:// 链接启动时，命令行参数里会带着完整 URL，解析后交给共享调度器
        /// （与 Android MainActivity 同一入口：DtimuLinkDispatcher）。
        /// 注意：必须传"原始字符串"，绝不能经过 System.Uri（包括
        /// SendOnAppLinkRequestReceived(new Uri(url)) 这类间接路径）——
        /// dtimu://http://... 内嵌完整 URL 的形态会被 Uri 规范化破坏：
        /// "http:" 被压成 "http"、%XX 百分号编码被解码成裸字符，链接即告失效。
        /// </summary>
        private static void ForwardDtimuLinkIfNeeded()
        {
            try
            {
                var url = Environment.GetCommandLineArgs()
                    .FirstOrDefault(a => a.StartsWith("dtimu://", StringComparison.OrdinalIgnoreCase));
                if (url is null) return;

                // Platforms\Windows 与共享代码编译进同一程序集（net10.0-windows TF），可直接调用
                DtimuLinkDispatcher.Report(url);
            }
            catch
            {
                // 链接格式异常不影响启动
            }
        }
    }
}
