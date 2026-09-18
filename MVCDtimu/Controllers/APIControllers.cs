using Dtimu.Core;
using Dtimu.IndexSchemas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WebDtimu.Dependency;

namespace MVCDtimu.Controllers
{
    [Route("api/[Controller]")]
    [ApiController]
    public class Lists : ControllerBase
    {
        [HttpGet("MusicList")]
        public Dictionary<string, Music> MusicList([FromQuery] string[]? hashes = null)
        {
            var all = Program.DireInfo.Musics;

            // 不传 hashes 时，保持旧行为：返回全部
            if (hashes == null || hashes.Length == 0)
                return all;

            var result = new Dictionary<string, Music>(hashes.Length);
            foreach (var h in hashes)
            {
                if (string.IsNullOrEmpty(h)) continue;
                if (all.TryGetValue(h, out var m))
                    result[h] = m;
            }
            return result;
        }
        
        [HttpGet("VideoList")]
        public IEnumerable<Video> VideoList()
        {
            return Program.DireInfo.Videos.Values.ToArray();
        }

        [HttpGet("AlumbList")]
        public Dictionary<string, Album> AlumbList()
        {
            return Program.DireInfo.Albums;
        }
        [HttpGet("PicturesList")]
        public Dictionary<string, string> PicturesList()
        {
            return Program.DireInfo.Pictures;
        }
        [HttpGet("All")]
        public DireInfo AllList()
        {
            return Program.DireInfo;
        }

    }

    [Route("api/[Controller]")]
    [ApiController]
    public class VersionInfos: ControllerBase
    {
        [HttpGet("Version")]
        public IResult Version()
        {
            var jsonText = "" +
                "{" + $"\"Version\":\"{Program.Version}\" }}";
                var mimeType = "text/json";

            return Results.Text(jsonText, mimeType);

        }

    }


    [Route("api/[Controller]")]
    [ApiController]
    public class Files : ControllerBase
    {
        
        public IActionResult GetVideoFile(string hash)
        {
            if (Program.DireInfo.Videos.TryGetValue(hash, out var video))
            {
                var filePath = video.FilePath;
                var fileName = System.IO.Path.GetFileName(filePath);
                var mimeType = "application/octet-stream"; // You can set the appropriate MIME type based on your file type
                return PhysicalFile(filePath, mimeType, fileName);
            }
            return NotFound();
        }

        [HttpGet("Music/{hash}")]
        public IActionResult GetMusicFile(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 6)
                return NotFound();

            var dir = Path.Combine(Program.RootPath, hash[..2]);
            if (!Directory.Exists(dir)) return NotFound();

            string? filePath = null;
            foreach (var item in Directory.GetFiles(dir))
            {
                if (Path.GetFileName(item).Contains(hash[..6], StringComparison.OrdinalIgnoreCase))
                {
                    filePath = item;
                    break;
                }
            }
            if (filePath == null) return NotFound();

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mimeType = ext switch
            {
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".ogg" => "audio/ogg",
                ".oga" => "audio/ogg",
                ".opus" => "audio/ogg",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                ".webm" => "audio/webm",
                _ => "application/octet-stream"
            };

            return PhysicalFile(filePath, mimeType, enableRangeProcessing: true);
        }

        //http://localhost:26131/api/Files/Pictures/4dea6e42a3d72c42f198d2fced3380ec27613149821cd84f577cd2d8742c5097
        [HttpGet("Pictures/{hash}")]
        public IActionResult GetPicture(string hash)
        {
            // 1. 校验哈希参数是否为空
            if (string.IsNullOrEmpty(hash))
            {
                return BadRequest("图片哈希值不能为空");
            }

            try
            {
                // 2. 根据哈希获取纯base64字符串
                var kvp = Program.DireInfo.Pictures.FirstOrDefault(x => x.Key == hash);

                // 检查是否找到对应的图片
                if (kvp.Equals(default) || string.IsNullOrEmpty(kvp.Value))
                {
                    return NotFound("未找到对应的图片");
                }

                var fileB64 = kvp.Value;

                // 3. 将纯base64字符串解码为字节数组
                byte[] imageBytes;
                try
                {
                    imageBytes = Convert.FromBase64String(fileB64);
                }
                catch (FormatException)
                {
                    return BadRequest("Base64格式不正确，无法解码");
                }

                // 4. 定义图片MIME类型并返回图片文件流
                var mimeType = "image/jpeg";
                return File(imageBytes, mimeType);
            }
            catch (Exception ex)
            {
                // 记录异常日志（实际项目建议使用日志框架）
                Console.WriteLine($"获取图片失败：{ex.Message}");
                return StatusCode(500, "服务器内部错误，获取图片失败");
            }
        }
    }

    [Route("api/[controller]")]
    [ApiController]
    public class GeneratedTextCoversController : ControllerBase
    {
        /// <summary>
        /// 生成文字专辑封面
        ///
        /// 示例：
        /// /api/GeneratedTextCovers/TextCover?text=我的专辑名
        /// </summary>
        [HttpGet("TextCover")]
        public IActionResult GetGeneratedCover([FromQuery] string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return BadRequest("text不能为空");
            }

            text = text.Trim();

            // 用专辑名计算唯一 hash
            var hash =  TextCoverUtil.GetTextHash(text);

            // 保存目录
            var directory = Path.Combine(Program.RootPath, "TextCovers");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // PNG 文件
            var filePath = Path.Combine(directory, $"{hash}.png");

            // 已经生成过，直接返回
            // 这样同一个专辑不会每次请求都换背景
            if (!System.IO.File.Exists(filePath))
            {
                TextCoverUtil.GenerateCover(text, filePath);
            }

            return PhysicalFile(
                filePath,
                "image/png",
                $"{hash}.png"
            );
        }
    }
}
