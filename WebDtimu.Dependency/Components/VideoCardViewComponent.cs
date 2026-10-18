using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebDtimu.Dependency.Components
{
    public class VideoCardViewComponent : ViewComponent
    {
        // 根据你的需求定义参数，例如 AppId 和 Mode
        public IViewComponentResult Invoke(bool appId431960Mode = false,bool isBurned = true)
        {
            // 可以将参数存入 ViewData 或直接传递给 View 模型
            ViewData["AppId431960Mode"] = appId431960Mode;
            ViewData["IsBurned"] = isBurned;
            return View();
        }
    }
}
