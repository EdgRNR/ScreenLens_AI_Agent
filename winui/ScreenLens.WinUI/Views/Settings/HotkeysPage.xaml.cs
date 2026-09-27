using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class HotkeysPage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public HotkeysPage()
        {
            InitializeComponent();
        }

        private void OnEditHotkeyClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("热键录入为原型演示，尚未接入");

        private void OnResetHotkeysClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("已恢复默认快捷键（演示）");
    }
}
