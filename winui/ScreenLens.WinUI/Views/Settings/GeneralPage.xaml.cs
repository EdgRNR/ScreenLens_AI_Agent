using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;
using ScreenLens.WinUI.Services;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class GeneralPage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public GeneralPage()
        {
            InitializeComponent();
            Loaded += async (_, _) => await SettingsService.RefreshStartupAsync(Vm);
        }

        private void OnPendingClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("当前支持简体中文，其他界面语言将在后续版本提供。");
    }
}
