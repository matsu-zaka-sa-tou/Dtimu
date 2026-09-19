using Microsoft.AspNetCore.Mvc;
using System.Security;
using System.Text;

namespace MVCDtimu.Controllers
{
    public class AlbumController : Controller
    {
        public IActionResult Detail(string name)
        {
            ViewBag.Name = name;
            return View();
        }
    }

    [ApiController]
    [Route("api/albums")]
    public class AlbumsApiController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get([FromQuery] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "需要名字" });

            var albums = Program.DireInfo?.Albums;
            if (albums == null || !albums.ContainsKey(name))
                return NotFound(new { error = "没找到专辑", name });

            // ContainsKey 命中后再取，OK() 会自动序列化为 JSON
            return Ok(albums[name]);
        }
    }

    [Route("Cover")]
    public class CoverController : Controller
    {
        // GET /Cover/{hash}
        // 从 Program.DireInfo.Pictures 取 base64 转二进制
        [HttpGet("{hash}")]
        [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any)]
        public IActionResult ByHash(string hash)
        {
            var pics = Program.DireInfo?.Pictures;
            if (pics == null || !pics.TryGetValue(hash, out var value) || string.IsNullOrWhiteSpace(value))
                return NotFound();

            // 兼容 "data:image/png;base64,xxx" 和纯 base64
            var comma = value.IndexOf(',');
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                value = value[(comma + 1)..];

            byte[] bytes;
            try { bytes = Convert.FromBase64String(value); }
            catch { return NotFound(); }

            var contentType = DetectContentType(bytes) ?? "image/png";
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return File(bytes, contentType);
        }

        // GET /Cover/Text?text=xxx&size=300
        // 无封面时生成 SVG 文字封面（跨平台，无需 System.Drawing）
        [HttpGet("Text")]
        [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
        public IActionResult Text(string text, int size = 300)
        {
            text ??= "?";
            size = Math.Clamp(size, 64, 1024);

            var hue = StableHue(text);
            var bg1 = $"hsl({hue},55%,45%)";
            var bg2 = $"hsl({(hue + 35) % 360},60%,28%)";

            // 估算字号：CJK 每字约 1em，拉丁约 0.55em
            double units = 0;
            foreach (var c in text) units += c > 0x2E80 ? 1.0 : 0.55;
            units = Math.Max(units, 1);
            var fontSize = Math.Clamp(size * 0.8 / units, 14, size * 0.4);

            var safe = SecurityElement.Escape(text);

            var sb = new StringBuilder(512);
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(size)
              .Append("\" height=\"").Append(size)
              .Append("\" viewBox=\"0 0 ").Append(size).Append(' ').Append(size).Append("\">");
            sb.Append("<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">")
              .Append("<stop offset=\"0%\" stop-color=\"").Append(bg1).Append("\"/>")
              .Append("<stop offset=\"100%\" stop-color=\"").Append(bg2).Append("\"/>")
              .Append("</linearGradient></defs>");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"url(#g)\"/>");
            sb.Append("<text x=\"50%\" y=\"50%\" text-anchor=\"middle\" dominant-baseline=\"central\" ")
              .Append("fill=\"#fff\" font-family=\"system-ui,-apple-system,'Segoe UI',Roboto,sans-serif\" ")
              .Append("font-weight=\"600\" font-size=\"").Append(fontSize.ToString("0.##")).Append("\">")
              .Append(safe)
              .Append("</text></svg>");

            return Content(sb.ToString(), "image/svg+xml; charset=utf-8");
        }

        private static int StableHue(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in s) { h ^= c; h *= 16777619; }
                return (int)(h % 360);
            }
        }

        private static string? DetectContentType(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
            if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
            return null;
        }
    }

}
