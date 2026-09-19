using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebDtimu.Dependency
{
    #region Utils
    public class TextCoverUtil
    {
        #region Fileds
        // 封面尺寸
        private const int CoverWidth = 420;
        private const int CoverHeight = 420;

        // 左下角文字边距
        private const float LeftMargin = 22f;
        private const float BottomMargin = 18f;

        // 左下角完整专辑名最大占用宽度
        // 故意只占左侧，避免和右下角超大首字重叠太严重
        private const float AlbumTextWidth = 220f;

        // 最多允许专辑名占用的高度
        private const float AlbumTextMaxHeight = 165f;
        #endregion

        #region Methods
        /// <summary>
        /// 左下角绘制完整专辑名
        /// 支持自动换行和字体缩放
        /// </summary>
        private static void DrawAlbumName(
            Graphics graphics,
            string text
        )
        {
            var fontFamily = GetCoverFontFamily();

            /*
             * 默认字号。
             *
             * 比右侧大字明显小很多。
             */
            var fontSize = 25f;

            /*
             * 如果名字实在很长，
             * 最低缩小到 15px。
             */
            const float minFontSize = 15f;

            using var brush = new SolidBrush(Color.White);

            using var format = new StringFormat();

            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Near;

            // 自动换行
            format.FormatFlags = 0;

            // 实在显示不下时，最后使用省略号
            format.Trimming = StringTrimming.EllipsisCharacter;

            Font? finalFont = null;
            SizeF finalSize = SizeF.Empty;

            /*
             * --------------------------------------------------
             * 自动判断专辑名高度
             * --------------------------------------------------
             *
             * 太长：
             *
             * 这是一张非常非常
             * 长的专辑名称
             *
             * 自动换行。
             *
             * 如果换行以后还是太高，
             * 就逐步缩小字体。
             */

            while (fontSize >= minFontSize)
            {
                var testFont = new Font(
                    fontFamily,
                    fontSize,
                    FontStyle.Regular,
                    GraphicsUnit.Pixel
                );

                var measured = graphics.MeasureString(
                    text,
                    testFont,
                    new SizeF(
                        AlbumTextWidth,
                        AlbumTextMaxHeight
                    ),
                    format
                );

                if (measured.Height <= AlbumTextMaxHeight)
                {
                    finalFont = testFont;
                    finalSize = measured;

                    break;
                }

                testFont.Dispose();

                fontSize -= 1f;
            }

            /*
             * 极端超长情况
             */
            if (finalFont == null)
            {
                finalFont = new Font(
                    fontFamily,
                    minFontSize,
                    FontStyle.Regular,
                    GraphicsUnit.Pixel
                );

                finalSize = graphics.MeasureString(
                    text,
                    finalFont,
                    new SizeF(
                        AlbumTextWidth,
                        AlbumTextMaxHeight
                    ),
                    format
                );
            }

            /*
             * --------------------------------------------------
             * 从底部向上计算 Y
             * --------------------------------------------------
             *
             * 保证：
             *
             * 单行名字贴近左下角。
             *
             * 多行名字则向上增长。
             */

            var textHeight = Math.Min(
                finalSize.Height,
                AlbumTextMaxHeight
            );

            var y =
                CoverHeight -
                BottomMargin -
                textHeight;

            /*
             * 防止特别长的时候太靠上
             */
            y = Math.Max(
                CoverHeight -
                BottomMargin -
                AlbumTextMaxHeight,
                y
            );

            var rectangle = new RectangleF(
                LeftMargin,
                y,
                AlbumTextWidth,
                AlbumTextMaxHeight
            );

            graphics.DrawString(
                text,
                finalFont,
                brush,
                rectangle,
                format
            );

            finalFont.Dispose();
        }

        /// <summary>
        /// 获取第一个 Unicode 字符
        /// </summary>
        private static string GetFirstTextElement(string text)
        {
            text = text.Trim();

            if (string.IsNullOrEmpty(text))
            {
                return "?";
            }

            var firstCharacter =
                StringInfo.GetNextTextElement(text);

            /*
             * 英文字母转大写。
             *
             * 中文不会受影响。
             */
            return firstCharacter.ToUpperInvariant();
        }

        /// <summary>
        /// 随机背景颜色
        /// </summary>
        private static Color GetRandomBackgroundColor()
        {
            /*
             * 不建议 RGB 完全随机，
             * 因为可能随机出：
             *
             * 白色
             * 浅黄色
             * 浅粉色
             *
             * 会导致白字看不清。
             *
             * 所以这里准备一组适合作为唱片封面的中深色。
             */

            var colors = new[]
            {
                // 深绿
                Color.FromArgb(49, 91, 37),

                // 墨绿
                Color.FromArgb(31, 78, 64),

                // 深蓝
                Color.FromArgb(39, 67, 105),

                // 蓝灰
                Color.FromArgb(54, 75, 94),

                // 深紫
                Color.FromArgb(72, 52, 91),

                // 紫红
                Color.FromArgb(95, 49, 78),

                // 酒红
                Color.FromArgb(106, 47, 52),

                // 暗红
                Color.FromArgb(113, 56, 48),

                // 棕色
                Color.FromArgb(92, 67, 45),

                // 深灰
                Color.FromArgb(61, 66, 70),

                // 深青
                Color.FromArgb(34, 81, 84),

                // 靛蓝
                Color.FromArgb(50, 58, 102),

                // 橄榄绿
                Color.FromArgb(76, 85, 42),

                // 深咖啡
                Color.FromArgb(82, 60, 53)
            };

            return colors[
                Random.Shared.Next(colors.Length)
            ];
        }

        /// <summary>
        /// 获取适合中文、英文的字体
        /// </summary>
        private static FontFamily GetCoverFontFamily()
        {
            /*
             * Windows 默认优先微软雅黑。
             */

            var fontNames = new[]
            {
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "Segoe UI",
                "Arial"
            };

            foreach (var fontName in fontNames)
            {
                try
                {
                    var family = new FontFamily(fontName);

                    if (family.IsStyleAvailable(FontStyle.Regular))
                    {
                        return family;
                    }

                    family.Dispose();
                }
                catch
                {
                    // 当前服务器没有该字体，继续尝试
                }
            }

            return FontFamily.GenericSansSerif;
        }

        /// <summary>
        /// 根据专辑名称计算 SHA256
        /// </summary>
        public static string GetTextHash(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);

            var hashBytes = System.Security.Cryptography.SHA256.HashData(bytes);

            /*
             * 输出：
             *
             * 2f856......
             *
             * 全部转成小写。
             */
            return Convert
                .ToHexString(hashBytes)
                .ToLowerInvariant();
        }


        /// <summary>
        /// 真正生成封面
        /// </summary>
        public static void GenerateCover(string text, string filePath)
        {
            using var bitmap = new Bitmap(
                CoverWidth,
                CoverHeight,
                PixelFormat.Format32bppArgb
            );

            using var graphics = Graphics.FromImage(bitmap);

            // 高质量绘制
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            /*
             * --------------------------------------------------
             * 1. 随机背景
             * --------------------------------------------------
             */

            var backgroundColor = GetRandomBackgroundColor();

            graphics.Clear(backgroundColor);

            /*
             * --------------------------------------------------
             * 2. 获取首字符
             * --------------------------------------------------
             *
             * 中文：
             * 我的专辑 -> 我
             *
             * 英文：
             * hello -> H
             *
             * 同时使用 TextElement，
             * 比直接 text[0] 对 Emoji、特殊 Unicode 更安全。
             */

            var firstCharacter = GetFirstTextElement(text);

            /*
             * --------------------------------------------------
             * 3. 绘制右下角超大首字
             * --------------------------------------------------
             */

            DrawLargeCharacter(
                graphics,
                firstCharacter
            );

            /*
             * --------------------------------------------------
             * 4. 绘制左下角完整专辑名
             * --------------------------------------------------
             */

            DrawAlbumName(
                graphics,
                text
            );

            /*
             * --------------------------------------------------
             * 5. 保存 PNG
             * --------------------------------------------------
             */

            bitmap.Save(
                filePath,
                ImageFormat.Png
            );
        }
        /// <summary>
        /// 绘制右下角巨大的首字符
        /// </summary>
        private static void DrawLargeCharacter(
            Graphics graphics,
            string character
        )
        {
            var fontFamily = GetCoverFontFamily();

            /*
             * 这里控制右下角超大字体尺寸。
             *
             * 参考你给的封面，
             * 故意让字符超出右侧和底部。
             */
            const float fontSize = 300f;

            using var font = new Font(
                fontFamily,
                fontSize,
                FontStyle.Bold,
                GraphicsUnit.Pixel
            );

            using var brush = new SolidBrush(
                Color.FromArgb(255, 255, 255, 255)
            );

            using var format = new StringFormat(
                StringFormat.GenericTypographic
            );

            format.FormatFlags =
                StringFormatFlags.NoWrap |
                StringFormatFlags.NoClip;

            /*
             * 获取文字实际尺寸
             */
            var textSize = graphics.MeasureString(
                character,
                font,
                PointF.Empty,
                format
            );

            /*
             * --------------------------------------------------
             * 关键布局
             * --------------------------------------------------
             *
             * 字符不是完整放在画布里，
             * 而是故意往右下角推。
             *
             * 因为 Graphics 的画布会自动裁掉超出部分，
             * 所以可以得到你参考图中的效果。
             */

            var x =
                CoverWidth -
                textSize.Width * 0.68f;

            var y =
                CoverHeight -
                textSize.Height * 0.72f;

            graphics.DrawString(
                character,
                font,
                brush,
                new PointF(x, y),
                format
            );
        }

        #endregion
    }
    #endregion
}
