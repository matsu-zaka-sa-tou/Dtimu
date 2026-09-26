using Dtimu.Models;
using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Dtimu.Views
{
    public sealed partial class MusicPage : Page
    {
        private async void SetImageFromBase64(string base64)
        {
            try
            {
                // 将 Base64 转为 byte[]
                byte[] bytes = Convert.FromBase64String(base64);

                // 写入 InMemoryStream
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);

                    // 创建 BitmapImage 并设置到 ImageBrush
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);

                    // 对于 Image 控件
                    AlumbCover.Source = bitmap;
                    //AlumbCover.Source = new ImageBrush
                    //{
                    //    ImageSource = bitmap,
                    //    Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill
                    //};
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("加载 Base64 图片失败: " + ex.Message);
            }
        }

        public MusicPage()
        {
            this.InitializeComponent();
            this.Loaded += MusicPage_Loaded;
        }

        private void MusicPage_Loaded(object sender, RoutedEventArgs e)
        {
            var hash = GlobalConfigs.CurrentHash;
            SetDataContext(hash);

            var groups = VisualStateManager.GetVisualStateGroups(RootGrid);
            if (groups.Count > 0)
            {
                var windowStates = groups[0];
                windowStates.CurrentStateChanged += WindowStates_CurrentStateChanged;
            }

            

            var fileUrl = $"http://{GlobalConfigs.CurrentServerInstance.IPAddress}/api/Files/Music/{GlobalConfigs.CurrentHash}";

            mediaPlayer.Source = new Uri(fileUrl);
            mediaPlayer.Play();
        }

        private void SetDataContext(string hash)
        {
            if (GlobalConfigs.CurrentServerInstance.DireInfo.Musics.ContainsKey(hash))
            {
                var music = GlobalConfigs.CurrentServerInstance.DireInfo.Musics[hash];
                DataContext = music;
                if (GlobalConfigs.CurrentServerInstance.DireInfo.Pictures.ContainsKey(music.Pictrue))
                {
                    var b64 = GlobalConfigs.CurrentServerInstance.DireInfo.Pictures[music.Pictrue];
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        AlumbCover.Visibility = Windows.UI.Xaml.Visibility.Visible;
                        SetImageFromBase64(b64);
                    }
                    else
                    {

                        AlumbCover.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                        FirstLetterBD.Background = new SolidColorBrush(Colors.DarkBlue);
                        var album = GlobalConfigs.CurrentServerInstance.DireInfo.Albums.ToList().Where(x=>x.Value.Musics.Contains(hash)).FirstOrDefault();
                        FirstLetterBDTB.Text = album.Key;
                    }
                }
                else
                {
                    AlumbCover.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                    FirstLetterBD.Background = new SolidColorBrush(Colors.DarkBlue);
                    FirstLetterBDTB.Text = music.Title.Substring(0, 1).ToUpper();
                }
            }
            else if(GlobalConfigs.CurrentServerInstance.DireInfo.Videos.ContainsKey(hash))
            {
                DataContext = GlobalConfigs.CurrentServerInstance.DireInfo.Videos[hash];
            }
        }

        private void WindowStates_CurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            string stateName = e.NewState?.Name ?? "Unknown";

            // 在 Code-behind 中处理不同状态逻辑
            if (stateName == "Narrow")
            {

            }
            else if (stateName == "Wide")
            {

            }
        }

        private void MySlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (mediaPlayer.CurrentState == MediaElementState.Playing)
            {
                // 检查视频时长是否可用
                if (mediaPlayer.NaturalDuration.HasTimeSpan)
                {
                    // Slider 值假设是 0~1
                    double progress = e.NewValue;

                    // 计算目标时间
                    TimeSpan targetTime = TimeSpan.FromSeconds(mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds * progress);

                    // 通过 Position 设置进度
                    mediaPlayer.Position = targetTime;
                }
            }
        }
    }
}
