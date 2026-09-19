using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;
using System.Xml;
using TagLib;
using System.Drawing;
using Image = System.Drawing.Image;
using System.Drawing.Drawing2D;
using Newtonsoft.Json;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Reflection;
using Dtimu.Core;
using TagLib.Ape;
using System.Threading;
using Newtonsoft.Json.Linq;
using System.Net.NetworkInformation;
using System.Runtime.ConstrainedExecution;
using Microsoft.SqlServer.Server;
using ExifLib;

namespace Dtimu.IndexSchemas
{
    public class DireInfo
    {
        public Dictionary<string, Album> Albums { get; set; }
        public Dictionary<string, Music> Musics { get; set; }
        public Dictionary<string, Video> Videos { get; set; }
        public Dictionary<string, Dtimu.Core.Image> Images { get; set; }
        public Dictionary<string, Collection> Collections { get; set; }
        public Dictionary<string, string> Pictures { get; set; }
        public Dictionary<string, List<string>> Performers { get; set; }
        public DireInfo()
        {
            Albums = new Dictionary<string, Album>();
            Musics = new Dictionary<string, Music>();
            Videos = new Dictionary<string, Video>();
            Images = new Dictionary<string, Dtimu.Core.Image>();
            Collections = new Dictionary<string, Collection>();
            Pictures = new Dictionary<string, string>();
            Performers = new Dictionary<string, List<string>>();
        }
    }
    public class Album
    {
        public List<string> Pictures { get; set; }
        public List<string> AlbumPerformers { get; set; }
        public List<string> Musics { get; set; }
        public Album()
        {
            AlbumPerformers = new List<string>();
            Musics = new List<string>();
            Pictures = new List<string>();
        }
    }
    public class Music
    {
        public string FileName { get; set; }
        public string Album { get; set; }
        public List<string> Performers { get; set; }
        public string Title { get; set; }
        public string Pictrue { get; set; }

        public Music()
        {
            Performers = new List<string>();
        }
    }

    public static class DireInfoUtil {
        public static void GenPictrue(string dire, int heightRatio, int widthRatio)
        {
            // 获取当前目录下所有支持的音频文件
            string[] audioFiles = Directory.GetFiles(dire, "*.*", SearchOption.TopDirectoryOnly)
                                            .Where(f => f.EndsWith(".mp3") || f.EndsWith(".ogg") || f.EndsWith(".flac"))
                                            .ToArray();

            if (audioFiles.Length == 0)
            {
                Console.WriteLine("当前目录下没有找到支持的音频文件！");
                return;
            }

            const int ThumbnailSize = 100; // 缩略图的尺寸
            int totalFiles = audioFiles.Length;

            // 根据高宽比计算行数和列数
            double targetRatio = (double)heightRatio / widthRatio;
            int rows = (int)Math.Ceiling(Math.Sqrt(totalFiles * targetRatio));
            int cols = (int)Math.Ceiling((double)totalFiles / rows);

            if (rows * cols < totalFiles)
            {
                Console.WriteLine("指定比例计算的行列数不足以容纳所有文件，请调整比例！");
                return;
            }

            Console.WriteLine($"计算的网格尺寸: {rows} 行 x {cols} 列");

            // 创建目标大图
            int canvasWidth = cols * ThumbnailSize;
            int canvasHeight = rows * ThumbnailSize;
            var canvas = new Bitmap(canvasWidth, canvasHeight);

            try
            {
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.Clear(Color.White); // 背景为白色

                    Random random = new Random();
                    int index = 0;

                    foreach (var audioFile in audioFiles)
                    {
                        int x = (index % cols) * ThumbnailSize;
                        int y = (index / cols) * ThumbnailSize;

                        // 读取封面图片或生成色块
                        Bitmap thumbnail;
                        try
                        {
                            var tagFile = TagLib.File.Create(audioFile);
                            if (tagFile.Tag.Pictures.Length > 0)
                            {
                                var pictureData = tagFile.Tag.Pictures[0].Data.Data;
                                using (MemoryStream ms = new MemoryStream(pictureData))
                                using (Image image = Image.FromStream(ms))
                                {
                                    thumbnail = new Bitmap(image, new Size(ThumbnailSize, ThumbnailSize));
                                }
                            }
                            else
                            {
                                string albumTitle = tagFile.Tag.Album ?? "Untitled";
                                string initial = GetAlbumInitial(albumTitle);
                                thumbnail = GenerateRandomColorBlockWithChar(ThumbnailSize, random, initial);
                            }
                        }
                        catch
                        {
                            thumbnail = GenerateRandomColorBlockWithChar(ThumbnailSize, random, "?");
                        }

                        graphics.DrawImage(thumbnail, x, y);
                        thumbnail.Dispose();
                        index++;
                    }
                }

                string outputFileName = Path.Combine(dire, "output.jpg");
                canvas.Save(outputFileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                Console.WriteLine($"图片生成成功，保存为 {outputFileName}");
            }
            finally
            {
                canvas.Dispose(); // 确保释放 Bitmap 资源
            }
        }

        static string GetAlbumInitial(string albumTitle)
        {
            // 判断是否是英文单词
            if (albumTitle.ToUpperInvariant().StartsWith("I "))
            {
                var words = albumTitle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if(words.Length > 1)
                {
                    return words[0] +" "+ words[1];
                }
                else
                {
                    return "I";
                }
            }
            else if (albumTitle.All(c => Char.IsLetter(c) || c == ' ')) // 允许空格作为单词分隔
            {
                var words = albumTitle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return words.Length > 0 ? words[0] : albumTitle.Substring(0, 1); // 返回第一个单词
            }
            else // 汉字或日文
            {
                return albumTitle.Length <= 9 ? albumTitle : albumTitle.Substring(0, 9); // 最多取9个字符
            }
        }

        static Bitmap GenerateRandomColorBlockWithChar(int size, Random random, string character)
        {
            Bitmap bitmap = new Bitmap(size, size);
            Graphics graphics = Graphics.FromImage(bitmap);

            // 随机背景色
            Color randomColor = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256));
            graphics.Clear(randomColor);

            // 绘制字符
            Font font = new Font("Microsoft YaHei", 20, FontStyle.Regular, GraphicsUnit.Pixel);
            Brush textBrush = Brushes.White;
            StringFormat stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            graphics.DrawString(character, font, textBrush, new RectangleF(0, 0, size, size), stringFormat);
            graphics.Dispose();
            return bitmap;
        }

        public static void CopyTagFromSameNamedMp3(string dire)
        {
            List<string> oggFiles = Directory.GetFiles(dire, "*.ogg").ToList();
            oggFiles.AddRange(Directory.GetFiles(dire, "*.flac"));

            foreach (var oggFile in oggFiles)
            {
                string mp3File = Path.ChangeExtension(oggFile, ".mp3");
                if (oggFile.EndsWith("flac"))
                {
                    mp3File = Path.ChangeExtension(oggFile, ".flac");
                }
                else
                if (!System.IO.File.Exists(mp3File))
                {
                    Console.WriteLine($"MP3 文件不存在: {mp3File}. 请先用 FFmpeg 转码。");
                    continue;
                }

                // 读取 OGG 文件的元数据
                var oggTag = TagLib.File.Create(oggFile);

                // 打开 MP3 文件
                var mp3Tag = TagLib.File.Create(mp3File);

                // 复制常见元数据字段
                mp3Tag.Tag.Performers = oggTag.Tag.Performers;
                mp3Tag.Tag.Album = oggTag.Tag.Album;
                mp3Tag.Tag.Title = oggTag.Tag.Title;
                mp3Tag.Tag.Genres = oggTag.Tag.Genres;
                mp3Tag.Tag.Year = oggTag.Tag.Year;
                mp3Tag.Tag.Comment = oggTag.Tag.Comment;

                // 复制封面图片
                if (oggTag.Tag.Pictures.Length > 0)
                {
                    var oggPictures = oggTag.Tag.Pictures;
                    var mp3Pictures = new TagLib.IPicture[oggPictures.Length];

                    for (int i = 0; i < oggPictures.Length; i++)
                    {
                        mp3Pictures[i] = new Picture
                        {
                            Data = oggPictures[i].Data, // 图片数据
                            MimeType = oggPictures[i].MimeType, // MIME 类型（如 "image/jpeg"）
                            Description = oggPictures[i].Description,
                            Type = oggPictures[i].Type // 图片类型（如封面）
                        };
                    }

                    mp3Tag.Tag.Pictures = mp3Pictures;
                }

                // 保存更改
                mp3Tag.Save();
                Console.WriteLine($"成功写入元数据: {mp3File}");
            }
        }
        public static void FFmpegConvert(string inputDirectory, string ffmpegPath = "ffmpeg")
        {
            OggAndFlacChangeFromDiskByFFmpeg(inputDirectory, ffmpegPath);
            VideoChangeFromDiskByFFmpeg(inputDirectory, ffmpegPath);
        }

        private static void OggAndFlacChangeFromDiskByFFmpeg(string inputDirectory, string ffmpegPath)

        {
            // 检查输入目录是否存在
            if (!Directory.Exists(inputDirectory))
            {
                Console.WriteLine($"目录不存在: {inputDirectory}");
                return;
            }

            // 获取目录中的所有 .ogg 文件
            List<string> files = Directory.GetFiles(inputDirectory, "*.ogg").ToList(); 
            files.AddRange( Directory.GetFiles(inputDirectory, "*.flac"));
            if (files.Count == 0)
            {
                Console.WriteLine("没有找到文件。");
                return;
            }

            // 遍历每个 .ogg 文件并转换为 .mp3
            foreach (string oggFile in files)
            {
                if (oggFile.EndsWith(".flac"))
                {
                    string mp3File = Path.ChangeExtension(oggFile, ".wav");

                    // 构建 FFmpeg 命令行参数

                    string arguments = $"-hwaccel auto -i \"{oggFile}\" -c:a pcm_s16le \"{mp3File}\"";

                    Console.WriteLine($"正在转换: {oggFile} -> {mp3File}");

                    // 调用 FFmpeg 进行转换
                    ProcessStartInfo processInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,  // FFmpeg 可执行文件路径
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        process.WaitForExit();
                        if (process.ExitCode == 0)
                        {
                            Console.WriteLine($"转换成功: {mp3File}");
                        }
                        else
                        {
                            Console.WriteLine($"转换失败: {oggFile}");
                            Console.WriteLine(process.StandardError.ReadToEnd());
                        }
                    }

                }
                else
                {
                    string mp3File = Path.ChangeExtension(oggFile, ".mp3");

                    // 构建 FFmpeg 命令行参数
                    string arguments = $"-i \"{oggFile}\" -q:a 2 \"{mp3File}\"";

                    Console.WriteLine($"正在转换: {oggFile} -> {mp3File}");

                    // 调用 FFmpeg 进行转换
                    ProcessStartInfo processInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,  // FFmpeg 可执行文件路径
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        process.WaitForExit();
                        if (process.ExitCode == 0)
                        {
                            Console.WriteLine($"转换成功: {mp3File}");
                        }
                        else
                        {
                            Console.WriteLine($"转换失败: {oggFile}");
                            Console.WriteLine(process.StandardError.ReadToEnd());
                        }
                    }

                }
            }
        }
        private static void VideoChangeFromDiskByFFmpeg(string inputDirectory, string ffmpegPath)
        {

            // 检查输入目录是否存在
            if (!Directory.Exists(inputDirectory))
            {
                Console.WriteLine($"目录不存在: {inputDirectory}");
                return;
            }

            List<string> files = new List<string>();
            files.AddRange(Directory.GetFiles(inputDirectory, "*.mp4"));

            if (files.Count == 0)
            {
                Console.WriteLine("没有找到 文件。");
                return;
            }
            foreach (string file in files)
            {
            //    string tempfile1 = Path.ChangeExtension(file, ".mp4");
            //    tempfile1 = Path.Combine(Path.GetDirectoryName(tempfile1), Path.GetFileNameWithoutExtension(file) + ".tmp.mp4");

            //    // 构建 FFmpeg 命令行参数 -vf "scale=1920:1080" -c:v h264_nvenc -b:v 8000k -c:a aac -b:a 192k
            //    string arguments1 = $"-hwaccel cuvid -c:v h264_cuvid -i \"{file}\" -vf \"scale=1920:1080\" -c:v h264_nvenc -b:v 8000k -c:a aac -b:a 192k \"{tempfile1}\" -y";

            //    Console.WriteLine($"正在转换: {file} -> {tempfile1}");

            //    // 调用 FFmpeg 进行转换
            //    ProcessStartInfo processInfo = new ProcessStartInfo
            //    {
            //        FileName = ffmpegPath,  // FFmpeg 可执行文件路径
            //        Arguments = arguments1,
            //        RedirectStandardOutput = true,
            //        RedirectStandardError = true,
            //        UseShellExecute = false,
            //        CreateNoWindow = true
            //    };

            //    using (Process process = Process.Start(processInfo))
            //    {
            //        process.WaitForExit();
            //        if (process.ExitCode == 0)
            //        {
            //            Console.WriteLine($"转换成功: {tempfile1}");
            //        }
            //        else
            //        {
            //            Console.WriteLine($"转换失败: {file}");
            //            Console.WriteLine(process.StandardError.ReadToEnd());
            //        }
            //    }


                string tafl = Path.ChangeExtension(file, ".wmv");

                // 构建 FFmpeg 命令行参数
                string arguments2 = $"-i \"{file}\" -c:v wmv2 -b:v 4096k -c:a wmav2 -b:a 192k -strict experimental -vf \"scale=1920:1080\" \"{tafl}\"";

                Console.WriteLine($"正在转换: {file} -> {tafl}");

                // 调用 FFmpeg 进行转换
                ProcessStartInfo processInfo2 = new ProcessStartInfo
                {
                    FileName = ffmpegPath,  // FFmpeg 可执行文件路径
                    Arguments = arguments2,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(processInfo2))
                {
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"转换成功: {tafl}");
                    }
                    else
                    {
                        Console.WriteLine($"转换失败: {tafl}");
                        Console.WriteLine(process.StandardError.ReadToEnd());
                    }
                }
            }

        }

        public static void BuildIndex(string dire)
        {
            var outputImagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NoFrameOrFrameException.png");
            if (!System.IO.File.Exists(outputImagePath))
            {
                DrawSPic(dire); 
            }

            // 初始化DireInfo对象
            var sche = new DireInfo();
            var di = new DirectoryInfo(dire);
            // 一次性收集所有目标文件
            var files = new List<FileInfo>();
            foreach (var item in di.EnumerateDirectories())
            {

                var ran = item.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                              .Where(x => x.Extension == ".flac"
                              || x.Extension == ".ogg"
                              || x.Extension == ".mp3"
                              || x.Extension == ".mp4"
                              || x.Extension == ".avi"
                              || x.Extension == ".ts"
                              || x.Extension == ".bmp"
                              || x.Extension == ".jpg"
                              || x.Extension == ".png"
                              || x.Extension == ".tif"
                              || x.Extension == ".gif"
                              || x.Extension == ".bmp")
                              .ToList();
                files.AddRange(ran);
            }


            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                Console.WriteLine($"{file.Name} ({i + 1} of {files.Count})");

                // 计算文件哈希值，跳过已存在的文件
                var fileHash = CertUtil.Sha256(file.FullName);
                if (!sche.Musics.ContainsKey(fileHash))
                {
                    ParseOne(sche, file, fileHash);
                }
            }
            //System.IO.File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NoFrameOrFrameException.png"));
            // 序列化并保存结果
            var r = JsonConvert.SerializeObject(sche, Newtonsoft.Json.Formatting.Indented);
            var p = Path.Combine(dire, "index.json");
            System.IO.File.Create(p).Close();
            System.IO.File.WriteAllText(p, r);
        }

        private static void DrawSPic(string dire)
        {
            var outputImagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NoFrameOrFrameException.png");
            if (System.IO.File.Exists(outputImagePath)) { return; }
            using (Bitmap bitmap = new Bitmap(160, 90)) // 创建 160x90 的图片
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black); // 设置背景为黑色
                                      // 设置字体和文字格式
                using (Font font = new Font("Microsoft Yahei", 24, FontStyle.Bold)) // 粗体微软雅黑字体，大小 24
                using (Brush brush = Brushes.White) // 白色文字
                {
                    SizeF textSize = g.MeasureString("S", font); // 测量文字大小
                    float x = (bitmap.Width - textSize.Width) / 2; // 计算文字水平居中位置
                    float y = (bitmap.Height - textSize.Height) / 2; // 计算文字垂直居中位置

                    g.DrawString("S", font, brush, x, y); // 在图片中心绘制文字
                }
                bitmap.Save(outputImagePath, ImageFormat.Jpeg); // 保存为 JPEG 格式
            }
        }

        public static void GenSimpleDiscInfo(string dire,string title = "Steam 创意工坊下载")
        {
            var p = Path.Combine(dire, "index.json");
            var direi = JsonConvert.DeserializeObject<DireInfo>(System.IO.File.ReadAllText(p));

            var ti = title + "&sprt;" +
                $"本光盘包括 {direi.Videos.Count} 个视频";
            foreach (var item in direi.Videos.Values)
            {
                ti += "\n";
                ti +=item.Title;
            }
            GenInfo(dire,ti);
        }

        public static void GenInfo(string dire,string info)
        {
            var i = Path.Combine(dire, "DiskInfo.txt"); 
            System.IO.File.Create(i).Close();
            System.IO.File.WriteAllText(i, info);
        }

        // 解析单个文件并添加到schema中
        public static void ParseOne(DireInfo sche, FileInfo file, string fileHash)
        {
            if (file.Name.EndsWith(".mp3") 
                || file.Name.EndsWith(".ogg") 
                || file.Name.EndsWith(".flac"))
            {
                var ta = Dtimu.Core.TagAnalyser.OpenFile(file.FullName);
                var b64 = "";
                if (!(ta.Pictures.Length < 1))
                {
                    b64 = ResizePictureToBase64(ta.Pictures.First().Data.ToArray(), 200, 200);
                }
                var hashOfPic = CertUtil.Sha256Fromb64(b64);
                if (!sche.Pictures.ContainsKey(hashOfPic))
                {
                    sche.Pictures.Add(hashOfPic, b64);
                }
                // 初始化音乐对象
                var music = new Music
                {
                    Album = ta.Album,
                    FileName = Path.Combine(Path.GetFileName(Path.GetDirectoryName(file.FullName)), file.Name),
                    Title = !ta.Title.Contains("unknown") ? ta.Title : file.Name,
                    Pictrue = hashOfPic
                };

                var performers = ta.Performers
                                   .Select(perf =>
                                   {
                                       var match = Regex.Match(perf, @"[（(]([^）)]*)[）)]");
                                       return match.Success ? match.Groups[1].Value : perf;
                                   })
                                   .ToList();

                music.Performers.AddRange(performers);
                sche.Musics[fileHash] = music;

                if (!sche.Albums.ContainsKey(ta.Album))
                {
                    sche.Albums[ta.Album] = new Album();
                }

                foreach (var performer in performers)
                {
                    if (!sche.Performers.ContainsKey(performer))
                    {
                        sche.Performers[performer] = new List<string>();
                    }
                    sche.Performers[performer].Add(fileHash);
                    if (!sche.Albums[ta.Album].AlbumPerformers.Contains(performer))
                    {
                        sche.Albums[ta.Album].AlbumPerformers.Add(performer);
                    }
                }
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    if (!sche.Albums[ta.Album].Pictures.Contains(hashOfPic))
                    {
                        sche.Albums[ta.Album].Pictures.Add(hashOfPic);
                    }
                }
                if (!sche.Albums[ta.Album].Musics.Contains(fileHash))
                {
                    sche.Albums[ta.Album].Musics.Add(fileHash);
                }
            }
            else if (file.Name.EndsWith(".wmv")
                || file.Name.EndsWith(".mp4")
                || file.Name.EndsWith(".ts"))
            {
                TagLib.File ta = null;
                try
                {
                    ta = TagLib.File.Create(file.FullName);
                }
                catch (CorruptFileException ex)
                {
                    ta = new FileInstance(file.FullName);
                }
                var vedio = new Dtimu.Core.Video()
                {
                    FilePath = Path.Combine(Path.GetFileName(Path.GetDirectoryName(file.FullName)), file.Name),
                };
                if(ta == null)
                {

                }
                vedio.Duration = ta.Properties.Duration;
                vedio.Title = ta.Tag.Title;
                vedio.Author = ta.Tag.Performers?.FirstOrDefault() ?? "未知作者";
                vedio.Description = ta.Tag.Description;
                vedio.FilePath = Path.Combine(Path.GetFileName(Path.GetDirectoryName(file.FullName)), file.Name);

                GenGallery(file.FullName);

                string galP = Path.Combine(Path.GetDirectoryName(file.FullName), 
                    Path.GetFileNameWithoutExtension(file.FullName) + "_frames"); // 截图保存目录
                var di = new DirectoryInfo(galP);
                foreach (var item in di.GetFiles())
                {
                    using (var fs = item.OpenRead())
                    {
                        byte[] bytes = new byte[fs.Length];
                        fs.Read(bytes, 0, bytes.Length);
                        vedio.Gallery.Add(ResizePictureToBase64(bytes, 160, 90));
                    }
                }
                di.Delete(true);
                sche.Videos[fileHash] = vedio;
            }
            else if (file.Name.EndsWith(".bmp")
                || file.Name.EndsWith(".jpg")
                || file.Name.EndsWith(".png") 
                || file.Name.EndsWith(".tif") 
                || file.Name.EndsWith(".gif"))
            {
                var b64 = ResizePictureToBase64In100Height(file.FullName);
                var hashOfPic = CertUtil.Sha256Fromb64(b64);
                if (!sche.Pictures.ContainsKey(hashOfPic))
                {
                    sche.Pictures.Add(hashOfPic, b64);
                }
                var image = new Dtimu.Core.Image();
                image.SmallPic = hashOfPic;
                try
                {
                    using (ExifReader reader = new ExifReader(file.FullName))
                    {
                        if (reader.GetTagValue(0x320, out string title))
                        {
                            image.Title = title ?? "Unknown";
                        }
                        else
                        {
                            image.Title = Path.GetFileNameWithoutExtension(file.FullName);
                        }
                        if (reader.GetTagValue(ExifTags.Make, out string cameraMake))
                        {
                            image.CameraMake = cameraMake ?? "Unknown";
                        }
                        if (reader.GetTagValue(ExifTags.Model, out string cameraModel))
                        {
                            image.CameraModel = cameraModel ?? "Unknown";
                        }
                        if (reader.GetTagValue(ExifTags.DateTimeOriginal, out DateTime dateTaken))
                        {
                            image.ShotTime = dateTaken;
                        }
                        #region GPS
                        // 读取纬度
                        var latitudeRef = "N";
                        bool hasLatitude = false;
                        if (reader.GetTagValue(ExifTags.GPSLatitude, out double[] latitudeComponents) &&
                                           reader.GetTagValue(ExifTags.GPSLatitudeRef, out string la))
                        {
                            hasLatitude = true;
                            latitudeRef = la;
                        }

                        // 读取经度
                        bool hasLongitude = false;
                        var longitudeRef = "N";
                        if( reader.GetTagValue(ExifTags.GPSLongitude, out double[] longitudeComponents) &&
                                            reader.GetTagValue(ExifTags.GPSLongitudeRef, out string lo))
                        {
                            hasLongitude = true;
                            longitudeRef = lo;
                        }

                        // 读取海拔
                        bool hasAltitude = reader.GetTagValue(ExifTags.GPSAltitude, out double altitude);

                        if (hasLatitude && hasLongitude)
                        {
                            double latitude = ConvertToDecimalDegrees(latitudeComponents, latitudeRef == "S");
                            double longitude = ConvertToDecimalDegrees(longitudeComponents, longitudeRef == "W");

                            // 格式化为向量形式
                            string gpsInfo = hasAltitude
                                ? $"({longitude}, {latitude}, {altitude})"
                                : $"({longitude}, {latitude})";

                            image.Location = gpsInfo;
                        }
                        else
                        {
                            Console.WriteLine("该图片没有GPS信息。");
                        }
                        #endregion
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"读取EXIF数据时出错: {ex.Message}");
                }
                if (sche.Images.ContainsKey(fileHash))
                {
                    return;
                }
                image.FilePath = Path.Combine(Path.GetFileName(Path.GetDirectoryName(file.FullName)), file.Name);
                sche.Images.Add(fileHash, image);
            }
        }

        private static double ConvertToDecimalDegrees(double[] components, bool isNegative)
        {
            // 将度、分、秒转换为十进制度
            double degrees = components[0];
            double minutes = components[1] / 60;
            double seconds = components[2] / 3600;
            double decimalDegrees = degrees + minutes + seconds;

            // 如果是南纬或西经，需要取负值
            return isNegative ? -decimalDegrees : decimalDegrees;
        }

        private static ImageFormat GetImageFormat(Image image)
        {
            if (image.RawFormat.Equals(ImageFormat.Jpeg))
                return ImageFormat.Jpeg;
            if (image.RawFormat.Equals(ImageFormat.Png))
                return ImageFormat.Png;
            if (image.RawFormat.Equals(ImageFormat.Gif))
                return ImageFormat.Gif;
            if (image.RawFormat.Equals(ImageFormat.Bmp))
                return ImageFormat.Bmp;
            if (image.RawFormat.Equals(ImageFormat.Tiff))
                return ImageFormat.Tiff;

            // 默认使用 PNG 格式
            return ImageFormat.Png;
        }
        private static string ResizePictureToBase64In100Height(string fullName)
        {
            try
            {
                // 打开本地文件并加载为 Bitmap
                using (Bitmap originalImage = new Bitmap(fullName))
                {
                    int originalWidth = originalImage.Width;
                    int originalHeight = originalImage.Height;

                    // 计算宽度，等比缩放
                    int targetWidth = originalWidth * 100 / originalHeight;

                    using (Bitmap resizedImage = new Bitmap(targetWidth, 100))
                    using (Graphics g = Graphics.FromImage(resizedImage))
                    {
                        // 设置高质量插值模式
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                        // 绘制缩放后的图片
                        g.DrawImage(originalImage, 0, 0, targetWidth, 100);

                        // 将图片转换为 Base64 字符串
                        string base64String = "";
                        using (MemoryStream ms = new MemoryStream())
                        {
                            resizedImage.Save(ms, GetImageFormat(resizedImage));
                            base64String = Convert.ToBase64String(ms.ToArray());
                        }
                        return base64String;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"图片加载失败：{ex.Message}");
                return null;
            }
        }

        private static void GenGallery(string inputVideo)
        {
            string ffmpegPath = @"ffmpeg"; // FFmpeg 可执行文件路径
            string outputDirectory = Path.Combine(Path.GetDirectoryName(inputVideo), Path.GetFileNameWithoutExtension(inputVideo) + "_frames"); // 截图保存目录
            int frameCount = 10; // 截取的帧数
            int width = 160; // 截图宽度
            int height = 90; // 截图高度

            // 创建输出目录
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // 执行 FFmpeg 指令获取关键帧
            string keyFrameDir = Path.Combine(outputDirectory, "keyframes");
            if (!Directory.Exists(keyFrameDir))
            {
                Directory.CreateDirectory(keyFrameDir);
            }

            string keyFrameArguments = $"-hwaccel auto -skip_frame nokey -i \"{inputVideo}\" " +
                                       $"-vf \"select='eq(pict_type,PICT_TYPE_I)',scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" " +
                                       $"-vsync vfr \"{keyFrameDir}/frame_%03d.jpg\"";

            // 启动任务并设置 45 秒超时
            using (var cts = new CancellationTokenSource())
            {
                var ffmpegTask = Task.Factory.StartNew(() =>
                {
                    RunFFmpeg(ffmpegPath, keyFrameArguments);
                }, cts.Token);

                // 设置 45 秒超时
                if (!ffmpegTask.Wait(45000))
                {
                    cts.Cancel(); // 取消任务
                    KillFFmpegProcess(); // 杀掉 FFmpeg 进程
                    Console.WriteLine("操作超时，已终止 FFmpeg 运行！");
                }
            }

            // 获取生成的关键帧图片
            var keyFrames = Directory.GetFiles(keyFrameDir, "frame_*.jpg");
            int keyFrameCount = keyFrames.Length;
            if (keyFrameCount == 0)
            {
                // 没有关键帧时，生成黑屏图片
                for (int i = 0; i < 10; i++)
                {
                    GC.Collect();
                    string outputImagePath = Path.Combine(outputDirectory, $"frame_{i}.jpg");
                    if (i == 0)
                    {
                        var s = Directory.GetParent(Directory.GetParent(inputVideo).FullName); 
                        var inputImagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NoFrameOrFrameException.png");

                        System.IO.File.Copy(inputImagePath, outputImagePath);
                    }
                    else
                    {
                        string o0 = Path.Combine(outputDirectory, $"frame_0.jpg");
                        System.IO.File.Copy(o0, outputImagePath, true);
                    }

                }

                Console.WriteLine($"没有关键帧时，生成黑屏图片");
            }
            // 确保输出帧数为 10 张
            for (int i = 0; i < frameCount; i++)
            {
                int index;

                if (keyFrameCount >= frameCount)
                {
                    // 如果关键帧数量 >= 10，平均分配
                    index = (int)((keyFrameCount - 1) * ((double)i / (frameCount - 1)));
                }
                else if (keyFrameCount == 0)
                {
                    continue;
                }
                else
                {
                    // 如果关键帧数量 < 10，循环重复已有的关键帧
                    index = i % keyFrameCount;
                }

                string sourceFile = keyFrames[index]; // 选择对应的帧
                string outputImagePath = Path.Combine(outputDirectory, $"frame_{i}.jpg");
                System.IO.File.Copy(sourceFile, outputImagePath, overwrite: true);

                Console.WriteLine($"帧 {i + 1} 已保存到: {outputImagePath}");
            }


            Console.WriteLine("截图完成！");
        }

        private static void KillFFmpegProcess()
        {
            // 查找并终止 FFmpeg 进程
            var processes = System.Diagnostics.Process.GetProcessesByName("ffmpeg");
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit();
                }
                catch
                {
                    // 忽略异常
                }
            }
        }
        private static void RunFFmpeg(string ffmpegPath, string arguments)
        {
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();
            Thread.Sleep(1000);
        }

        private static double GetVideoDuration(string ffmpegPath, string inputVideo)
        {
            string arguments = $"-hwaccel auto -i \"{inputVideo}\"";
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            string output = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // 提取视频时长信息
            var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d+):(\d+):(\d+\.\d+)");
            if (match.Success)
            {
                int hours = int.Parse(match.Groups[1].Value);
                int minutes = int.Parse(match.Groups[2].Value);
                double seconds = double.Parse(match.Groups[3].Value);

                return hours * 3600 + minutes * 60 + seconds;
            }

            return 0;
        }

        public static string ResizePictureToBase64(byte[] imageBytes, int width, int height)
        {
            //ByteVector pictureData
            // 将 ByteVector 转换为字节数组

            // 将字节数组加载为 Image 对象
            using (var ms = new MemoryStream(imageBytes))
            using (var originalImage = Image.FromStream(ms))
            {
                // 创建目标大小的空白图像
                using (var resizedImage = new Bitmap(width, height))
                {
                    // 设置高质量插值模式
                    using (var graphics = Graphics.FromImage(resizedImage))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;

                        // 绘制缩放后的图像
                        graphics.DrawImage(originalImage, 0, 0, width, height);
                    }

                    // 将图像保存到内存流中
                    using (var resultStream = new MemoryStream())
                    {
                        resizedImage.Save(resultStream, ImageFormat.Jpeg); // 可以选择其他格式

                        // 获取图像字节数组
                        byte[] resizedImageBytes = resultStream.ToArray();

                        // 转换为Base64字符串（无前缀头）
                        return Convert.ToBase64String(resizedImageBytes);
                    }
                }
            }
        }

        public static void ReNameToSha256(string dire)
        {
            //System.IO.File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NoFrameOrFrameException.png"));
            var f = new DirectoryInfo(dire).GetFiles();
            foreach (var item in f)
            {
                var des = item.FullName;
                des = des.Replace(item.Name, CertUtil.Sha256(des).Substring(0, 6)) + Path.GetExtension(des);
                des = des.Replace("..", ".");
                if ((item.FullName.ToLower().EndsWith(".flac") 
                    || item.FullName.ToLower().EndsWith(".wav") 
                    || item.FullName.ToLower().EndsWith(".ogg")
                    || item.FullName.ToLower().EndsWith(".avi") 
                    || item.FullName.ToLower().EndsWith(".mp4")
                    || item.FullName.ToLower().EndsWith(".ts") 
                    || item.FullName.ToLower().EndsWith(".bmp")
                    || item.FullName.ToLower().EndsWith(".jpg")
                    || item.FullName.ToLower().EndsWith(".png")
                    || item.FullName.ToLower().EndsWith(".tif")
                    || item.FullName.ToLower().EndsWith(".gif")
                    || item.FullName.ToLower().EndsWith(".bmp")
                    ) == false)
                {
                    continue;
                }
                des = Path.Combine(Path.GetDirectoryName(des), Path.GetFileName(des).Substring(0, 2), Path.GetFileName(des));
                Directory.CreateDirectory(Path.GetDirectoryName(des));
                item.MoveTo(des);

            }
        }

        public static void ReTagFromFileName(string dire)
        {
            var f = new DirectoryInfo(dire).GetFiles();
            foreach (var item in f)
            {
                var des = item.FullName;
                des = des.Replace(item.Name, CertUtil.Sha256(des).Substring(0, 6)) + Path.GetExtension(des);
                des = des.Replace("..", ".");
                if ((item.FullName.ToLower().EndsWith(".flac")
                    || item.FullName.ToLower().EndsWith(".wav")
                    || item.FullName.ToLower().EndsWith(".ogg")
                    || item.FullName.ToLower().EndsWith(".avi")
                    || item.FullName.ToLower().EndsWith(".mp4")
                    || item.FullName.ToLower().EndsWith(".ts")
                    || item.FullName.ToLower().EndsWith(".bmp")
                    || item.FullName.ToLower().EndsWith(".jpg")
                    || item.FullName.ToLower().EndsWith(".png")
                    || item.FullName.ToLower().EndsWith(".tif")
                    || item.FullName.ToLower().EndsWith(".gif")
                    || item.FullName.ToLower().EndsWith(".bmp")
                    ) == false)
                {
                    continue;
                }

                try
                {
                    if (((item.FullName.ToLower().EndsWith(".flac")
                    || item.FullName.ToLower().EndsWith(".wav")
                    || item.FullName.ToLower().EndsWith(".ogg")
                    || item.FullName.ToLower().EndsWith(".avi")
                    || item.FullName.ToLower().EndsWith(".mp4")
                    || item.FullName.ToLower().EndsWith(".ts"))))
                    {
                        var file = TagLib.File.Create(item.FullName);
                        var nt = Path.GetFileNameWithoutExtension(item.FullName);
                        file.Tag.Title = file.Tag.Title ?? nt;
                        try
                        {
                            file.Save();
                            Console.WriteLine($"标题已修改为：{nt}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("保存标题失败，可能是因为标题已存在，或者文件有版权保护。");
                        }
                    }
                    else
                    {
                        try
                        {
                            var imagePath = item.FullName;

                            using (MemoryStream ms = new MemoryStream(System.IO.File.ReadAllBytes(imagePath)))
                            {
                                using (Image image = Image.FromStream(ms))
                                {
                                    // 读取图片中的属性项（Exif 数据）
                                    var propItems = image.PropertyItems;

                                    var titleProp = Array.Find(propItems, p => p.Id == 0x0320); // 0x0320 是 Exif 的标题标签
                                    string currentTitle = null;

                                    if (titleProp != null && titleProp.Value != null && titleProp.Value.Length > 0)
                                    {
                                        // 提取标题（去掉末尾的 '\0'）
                                        currentTitle = System.Text.Encoding.UTF8.GetString(titleProp.Value).TrimEnd('\0');
                                        Console.WriteLine($"当前标题: {currentTitle}");
                                    }

                                    if (string.IsNullOrEmpty(currentTitle))
                                    {
                                        // 如果没有标题，将标题设置为文件名（不含扩展名）
                                        string fileName = Path.GetFileNameWithoutExtension(imagePath);
                                        Console.WriteLine($"标题不存在，设置为文件名: {fileName}");

                                        // 创建或更新标题属性
                                        if (titleProp != null)
                                        {
                                            // 修改现有属性
                                            titleProp.Value = System.Text.Encoding.UTF8.GetBytes(fileName + "\0");
                                            titleProp.Len = titleProp.Value.Length;
                                        }
                                        else
                                        {
                                            // 创建新属性
                                            var newProp = image.PropertyItems[0]; // 克隆一个现有的 PropertyItem
                                            newProp.Id = 0x0320;
                                            newProp.Type = 2; // ASCII 字符串
                                            newProp.Value = System.Text.Encoding.UTF8.GetBytes(fileName + "\0");
                                            newProp.Len = newProp.Value.Length;
                                            titleProp = newProp;
                                        }

                                        // 将属性写回图片
                                        image.SetPropertyItem(titleProp);

                                        // 保存到原文件
                                        image.Save(imagePath, ImageFormat.Jpeg);
                                        Console.WriteLine($"图片标题已修改并覆盖到原文件: {imagePath}");
                                    }
                                    else
                                    {
                                        Console.WriteLine("标题已存在，未进行修改。");
                                    }
                                }

                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"发生错误: {ex.Message}");
                        }
                    }
                }
                catch (CorruptFileException ex)
                {
                    continue;
                }
            }
        }

        public static void GroupFiles(string dire, long maxMBytes=8000L)
        {
            // 初始化文件名和大小的字典
            var dic = new Dictionary<string, long>();
            foreach (var item in new DirectoryInfo(dire).EnumerateFiles())
            {
                dic.Add(item.FullName, item.Length);
            }

            long maxSize = maxMBytes * 1024 * 1024; // 组大小（以字节为单位）

            // 按文件大小从大到小排序
            var files = dic.OrderByDescending(x => x.Value).ToList();
            var groups = new List<Dictionary<string, long>>();

            foreach (var file in files)
            {
                bool placed = false;

                // 尝试将文件放入已有的组中
                foreach (var group in groups)
                {
                    if (group.Values.Sum() + file.Value <= maxSize)
                    {
                        group.Add(file.Key, file.Value);
                        placed = true;
                        break;
                    }
                }

                // 如果无法放入任何已有组，则新建一个组
                if (!placed)
                {
                    var newGroup = new Dictionary<string, long> { { file.Key, file.Value } };
                    groups.Add(newGroup);
                }
            }

            for (int i = 0; i < groups.Count; i++)
            {
                Dictionary<string, long> item = groups[i];
                var nd = Path.Combine(dire, i.ToString());
                Directory.CreateDirectory(nd);
                foreach (var kvp in item)
                {
                    if ((!(kvp.Key.EndsWith("inf"))) && (!(kvp.Key.EndsWith("exe"))))
                    {
                        System.IO.File.Move(kvp.Key, Path.Combine(nd, System.IO.Path.GetFileName(kvp.Key)));
                    }
                }
            }
        }
        //public static void OrderPicturesByTime(string dire)
        //{
        //    var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NoFrameOrFrameException.png");
        //    if (System.IO.File.Exists(outputDir))
        //    {
        //        System.IO.File.Delete(outputDir);
        //    }

        //    var files = new DirectoryInfo(dire).GetFiles();

        //    foreach (var file in files)
        //    {
        //        if (!file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
        //            !file.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
        //            !file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
        //            !file.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
        //            !file.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) &&
        //            !file.Extension.Equals(".tif", StringComparison.OrdinalIgnoreCase))
        //        {
        //            continue;
        //        }
        //        var name = "0000-00-00";
        //        try
        //        {
        //            using (var reader = new ExifReader(file.FullName))
        //            {
        //                if (reader.GetTagValue(ExifTags.DateTimeOriginal, out DateTime dateTaken))
        //                {
        //                    name = dateTaken.ToString("yyyy-MM-dd");
        //                }
        //            }
        //        }
        //        catch
        //        {
        //            // Ignore files that fail to read EXIF data
        //        }

        //        var destinationDirectory = Path.Combine(file.DirectoryName, "Pictures");
        //        Directory.CreateDirectory(destinationDirectory);
        //        var ext = file.Extension;
        //        var inde = 0;
        //        var newFileName = Path.Combine(destinationDirectory, name +$"_{inde}{ext}");

        //        while (System.IO.File.Exists(newFileName))
        //        {
        //            inde++;
        //            newFileName = Path.Combine(destinationDirectory, name + $"_{inde}{ext}");
        //        }
        //        file.MoveTo(newFileName);
        //    }
        //}
    }

}

