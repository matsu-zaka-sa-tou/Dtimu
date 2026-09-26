using Dtimu.Models;
using Dtimu.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Dtimu.Controls
{
    public sealed partial class ServerEntrance : UserControl
    {
        public ServerEntrance()
        {
            this.InitializeComponent();
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set
            {
                SetValue(TitleProperty, value);
            }
        }


        // Using a DependencyProperty as the backing store for Title.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(

                "Title",
                typeof(string),
                typeof(ServerEntrance),
                new PropertyMetadata("Defualt_Title", (d, e) => {
                    var r = (d as ServerEntrance);
                    r.PART_Title.Text = e.NewValue as string;
                })
        );
        
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Type np = null;
            if (this.Tag.ToString() == "ths_dev")
            {
                
            }
            else
            {
                
            }
            var instance = DataContext as Models.ServerInstance;
            try
            {

                instance.RefreshList();
                GlobalConfigs.CurrentServerInstance = instance;
                np = typeof(ServerNavigatePage);
                MainPage.CFM.Navigate(np,
                    null, new
                    DrillInNavigationTransitionInfo());
            }
            catch (Exception ex)
            {

                var dialog = new MessageDialog($"无法连接到服务器{instance.IPAddress}, 详情：{ex.Message}");
                dialog.ShowAsync();
            }
        }
    }
}
