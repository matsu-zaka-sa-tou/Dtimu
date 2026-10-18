using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.StaticFiles;
using System;
using System.IO;

namespace MVCDtimu.Controllers
{
    [Route("api/[Controller]")]
    [ApiController]
    public class AppId431960ModeController : Controller
    {
        [HttpGet]
        public IActionResult Preview(string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                    return BadRequest("id is required");

                var rootP = Program.RootPath;
                if (Program.IsBurned)
                {
                    rootP = Path.Combine(rootP, "431960");
                }

                // 假设前端传过来的 id 是类似 "xxx.json" 或相对路径
                var targetJsonP = Path.Combine(rootP, id, "project.json");
                if (!System.IO.File.Exists(targetJsonP))
                    return NotFound($"配置文件不存在: {targetJsonP}");

                var jRawText = System.IO.File.ReadAllText(targetJsonP);
                var jO = JObject.Parse(jRawText);
                var previewName = (string)jO["preview"];

                if (string.IsNullOrWhiteSpace(previewName))
                    return NotFound("JSON中没有找到 preview 字段");

                // 【重要修正】：通常 preview 图片和 json 在同一个目录下，直接取 json 所在的目录
                var jsonDir = Path.GetDirectoryName(targetJsonP);
                var fileP = Path.Combine(jsonDir, previewName);

                if (!System.IO.File.Exists(fileP))
                    return NotFound($"预览图不存在: {fileP}");

                // 自动判断图片格式（png, jpg 等）
                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(fileP, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                // 返回物理文件流
                return PhysicalFile(fileP, contentType);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "服务器内部错误：" + ex.Message);
            }
        }
    }
}