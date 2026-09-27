using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class AppearancePage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public AppearancePage()
        {
            InitializeComponent();
            (FindName($"Accent{Vm.AccentIndex}") as RadioButton)!.IsChecked = true;
        }

        private void OnAccentChecked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var index))
            {
                Vm.AccentIndex = index;
                Vm.ShowToast($"已选择强调色（演示状态，未全量应用）");
            }
        }

        private void OnPendingClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("原型界面，尚未接入功能");
    }
}
