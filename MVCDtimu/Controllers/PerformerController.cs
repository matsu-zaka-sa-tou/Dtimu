using Microsoft.AspNetCore.Mvc;

namespace MVCDtimu.Controllers
{
    public class PerformancerController : Controller
    {
        // 支持 /Performancer/Detail?name=xxx
        public IActionResult Detail([FromQuery] string name)
        {
            ViewBag.Name = name;
            return View();
        }
    }

    [ApiController]
    [Route("api/performancers")]
    public class PerformancerApiController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get([FromQuery] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "需要名字" });

            // 这个 Performers 是 DireInfo 上的属性名（不要改，改了会编译不过）
            var performancer = Program.DireInfo?.Performers;

            /// <summary>
            /// 前一个 string 是表演者的名字，后一个 List<string> 是该表演者参与的音乐文件的哈希值列表
            /// </summary>
            //public Dictionary<string, List<string>> Performers { get; set; }

            if (performancer == null || !performancer.ContainsKey(name))
                return NotFound(new { error = "未找到该歌手", name });

            // 返回的是音乐的 hash 列表交给前端。
            return Ok(performancer[name]);
        }
    }
}