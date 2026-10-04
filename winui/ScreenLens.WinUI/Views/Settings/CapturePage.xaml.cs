using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class CapturePage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public CapturePage()
        {
            InitializeComponent();
        }

        private void OnToolbarHelpClick(object sender, RoutedEventArgs e)
            => Vm.ShowToast("工具条行为说明见下方卡片");
    }
}
