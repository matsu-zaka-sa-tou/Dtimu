using Dtimu.IndexSchemas;
using MediaReader;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.AspNetCore.StaticFiles;
using Newtonsoft.Json;
using System.Reflection;

namespace MVCDtimu
{
    public class Program
    {

        public static string Version { get; set; }
        public static string RootPath { get; set; }
        /// <summary>
        /// 该目录是否为烧录后的目录，烧录后的目录不允许上传（修改）文件。
        /// </summary>
        public static bool IsBurned { get; set; }
        /// <summary>
        /// 启用或禁用针对创意工坊格式的特殊运行模式。
        /// </summary>
        /// <remarks>静态全局设置，会影响应用域内所有相关功能。默认为 false；在运行时修改会立即生效，但可能导致状态不一致，修改前应评估副作用。</remarks>
        public static bool AppId431960Mode { get; set; }
        public static FileExtensionContentTypeProvider Provider { get; internal set; } = new FileExtensionContentTypeProvider();
        public static DireInfo? DireInfo { get; 
            private set; }

        public static void Main(string[] args)
        {
            var server = new BroadcastServer();
            server.Start();

            var assembly = Assembly.GetExecutingAssembly();
            var fileVersionAttr = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
            Version = fileVersionAttr?.Version;
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllersWithViews();

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Configure(builder.Configuration.GetSection("Kestrel"));
            });
            RootPath = Environment.CurrentDirectory;
            if (builder.Configuration.GetSection("dir") is IConfigurationSection ic)
            {
                RootPath = ic["root"] ?? RootPath;
                IsBurned = bool.TryParse(ic["IsBurned"], out var burned) && burned;
            }
            try
            {
                var direc = args[args.ToList().IndexOf(args.Where(x => x == "--dir").First()) + 1];
                RootPath = direc;
            }
            catch (Exception ex)
            {

            }

            var app = builder.Build();
            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();

            }
            // 定义 Rewrite 规则
            var options = new RewriteOptions()
                .AddRewrite(@"^files(.*)", "Home/FileView?path=$1", skipRemainingRules: true);

            app.UseStatusCodePages(async context =>
            {
                var response = context.HttpContext.Response;

                if (response.StatusCode == 404 &&
                    context.HttpContext.Request.Headers["Accept"].ToString().Contains("text/html"))
                {
                    response.Redirect("/Home/Code404Page");
                }
            });
            app.UseRewriter(options);

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            #region DtimuOp
            var ip = System.IO.Path.Combine(RootPath, "index.json");
            if (System.IO.File.Exists(ip))
            {
                var json = System.IO.File.ReadAllText(ip);
                DireInfo = JsonConvert.DeserializeObject<DireInfo>(json);
            }
            else if (builder.Configuration.GetSection("dir") is IConfigurationSection ic2)
            {
                if (ic2.GetValue<bool>("AppId431960Mode") && (!ic2.GetValue<bool>("IsBurned")))
                {
                    AppId431960Mode = true;
                    app.Logger.LogInformation("构建索引中，启动会比较慢。");
                    DireInfo = AppId431960IndexUtil.BuildIndex(RootPath, true);
                }
            }
            #endregion

            app.Run();
        }
    }
}
