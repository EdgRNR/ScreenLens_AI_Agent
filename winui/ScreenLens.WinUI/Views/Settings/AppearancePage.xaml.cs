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
        }

        private void OnPendingClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("原型界面，尚未接入功能");
    }
}
