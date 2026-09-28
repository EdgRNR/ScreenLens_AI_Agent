using Microsoft.UI.Xaml;
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

        /// <summary>0 Google 免费 / 3 关闭：不显示连接参数表单。</summary>
        public Visibility ProviderFormVis(int provider)
            => provider is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>仅「关闭翻译」时显示说明。</summary>
        public Visibility ProviderOffVis(int provider)
            => provider == 3 ? Visibility.Visible : Visibility.Collapsed;
    }
}
