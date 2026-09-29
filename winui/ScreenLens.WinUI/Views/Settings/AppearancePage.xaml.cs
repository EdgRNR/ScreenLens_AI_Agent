using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class AppearancePage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;
        private bool _isInitializing = true;

        public AppearancePage()
        {
            InitializeComponent();
            (FindName($"Accent{Vm.AccentIndex}") as RadioButton)!.IsChecked = true;
            _isInitializing = false;
        }

        private void OnAccentChecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var index))
            {
                Vm.AccentIndex = index; // setter 触发 AccentChanged → App.ApplyAccent 即时应用
                Vm.ShowToast($"已应用强调色（演示值，仅前端预览）");
            }
        }

        private void OnPendingClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("原型界面，尚未接入功能");
    }
}
