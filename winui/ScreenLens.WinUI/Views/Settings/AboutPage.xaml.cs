using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class AboutPage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public AboutPage()
        {
            InitializeComponent();
        }

        private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("原型版本，不执行更新检查");

        private void OnProjectLinkClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("开源项目链接为占位，尚未发布");
    }
}
