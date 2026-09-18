using Dtimu.IndexSchemas;
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
                RootPath = ic["root"];
            }
            try
            {
                var direc = args[args.ToList().IndexOf(args.Where(x => x == "--dir").First()) + 1];
                RootPath = direc;
            }
            catch (Exception ex)
            {

            }

            #region DtimuOp
            var ip = System.IO.Path.Combine(RootPath, "index.json");
            if (System.IO.File.Exists(ip))
            {
                var json = System.IO.File.ReadAllText(ip);
                DireInfo = JsonConvert.DeserializeObject<DireInfo>(json);
            }
            #endregion

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

            app.Run();
        }
    }
}
