using Microsoft.AspNetCore.Mvc;

namespace MVCDtimu.Controllers
{
    public class VideoController : Controller
    {
        [HttpGet]
        public IActionResult Detail(string? id)
        {
            ViewData["Title"] = "视频播放";
            ViewData["VideoId"] = id ?? string.Empty;

            // 与 VideoCard 组件保持一致：
            // true  = 封面优先用 gallery 里的 Base64
            // false = 封面优先走 /api/AppId431960Mode?id=xxx
            // 注意：前端现在两路都做兜底，这里只是决定"谁优先"。
            ViewData["IsBurned"] = true;

            return View();
        }
    }
}