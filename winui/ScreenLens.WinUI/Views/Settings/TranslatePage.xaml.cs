using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class TranslatePage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public TranslatePage()
        {
            InitializeComponent();
        }
    }
}
