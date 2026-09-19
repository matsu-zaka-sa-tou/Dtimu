using Dtimu.IndexSchemas;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IndexGen
{
    internal class Program
    {
        
        static void Main(string[] args)
        {
#if DEBUG
            string d = "";
            //d = "I:\\TempSteam";
            d = $"\\\\host.storage.home\\H\\anotherTemp\\GoingTemp";
            args = new string[] { d };
#endif
            var dire = args[0];
            //DireInfoUtil.FFmpegConvert(dire);
            //DireInfoUtil.CopyTagFromSameNamedMp3(dire);
            //DireInfoUtil.GroupFiles(dire);
            int i = 28+15;
            foreach (var di in new DirectoryInfo(dire).EnumerateDirectories())
            {
                dire = di.FullName;
                DireInfoUtil.ReTagFromFileName(dire);
                DireInfoUtil.ReNameToSha256(dire);
                ////DireInfoUtil.OrderPicturesByTime(dire);
                DireInfoUtil.BuildIndex(dire);
                DireInfoUtil.GenSimpleDiscInfo(dire, $"创意工坊内容 ({i})");
                //DireInfoUtil.GenSimpleDiscInfo(dire, $"Hanime - 春羽しか");
                //DireInfoUtil.GenInfo(dire, "迷宫河&sprt;2024 年 01 月 31 日游玩迷宫河视频");
                //DireInfoUtil.GenPictrue(dire,123,255);

                i++;
            }
        }
    }
}