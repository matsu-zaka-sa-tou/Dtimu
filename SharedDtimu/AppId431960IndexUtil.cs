using Dtimu.IndexSchemas;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MediaReader
{
    /// <summary>
    /// WallPaper Engine 格式读取器，最后还是通过<see cref="LoadVedios"/>转换到 <see cref="DireInfo"/>
    /// </summary>
    public class AppId431960IndexUtil
    {
        private static TimeSpan GetVideoDurationSafe(string path)
        {
            try
            {
                return TagLib.File.Create(path).Properties.Duration;
            }
            catch (TagLib.CorruptFileException)
            {
                // 文件损坏/封装不识别：走 ffmpeg 备用解析，失败返回 0
                try { return TimeSpan.FromSeconds(DireInfoUtil.GetVideoDuration("ffmpeg", path)); }
                catch { return TimeSpan.Zero; }
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
        public static DireInfo BuildIndex(string rootPath,bool onlineBuild = false)
        {
            var res = new DireInfo();
            var directory = new System.IO.DirectoryInfo(rootPath);
            res.Videos = new();
            foreach (var subDirectory in directory.EnumerateDirectories())
            {
                var rawTextp = Path.Combine(rootPath,subDirectory.Name, "project.json");
                if (!File.Exists(rawTextp))
                {
                    continue;
                }
                var rawText = File.ReadAllText(rawTextp);
                var jo = JObject.Parse(rawText);
                string type = (string)jo["type"];
                if (type!="video")
                {
                    continue;
                }
                string desp = "";
                //if (!onlineBuild)  desp = (string)jo["description"];
                //    else desp = rawTextp;
                desp = (string)jo["description"];
                var vInstance = new Dtimu.Core.Video
                {
                    Description = desp,
                    Title = (string)jo["title"],
                    Tags = jo["tags"]?.ToObject<string[]>()
                               ?? Enumerable.Empty<string>(),
                    Author = "Steam Workshop",
                    Duration = GetVideoDurationSafe(Path.Combine(rootPath, subDirectory.Name, (string)jo["file"]))
                };
                if (onlineBuild)
                {
                    vInstance.FilePath = Path.Combine(subDirectory.Name, (string)jo["file"]);
                }
                else
                {
                    vInstance.Gallery = [Convert.ToBase64String(
                        File.ReadAllBytes(
                            Path.Combine(rootPath, subDirectory.Name,
                            (string)jo["preview"])))];
                    vInstance.FilePath = Path.Combine("431960", subDirectory.Name, (string)jo["file"]);
                }
                res.Videos.Add(subDirectory.Name,vInstance);
            }
            return res;
        }
    }
}
