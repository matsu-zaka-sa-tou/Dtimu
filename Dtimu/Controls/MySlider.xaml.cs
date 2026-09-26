using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

//https://go.microsoft.com/fwlink/?LinkId=234236 上介绍了“用户控件”项模板

namespace Dtimu.Controls
{
    public sealed partial class MySlider : Slider
    {
        public MySlider()
        {
            this.InitializeComponent();
            this.Maximum = 1;
        }
        Thumb HorizontalThumb = null;
        private void HorizontalThumb_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Thumb thumb = this.HorizontalThumb;
            if (thumb == null) return;

            // 获取鼠标在 Canvas 上的坐标
            Canvas canvas = thumb.Parent as Canvas;
            if (canvas == null) return;

            var pointerPoint = e.GetCurrentPoint(canvas);
            double mouseX = pointerPoint.Position.X;

            // 记录 Thumb 相对鼠标的偏移，便于拖动
            thumb.CapturePointer(e.Pointer);
            double left = mouseX-10; // 用 Tag 存储偏移
            Canvas.SetLeft(thumb, left);
        }

        private void HorizontalThumb_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            Thumb thumb = sender as Thumb;
            var width = this.ActualWidth;
            var proc = (double)thumb.GetValue(Canvas.LeftProperty) /(double)width;
            this.Value = proc;

        }

        private void HorizontalThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            
                Thumb thumb = sender as Thumb;
                if (thumb == null) return;

                Canvas canvas = thumb.Parent as Canvas;
                if (canvas == null) return;

                // 当前 Thumb 左边距
                double left = Canvas.GetLeft(thumb);
                if (double.IsNaN(left)) left = 0;

                // 累加鼠标拖动距离
                left += e.HorizontalChange;

                // 限制范围在 Canvas 内
                left = Math.Max(0, Math.Min(left, canvas.ActualWidth - thumb.ActualWidth));

                Canvas.SetLeft(thumb, left);
        }

        private void HorizontalThumb_Loaded(object sender, RoutedEventArgs e)
        {
            HorizontalThumb = sender as Thumb;
        }

        private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {

            Thumb thumb = sender as Thumb;
            var width = this.ActualWidth;
            var proc = (double)thumb.GetValue(Canvas.LeftProperty) / (double)width;
            this.Value = proc;
        }
    }
}
