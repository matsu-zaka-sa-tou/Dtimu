using LibVLCSharp.MAUI;
using Microsoft.Extensions.Logging;

namespace MoblieDtimu
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
#if ANDROID
            // LibVLC 原生库初始化：必须在进程内第一次 new LibVLC() 之前完成。
            // App、MainPage、VedioPlayer 都在本方法之后才构建，放这里最早最稳。
            // 注：net10 版 LibVLCSharp 只提供带目录参数的重载，null = 使用默认 native 库目录
            LibVLCSharp.Shared.Core.Initialize(null);
#elif WINDOWS
            // Windows：VLC 原生库由 VideoLAN.LibVLC.Windows 包复制到 输出目录\libvlc\win-{架构}\。
            // 实测（WinUI 版 LibVLCSharp.dll 字符串堆里只有 win-x86/win-arm64）自动搜索路径
            // 不含 x64 子目录，x64 进程必须显式指定，否则 new LibVLC() 抛 DllNotFoundException。
            var vlcArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X86 => "win-x86",
                System.Runtime.InteropServices.Architecture.X64 => "win-x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
                _ => null
            };
            if (vlcArch is not null)
            {
                var vlcDir = System.IO.Path.Combine(AppContext.BaseDirectory, "libvlc", vlcArch);
                if (System.IO.Directory.Exists(vlcDir))
                    LibVLCSharp.Shared.Core.Initialize(vlcDir);
                // 目录不存在（如换了部署方式）则跳过，交给 LibVLCSharp 自己的搜索逻辑兜底
            }
#endif
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseLibVLCSharp()  // 注册 VLC VideoView 的平台 handler（缺了视频不渲染）
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
